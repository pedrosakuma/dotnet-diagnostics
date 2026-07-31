using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.Safety;
using DotnetDiagnostics.Mcp.Safety;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class McpInvocationSafetyTests : IClassFixture<McpInvocationSafetyTests.SafetyFactory>
{
    private readonly SafetyFactory _factory;

    public McpInvocationSafetyTests(SafetyFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ListTools_AdvertisesStaticSafety_ConditionalFlag_AndReservedAcknowledgementOnlyWhereNeeded()
    {
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        foreach (var tool in tools)
        {
            var diagnostics = tool.ProtocolTool.Meta?["dotnetDiagnostics"]?.AsObject();
            diagnostics.Should().NotBeNull();
            diagnostics!["safety"].Should().NotBeNull();
            diagnostics["safety"]!["riskLevel"]!.GetValue<string>()
                .Should().BeOneOf("low", "moderate", "high", "critical");
            diagnostics["hasConditionalSafety"].Should().NotBeNull();
            tool.Description.Should().Contain("_meta.dotnetDiagnostics.safety");
        }

        var low = tools.Single(static tool => tool.Name == DiagnosticOperationCatalog.StartInvestigation);
        low.ProtocolTool.Meta!["dotnetDiagnostics"]!["safety"]!["riskLevel"]!.GetValue<string>()
            .Should().Be("low");
        low.JsonSchema.GetProperty("properties")
            .TryGetProperty(McpInvocationSafetyFilter.ReservedArgumentName, out _)
            .Should().BeFalse("low-only tools must not train clients to send generic confirmations");

        var conditional = tools.Single(static tool => tool.Name == DiagnosticOperationCatalog.CollectSample);
        conditional.ProtocolTool.Meta!["dotnetDiagnostics"]!["hasConditionalSafety"]!.GetValue<bool>()
            .Should().BeTrue();
        conditional.JsonSchema.GetProperty("properties")
            .TryGetProperty(McpInvocationSafetyFilter.ReservedArgumentName, out _)
            .Should().BeTrue();

        var dump = tools.Single(static tool => tool.Name == DiagnosticOperationCatalog.CollectProcessDump);
        dump.JsonSchema.GetProperty("properties")
            .TryGetProperty(McpInvocationSafetyFilter.ReservedArgumentName, out _)
            .Should().BeFalse("the established dump confirm/elicitation contract must remain the only dump fallback");
    }

    [Fact]
    public async Task LowCall_IsPromptFree_AndModerateCallCarriesWarnings()
    {
        await using var client = await ConnectAsync();

        var low = await client.CallToolAsync(
            DiagnosticOperationCatalog.InspectProcess,
            new Dictionary<string, object?> { ["view"] = "list" },
            cancellationToken: CancellationToken.None);
        low.IsError.Should().NotBe(true);
        Structured(low).GetProperty("safety").GetProperty("riskLevel").GetString().Should().Be("low");
        Structured(low).TryGetProperty("safetyWarnings", out _).Should().BeFalse();
        Structured(low).TryGetProperty("safetyApproval", out _).Should().BeFalse();

        var moderate = await client.CallToolAsync(
            DiagnosticOperationCatalog.InspectProcess,
            new Dictionary<string, object?>
            {
                ["view"] = "runtime-config",
                ["processId"] = Environment.ProcessId,
            },
            cancellationToken: CancellationToken.None);
        moderate.IsError.Should().NotBe(true);
        var root = Structured(moderate);
        root.GetProperty("safety").GetProperty("riskLevel").GetString().Should().Be("moderate");
        root.GetProperty("safetyWarnings").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HighCall_MissingOrWrongAcknowledgement_FailsBeforeInvocation_ThenExactPreviewPasses()
    {
        await using var client = await ConnectAsync();
        var baseArguments = new Dictionary<string, object?>
        {
            ["source"] = "live",
            ["processId"] = 99999999,
        };

        var missing = await client.CallToolAsync(
            DiagnosticOperationCatalog.InspectHeap,
            baseArguments,
            cancellationToken: CancellationToken.None);
        missing.IsError.Should().NotBe(true, Structured(missing).GetRawText());
        ApprovalStatus(missing).Should().Be("acknowledgement-required");
        Structured(missing).TryGetProperty("error", out _).Should().BeFalse();

        var wrongArguments = new Dictionary<string, object?>(baseArguments)
        {
            [McpInvocationSafetyFilter.ReservedArgumentName] = new Dictionary<string, object?>
            {
                [McpInvocationSafetyFilter.AcknowledgementPropertyName] = new
                {
                    riskLevel = "high",
                    targetImpact = Array.Empty<string>(),
                },
            },
        };
        var wrong = await client.CallToolAsync(
            DiagnosticOperationCatalog.InspectHeap,
            wrongArguments,
            cancellationToken: CancellationToken.None);
        ApprovalStatus(wrong).Should().Be("acknowledgement-required");

        var acknowledgedArguments = new Dictionary<string, object?>(baseArguments)
        {
            [McpInvocationSafetyFilter.ReservedArgumentName] = AcknowledgementFrom(missing),
        };
        var acknowledged = await client.CallToolAsync(
            DiagnosticOperationCatalog.InspectHeap,
            acknowledgedArguments,
            cancellationToken: CancellationToken.None);
        ApprovalStatusOrNull(acknowledged).Should().BeNull();
        Structured(acknowledged).GetProperty("safety").GetProperty("riskLevel").GetString().Should().Be("high");
        Structured(acknowledged).GetProperty("error").GetProperty("kind").GetString()
            .Should().NotBe("SafetyAcknowledgementRequired");
    }

    [Fact]
    public async Task CriticalFallback_IsFailClosed_AndExactAcknowledgementIsRemovedBeforeBinding()
    {
        await using var client = await ConnectAsync();
        var handle = RegisterCriticalHandle();
        var arguments = new Dictionary<string, object?>
        {
            ["handle"] = handle,
            ["view"] = "events",
            ["includeSensitiveValues"] = true,
        };

        var preview = await client.CallToolAsync(
            DiagnosticOperationCatalog.QuerySnapshot,
            arguments,
            cancellationToken: CancellationToken.None);
        preview.IsError.Should().NotBe(true, Structured(preview).GetRawText());
        ApprovalStatus(preview).Should().Be("human-approval-required");
        Structured(preview).GetProperty("safety").GetProperty("riskLevel").GetString().Should().Be("critical");

        arguments[McpInvocationSafetyFilter.ReservedArgumentName] = AcknowledgementFrom(preview);
        var acknowledged = await client.CallToolAsync(
            DiagnosticOperationCatalog.QuerySnapshot,
            arguments,
            cancellationToken: CancellationToken.None);

        acknowledged.IsError.Should().BeTrue();
        var root = Structured(acknowledged);
        root.TryGetProperty("safetyApproval", out _).Should().BeFalse();
        root.GetProperty("safety").GetProperty("riskLevel").GetString().Should().Be("critical");
        root.GetProperty("error").GetProperty("kind").GetString().Should().Be("MethodParameterCaptureDisabled",
            "the reserved safety argument must be removed before SDK binding/tool invocation");
    }

    [Fact]
    public async Task CriticalElicitation_ApproveContinues_DeclineAndFailureFailClosed()
    {
        var handle = RegisterCriticalHandle();
        var arguments = new Dictionary<string, object?>
        {
            ["handle"] = handle,
            ["view"] = "events",
            ["includeSensitiveValues"] = true,
        };

        var approvedRequests = new List<ElicitRequestParams>();
        await using (var approvedClient = await ConnectAsync(ElicitationOptions(
                         approve: true,
                         approvedRequests,
                         throwOnElicit: false)))
        {
            var approved = await approvedClient.CallToolAsync(
                DiagnosticOperationCatalog.QuerySnapshot,
                arguments,
                cancellationToken: CancellationToken.None);
            approvedRequests.Should().ContainSingle();
            approvedRequests[0].Message.Should().Contain("Target impact:");
            approvedRequests[0].Message.Should().Contain("Data exposure:");
            approved.IsError.Should().BeTrue();
            Structured(approved).GetProperty("error").GetProperty("kind").GetString()
                .Should().Be("MethodParameterCaptureDisabled");
            Structured(approved).GetProperty("safety").GetProperty("riskLevel").GetString().Should().Be("critical");
        }

        var declinedRequests = new List<ElicitRequestParams>();
        await using (var declinedClient = await ConnectAsync(ElicitationOptions(
                         approve: false,
                         declinedRequests,
                         throwOnElicit: false)))
        {
            var declined = await declinedClient.CallToolAsync(
                DiagnosticOperationCatalog.QuerySnapshot,
                arguments,
                cancellationToken: CancellationToken.None);
            declinedRequests.Should().ContainSingle();
            declined.IsError.Should().NotBe(true);
            ApprovalStatus(declined).Should().Be("declined");
            Structured(declined).GetProperty("safetyApproval")
                .TryGetProperty("requiredAcknowledgement", out _).Should().BeFalse(
                    "a human decline must not invite a fallback retry");
        }

        var failedRequests = new List<ElicitRequestParams>();
        await using var failedClient = await ConnectAsync(ElicitationOptions(
            approve: true,
            failedRequests,
            throwOnElicit: true));
        var failed = await failedClient.CallToolAsync(
            DiagnosticOperationCatalog.QuerySnapshot,
            arguments,
            cancellationToken: CancellationToken.None);
        failedRequests.Should().ContainSingle();
        failed.IsError.Should().BeTrue();
        ApprovalStatus(failed).Should().Be("failed");
        Structured(failed).GetProperty("error").GetProperty("kind").GetString()
            .Should().Be("ElicitationFailed");
    }

    [Fact]
    public async Task Batch_InheritsHighestRisk_ExposesEveryChild_AndStartsNoCollectionWithoutAcknowledgement()
    {
        await using var client = await ConnectAsync();
        var stopwatch = Stopwatch.StartNew();
        var result = await client.CallToolAsync(
            DiagnosticOperationCatalog.CollectBatch,
            new Dictionary<string, object?>
            {
                ["durationSeconds"] = 30,
                ["requests"] = new object[]
                {
                    new { tool = "collect_events", kind = "counters" },
                    new { tool = "collect_sample", kind = "off_cpu" },
                },
            },
            cancellationToken: CancellationToken.None);
        stopwatch.Stop();

        result.IsError.Should().NotBe(true);
        ApprovalStatus(result).Should().Be("acknowledgement-required");
        var root = Structured(result);
        root.GetProperty("safety").GetProperty("riskLevel").GetString().Should().Be("high");
        var children = root.GetProperty("childSafety");
        children.GetArrayLength().Should().Be(2);
        children.EnumerateArray().Select(static child =>
                child.GetProperty("safety").GetProperty("riskLevel").GetString())
            .Should().Contain(["low", "high"]);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "the 30-second collectors must not start before the batch's highest-risk child is acknowledged");
    }

    private async Task<McpClient> ConnectAsync(McpClientOptions? options = null)
    {
        var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", SafetyFactory.Token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, options, cancellationToken: CancellationToken.None);
    }

    private string RegisterCriticalHandle()
        => _factory.Services.GetRequiredService<IDiagnosticHandleStore>().Register(
            Environment.ProcessId,
            "method-params-capture",
            new MethodParameterCaptureArtifact(
                Environment.ProcessId,
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(1),
                "CoreCLR",
                "10.0.0",
                [],
                [],
                MaxEvents: 10,
                PreviewCount: 10,
                CaptureCount: 0,
                DroppedCount: 0,
                TruncatedValueCount: 0,
                RedactedValueCount: 0,
                ValuesTruncated: false,
                ValuesRedacted: false,
                StopReason: "completed",
                Events: []),
            TimeSpan.FromMinutes(1),
            evictWhenProcessExits: false).Id;

    private static McpClientOptions ElicitationOptions(
        bool approve,
        List<ElicitRequestParams> captured,
        bool throwOnElicit)
        => new()
        {
            Capabilities = new ClientCapabilities
            {
                Elicitation = new ElicitationCapability(),
            },
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    if (request is not null)
                    {
                        captured.Add(request);
                    }

                    if (throwOnElicit)
                    {
                        throw new InvalidOperationException("simulated elicitation failure");
                    }

                    return ValueTask.FromResult(new ElicitResult
                    {
                        Action = approve ? "accept" : "decline",
                        Content = new Dictionary<string, JsonElement>
                        {
                            ["approve"] = JsonSerializer.SerializeToElement(approve),
                        },
                    });
                },
            },
        };

    private static JsonElement Structured(CallToolResult result)
        => result.StructuredContent
            ?? throw new InvalidOperationException(
                "Expected structured content. Text: " +
                string.Join(" | ", result.Content.OfType<TextContentBlock>().Select(static block => block.Text)));

    private static string ApprovalStatus(CallToolResult result)
        => Structured(result).GetProperty("safetyApproval").GetProperty("status").GetString()
            ?? throw new InvalidOperationException("Expected safety approval status.");

    private static string? ApprovalStatusOrNull(CallToolResult result)
        => Structured(result).TryGetProperty("safetyApproval", out var approval)
            ? approval.GetProperty("status").GetString()
            : null;

    private static Dictionary<string, object?> AcknowledgementFrom(CallToolResult preview)
    {
        var required = Structured(preview)
            .GetProperty("safetyApproval")
            .GetProperty("requiredAcknowledgement");
        return new Dictionary<string, object?>
        {
            [McpInvocationSafetyFilter.AcknowledgementPropertyName] =
                JsonSerializer.Deserialize<JsonElement>(required.GetRawText()),
        };
    }

    public sealed class SafetyFactory : WebApplicationFactory<DotnetDiagnostics.Mcp.Program>
    {
        public const string Token = "mcp-safety-integration-token";

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:BearerTokens:0:Name"] = "safety-tests",
                    ["Auth:BearerTokens:0:Token"] = Token,
                    ["Auth:BearerTokens:0:Scopes:0"] = "*",
                    ["Auth:BearerTokens:0:Scopes:1"] = "sensitive-heap-read",
                    ["Auth:BearerTokens:0:Scopes:2"] = "sensitive-parameter-read",
                });
            });
            base.ConfigureWebHost(builder);
        }
    }
}
