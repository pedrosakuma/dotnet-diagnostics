using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(nameof(EnvSerial))]
public sealed class PodDelegatedAuthorizationIntegrationTests
{
    private const string PodToken = "pod-root-token";
    private const string DelegationKey = "pod-internal-delegation-key";

    [Theory]
    [InlineData("collect_sample")]
    [InlineData("get_bytes")]
    public async Task PodRoot_Executes_With_Exact_Centrally_Delegated_Modifier(string toolName)
    {
        await using var factory = CreatePodFactory();
        await using var client = await ConnectAsync(factory);
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var policies = CreatePolicies();
        var (arguments, callerScopes) = Invocation(toolName);
        var caller = new BearerPrincipal(
            "central-caller",
            callerScopes.ToImmutableHashSet(StringComparer.Ordinal));
        var authorization = registry.Authorize(
            toolName,
            arguments,
            caller,
            proxyInvocation: true,
            policies: policies);
        authorization.IsAllowed.Should().BeTrue();
        var delegated = ToolScopeDelegation.Add(
            new CallToolRequestParams { Name = toolName, Arguments = arguments },
            authorization,
            caller,
            DelegationKey);

        var result = await client.CallToolAsync(
            toolName,
            ToClientArguments(delegated.Arguments),
            cancellationToken: CancellationToken.None);

        ResultText(result).Should().NotContain("literal modifier scope");
        ResultText(result).Should().NotContain("internal scope delegation");
    }

    [Theory]
    [InlineData("collect_sample")]
    [InlineData("get_bytes")]
    public async Task PodRoot_Without_Delegation_Cannot_Call_Tools(string toolName)
    {
        await using var factory = CreatePodFactory();
        await using var client = await ConnectAsync(factory);
        var (arguments, _) = Invocation(toolName);

        var result = await client.CallToolAsync(
            toolName,
            ToClientArguments(arguments),
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeTrue();
        ResultText(result).Should().Contain("require an internal scope delegation");
    }

    [Fact]
    public async Task PodCatalog_MarksToolsUnauthorized_WhenDelegationIsRequired()
    {
        await using var factory = CreatePodFactory();
        await using var client = await ConnectAsync(factory);

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        tools.Should().NotBeEmpty();
        foreach (var tool in tools)
        {
            var auth = tool.ProtocolTool.Meta!["dotnetDiagnostics"]!["auth"]!.AsObject();
            auth["delegationRequired"]!.GetValue<bool>().Should().BeTrue();
            auth["authorized"]!.GetValue<bool>().Should().BeFalse();
        }
    }

    [Fact]
    public async Task TaskPromotedModifierCall_RetainsDelegation_WithoutLeakingToFollowUp()
    {
        await using var factory = CreatePodFactory();
        await using var client = await ConnectAsync(factory);
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var policies = CreatePolicies();
        var (arguments, callerScopes) = Invocation("collect_sample");
        var caller = new BearerPrincipal(
            "central-task-caller",
            callerScopes.ToImmutableHashSet(StringComparer.Ordinal));
        var taskMetadata = new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) };
        var authorization = registry.Authorize(
            "collect_sample",
            arguments,
            caller,
            proxyInvocation: true,
            policies: policies);
        var delegated = ToolScopeDelegation.Add(
            new CallToolRequestParams
            {
                Name = "collect_sample",
                Arguments = arguments,
                Task = taskMetadata,
            },
            authorization,
            caller,
            DelegationKey);

        var task = await client.CallToolAsTaskAsync(
            "collect_sample",
            ToClientArguments(delegated.Arguments),
            taskMetadata,
            cancellationToken: CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (task.Status is McpTaskStatus.Working && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(task.PollInterval ?? TimeSpan.FromMilliseconds(100));
            task = await client.GetTaskAsync(task.TaskId, cancellationToken: CancellationToken.None);
        }

        task.Status.Should().Be(McpTaskStatus.Completed);
        var rawResult = await client.GetTaskResultAsync(
            task.TaskId,
            cancellationToken: CancellationToken.None);
        var taskResult = JsonSerializer.Deserialize<CallToolResult>(
            rawResult.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        ResultText(taskResult).Should().NotContain("internal scope delegation");
        ResultText(taskResult).Should().NotContain("literal modifier scope");

        var followUp = await client.CallToolAsync(
            "collect_sample",
            ToClientArguments(arguments),
            cancellationToken: CancellationToken.None);
        followUp.IsError.Should().BeTrue();
        ResultText(followUp).Should().Contain("require an internal scope delegation");
    }

    private static WebApplicationFactory<Program> CreatePodFactory()
    {
        Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", null);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:BearerTokens:0:Name", "pod-root");
            builder.UseSetting("Auth:BearerTokens:0:Token", PodToken);
            builder.UseSetting("Auth:BearerTokens:0:Scopes:0", BearerPrincipal.RootScope);
            builder.UseSetting("Diagnostics:AllowMethodParameterCapture", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ToolScopeDelegationKeyProvider>();
                services.AddSingleton(new ToolScopeDelegationKeyProvider(DelegationKey));
            });
        });
    }

    private static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PodToken);
        return await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                httpClient,
                ownsHttpClient: true),
            cancellationToken: CancellationToken.None);
    }

    private static ToolScopeResolutionPolicies CreatePolicies()
    {
        var options = new SecurityOptions { AllowMethodParameterCapture = true };
        return new ToolScopeResolutionPolicies(
            new SymbolServerAllowlist(options),
            new EventSourceAllowlist(options),
            new SensitiveValueGate(options),
            new OrchestratorOptions());
    }

    private static (IDictionary<string, JsonElement> Arguments, string[] CallerScopes)
        Invocation(string toolName)
        => toolName switch
        {
            "collect_sample" => (
                Arguments(new
                {
                    kind = "method-params",
                    processId = int.MaxValue,
                    methodFilters = new[] { "Example.Type::Method" },
                    includeSensitiveValues = true,
                    reason = "authorization regression test",
                    durationSeconds = 1,
                }),
                ["eventpipe", "sensitive-parameter-read"]),
            "get_bytes" => (
                Arguments(new
                {
                    kind = "delete",
                    artifactPath = "nonexistent-delegation-test-artifact",
                }),
                ["module-bytes-read", "delete-artifact"]),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName)),
        };

    private static IDictionary<string, JsonElement> Arguments<T>(T value)
        => JsonSerializer.SerializeToElement(value).EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);

    private static string ResultText(CallToolResult result)
        => string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(static content => content.Text));

    private static IReadOnlyDictionary<string, object?> ToClientArguments(
        IDictionary<string, JsonElement>? arguments)
        => arguments?.ToDictionary(
            static pair => pair.Key,
            static pair => (object?)pair.Value,
            StringComparer.Ordinal)
        ?? new Dictionary<string, object?>(StringComparer.Ordinal);
}
