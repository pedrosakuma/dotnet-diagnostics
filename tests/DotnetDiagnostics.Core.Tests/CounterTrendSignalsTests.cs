using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Signals;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests the diagnosis-agnostic counter-trend signal grouping (#527): which counter moved the most
/// within the collection window (<c>counters.trend</c>). This describes movement only — never what a
/// moving counter implies (no "ThreadPool starvation" / "GC pressure" naming).
/// </summary>
public sealed class CounterTrendSignalsTests
{
    private const string HandleId = "handle-1";

    private static CounterValue Counter(string name, double value, string? unit = null, string? provider = "System.Runtime") =>
        new(provider!, name, name, value, CounterKind.Mean, unit);

    [Fact]
    public void Detect_ReturnsEmpty_WhenNoCounterMovesEnough()
    {
        var first = new[] { Counter("cpu-usage", 10), Counter("threadpool-queue-length", 2) };
        var last = new[] { Counter("cpu-usage", 11), Counter("threadpool-queue-length", 2) };

        var signals = CounterTrendSignals.Detect(new CounterTrendContext(first, last, HandleId));

        signals.Should().BeEmpty();
    }

    [Fact]
    public void Detect_SurfacesTopMover_WhenOneCounterClimbsSharply()
    {
        var first = new[]
        {
            Counter("cpu-usage", 20, "%"),
            Counter("threadpool-queue-length", 0, "count"),
            Counter("working-set", 500, "MB"),
        };
        var last = new[]
        {
            Counter("cpu-usage", 22, "%"),
            Counter("threadpool-queue-length", 40, "count"),
            Counter("working-set", 510, "MB"),
        };

        var signals = CounterTrendSignals.Detect(new CounterTrendContext(first, last, HandleId));

        signals.Should().ContainSingle();
        var signal = signals[0];
        signal.Signal.Should().Be("counters.trend");
        signal.Buckets.Should().NotBeEmpty();
        signal.Buckets[0].Key.Should().Be("threadpool-queue-length");
        signal.Buckets[0].Handle.Should().Be(HandleId);
        signal.Salience.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void Detect_Ignores_NearZeroBothCounters()
    {
        var first = new[] { Counter("gen-2-gc-count", 0) };
        var last = new[] { Counter("gen-2-gc-count", 0) };

        var signals = CounterTrendSignals.Detect(new CounterTrendContext(first, last, HandleId));

        signals.Should().BeEmpty();
    }

    [Fact]
    public void Detect_Ignores_ModerateChangeBelowThreshold()
    {
        var first = new[] { Counter("working-set", 1000) };
        var last = new[] { Counter("working-set", 1200) };

        // relative change = 200 / 1200 = 0.166, below MinRelativeChange (0.4)
        var signals = CounterTrendSignals.Detect(new CounterTrendContext(first, last, HandleId));

        signals.Should().BeEmpty();
    }

    [Fact]
    public void Detect_ReportsDecrease_WhenCounterDropsSharply()
    {
        var first = new[] { Counter("active-timer-count", 50) };
        var last = new[] { Counter("active-timer-count", 2) };

        var signals = CounterTrendSignals.Detect(new CounterTrendContext(first, last, HandleId));

        signals.Should().ContainSingle();
        signals[0].Summary.Should().Contain("decreased");
        signals[0].Buckets[0].Magnitude.Should().BeLessThan(0);
    }

    [Fact]
    public void FromSnapshot_UsesLastAsFirst_WhenFirstCountersMissing()
    {
        var counters = new[] { Counter("cpu-usage", 42) };
        var snapshot = new CounterSnapshot(1234, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), counters, [], []);

        var signals = CounterTrendSignals.Detect(snapshot, HandleId);

        signals.Should().BeEmpty();
    }

    [Fact]
    public void FromSnapshot_SurfacesTrend_WhenFirstCountersDiffer()
    {
        var lastCounters = new[] { Counter("threadpool-queue-length", 80, "count") };
        var firstCounters = new[] { Counter("threadpool-queue-length", 0, "count") };
        var snapshot = new CounterSnapshot(1234, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), lastCounters, [], [])
        {
            FirstCounters = firstCounters,
        };

        var signals = CounterTrendSignals.Detect(snapshot, HandleId);

        signals.Should().ContainSingle();
        signals[0].Buckets[0].Key.Should().Be("threadpool-queue-length");
    }
}
