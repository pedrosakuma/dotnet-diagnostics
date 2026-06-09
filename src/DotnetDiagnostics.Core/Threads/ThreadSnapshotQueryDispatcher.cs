using System.Globalization;

namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// Host-neutral drill-down engine for <see cref="ThreadSnapshotArtifact"/> handles — the thread
/// analogue of <see cref="DotnetDiagnostics.Core.Dump.HeapSnapshotQueryDispatcher"/> and
/// <see cref="DotnetDiagnostics.Core.CpuSampling.CpuSampleQueryDispatcher"/>. Every thread view
/// (<c>threads-summary</c>, <c>stack</c>, <c>lock-graph</c>, <c>deadlocks</c>, <c>top-blocked</c>,
/// <c>unique-stacks</c>, <c>async-stalls</c>, <c>threadpool</c>) renders purely from the already
/// captured artifact — no live ClrMD attach, no authorization — so both the MCP server's
/// <c>query_thread_snapshot</c> tool and the standalone CLI <c>session</c> REPL (issue #300) share
/// one implementation.
/// </summary>
public static class ThreadSnapshotQueryDispatcher
{
    /// <summary>The view names this dispatcher renders from a snapshot alone (drill-down without re-capturing).</summary>
    public static IReadOnlyList<string> SessionViews { get; } = new[]
    {
        "threads-summary", "stack", "lock-graph", "deadlocks", "top-blocked", "unique-stacks", "async-stalls", "threadpool",
    };

