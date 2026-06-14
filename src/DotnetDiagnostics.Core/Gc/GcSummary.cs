namespace DotnetDiagnostics.Core.Gc;

/// <summary>Statistics for a single GC generation across the sample window.</summary>
public sealed record GenerationStats(int Generation, int Count);

/// <summary>A single observed GC event with its computed pause duration.</summary>
public sealed record GcEvent(
    DateTimeOffset Timestamp,
    int Generation,
    string Reason,
    string Type,
    TimeSpan PauseDuration);

/// <summary>
/// A per-collection <c>GCHeapStats</c> sample: per-generation heap sizes, promoted bytes,
/// pinned-object and GC-handle counts, and finalization survivors. Emitted once per GC on
/// CoreCLR / R2R / NativeAOT. <see cref="PohSizeBytes"/> / <see cref="PohPromotedBytes"/> are
/// populated only by the V2 event (pinned object heap) and are 0 on runtimes that emit V1.
/// </summary>
public sealed record GcHeapStatsSample(
    DateTimeOffset Timestamp,
    long Gen0SizeBytes,
    long Gen1SizeBytes,
    long Gen2SizeBytes,
    long LohSizeBytes,
    long PohSizeBytes,
    long TotalHeapSizeBytes,
    long TotalPromotedBytes,
    long Gen2PromotedBytes,
    long PohPromotedBytes,
    long FinalizationPromotedBytes,
    long FinalizationPromotedCount,
    long PinnedObjectCount,
    long GcHandleCount);

/// <summary>Aggregated GC activity over a window.</summary>
public sealed record GcSummary(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int TotalCollections,
    TimeSpan TotalPauseTime,
    TimeSpan MaxPauseTime,
    IReadOnlyList<GenerationStats> Generations,
    IReadOnlyList<GcEvent> Events,
    IReadOnlyList<GcHeapStatsSample>? HeapStats = null);
