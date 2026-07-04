using DotnetDiagnostics.Core.Gc;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Runs the registered GC <see cref="ISignalProvider{TContext}"/>s and returns a ranked, capped set
/// of <see cref="SignalGroup"/>s — the salient "vector" the engine forwards instead of the full event
/// list. Diagnosis-agnostic: surfaces pause-time pressure, gen2 concentration and LOH growth, never
/// why they are happening (that is #514-F's cross-signal correlation, and ultimately the consumer's
/// call).
/// </summary>
public static class GcSignals
{
    private static readonly IReadOnlyList<ISignalProvider<GcSignalContext>> Providers =
        new ISignalProvider<GcSignalContext>[]
        {
            new GcPauseTimeShareProvider(),
            new GcGen2ShareProvider(),
            new GcLohGrowthProvider(),
        };

    /// <summary>Derives signals from a GC summary (the full, un-truncated snapshot — not the inline-depth-trimmed view).</summary>
    public static IReadOnlyList<SignalGroup> Detect(GcSummary summary, string handleId)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return Detect(new GcSignalContext(
            summary.Duration,
            summary.TotalCollections,
            summary.TotalPauseTime,
            summary.Generations,
            summary.HeapStats ?? Array.Empty<GcHeapStatsSample>(),
            handleId));
    }

    /// <summary>Runs every registered provider over the context and ranks the union by salience.</summary>
    public static IReadOnlyList<SignalGroup> Detect(GcSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SignalRanker.Rank(Providers.SelectMany(p => p.Detect(context)));
    }
}
