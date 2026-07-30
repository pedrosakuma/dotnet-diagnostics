using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
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
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    public static TheoryData<string, string> ExportEvidenceKinds()
        => new()
        {
            { "cpu-sample", "eventpipe" },
            { CollectionHandleKinds.Counters, "read-counters" },
            { CollectionHandleKinds.GcEvents, "eventpipe" },
            { CollectionHandleKinds.GcDatas, "eventpipe" },
            { SamplerUseCases.ThreadSnapshotKind, "ptrace" },
        };

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

    [Theory]
    [InlineData("collect_sample")]
    [InlineData("get_bytes")]
    public async Task PodRoot_CannotForgeDelegationOutsideCentralAuthorizationPath(string toolName)
    {
        await using var factory = CreatePodFactory();
        await using var client = await ConnectAsync(factory);
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var policies = CreatePolicies();
        var (arguments, callerScopes) = Invocation(toolName);
        var caller = new BearerPrincipal(
            "attacker",
            callerScopes.ToImmutableHashSet(StringComparer.Ordinal));
        var authorization = registry.Authorize(
            toolName,
            arguments,
            caller,
            proxyInvocation: true,
            policies: policies);
        var forged = ToolScopeDelegation.Add(
            new CallToolRequestParams { Name = toolName, Arguments = arguments },
            authorization,
            caller,
            "attacker-controlled-key");

        var result = await client.CallToolAsync(
            toolName,
            ToClientArguments(forged.Arguments),
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeTrue();
        ResultText(result).Should().Contain("signature is invalid");
    }

    [Theory]
    [MemberData(nameof(ExportEvidenceKinds))]
    public async Task PodRoot_Export_RequiresExactDelegatedEvidenceScope(
        string kind,
        string requiredScope)
    {
        await using var factory = CreatePodFactory();
        await using var client = await ConnectAsync(factory);
        var store = factory.Services.GetRequiredService<IDiagnosticHandleStore>();
        var handle = store.Register(
            1234,
            kind,
            ExportArtifact(kind),
            TimeSpan.FromMinutes(5),
            origin: HandleOrigin.Live);
        var arguments = Arguments(new { handle = handle.Id });

        var denied = await CallDelegatedAsync(
            client,
            "export_investigation_summary",
            arguments,
            ["investigation-export"]);
        ResultText(denied).Should().Contain("Forbidden").And.Contain(requiredScope);

        var allowed = await CallDelegatedAsync(
            client,
            "export_investigation_summary",
            arguments,
            ["investigation-export", requiredScope]);
        ResultText(allowed).Should().NotContain("Forbidden");
        ResultText(allowed).Should().Contain("investigationId");
    }

    [Fact]
    public async Task PodRoot_ExportMixedHandles_RequiresAllDelegatedEvidenceScopes()
    {
        await using var factory = CreatePodFactory();
        await using var client = await ConnectAsync(factory);
        var store = factory.Services.GetRequiredService<IDiagnosticHandleStore>();
        var counters = RegisterExportArtifact(store, CollectionHandleKinds.Counters);
        var gc = RegisterExportArtifact(store, CollectionHandleKinds.GcEvents);
        var threads = RegisterExportArtifact(store, SamplerUseCases.ThreadSnapshotKind);
        var arguments = Arguments(new
        {
            handle = counters.Id,
            additionalHandles = new[] { gc.Id, threads.Id },
        });

        var denied = await CallDelegatedAsync(
            client,
            "export_investigation_summary",
            arguments,
            ["investigation-export", "read-counters", "eventpipe"]);
        ResultText(denied).Should().Contain("Forbidden").And.Contain("ptrace");

        var allowed = await CallDelegatedAsync(
            client,
            "export_investigation_summary",
            arguments,
            ["investigation-export", "read-counters", "eventpipe", "ptrace"]);
        ResultText(allowed).Should().NotContain("Forbidden");
        ResultText(allowed).Should().Contain("\"evidence\"");
    }

    [Fact]
    public async Task PodRoot_ExportNonFiniteMetric_ReturnsStructuredDiagnostic()
    {
        await using var factory = CreatePodFactory();
        await using var client = await ConnectAsync(factory);
        var store = factory.Services.GetRequiredService<IDiagnosticHandleStore>();
        var handle = store.Register(
            1234,
            CollectionHandleKinds.Counters,
            new CounterSnapshot(
                1234,
                T0,
                TimeSpan.FromSeconds(1),
                [new CounterValue(
                    "System.Runtime",
                    "threadpool-queue-length",
                    "Queue",
                    double.PositiveInfinity,
                    CounterKind.Mean)],
                [],
                []),
            TimeSpan.FromMinutes(5),
            origin: HandleOrigin.Live);

        var result = await CallDelegatedAsync(
            client,
            "export_investigation_summary",
            Arguments(new { handle = handle.Id }),
            ["investigation-export", "read-counters"]);

        ResultText(result).Should().Contain("InvalidEvidenceMetric")
            .And.Contain("Evidence contains a non-finite metric value.")
            .And.Contain("NonFiniteMetricValue");
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

        task.Status.Should().BeOneOf(McpTaskStatus.Completed, McpTaskStatus.Failed);
        var rawResult = await client.GetTaskResultAsync(
            task.TaskId,
            cancellationToken: CancellationToken.None);
        var taskResult = JsonSerializer.Deserialize<CallToolResult>(
            rawResult.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        taskResult.IsError.Should().Be(task.Status == McpTaskStatus.Failed);
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

    private static async Task<CallToolResult> CallDelegatedAsync(
        McpClient client,
        string toolName,
        IDictionary<string, JsonElement> arguments,
        string[] callerScopes)
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var caller = new BearerPrincipal(
            "central-caller",
            callerScopes.ToImmutableHashSet(StringComparer.Ordinal));
        var authorization = registry.Authorize(
            toolName,
            arguments,
            caller,
            proxyInvocation: true,
            policies: CreatePolicies());
        authorization.IsAllowed.Should().BeTrue();
        var delegated = ToolScopeDelegation.Add(
            new CallToolRequestParams { Name = toolName, Arguments = arguments },
            authorization,
            caller,
            DelegationKey);

        return await client.CallToolAsync(
            toolName,
            ToClientArguments(delegated.Arguments),
            cancellationToken: CancellationToken.None);
    }

    private static DiagnosticHandle RegisterExportArtifact(
        IDiagnosticHandleStore store,
        string kind)
        => store.Register(
            1234,
            kind,
            ExportArtifact(kind),
            TimeSpan.FromMinutes(5),
            origin: HandleOrigin.Live);

    private static object ExportArtifact(string kind)
        => kind switch
        {
            "cpu-sample" => new CpuSampleTraceArtifact(
                1234,
                T0,
                TimeSpan.FromSeconds(1),
                1,
                new CallTreeNode(
                    new SampledFrame(string.Empty, "<root>"),
                    1,
                    0,
                    [new CallTreeNode(new SampledFrame("App.dll", "App.Work"), 1, 1, [])])),
            CollectionHandleKinds.Counters => new CounterSnapshot(
                1234,
                T0,
                TimeSpan.FromSeconds(1),
                [new CounterValue("System.Runtime", "threadpool-queue-length", "Queue", 1, CounterKind.Mean)],
                [],
                []),
            CollectionHandleKinds.GcEvents => new GcSummary(
                1234,
                T0,
                TimeSpan.FromSeconds(1),
                1,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
                [new GenerationStats(0, 1)],
                []),
            CollectionHandleKinds.GcDatas => new GcDatasSnapshot(
                1234,
                T0,
                TimeSpan.FromSeconds(1),
                [new DatasSampleEvent(T0, 1, 100, 1, 0, 0, 1024, 512)],
                [],
                [],
                new DatasParseStats(0, 0, 0)),
            SamplerUseCases.ThreadSnapshotKind => new ThreadSnapshotArtifact(
                ThreadSnapshotOrigin.Live,
                1234,
                T0,
                TimeSpan.FromMilliseconds(1),
                ".NET",
                "10.0.0",
                [],
                []),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported evidence kind."),
        };

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
