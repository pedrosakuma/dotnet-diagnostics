using System.Text.Json;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using DotnetDiagnostics.TestSupport;
using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class NetworkingCorrelationContractTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "absent")]
    [InlineData(false, "zero")]
    [InlineData(false, "positive")]
    [InlineData(false, "failed")]
    [InlineData(false, "partial")]
    [InlineData(false, "reservoir")]
    [InlineData(false, "unpaired")]
    [InlineData(false, "incomplete")]
    [InlineData(false, "loss")]
    [InlineData(false, "loss-empty")]
    [InlineData(false, "unknown-loss")]
    [InlineData(false, "invalid-queue")]
    [InlineData(false, "legacy")]
    [InlineData(false, "legacy-counts")]
    public async Task ActualCollectEventsEnvelope_PreservesLimitationsAndLegacyUnknown(bool legacy, string? scenario)
    {
        var snapshot = scenario is null ? NetworkingCorrelationContractFixture.Create(legacy)
            : NetworkingCorrelationContractFixture.CreateLatencyScenario(scenario);
        var result = await CollectEventsTool.CollectEvents(
            counterCollector: null!, exceptionCollector: null!, crashGuardCollector: null!,
            gcCollector: null!, gcDatasCollector: null!, activityCollector: null!, eventSourceCollector: null!,
            eventCatalogCollector: null!, logCollector: null!, jitCollector: null!, threadPoolCollector: null!,
            contentionCollector: null!, dbCollector: null!, kestrelCollector: null!,
            networkingCollector: new NetworkingCorrelationContractFixture.Collector(snapshot),
            inFlightRequestCollector: null!, startupCollector: null!, processResourcesCollector: null!,
            gatedCaptureCollector: null!, cpuSampler: null!, threadSnapshotInspector: null!, dumpInspector: null!,
            processDumper: null!, resolver: new NetworkingCorrelationContractFixture.Resolver(),
            handles: new MemoryDiagnosticHandleStore(), allowlist: new EventSourceAllowlist(null),
            sensitiveGate: new SensitiveValueGate(new SecurityOptions()),
            principalAccessor: StdioRootPrincipalAccessor.Instance, securityOptions: new SecurityOptions(),
            loggerFactory: null, kind: "networking", processId: Environment.ProcessId, durationSeconds: 1,
            cancellationToken: CancellationToken.None);
        Assert.False(result.IsError);
        if (scenario is not null)
        {
            var envelope = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            NetworkingCorrelationContractFixture.AssertLatencyScenario(scenario, envelope.GetProperty("data").GetProperty("networking"),
                result.Summary);
            return;
        }
        Assert.Contains(legacy ? "unknown" : "HTTP 1/3", result.Summary, StringComparison.Ordinal);
        Assert.Contains(legacy ? "Latency population/outcomes are unknown" : "v2 includes failed completions",
            result.Summary, StringComparison.Ordinal);
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains(legacy ? "transport loss are unknown" : "completion=early", result.Summary, StringComparison.Ordinal);
        var quality = json.GetProperty("data").GetProperty("networking").GetProperty("captureQuality");
        if (legacy) Assert.Equal(JsonValueKind.Null, quality.ValueKind);
        else
        {
            Assert.Equal("early", quality.GetProperty("completion").GetString());
            Assert.Equal(7, quality.GetProperty("eventsLost").GetInt64());
            Assert.Equal(1, quality.GetProperty("parseErrors").GetInt64());
        }
        if (!legacy)
        {
            Assert.Equal(2, json.GetProperty("data").GetProperty("networking").GetProperty("correlation")
                .GetProperty("byKind").GetProperty("http").GetProperty("counts").GetProperty("ambiguousStarts").GetInt64());
            foreach (var kind in new[] { "http", "dns", "tls" })
            {
                var counts = json.GetProperty("data").GetProperty("networking").GetProperty("correlation")
                    .GetProperty("byKind").GetProperty(kind).GetProperty("counts");
                Assert.Equal(2, counts.GetProperty("latencyPopulationVersion").GetInt64());
                Assert.Equal(1, counts.GetProperty("pairedFailed").GetInt64());
                Assert.Equal(0, counts.GetProperty("pairedWithoutFailure").GetInt64());
            }
        }
    }
}
