using System.Text.Json;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Tests;

public sealed class NetworkingCorrelationContractTests
{
    [Theory]
    [InlineData("summary", false)]
    [InlineData("byOperation", false)]
    [InlineData("queue", false)]
    [InlineData("tls", false)]
    [InlineData("dns", false)]
    [InlineData("summary", true)]
    [InlineData("byOperation", true)]
    [InlineData("queue", true)]
    [InlineData("tls", true)]
    [InlineData("dns", true)]
    public void AllQueries_PreserveCorrelation(string view, bool legacy)
    {
        var snapshot = NetworkingCorrelationContractFixture.Create(legacy);
        var result = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.NetworkingSnapshot, view, snapshot, 1);
        var json = JsonSerializer.SerializeToElement(result.Result!.Payload);
        if (legacy)
        {
            Assert.Equal(JsonValueKind.Null, json.GetProperty("Correlation").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("CaptureQuality").ValueKind);
            return;
        }
        Assert.Equal(2, json.GetProperty("Correlation").GetProperty("ByKind").GetProperty("http")
            .GetProperty("Counts").GetProperty("ambiguousStarts").GetInt64());
        Assert.Equal("early", json.GetProperty("CaptureQuality").GetProperty("Completion").GetString());
        Assert.Equal(7, json.GetProperty("CaptureQuality").GetProperty("EventsLost").GetInt64());
        Assert.Equal(1, json.GetProperty("CaptureQuality").GetProperty("ParseErrors").GetInt64());
        Assert.True(json.GetProperty("CaptureQuality").GetProperty("HasLimitations").GetBoolean());
        Assert.True(json.GetProperty("Correlation").GetProperty("ByKind").GetProperty("tls").GetProperty("HasLimitations").GetBoolean());
        foreach (var kind in new[] { "http", "dns", "tls" })
        {
            var counts = json.GetProperty("Correlation").GetProperty("ByKind").GetProperty(kind).GetProperty("Counts");
            Assert.Equal(2, counts.GetProperty("latencyPopulationVersion").GetInt64());
            Assert.Equal(1, counts.GetProperty("paired").GetInt64());
            Assert.Equal(1, counts.GetProperty("pairedFailed").GetInt64());
            Assert.Equal(0, counts.GetProperty("pairedWithoutFailure").GetInt64());
        }
        Assert.Equal(1, json.GetProperty("Correlation").GetProperty("ByKind").GetProperty("http")
            .GetProperty("Counts").GetProperty("httpStatusErrorStops").GetInt64());
    }

    [Theory]
    [InlineData(false, SamplingDepth.Summary)]
    [InlineData(true, SamplingDepth.Summary)]
    [InlineData(false, SamplingDepth.Detail)]
    [InlineData(true, SamplingDepth.Detail)]
    public async Task SharedUseCase_AndLegacyJson_DoNotInventCompleteCoverage(bool legacy, SamplingDepth depth)
    {
        var snapshot = NetworkingCorrelationContractFixture.Create(legacy);
        snapshot = snapshot with { ByOperation = Enumerable.Repeat(snapshot.ByOperation[0], 8).ToArray() };
        var handles = new MemoryDiagnosticHandleStore();
        var result = await EventCollectionUseCases.CollectNetworking(new NetworkingCorrelationContractFixture.Collector(snapshot),
            new NetworkingCorrelationContractFixture.Resolver(), handles,
            Environment.ProcessId, 1, depth: depth);
        Assert.False(result.IsError);
        Assert.Equal(snapshot.Correlation, result.Data!.Correlation);
        Assert.Equal(snapshot.CaptureQuality, result.Data.CaptureQuality);
        Assert.Equal(snapshot.Duration, result.Data.Duration);
        Assert.Equal(depth == SamplingDepth.Summary ? 5 : 8, result.Data.ByOperation.Count);
        var stored = handles.TryGet<NetworkingSnapshot>(result.Handle!);
        Assert.NotNull(stored);
        Assert.Same(snapshot, stored);
        Assert.Equal(snapshot.CaptureQuality, stored.CaptureQuality);
        Assert.Equal(8, stored.ByOperation.Count);
        Assert.Contains(legacy ? "transport loss are unknown" : "completion=early", result.Summary, StringComparison.Ordinal);
        var roundTrip = JsonSerializer.Deserialize<NetworkingSnapshot>(JsonSerializer.Serialize(result.Data))!;
        Assert.Equal(snapshot.CaptureQuality, roundTrip.CaptureQuality);
        if (!legacy)
            Assert.Equal(2, roundTrip.Correlation!.Http.AmbiguousStarts);
        Assert.Contains(legacy ? "unknown" : "HTTP 1/3", result.Summary, StringComparison.Ordinal);
        Assert.Contains(legacy ? "Latency population/outcomes are unknown" : "v2 includes failed completions", result.Summary, StringComparison.Ordinal);
        if (!legacy)
        {
            Assert.Equal(1, roundTrip.Correlation!.Http.PairedFailed);
            Assert.Equal(1, stored.Correlation!.Tls.PairedFailed);
        }
        if (legacy)
        {
            var json = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
            json.Remove("Correlation");
            json.Remove("CaptureQuality");
            Assert.Null(json.Deserialize<NetworkingSnapshot>()!.Correlation);
            Assert.Null(json.Deserialize<NetworkingSnapshot>()!.CaptureQuality);
        }
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("byOperation")]
    [InlineData("queue")]
    [InlineData("tls")]
    [InlineData("dns")]
    public async Task LegacyAccountingWithoutPopulationVersion_RemainsUnknown(string view)
    {
        var oldCounts = new NetworkingCorrelationCounts(1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, false);
        var snapshot = NetworkingCorrelationContractFixture.Create() with
        {
            Correlation = new(oldCounts, oldCounts, oldCounts),
        };
        var restored = JsonSerializer.Deserialize<NetworkingSnapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.Null(restored.Correlation!.Http.LatencyPopulationVersion);
        Assert.Null(restored.Correlation.Http.PairedFailed);
        Assert.Null(restored.Correlation.Http.PairedWithoutFailure);
        var query = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.NetworkingSnapshot, view, restored, 5);
        var counts = JsonSerializer.SerializeToElement(query.Result!.Payload)
            .GetProperty("Correlation").GetProperty("ByKind").GetProperty("http").GetProperty("Counts");
        Assert.False(counts.TryGetProperty("latencyPopulationVersion", out _));
        Assert.False(counts.TryGetProperty("pairedFailed", out _));
        var result = await EventCollectionUseCases.CollectNetworking(new NetworkingCorrelationContractFixture.Collector(restored),
            new NetworkingCorrelationContractFixture.Resolver(), new MemoryDiagnosticHandleStore(), durationSeconds: 1);
        Assert.Contains("Latency population/outcomes are unknown", result.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnfinishedFailureAndTlsOnly_AreNotReportedAsMeasuredZeroOrNoActivity(bool tlsOnly)
    {
        var pairs = new NetworkingActivityCorrelator<int>();
        var id = Guid.NewGuid();
        pairs.Start(id, DateTimeOffset.UnixEpoch, 0);
        pairs.Fail(id, DateTimeOffset.UnixEpoch.AddMilliseconds(255));
        var empty = new NetworkingActivityCorrelator<int>().Snapshot();
        var snapshot = NetworkingCorrelationContractFixture.Create() with
        {
            HttpRequestsStarted = tlsOnly ? 0 : 1, HttpRequestsStopped = 0, HttpRequestsFailed = tlsOnly ? 0 : 1,
            HttpRequestP95 = TimeSpan.Zero, DnsLookupsStarted = 0, TlsHandshakesStarted = tlsOnly ? 1 : 0,
            Correlation = tlsOnly ? new(empty, empty, pairs.Snapshot()) : new(pairs.Snapshot(), empty, empty),
        };
        var result = await EventCollectionUseCases.CollectNetworking(new NetworkingCorrelationContractFixture.Collector(snapshot),
            new NetworkingCorrelationContractFixture.Resolver(), new MemoryDiagnosticHandleStore(), durationSeconds: 1);
        Assert.DoesNotContain("No networking activity", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Request p95=unavailable", result.Summary, StringComparison.Ordinal);
        Assert.Equal(1, (tlsOnly ? result.Data!.Correlation!.Tls : result.Data!.Correlation!.Http).UnfinishedFailed);
    }
}
