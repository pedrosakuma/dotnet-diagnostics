using System.Collections.Concurrent;
using DotnetDiagnostics.Core.Counters;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Exercises <see cref="EventPipeCounterCollector"/>'s max-per-tick aggregation logic directly
/// (independent of a live EventPipe session), so a scenario's window-level invariant (#858) is
/// provably backed by real aggregation rather than by a fixture that merely relabels a single
/// static value.
/// </summary>
public sealed class EventPipeCounterCollectorTests
{
    [Fact]
    public void TrackMaxCounter_AcrossPeakThenDropTickSequence_ReportsTruePeakNotFinalTick()
    {
        // Simulates the exact regression from #858: `loh-size` starts at 0, spikes to 3.6MB
        // mid-window as the workload allocates large objects, drops to 1.2MB after a partial
        // collection, and is back down to 0 by the final tick once gen2 reclaims the LOH. The
        // last-observed value (what `Counters` reports) is 0 — but `MaxCounters` must report the
        // true peak of 3.6MB, proving the aggregation itself (not a static fixture) is correct.
        var maxCounters = new ConcurrentDictionary<string, CounterValue>(StringComparer.Ordinal);
        const string key = "System.Runtime/loh-size";
        double[] ticks = [0, 3_601_008, 1_200_000, 0];

        CounterValue? latestObserved = null;
        foreach (var tick in ticks)
        {
            var value = LohSizeTick(tick);
            latestObserved = value;
            EventPipeCounterCollector.TrackMaxCounter(maxCounters, key, value);
        }

        latestObserved!.Value.Should().Be(0, "the last tick in the sequence is 0, mirroring the observed CI failure mode");
        maxCounters.Should().ContainKey(key);
        maxCounters[key].Value.Should().Be(3_601_008, "MaxCounters must report the true peak observed anywhere in the window, not the final tick");
    }

    [Fact]
    public void TrackMaxCounter_MonotonicallyIncreasingTicks_TracksRunningMaximum()
    {
        var maxCounters = new ConcurrentDictionary<string, CounterValue>(StringComparer.Ordinal);
        const string key = "System.Runtime/loh-size";
        double[] ticks = [100, 200, 300];

        foreach (var tick in ticks)
        {
            EventPipeCounterCollector.TrackMaxCounter(maxCounters, key, LohSizeTick(tick));
        }

        maxCounters[key].Value.Should().Be(300);
    }

    [Fact]
    public void TrackMaxCounter_SingleTick_MaxEqualsThatTick()
    {
        var maxCounters = new ConcurrentDictionary<string, CounterValue>(StringComparer.Ordinal);
        const string key = "System.Runtime/loh-size";

        EventPipeCounterCollector.TrackMaxCounter(maxCounters, key, LohSizeTick(42));

        maxCounters[key].Value.Should().Be(42);
    }

    [Fact]
    public void TrackMaxCounter_MultipleKeys_TracksEachIndependently()
    {
        var maxCounters = new ConcurrentDictionary<string, CounterValue>(StringComparer.Ordinal);

        EventPipeCounterCollector.TrackMaxCounter(maxCounters, "System.Runtime/loh-size", LohSizeTick(10));
        EventPipeCounterCollector.TrackMaxCounter(maxCounters, "System.Runtime/gen-2-gc-count", LohSizeTick(1));
        EventPipeCounterCollector.TrackMaxCounter(maxCounters, "System.Runtime/loh-size", LohSizeTick(500));
        EventPipeCounterCollector.TrackMaxCounter(maxCounters, "System.Runtime/gen-2-gc-count", LohSizeTick(0));

        maxCounters["System.Runtime/loh-size"].Value.Should().Be(500);
        maxCounters["System.Runtime/gen-2-gc-count"].Value.Should().Be(1);
    }

    private static CounterValue LohSizeTick(double value) => new(
        Provider: "System.Runtime",
        Name: "loh-size",
        DisplayName: "LOH Size",
        Value: value,
        Kind: CounterKind.Mean,
        Unit: "B");
}
