using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Signals;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests the diagnosis-agnostic GC trend signal groupings (#525): pause-time share of the window
/// (<c>gc.pause-time-share</c>), gen2 collection share (<c>gc.gen2-share</c>) and LOH growth across
/// the window (<c>gc.loh-growth</c>). These describe magnitude/trend, never a cause.
/// </summary>
public sealed class GcSignalsTests
{
    private static GcHeapStatsSample HeapStats(DateTimeOffset ts, long lohBytes) => new(
        Timestamp: ts,
        Gen0SizeBytes: 0,
        Gen1SizeBytes: 0,
        Gen2SizeBytes: 0,
        LohSizeBytes: lohBytes,
        PohSizeBytes: 0,
        TotalHeapSizeBytes: lohBytes,
        TotalPromotedBytes: 0,
        Gen2PromotedBytes: 0,
        PohPromotedBytes: 0,
        FinalizationPromotedBytes: 0,
        FinalizationPromotedCount: 0,
        PinnedObjectCount: 0,
        GcHandleCount: 0);

    private static GcSummary Summary(
        TimeSpan duration,
        int totalCollections,
        TimeSpan totalPauseTime,
        GenerationStats[] generations,
        GcHeapStatsSample[]? heapStats = null) => new(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: duration,
            TotalCollections: totalCollections,
            TotalPauseTime: totalPauseTime,
            MaxPauseTime: totalPauseTime,
            Generations: generations,
            Events: Array.Empty<GcEvent>(),
            HeapStats: heapStats);

    // ---- pause-time share -------------------------------------------------------------------

    [Fact]
    public void PauseTimeShare_Emits_WhenAboveFloor()
    {
        var summary = Summary(
            duration: TimeSpan.FromSeconds(10),
            totalCollections: 5,
            totalPauseTime: TimeSpan.FromSeconds(1.2),
            generations: new[] { new GenerationStats(0, 5) });

        var signals = GcSignals.Detect(summary, "handle-gc");

        var pauseShare = signals.Should().ContainSingle(s => s.Signal == "gc.pause-time-share").Subject;
        pauseShare.Salience.Should().BeApproximately(0.12, 0.001);
        pauseShare.Buckets[0].Handle.Should().Be("handle-gc");
        pauseShare.NextAction!.SuggestedArguments!["view"].Should().Be("pauseHistogram");
    }

    [Fact]
    public void PauseTimeShare_EmitsNothing_WhenBelowFloor()
    {
        var summary = Summary(
            duration: TimeSpan.FromSeconds(10),
            totalCollections: 5,
            totalPauseTime: TimeSpan.FromMilliseconds(50),
            generations: new[] { new GenerationStats(0, 5) });

        GcSignals.Detect(summary, "h").Should().NotContain(s => s.Signal == "gc.pause-time-share");
    }

    // ---- gen2 share ---------------------------------------------------------------------------

    [Fact]
    public void Gen2Share_Emits_WhenElevated()
    {
        var summary = Summary(
            duration: TimeSpan.FromSeconds(10),
            totalCollections: 10,
            totalPauseTime: TimeSpan.FromMilliseconds(10),
            generations: new[] { new GenerationStats(0, 3), new GenerationStats(1, 2), new GenerationStats(2, 5) });

        var signals = GcSignals.Detect(summary, "handle-gc");

        var gen2 = signals.Should().ContainSingle(s => s.Signal == "gc.gen2-share").Subject;
        gen2.Salience.Should().BeApproximately(0.5, 0.001);
        gen2.Buckets[0].Magnitude.Should().Be(5);
        gen2.NextAction!.SuggestedArguments!["view"].Should().Be("byGeneration");
    }

    [Fact]
    public void Gen2Share_EmitsNothing_WhenGen0Dominates()
    {
        var summary = Summary(
            duration: TimeSpan.FromSeconds(10),
            totalCollections: 10,
            totalPauseTime: TimeSpan.FromMilliseconds(10),
            generations: new[] { new GenerationStats(0, 9), new GenerationStats(2, 1) });

        GcSignals.Detect(summary, "h").Should().NotContain(s => s.Signal == "gc.gen2-share");
    }

    [Fact]
    public void Gen2Share_EmitsNothing_WhenTooFewCollections()
    {
        var summary = Summary(
            duration: TimeSpan.FromSeconds(10),
            totalCollections: 2,
            totalPauseTime: TimeSpan.FromMilliseconds(10),
            generations: new[] { new GenerationStats(2, 2) });

        GcSignals.Detect(summary, "h").Should().NotContain(s => s.Signal == "gc.gen2-share");
    }

    // ---- LOH growth ----------------------------------------------------------------------------

    [Fact]
    public void LohGrowth_Emits_WhenGrowingAcrossWindow()
    {
        var start = DateTimeOffset.UtcNow;
        var summary = Summary(
            duration: TimeSpan.FromSeconds(10),
            totalCollections: 3,
            totalPauseTime: TimeSpan.FromMilliseconds(10),
            generations: new[] { new GenerationStats(0, 3) },
            heapStats: new[]
            {
                HeapStats(start, 2 * 1024 * 1024),
                HeapStats(start.AddSeconds(5), 3 * 1024 * 1024),
                HeapStats(start.AddSeconds(10), 5 * 1024 * 1024),
            });

        var signals = GcSignals.Detect(summary, "handle-gc");

        var loh = signals.Should().ContainSingle(s => s.Signal == "gc.loh-growth").Subject;
        loh.Salience.Should().BeApproximately(1.0, 0.001); // relative growth (150%) is clamped
        loh.Buckets[0].Magnitude.Should().Be(3 * 1024 * 1024);
        loh.NextAction!.SuggestedArguments!["view"].Should().Be("timeline");
    }

    [Fact]
    public void LohGrowth_EmitsNothing_WhenFlat()
    {
        var start = DateTimeOffset.UtcNow;
        var summary = Summary(
            duration: TimeSpan.FromSeconds(10),
            totalCollections: 3,
            totalPauseTime: TimeSpan.FromMilliseconds(10),
            generations: new[] { new GenerationStats(0, 3) },
            heapStats: new[]
            {
                HeapStats(start, 2 * 1024 * 1024),
                HeapStats(start.AddSeconds(10), 2 * 1024 * 1024),
            });

        GcSignals.Detect(summary, "h").Should().NotContain(s => s.Signal == "gc.loh-growth");
    }

    [Fact]
    public void LohGrowth_EmitsNothing_WhenNoHeapStats()
    {
        var summary = Summary(
            duration: TimeSpan.FromSeconds(10),
            totalCollections: 3,
            totalPauseTime: TimeSpan.FromMilliseconds(10),
            generations: new[] { new GenerationStats(0, 3) },
            heapStats: null);

        GcSignals.Detect(summary, "h").Should().NotContain(s => s.Signal == "gc.loh-growth");
    }
}
