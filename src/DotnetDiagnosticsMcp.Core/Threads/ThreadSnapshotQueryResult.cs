namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Typed payload returned by <c>query_thread_snapshot</c>. Carries the slice requested by the LLM
/// (threads list, one thread's stack, lock graph, or top-blocked ranking) plus provenance fields
/// (origin, pid, captured-at, suspend duration) so the model can reason about freshness without a
/// second roundtrip.
/// </summary>
public sealed record ThreadSnapshotQueryResult(
    string Handle,
    string View,
    string Origin,
    int ProcessId,
    DateTimeOffset CapturedAt,
    TimeSpan WalkDuration)
{
    /// <summary>Populated for <c>threads-summary</c> and <c>top-blocked</c>.</summary>
    public IReadOnlyList<ManagedThread>? Threads { get; init; }
    /// <summary>Populated for <c>stack</c>.</summary>
    public ManagedThread? Thread { get; init; }
    /// <summary>Populated for <c>lock-graph</c>.</summary>
    public IReadOnlyList<MonitorLockState>? Locks { get; init; }
    /// <summary>Echoes the thread id used by the <c>stack</c> view.</summary>
    public int? ThreadId { get; init; }
}
