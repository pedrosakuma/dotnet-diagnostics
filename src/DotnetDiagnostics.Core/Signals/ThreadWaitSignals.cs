using DotnetDiagnostics.Core.Threads;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Runs the registered thread-wait <see cref="ISignalProvider{TContext}"/>s and returns a ranked,
/// capped set of <see cref="SignalGroup"/>s — the salient "vector" the engine forwards instead of the
/// full thread + lock lists. Diagnosis-agnostic: surfaces where threads concentrate by wait state and
/// by wait target, never why (no lock-contention / sync-over-async naming).
/// </summary>
public static class ThreadWaitSignals
{
    private static readonly IReadOnlyList<ISignalProvider<ThreadWaitSignalContext>> Providers =
        new ISignalProvider<ThreadWaitSignalContext>[]
        {
            new ThreadByWaitStateProvider(),
            new ThreadByWaitTargetProvider(),
        };

    /// <summary>Derives signals from a thread-snapshot artifact (the full, un-truncated snapshot).</summary>
    public static IReadOnlyList<SignalGroup> Detect(ThreadSnapshotArtifact snapshot, string handleId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Detect(new ThreadWaitSignalContext(snapshot.Threads.Count, snapshot.Threads, snapshot.Locks, handleId));
    }

    /// <summary>Runs every registered provider over the context and ranks the union by salience.</summary>
    public static IReadOnlyList<SignalGroup> Detect(ThreadWaitSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SignalRanker.Rank(Providers.SelectMany(p => p.Detect(context)));
    }
}
