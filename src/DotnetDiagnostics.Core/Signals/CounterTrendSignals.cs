using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Runs the registered counter-trend <see cref="ISignalProvider{TContext}"/>s and returns a ranked,
/// capped set of <see cref="SignalGroup"/>s — the salient "vector" the engine forwards instead of the
/// full counter table. Diagnosis-agnostic: surfaces which counters moved the most within the window,
/// never why.
/// </summary>
public static class CounterTrendSignals
{
    private static readonly IReadOnlyList<ISignalProvider<CounterTrendContext>> Providers =
        new ISignalProvider<CounterTrendContext>[]
        {
            new CounterTrendProvider(),
        };

    /// <summary>
    /// Derives signals from a counter snapshot (the full, un-filtered snapshot — not the
    /// headline-filtered inline view). Falls back to comparing <see cref="CounterSnapshot.Counters"/>
    /// against itself when <see cref="CounterSnapshot.FirstCounters"/> is unavailable, which yields no
    /// signals (zero delta everywhere) rather than throwing.
    /// </summary>
    public static IReadOnlyList<SignalGroup> Detect(CounterSnapshot snapshot, string handleId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Detect(new CounterTrendContext(snapshot.FirstCounters ?? snapshot.Counters, snapshot.Counters, handleId));
    }

    /// <summary>Runs every registered provider over the context and ranks the union by salience.</summary>
    public static IReadOnlyList<SignalGroup> Detect(CounterTrendContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SignalRanker.Rank(Providers.SelectMany(p => p.Detect(context)));
    }
}
