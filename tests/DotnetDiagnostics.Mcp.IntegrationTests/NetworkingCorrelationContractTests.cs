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
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCollectEventsEnvelope_PreservesLimitationsAndLegacyUnknown(bool legacy)
    {
        var snapshot = NetworkingCorrelationContractFixture.Create(legacy);
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
        Assert.Contains(legacy ? "unknown" : "HTTP 1/3", result.Summary, StringComparison.Ordinal);
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
            Assert.Equal(2, json.GetProperty("data").GetProperty("networking").GetProperty("correlation")
                .GetProperty("byKind").GetProperty("http").GetProperty("counts").GetProperty("ambiguousStarts").GetInt64());
    }
}
