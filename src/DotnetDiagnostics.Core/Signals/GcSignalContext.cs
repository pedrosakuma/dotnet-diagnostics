using DotnetDiagnostics.Core.Gc;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Already-collected GC data a <see cref="ISignalProvider{TContext}"/> groups over. Three neutral
/// trend/concentration dimensions are carried: overall pause-time share of the window, gen2
/// collection share (of total collections), and LOH size growth across the window (from the ordered
/// <see cref="HeapStats"/> time series).
/// </summary>
/// <param name="Duration">Length of the collection window.</param>
/// <param name="TotalCollections">Total GC collections observed.</param>
/// <param name="TotalPauseTime">Sum of pause durations across all collections.</param>
/// <param name="Generations">Per-generation collection counts.</param>
/// <param name="HeapStats">GCHeapStats samples in ascending timestamp order (one per collection), or empty if none were captured.</param>
/// <param name="HandleId">Drill-down handle the GC summary was registered under, referenced by every bucket.</param>
public sealed record GcSignalContext(
    TimeSpan Duration,
    int TotalCollections,
    TimeSpan TotalPauseTime,
    IReadOnlyList<GenerationStats> Generations,
    IReadOnlyList<GcHeapStatsSample> HeapStats,
    string HandleId);