    /// <summary>
    /// Renders <paramref name="view"/> from <paramref name="snapshot"/>. Mirrors the MCP server's
    /// original <c>QueryThreadSnapshot</c> switch byte-for-byte: <paramref name="view"/> is normalized
    /// (trim + lower-invariant) and an unrecognized name yields the same <c>InvalidArgument</c> listing
    /// the eight valid views.
    /// </summary>
    public static DiagnosticResult<ThreadSnapshotQueryResult> Dispatch(
        ThreadSnapshotArtifact snapshot, string handle, string view, int? threadId, int topN, int framesToHash, int minCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Defensive guard for non-server callers (the MCP server validates topN before delegating, so
        // this is unreachable on the server path and keeps its behavior byte-identical).
        if (topN < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(topN), "must be >= 1");

        var origin = snapshot.Origin.ToString().ToLowerInvariant();
        var normalized = view.Trim().ToLowerInvariant();
        return normalized switch
        {
            "threads-summary" => QueryThreadsSummary(snapshot, handle, origin, topN),
            "stack" => QueryThreadStack(snapshot, handle, origin, threadId),
            "lock-graph" => QueryLockGraph(snapshot, handle, origin, topN),
            "deadlocks" => QueryDeadlocks(snapshot, handle, origin, topN),
            "top-blocked" => QueryTopBlocked(snapshot, handle, origin, topN),
            "unique-stacks" => QueryUniqueStacks(snapshot, handle, origin, topN, framesToHash, minCount),
            "async-stalls" => QueryAsyncStalls(snapshot, handle, origin, topN),
            "threadpool" => QueryThreadPool(snapshot, handle, origin),
            _ => InvalidArg<ThreadSnapshotQueryResult>(nameof(view), $"must be 'threads-summary', 'stack', 'lock-graph', 'deadlocks', 'top-blocked', 'unique-stacks', 'async-stalls' or 'threadpool' (got '{view}')"),
        };
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadsSummary(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var ordered = snapshot.Threads.Take(topN).ToArray();
        var summary = $"Returning {ordered.Length}/{snapshot.Threads.Count} thread(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "threads-summary", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Threads = ordered },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadStack(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int? threadId)
    {
        if (threadId is null)
        {
            return InvalidArg<ThreadSnapshotQueryResult>(nameof(threadId), "is required for view='stack'");
        }
        var isLinuxNativeStack = string.Equals(snapshot.Source, "linux-native-stack", StringComparison.Ordinal);
        var thread = isLinuxNativeStack
            ? snapshot.Threads.FirstOrDefault(t =>
                threadId.Value > 0 &&
                (uint)threadId.Value == t.OSThreadId)
            : snapshot.Threads.FirstOrDefault(t => t.ManagedThreadId == threadId.Value);
        if (thread is null)
        {
            var threadKind = isLinuxNativeStack ? "OS thread" : "managed thread";
            return DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"{threadKind} {threadId.Value} not present in snapshot '{handle}'.",
                new DiagnosticError("ThreadNotFound", "The captured snapshot does not contain this thread id.", threadId.Value.ToString(CultureInfo.InvariantCulture)),
                new NextActionHint("query_snapshot",
                    "List the captured threads first.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = "threads-summary" }));
        }
        var selectedId = isLinuxNativeStack ? threadId.Value : thread.ManagedThreadId;
        var threadLabel = isLinuxNativeStack ? "OS thread" : "managed thread";
        var summary = $"Stack of {threadLabel} {selectedId} (OS {thread.OSThreadId}, state {thread.State}) from snapshot '{handle}' — {thread.Frames.Count} frame(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "stack", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                Thread = thread,
                ThreadId = selectedId,
            },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryLockGraph(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var ordered = snapshot.Locks.Take(topN).ToArray();
        var summary = ordered.Length == 0
            ? $"Snapshot '{handle}' contains no held or contended SyncBlocks."
            : $"Returning {ordered.Length}/{snapshot.Locks.Count} SyncBlock(s) from snapshot '{handle}'. Most contended: object 0x{ordered[0].ObjectAddress:x} ({ordered[0].ObjectTypeFullName ?? "<unknown>"}) — {ordered[0].WaitingThreadCount} waiter(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "lock-graph", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Locks = ordered },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryDeadlocks(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var deadlocks = ThreadDeadlockDetector.Detect(snapshot, handle, topN);
        var edgeCount = snapshot.Locks.Sum(lockState => lockState.WaitingManagedThreadIds.Count(waiterId => waiterId > 0 && waiterId != lockState.OwnerManagedThreadId));
        var summary = deadlocks.Count == 0
            ? $"No deadlock cycles detected in snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) across {edgeCount} waiter→owner edge(s)."
            : $"Detected {deadlocks.Count} deadlock cycle(s) in snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) across {edgeCount} waiter→owner edge(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "deadlocks", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Deadlocks = deadlocks },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryTopBlocked(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var ordered = snapshot.Threads
            .OrderByDescending(t => t.IsLikelyBlocked)
            .ThenByDescending(t => t.LockCount)
            .ThenByDescending(t => t.Frames.Count)
            .Take(topN)
            .ToArray();
        var blocked = ordered.Count(t => t.IsLikelyBlocked);
        var summary = $"Returning {ordered.Length} thread(s) from snapshot '{handle}' ranked by likely-blocked then LockCount — {blocked} flagged as likely blocked.";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "top-blocked", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Threads = ordered },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryUniqueStacks(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN, int framesToHash, int minCount)
    {
        if (framesToHash < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(framesToHash), "must be >= 1");
        if (minCount < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(minCount), "must be >= 1");

        var allGroups = ThreadSnapshotUniqueStackGrouper.Group(snapshot.Threads, framesToHash, minCount, int.MaxValue);
        var pagedGroups = allGroups.Take(topN).ToArray();
        var summary = pagedGroups.Length == 0
            ? $"Snapshot '{handle}' has no unique stack groups with at least {minCount} thread(s)."
            : $"Returning {pagedGroups.Length}/{allGroups.Count} unique stack group(s) from snapshot '{handle}' hashed over the top {framesToHash} frame(s). Largest group: {pagedGroups[0].ThreadCount}/{snapshot.Threads.Count} thread(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "unique-stacks", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                UniqueStacks = pagedGroups,
            },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryAsyncStalls(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var view = AsyncStallClassifier.Classify(snapshot, topN);
        var summary = view.ClassifiedThreads == 0
            ? $"No async-looking parked continuations detected in snapshot '{handle}' ({origin}, pid {snapshot.ProcessId})."
            : $"Classified {view.ClassifiedThreads} async-looking thread(s) from snapshot '{handle}'. Top bucket: {view.ByBucket[0].Bucket} ({view.ByBucket[0].Count}).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "async-stalls", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                AsyncStalls = view,
            },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadPool(
        ThreadSnapshotArtifact snapshot, string handle, string origin)
    {
        if (snapshot.ThreadPool is null)
        {
            return DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"Snapshot '{handle}' does not contain ThreadPool counters/queues.",
                new DiagnosticError("ViewNotCaptured", "Re-run collect_thread_snapshot on a ClrMD-backed CoreCLR target; fallback backends may not capture ThreadPool internals.", handle));
        }

        var threadPool = snapshot.ThreadPool;
        var summary = $"ThreadPool from snapshot '{handle}': workers {threadPool.Workers.Current} current ({threadPool.Workers.Active} active, {threadPool.Workers.Idle} idle, min {threadPool.Workers.Min}, max {threadPool.Workers.Max}), IOCP {threadPool.Iocp.Current} current (idle {threadPool.Iocp.Idle}, min {threadPool.Iocp.Min}, max {threadPool.Iocp.Max}), pending work items {threadPool.PendingWorkItems} (global {threadPool.Queues.GlobalQueueLength}, local {threadPool.Queues.LocalQueues.Sum(q => q.QueueLength)} across {threadPool.Queues.LocalQueues.Count} local queue(s)).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "threadpool", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                ThreadPool = threadPool,
            },
            summary);
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
