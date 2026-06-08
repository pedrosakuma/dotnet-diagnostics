namespace DotnetDiagnosticsMcp.Core.Gc;

/// <summary>
/// One <c>SizeAdaptationSample</c> DATAS event: a per-(gen0/gen1)-GC measurement the runtime
/// feeds into its heap-count tuning loop. All times are microseconds, sizes are bytes.
/// </summary>
public sealed record DatasSampleEvent(
    DateTimeOffset Timestamp,
    ulong GcIndex,
    uint ElapsedBetweenGcsUs,
    uint GcPauseTimeUs,
    uint SohMslWaitUs,
    uint UohMslWaitUs,
    ulong TotalSohStableSize,
    uint Gen0BudgetPerHeap)
{
    /// <summary>
    /// Client-side throughput-cost-percentage approximation (pause / (pause + elapsed)), matching
    /// pvanalyze's per-sample TCP. Distinct from the runtime's MSL-adjusted median in tuning events.
    /// </summary>
    public double ThroughputCostPercent =>
        (ElapsedBetweenGcsUs + GcPauseTimeUs) == 0
            ? 0
            : GcPauseTimeUs * 100.0 / (ElapsedBetweenGcsUs + GcPauseTimeUs);
}

/// <summary>
/// One <c>SizeAdaptationTuning</c> DATAS event: an ephemeral-GC heap-count decision. The runtime
/// emits this every few gen0/gen1 GCs when it evaluates whether to change the heap count.
/// </summary>
public sealed record DatasTuningEvent(
    DateTimeOffset Timestamp,
    int NewHeapCount,
    int MaxHeapCount,
    int MinHeapCount,
    ulong GcIndex,
    ulong TotalSohStableSize,
    float MedianThroughputCostPercent,
    float TcpToConsider,
    float CurrentAroundTargetAccumulation,
    int RecordedTcpCount,
    float RecordedTcpSlope,
    uint NumGcsSinceLastChange,
    int AggFactor,
    int ChangeDecision,
    int AdjustmentReason,
    int HeapCountChangeFreqFactor,
    int HeapCountFreqReason,
    int AdjustMetric);

/// <summary>
/// One <c>SizeAdaptationFullGCTuning</c> DATAS event: the gen2 "backstop" heap-count decision,
/// carrying the three most recent gen2 samples (age in GC-index delta + gen2 GC time percent).
/// </summary>
public sealed record DatasFullGcTuningEvent(
    DateTimeOffset Timestamp,
    int NewHeapCount,
    ulong GcIndex,
    float MedianGen2Tcp,
    uint NumGen2sSinceLastChange,
    uint Gen2Sample0Age,
    float Gen2Sample0Percent,
    uint Gen2Sample1Age,
    float Gen2Sample1Percent,
    uint Gen2Sample2Age,
    float Gen2Sample2Percent);

/// <summary>
/// Diagnostics about payloads the DATAS collector saw but could not decode. A non-zero
/// <see cref="UnsupportedVersion"/> count usually means the target runtime emits a newer DATAS
/// event version than this parser understands.
/// </summary>
public sealed record DatasParseStats(
    int MalformedPayloads,
    int UnsupportedVersion,
    int ExtraBytes);

/// <summary>
/// Aggregated DATAS (Dynamic Adaptation To Application Sizes) tuning activity over a collection
/// window. Populated only on Server GC with DATAS enabled (default-on in .NET 9+); Workstation GC
/// emits no DATAS events.
/// </summary>
public sealed record GcDatasSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<DatasSampleEvent> Samples,
    IReadOnlyList<DatasTuningEvent> TuningEvents,
    IReadOnlyList<DatasFullGcTuningEvent> FullGcTuningEvents,
    DatasParseStats ParseStats)
{
    /// <summary>True when at least one DATAS event of any kind was decoded.</summary>
    public bool HasData => Samples.Count > 0 || TuningEvents.Count > 0 || FullGcTuningEvents.Count > 0;
}

/// <summary>High-level DATAS rollup: heap-count range, change count, TCP statistics and budgets.</summary>
public sealed record DatasOverviewView(
    int ProcessId,
    int SampleCount,
    int TuningEventCount,
    int FullGcTuningCount,
    int? MinHeapCount,
    int? MaxHeapCount,
    int HeapCountChanges,
    double? MeanMedianThroughputCostPercent,
    double? MaxMedianThroughputCostPercent,
    double? MeanGen0BudgetMB,
    double? MeanSohStableSizeMB,
    DatasParseStats ParseStats);

/// <summary>One row of the heap-count tuning timeline.</summary>
public sealed record DatasTuningRow(
    DateTimeOffset Timestamp,
    ulong GcIndex,
    int NewHeapCount,
    int? PreviousHeapCount,
    bool Changed,
    float MedianThroughputCostPercent,
    uint NumGcsSinceLastChange);

public sealed record DatasTuningView(
    int ProcessId,
    bool ChangesOnly,
    int TotalTuningEvents,
    int Returned,
    IReadOnlyList<DatasTuningRow> Rows);

public sealed record DatasSamplesView(
    int ProcessId,
    int TotalSamples,
    int Returned,
    IReadOnlyList<DatasSampleEvent> Samples);

public sealed record DatasGen2View(
    int ProcessId,
    int TotalFullGcTuningEvents,
    int Returned,
    IReadOnlyList<DatasFullGcTuningEvent> Events);
