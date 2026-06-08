using DotnetDiagnosticsMcp.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class GcDatasQueryDispatcherTests
{
    private const string Handle = "datas-1";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DatasTuningEvent Tuning(int seconds, ulong gcIndex, int newHeapCount, float medianTcp = 1.0f)
        => new(T0.AddSeconds(seconds), newHeapCount, MaxHeapCount: 16, MinHeapCount: 1, gcIndex,
            TotalSohStableSize: 1000, medianTcp, TcpToConsider: 0, CurrentAroundTargetAccumulation: 0,
            RecordedTcpCount: 0, RecordedTcpSlope: 0, NumGcsSinceLastChange: 0, AggFactor: 0,
            ChangeDecision: 0, AdjustmentReason: 0, HeapCountChangeFreqFactor: 0, HeapCountFreqReason: 0, AdjustMetric: 0);

    private static DatasSampleEvent Sample(int seconds, ulong gcIndex, uint budget = 1024 * 1024, ulong soh = 2UL * 1024 * 1024)
        => new(T0.AddSeconds(seconds), gcIndex, ElapsedBetweenGcsUs: 1000, GcPauseTimeUs: 100,
            SohMslWaitUs: 0, UohMslWaitUs: 0, TotalSohStableSize: soh, Gen0BudgetPerHeap: budget);

    private static GcDatasSnapshot Snapshot(
        IReadOnlyList<DatasSampleEvent>? samples = null,
        IReadOnlyList<DatasTuningEvent>? tuning = null,
        IReadOnlyList<DatasFullGcTuningEvent>? gen2 = null,
        DatasParseStats? stats = null)
        => new(1234, T0, TimeSpan.FromSeconds(15),
            samples ?? Array.Empty<DatasSampleEvent>(),
            tuning ?? Array.Empty<DatasTuningEvent>(),
            gen2 ?? Array.Empty<DatasFullGcTuningEvent>(),
            stats ?? new DatasParseStats(0, 0, 0));

    [Fact]
    public void Render_DefaultsToOverview()
    {
        var snap = Snapshot(tuning: new[] { Tuning(0, 1, 4), Tuning(1, 2, 8) });

        var result = GcDatasQueryDispatcher.Render(snap, Handle, view: null, topN: 50);

        result.Error.Should().BeNull();
        result.Data.Should().BeOfType<DatasOverviewView>();
    }

    [Fact]
    public void Overview_ComputesHeapCountRangeAndChanges()
    {
        var snap = Snapshot(
            samples: new[] { Sample(0, 1) },
            tuning: new[] { Tuning(0, 1, 4), Tuning(1, 2, 4), Tuning(2, 3, 8), Tuning(3, 4, 6) });

        var result = GcDatasQueryDispatcher.RenderOverview(snap, Handle);

        var v = result.Data!;
        v.MinHeapCount.Should().Be(4);
        v.MaxHeapCount.Should().Be(8);
        v.HeapCountChanges.Should().Be(2); // 4->4 no, 4->8 yes, 8->6 yes
        v.MeanGen0BudgetMB.Should().BeApproximately(1.0, 1e-9);
        v.MeanSohStableSizeMB.Should().BeApproximately(2.0, 1e-9);
    }

    [Fact]
    public void Overview_NoTuning_NullsHeapMetrics()
    {
        var result = GcDatasQueryDispatcher.RenderOverview(Snapshot(), Handle);

        var v = result.Data!;
        v.MinHeapCount.Should().BeNull();
        v.MaxHeapCount.Should().BeNull();
        v.MeanMedianThroughputCostPercent.Should().BeNull();
        v.MeanGen0BudgetMB.Should().BeNull();
        v.HeapCountChanges.Should().Be(0);
    }

    [Fact]
    public void Tuning_OrdersByTimestampAndMarksChanges()
    {
        // Provide out-of-order to confirm ordering by (timestamp, gcIndex).
        var snap = Snapshot(tuning: new[] { Tuning(2, 3, 8), Tuning(0, 1, 4), Tuning(1, 2, 4) });

        var result = GcDatasQueryDispatcher.RenderTuning(snap, Handle, topN: 50, changesOnly: false);

        var rows = result.Data!.Rows;
        rows.Select(r => r.GcIndex).Should().Equal(1UL, 2UL, 3UL);
        rows[0].Changed.Should().BeFalse();
        rows[0].PreviousHeapCount.Should().BeNull();
        rows[1].Changed.Should().BeFalse(); // 4->4
        rows[2].Changed.Should().BeTrue();  // 4->8
    }

    [Fact]
    public void Tuning_ChangesOnly_KeepsBaselinePlusChanges()
    {
        var snap = Snapshot(tuning: new[] { Tuning(0, 1, 4), Tuning(1, 2, 4), Tuning(2, 3, 8), Tuning(3, 4, 8) });

        var result = GcDatasQueryDispatcher.RenderTuning(snap, Handle, topN: 50, changesOnly: true);

        var rows = result.Data!.Rows;
        // baseline (gc 1) + the single change (gc 3)
        rows.Select(r => r.GcIndex).Should().Equal(1UL, 3UL);
        result.Data.ChangesOnly.Should().BeTrue();
        result.Data.TotalTuningEvents.Should().Be(4);
    }

    [Fact]
    public void Tuning_TopN_AppliedAfterChangesFilter()
    {
        var snap = Snapshot(tuning: new[] { Tuning(0, 1, 4), Tuning(1, 2, 8), Tuning(2, 3, 6) });

        var result = GcDatasQueryDispatcher.RenderTuning(snap, Handle, topN: 1, changesOnly: true);

        result.Data!.Rows.Should().HaveCount(1);
        result.Data.Rows[0].GcIndex.Should().Be(1UL);
    }

    [Fact]
    public void Samples_OrdersAndCapsByTopN()
    {
        var snap = Snapshot(samples: new[] { Sample(2, 3), Sample(0, 1), Sample(1, 2) });

        var result = GcDatasQueryDispatcher.RenderSamples(snap, Handle, topN: 2);

        result.Data!.TotalSamples.Should().Be(3);
        result.Data.Samples.Select(s => s.GcIndex).Should().Equal(1UL, 2UL);
    }

    [Fact]
    public void Gen2_OrdersAndCapsByTopN()
    {
        var e1 = new DatasFullGcTuningEvent(T0.AddSeconds(1), 8, 2, 1f, 0, 0, 0, 0, 0, 0, 0);
        var e0 = new DatasFullGcTuningEvent(T0, 8, 1, 1f, 0, 0, 0, 0, 0, 0, 0);
        var snap = Snapshot(gen2: new[] { e1, e0 });

        var result = GcDatasQueryDispatcher.RenderGen2(snap, Handle, topN: 50);

        result.Data!.TotalFullGcTuningEvents.Should().Be(2);
        result.Data.Events.Select(e => e.GcIndex).Should().Equal(1UL, 2UL);
    }

    [Fact]
    public void Render_UnknownView_FailsWithValidViewList()
    {
        var result = GcDatasQueryDispatcher.Render(Snapshot(), Handle, view: "bogus", topN: 50);

        result.Data.Should().BeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("overview").And.Contain("tuning");
    }

    [Fact]
    public void Render_InvalidTopN_Fails()
    {
        var result = GcDatasQueryDispatcher.Render(Snapshot(), Handle, view: "samples", topN: 0);

        result.Data.Should().BeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void SessionViews_AreTheFourKnownViews()
    {
        GcDatasQueryDispatcher.SessionViews.Should().Equal("overview", "tuning", "samples", "gen2");
        GcDatasQueryDispatcher.IsKnownView("tuning").Should().BeTrue();
        GcDatasQueryDispatcher.IsKnownView("nope").Should().BeFalse();
    }
}
