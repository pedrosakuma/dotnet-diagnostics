namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// Typed payload returned by the thread views of <c>query_snapshot</c>. Carries the slice requested by the LLM
/// (threads list, one thread's stack, lock graph, deadlock analysis, top-blocked ranking,
/// unique stack groups, or async-stall classification) plus provenance fields (origin, pid,
/// captured-at, suspend duration) so the model can reason about freshness without a second
/// roundtrip.
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
    /// <summary>Populated for <c>deadlocks</c>.</summary>
    public IReadOnlyList<ThreadDeadlockCycle>? Deadlocks { get; init; }
    /// <summary>Populated for <c>unique-stacks</c>.</summary>
    public IReadOnlyList<UniqueThreadStackGroup>? UniqueStacks { get; init; }
    /// <summary>Populated for <c>threadpool</c>.</summary>
    public ThreadPoolSnapshot? ThreadPool { get; init; }
    /// <summary>Populated for <c>async-stalls</c>.</summary>
    public AsyncStallsView? AsyncStalls { get; init; }
    /// <summary>Populated for <c>wait-chains</c>.</summary>
    public WaitChainsView? WaitChains { get; init; }
    /// <summary>Echoes the thread id used by the <c>stack</c> view.</summary>
    public int? ThreadId { get; init; }
    /// <summary>Populated for <c>resolve-address</c> (issue #275).</summary>
    public IReadOnlyList<ResolvedAddressEntry>? ResolvedAddresses { get; init; }
    /// <summary>Populated for <c>frame-vars</c> (issue #449).</summary>
    public FrameVariablesResult? FrameVariables { get; init; }
}

/// <summary>
/// One classified address returned by <c>query_snapshot(view="resolve-address")</c>. All numeric
/// fields are rendered as hex strings so the wire format never surfaces a bare integer the LLM has
/// to re-interpret. <see cref="Display"/> is always safe to show verbatim (issue #275).
/// </summary>
public sealed record ResolvedAddressEntry(
    string Address,
    string Kind,
    string? Module,
    string? ModulePath,
    string? Rva,
    string? BuildId,
    bool? Readable,
    string Display)
{
    /// <summary>Managed method identity when the address resolves to JIT/R2R code; null otherwise.</summary>
    public DotnetDiagnostics.Core.Memory.MethodIdentity? ManagedMethod { get; init; }

    /// <summary>
    /// Runtime image base of the containing module as hex (issue #375), i.e. <see cref="Address"/>
    /// minus <see cref="Rva"/>. Lets a consumer rebase the absolute address for position-independent
    /// (PIE / NativeAOT) images. Null outside any module. Prefer handing off <see cref="Rva"/>.
    /// </summary>
    public string? LoadBase { get; init; }
}

public sealed record ThreadDeadlockCycle(
    IReadOnlyList<ThreadDeadlockMember> CycleMembers,
    IReadOnlyList<ThreadDeadlockLink> LockChain,
    IReadOnlyList<ThreadDeadlockCommand> RecommendedCommands);

public sealed record ThreadDeadlockMember(
    int ThreadId,
    uint OSThreadId,
    string State,
    string? TopFrameMethod,
    string? InferredWaitReason);

public sealed record ThreadDeadlockLink(
    int WaitingThreadId,
    int OwnerThreadId,
    ulong LockObjectAddress,
    string? LockObjectTypeFullName,
    string LockKind);

public sealed record ThreadDeadlockCommand(string Command, string Purpose);

/// <summary>Small thread-id sample surfaced for a unique stack group.</summary>
public sealed record ThreadSampleId(int ManagedThreadId, uint OSThreadId);

/// <summary>
/// Aggregate returned by <c>query_snapshot(view="unique-stacks")</c>. The canonical stack
/// is returned root → leaf for readability, while the signature hash is computed from the top
/// frames selected by the caller.
/// </summary>
public sealed record UniqueThreadStackGroup(
    string SignatureHash,
    int ThreadCount,
    double ThreadPercentage,
    IReadOnlyList<ThreadSampleId> SampleThreads,
    IReadOnlyList<ManagedStackFrame> CanonicalFrames)
{
    /// <summary>Coarse wait reason inferred from the representative thread when available.</summary>
    public string? InferredWaitReason { get; init; }
}

/// <summary>Aggregate returned by <c>query_snapshot(view="async-stalls")</c>.</summary>
public sealed record AsyncStallsView(
    string View,
    int ClassifiedThreads,
    IReadOnlyList<AsyncStallBucketSummary> ByBucket,
    IReadOnlyList<AsyncStalledThread> TopBlockedAsync);

/// <summary>One async-stall bucket with a small sample of matching managed thread ids.</summary>
public sealed record AsyncStallBucketSummary(
    string Bucket,
    int Count,
    IReadOnlyList<int> SampleThreadIds);

/// <summary>Representative classified thread surfaced by <c>query_snapshot(view="async-stalls")</c>.</summary>
public sealed record AsyncStalledThread(
    int ThreadId,
    string Bucket,
    double? DurationMs,
    IReadOnlyList<string> TopFrames);

/// <summary>
/// Aggregate returned by <c>query_snapshot(view="wait-chains")</c>. Unifies three wait-edge kinds
/// (sync monitor locks, async continuations, ThreadPool starvation) into ranked directed wait-chains.
/// <see cref="Chains"/> is ordered longest / most-blocked first; cycles (true deadlocks) are flagged
/// distinctly from open chains via <see cref="WaitChain.IsCycle"/>.
/// </summary>
public sealed record WaitChainsView(
    string View,
    int ThreadCount,
    int EdgeCount,
    int CycleCount,
    int OpenChainCount,
    bool ThreadPoolStarved,
    IReadOnlyList<WaitChain> Chains)
{
    /// <summary>Analyzer-wide honest caveats (e.g. async-ownership indeterminacy from a snapshot).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One ranked wait-chain: a directed walk <c>root → wait-reason → next node → …</c> terminating in a
/// cycle, a ThreadPool-starvation sink, an async construct, or a running lock owner.
/// </summary>
public sealed record WaitChain(
    int Rank,
    bool IsCycle,
    int Length,
    int BlockedThreadCount,
    int RootThreadId,
    string TerminalKind,
    IReadOnlyList<WaitChainLink> Links)
{
    /// <summary>Per-chain caveats (e.g. indeterminate async-resumption ownership) — never guesses.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One hop of a <see cref="WaitChain"/>: thread <see cref="WaitingThreadId"/> is blocked by
/// <see cref="WaitReason"/> (edge kind <see cref="EdgeKind"/>) on the node described by
/// <see cref="TargetKind"/> / <see cref="TargetLabel"/>.
/// </summary>
public sealed record WaitChainLink(
    int WaitingThreadId,
    uint WaitingOSThreadId,
    string EdgeKind,
    string WaitReason,
    string TargetKind,
    string? TargetLabel)
{
    /// <summary>Owner thread id for a <c>monitor-lock</c> edge; <c>null</c> for async / ThreadPool sinks.</summary>
    public int? OwnerThreadId { get; init; }
    /// <summary>Contended lock object address for a <c>monitor-lock</c> edge as hex; <c>null</c> otherwise.</summary>
    public string? LockObjectAddress { get; init; }
    /// <summary>Contended lock object type for a <c>monitor-lock</c> edge; <c>null</c> otherwise.</summary>
    public string? LockObjectTypeFullName { get; init; }
    /// <summary>Honest caveat attached to this hop (e.g. async-ownership not recoverable from a snapshot).</summary>
    public string? Note { get; init; }
}
