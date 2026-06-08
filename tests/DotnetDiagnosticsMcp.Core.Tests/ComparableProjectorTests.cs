using DotnetDiagnosticsMcp.Core.Comparison;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class ComparableProjectorTests
{
    private static DatasTuningEvent Tuning(ulong gcIndex, int newHeapCount, float medianTcp) => new(
        Timestamp: new DateTimeOffset(2026, 1, 1, 0, 0, (int)gcIndex, TimeSpan.Zero),
        NewHeapCount: newHeapCount,
        MaxHeapCount: 16,
        MinHeapCount: 1,
        GcIndex: gcIndex,
        TotalSohStableSize: 10_000_000,
        MedianThroughputCostPercent: medianTcp,
        TcpToConsider: medianTcp,
        CurrentAroundTargetAccumulation: 0,
        RecordedTcpCount: 3,
        RecordedTcpSlope: 0,
        NumGcsSinceLastChange: 5,
        AggFactor: 1,
        ChangeDecision: 0,
        AdjustmentReason: 0,
        HeapCountChangeFreqFactor: 1,
        HeapCountFreqReason: 0,
        AdjustMetric: 0);

    private static DatasSampleEvent Sample(ulong gcIndex, uint gen0Budget, ulong soh) => new(
        Timestamp: new DateTimeOffset(2026, 1, 1, 0, 0, (int)gcIndex, TimeSpan.Zero),
        GcIndex: gcIndex,
        ElapsedBetweenGcsUs: 1000,
        GcPauseTimeUs: 50,
        SohMslWaitUs: 0,
        UohMslWaitUs: 0,
        TotalSohStableSize: soh,
        Gen0BudgetPerHeap: gen0Budget);

    [Fact]
    public void GcDatasProjector_EmitsHeadlineMetrics_AndNoRows()
    {
        var snapshot = new GcDatasSnapshot(
            ProcessId: 777,
            StartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Duration: TimeSpan.FromSeconds(10),
            Samples: new[] { Sample(1, 12 * 1024 * 1024, 20_000_000), Sample(2, 12 * 1024 * 1024, 20_000_000) },
            TuningEvents: new[] { Tuning(1, 4, 3.0f), Tuning(2, 8, 5.0f) },
            FullGcTuningEvents: Array.Empty<DatasFullGcTuningEvent>(),
            ParseStats: new DatasParseStats(0, 0, 0));

        var snap = new GcDatasComparableProjector().Project(snapshot, "baseline");

        snap.Kind.Should().Be("gc-datas");
        snap.Label.Should().Be("baseline");
        snap.ProcessId.Should().Be(777);
        snap.Rows.Should().BeEmpty();

        var byName = snap.Metrics.ToDictionary(m => m.Definition.Name);
        byName.Should().ContainKey("heapCountChanges");
        byName["heapCountChanges"].Value.Should().Be(1);
        byName["heapCountChanges"].Definition.BetterDirection.Should().Be(BetterDirection.Lower);
        byName.Should().ContainKey("meanMedianThroughputCostPercent");
        byName["meanMedianThroughputCostPercent"].Value.Should().Be(4.0);
        byName.Should().ContainKey("minHeapCount");
        byName.Should().ContainKey("maxHeapCount");
    }

    [Fact]
    public void GcDatasProjector_EmptySnapshot_SkipsNullMetrics()
    {
        var snapshot = new GcDatasSnapshot(
            ProcessId: 1,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(1),
            Samples: Array.Empty<DatasSampleEvent>(),
            TuningEvents: Array.Empty<DatasTuningEvent>(),
            FullGcTuningEvents: Array.Empty<DatasFullGcTuningEvent>(),
            ParseStats: new DatasParseStats(0, 0, 0));

        var snap = new GcDatasComparableProjector().Project(snapshot, "empty");

        var names = snap.Metrics.Select(m => m.Definition.Name).ToHashSet();
        names.Should().NotContain("meanMedianThroughputCostPercent");
        names.Should().NotContain("minHeapCount");
        // Pure counts are always present.
        names.Should().Contain("heapCountChanges");
        names.Should().Contain("sampleCount");
    }

    [Fact]
    public void CountersProjector_FlattensCountersAndMeters()
    {
        var snapshot = new CounterSnapshot(
            ProcessId: 9,
            StartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Duration: TimeSpan.FromSeconds(5),
            Counters: new[]
            {
                new CounterValue("System.Runtime", "cpu-usage", "CPU Usage", 42.5, CounterKind.Mean, "%"),
                new CounterValue("System.Runtime", "gen-0-gc-count", "Gen 0 GC Count", 7, CounterKind.Sum, "count"),
            },
            Meters: new[]
            {
                new MeterInstrumentValue(
                    "MyApp", "request-duration", "ms", "Histogram",
                    new Dictionary<string, string?> { ["route"] = "/api" },
                    LastValue: null, Rate: null,
                    Histogram: new HistogramSnapshot(100, 1234, 5, 9, 11)),
            },
            Notes: Array.Empty<string>());

        var snap = new CountersComparableProjector().Project(snapshot, "after");

        snap.Kind.Should().Be("counters");
        snap.Rows.Should().BeEmpty();

        var names = snap.Metrics.Select(m => m.Definition.Name).ToList();
        names.Should().Contain("counter:System.Runtime/cpu-usage");
        names.Should().Contain("counter:System.Runtime/gen-0-gc-count");
        names.Should().Contain("meter:MyApp/request-duration[route=/api]/p50");
        names.Should().Contain("meter:MyApp/request-duration[route=/api]/p99");

        var byName = snap.Metrics.ToDictionary(m => m.Definition.Name);
        byName["counter:System.Runtime/gen-0-gc-count"].Definition.Aggregation.Should().Be(MetricAggregation.Total);
        byName["counter:System.Runtime/cpu-usage"].Definition.Aggregation.Should().Be(MetricAggregation.Point);
    }

    [Fact]
    public void CountersProjector_RateMeter_EmitsBothPointAndRate()
    {
        var snapshot = new CounterSnapshot(
            ProcessId: 9,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            Counters: Array.Empty<CounterValue>(),
            Meters: new[]
            {
                new MeterInstrumentValue(
                    "MyApp", "requests", "count", "Counter",
                    new Dictionary<string, string?>(),
                    LastValue: 500, Rate: 100, Histogram: null),
            },
            Notes: Array.Empty<string>());

        var snap = new CountersComparableProjector().Project(snapshot, "after");
        var byName = snap.Metrics.ToDictionary(m => m.Definition.Name);

        byName.Should().ContainKey("meter:MyApp/requests");
        byName["meter:MyApp/requests"].Value.Should().Be(500);
        byName["meter:MyApp/requests"].Definition.Aggregation.Should().Be(MetricAggregation.Point);
        byName.Should().ContainKey("meter:MyApp/requests/rate");
        byName["meter:MyApp/requests/rate"].Value.Should().Be(100);
        byName["meter:MyApp/requests/rate"].Definition.Aggregation.Should().Be(MetricAggregation.Rate);
    }

    [Fact]
    public void CountersProjector_AmbiguousTagSets_StayDistinct()
    {
        MeterInstrumentValue Meter(IReadOnlyDictionary<string, string?> tags) => new(
            "M", "i", null, "Gauge", tags, LastValue: 1, Rate: null, Histogram: null);

        var snapshot = new CounterSnapshot(
            ProcessId: 1,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(1),
            Counters: Array.Empty<CounterValue>(),
            Meters: new[]
            {
                Meter(new Dictionary<string, string?> { ["a"] = "b,c=d" }),
                Meter(new Dictionary<string, string?> { ["a"] = "b", ["c"] = "d" }),
            },
            Notes: Array.Empty<string>());

        var snap = new CountersComparableProjector().Project(snapshot, "x");

        // Two genuinely different tag sets must not collapse into one overwriting metric.
        snap.Metrics.Should().HaveCount(2);
        snap.Metrics.Select(m => m.Definition.Name).Distinct().Should().HaveCount(2);
    }
}
