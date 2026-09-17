using System.Text.Json;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Tests;

public sealed class NetworkingLatencyTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("zero")]
    [InlineData("positive")]
    [InlineData("failed")]
    [InlineData("partial")]
    [InlineData("reservoir")]
    [InlineData("unpaired")]
    [InlineData("incomplete")]
    [InlineData("loss")]
    [InlineData("loss-empty")]
    [InlineData("unknown-loss")]
    [InlineData("invalid-queue")]
    [InlineData("legacy")]
    [InlineData("legacy-counts")]
    public async Task SnapshotHandleSummaryAndAllViews_PreserveAvailability(string scenario)
    {
        var snapshot = NetworkingCorrelationContractFixture.CreateLatencyScenario(scenario);
        var json = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
        // Derived values cannot be forged by a serialized legacy artifact.
        json["LatencyAvailability"] = JsonSerializer.SerializeToNode(new { http = "measured" });
        if (scenario == "legacy")
        {
            json.Remove("Correlation");
            json.Remove("CaptureQuality");
            json["ByOperation"]![0]!.AsObject().Remove("PercentileSamples");
        }
        var restored = json.Deserialize<NetworkingSnapshot>()!;
        var handles = new MemoryDiagnosticHandleStore();
        var result = await EventCollectionUseCases.CollectNetworking(new NetworkingCorrelationContractFixture.Collector(restored),
            new NetworkingCorrelationContractFixture.Resolver(), handles, durationSeconds: 1);
        Assert.False(result.IsError);
        NetworkingCorrelationContractFixture.AssertLatencyScenario(scenario, JsonSerializer.SerializeToElement(result.Data), result.Summary);
        var stored = handles.TryGet<NetworkingSnapshot>(result.Handle!);
        Assert.Same(restored, stored);
        foreach (var view in new[] { "summary", "byOperation", "queue", "tls", "dns" })
        {
            var query = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.NetworkingSnapshot, view, stored!, 1);
            NetworkingCorrelationContractFixture.AssertLatencyScenario(scenario, JsonSerializer.SerializeToElement(query.Result!.Payload));
        }
    }

    [Theory]
    [InlineData("Resolution", 0)]
    [InlineData("Resolution", 255)]
    [InlineData("Handshake", 0)]
    [InlineData("Handshake", 255)]
    public void ProductionPairs_ZeroAndFailedPositiveAreMeasured(string prefix, int milliseconds)
    {
        var pending = new NetworkingActivityCorrelator<DateTimeOffset>();
        var durations = new BoundedDurationSampler();
        long started = 0, stopped = 0, failed = 0;
        var id = Guid.NewGuid();
        Handle("Start", 0);
        Handle("Failed", 0);
        Handle("Stop", milliseconds);
        var counts = EventPipeNetworkingCollector.WithPercentileSamples(pending.Snapshot(), durations);
        Assert.Equal(1, counts.LatencySamples);
        Assert.Equal(1, counts.PercentileSamples);
        Assert.Equal(1, counts.PairedFailed);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), durations.GetPercentile(0.95));
        Assert.All(NetworkingLatency.Availability(new(counts, counts, counts), null)
            .Where(x => x.Key != "queue"), x => Assert.Equal("measured", x.Value));

        void Handle(string suffix, int elapsed)
            => EventPipeNetworkingCollector.HandlePaired(prefix + suffix, prefix + "Start", prefix + "/Start",
                prefix + "Stop", prefix + "/Stop", prefix + "Failed", prefix + "/Failed",
                id, DateTimeOffset.UnixEpoch.AddMilliseconds(elapsed), pending, durations, ref started, ref stopped, ref failed);
    }

    [Fact]
    public void ProductionSamples_ReservoirPopulationAndCoverageRemainIndependent()
    {
        var pending = new NetworkingActivityCorrelator<int>();
        var durations = new BoundedDurationSampler();
        var group = new EventPipeNetworkingCollector.MutableHttpGroup("http://localhost", "/");
        for (var i = 0; i < BoundedPercentileSampler.ExactSampleCapacity + 1; i++)
        {
            var id = Guid.NewGuid();
            pending.Start(id, DateTimeOffset.UnixEpoch, 0);
            Assert.True(pending.Stop(id, DateTimeOffset.UnixEpoch, out _, out var elapsed));
            durations.Add(elapsed);
            group.Add(elapsed);
        }
        pending.Start(Guid.Empty, DateTimeOffset.UnixEpoch, 0);
        var counts = EventPipeNetworkingCollector.WithPercentileSamples(pending.Snapshot(), durations);
        Assert.Equal(4097, counts.LatencySamples);
        Assert.Equal(4096, counts.PercentileSamples);
        Assert.True(counts.HasLimitations);
        Assert.True(durations.IsApproximate);
        var record = group.ToRecord();
        Assert.Equal(4097, record.Count);
        Assert.Equal(4096, record.PercentileSamples);
        Assert.Equal("measured", record.LatencyAvailability);
        Assert.Equal(TimeSpan.Zero, record.MaxDuration);
        Assert.Equal("measured", NetworkingLatency.Availability(new(counts, counts, counts),
            new("source-failure", null, TimeSpan.Zero, 0))["http"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData("not-a-number")]
    public void ProductionQueue_InvalidPayloadsCannotManufactureZero(object? payload)
    {
        var durations = new BoundedDurationSampler();
        Assert.Throws<FormatException>(() => EventPipeNetworkingCollector.AddQueueSample(payload, durations));
        Assert.Equal(0, durations.Count);
    }

    [Fact]
    public void ProductionQueue_ValidZeroAndPositiveCountSeparatelyFromStops()
    {
        var durations = new BoundedDurationSampler();
        EventPipeNetworkingCollector.AddQueueSample(0d, durations);
        Assert.Equal(1, durations.Count);
        Assert.Equal(TimeSpan.Zero, durations.Max);
        EventPipeNetworkingCollector.AddQueueSample(255d, durations);
        Assert.Equal(2, durations.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(255), durations.GetPercentile(0.95));
    }

    [Fact]
    public void ProductionQueue_UnrepresentableDurationDoesNotAddASample()
    {
        var durations = new BoundedDurationSampler();
        Assert.Throws<OverflowException>(() => EventPipeNetworkingCollector.AddQueueSample(double.MaxValue, durations));
        Assert.Equal(0, durations.Count);
    }

    [Theory]
    [InlineData("early", 0L, 0L)]
    [InlineData("source-failure", null, 0L)]
    [InlineData("unknown", null, 0L)]
    [InlineData("normal", null, 0L)]
    [InlineData("normal", 0L, 1L)]
    public void NoSamples_IncompleteOrUnknownAcquisitionIsNotNotObserved(string completion, long? loss, long errors)
    {
        var snapshot = NetworkingCorrelationContractFixture.CreateLatencyScenario("absent") with
        {
            CaptureQuality = new(completion, loss, TimeSpan.Zero, errors),
        };
        Assert.All(snapshot.LatencyAvailability.Values, value => Assert.Equal("incomplete", value));
    }

    [Fact]
    public void OperationConstructorAndDeconstruction_RemainLegacyCompatible()
    {
        var group = new NetworkingHttpGroup("host", "/path", 1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        var (host, path, count, total, p95, max) = group;
        Assert.Equal(("host", "/path", 1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            (host, path, count, total, p95, max));
        Assert.Null(group.PercentileSamples);
        Assert.Equal("unknown", group.LatencyAvailability);
    }

    [Fact]
    public void MissingPairsAndQuality_HaveDistinctAvailabilityAndNeverUseHeadlineOrScalar()
    {
        var pending = new NetworkingActivityCorrelator<int>();
        pending.Start(Guid.Empty, DateTimeOffset.UnixEpoch, 0);
        var counts = pending.Snapshot();
        var snapshot = NetworkingCorrelationContractFixture.CreateLatencyScenario("positive") with
        {
            HttpRequestsStopped = 100, HttpRequestP95 = TimeSpan.FromSeconds(10),
            Correlation = new(counts, counts, counts),
        };
        Assert.Equal("uncorrelatable", snapshot.LatencyAvailability["http"]);
        Assert.Equal("unknown", snapshot.LatencyAvailability["queue"]);
        var absent = NetworkingCorrelationContractFixture.CreateLatencyScenario("absent") with { CaptureQuality = null };
        Assert.Equal("unknown", absent.LatencyAvailability["http"]);
    }
}
