using DotnetDiagnostics.Core.Threads;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Already-collected thread-snapshot data a <see cref="ISignalProvider{TContext}"/> groups over.
/// <see cref="Threads"/> feeds the coarse wait-state roll-up (how many threads are parked in what
/// kind of wait), <see cref="Locks"/> feeds the finer wait-target roll-up (do many threads converge
/// on the same SyncBlock/monitor).
/// </summary>
/// <param name="TotalThreads">Total managed threads observed in the snapshot.</param>
/// <param name="Threads">All managed threads captured by the walk.</param>
/// <param name="Locks">All SyncBlock-based locks captured by the walk.</param>
/// <param name="HandleId">Drill-down handle the snapshot was registered under, referenced by every bucket.</param>
public sealed record ThreadWaitSignalContext(
    int TotalThreads,
    IReadOnlyList<ManagedThread> Threads,
    IReadOnlyList<MonitorLockState> Locks,
    string HandleId);
