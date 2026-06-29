using System.Globalization;

namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// Pure in-memory wait-chain analysis over a captured <see cref="ThreadSnapshotArtifact"/>. Where
/// <see cref="ThreadDeadlockDetector"/> only finds waiter→owner <em>cycles</em> in the monitor lock
/// graph and <see cref="AsyncStallClassifier"/> only buckets parked async stacks, this analyzer
/// unifies three wait-edge kinds into ranked, possibly-multi-hop <em>wait-chains</em>:
/// <list type="number">
///   <item><description><b>sync monitor lock</b> — a thread waiting on a contended SyncBlock → the
///   thread that owns it (derived from <see cref="ThreadSnapshotArtifact.Locks"/>, exactly the edge
///   set <see cref="ThreadDeadlockDetector"/> walks).</description></item>
///   <item><description><b>async continuation</b> — a thread parked sync-over-async or awaiting an
///   incomplete construct (Task.Wait/.Result, SemaphoreSlim.WaitAsync, channel awaits, generic
///   <c>MoveNext</c>) → the construct it is waiting on (classified by
///   <see cref="AsyncStallClassifier.ClassifyAsyncWait"/>).</description></item>
///   <item><description><b>ThreadPool starvation</b> — a sync-over-async chain that terminates in
///   "waiting for a ThreadPool thread that isn't available", detected from the snapshot's
///   <see cref="ThreadPoolSnapshot"/> exhaustion signals.</description></item>
/// </list>
/// <para><b>Async-ownership honesty.</b> Monitor ownership is recorded in the snapshot, so monitor
/// hops carry a concrete owner thread. Async-continuation resumption ownership generally is <em>not</em>
/// recoverable from a point-in-time snapshot — nothing in thread state records which thread/task will
/// complete an outstanding await — so async hops emit an explicit note instead of guessing an owner.</para>
/// </summary>
public static class WaitChainAnalyzer
{
    private const string MonitorEdge = "monitor-lock";
    private const string AsyncEdge = "async-continuation";
    private const string ThreadPoolEdge = "threadpool-starvation";

    private const string TargetThread = "thread";
    private const string TargetConstruct = "async-construct";
    private const string TargetThreadPool = "threadpool-starvation";
    private const string TargetCycle = "cycle";
    private const string TargetOwnerRunning = "owner-running";

    private const string AsyncOwnershipNote =
        "Async-continuation resumption ownership is not determinable from a point-in-time snapshot: thread state does not record which thread or task will complete this await.";

    private const string ThreadPoolStarvationNote =
        "Chain sinks in ThreadPool starvation: pending work is queued but all worker threads are busy and the pool is at its maximum, so the awaited continuation cannot be scheduled.";

    public static WaitChainsView Analyze(ThreadSnapshotArtifact snapshot, string handle, int maxChains)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(handle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChains);

        var threadsById = snapshot.Threads
            .Where(t => t.ManagedThreadId > 0)
            .GroupBy(t => t.ManagedThreadId)
            .ToDictionary(g => g.Key, g => g.First());

        var threadPoolStarved = IsThreadPoolStarved(snapshot.ThreadPool);

        // Each blocked thread has at most one outgoing wait edge (a thread waits on one thing at a
        // time), so the wait-graph is a functional graph: chains, plus rho-shapes that close into a
        // cycle. Monitor edges take precedence over async edges — a concrete contended lock is a
        // stronger signal than a parked continuation.
        var edges = new Dictionary<int, WaitEdge>();
        var monitorTargets = new HashSet<int>();
        var edgeCount = 0;

        foreach (var thread in threadsById.Values.OrderBy(t => t.ManagedThreadId))
        {
            var monitorEdge = BuildMonitorEdge(thread.ManagedThreadId, snapshot, threadsById);
            if (monitorEdge is not null)
            {
                edges[thread.ManagedThreadId] = monitorEdge;
                monitorTargets.Add(monitorEdge.OwnerThreadId!.Value);
                edgeCount++;
                continue;
            }

            var asyncEdge = BuildAsyncEdge(thread, threadPoolStarved);
            if (asyncEdge is not null)
            {
                edges[thread.ManagedThreadId] = asyncEdge;
                edgeCount++;
            }
        }

        var chains = new List<WaitChain>();
        var coveredRoots = new HashSet<int>();

        // Roots first: blocked threads that nobody else is waiting on (no incoming monitor edge).
        // Walking from sources yields the longest chains and naturally subsumes their mid-chain hops.
        var roots = edges.Keys
            .Where(id => !monitorTargets.Contains(id))
            .OrderBy(id => id)
            .ToArray();

        foreach (var root in roots)
        {
            var chain = Walk(root, edges, threadsById);
            chains.Add(chain);
            foreach (var link in chain.Links)
            {
                coveredRoots.Add(link.WaitingThreadId);
            }
        }

        // Pure cycles have no source node — every member has an incoming monitor edge. Pick the
        // lowest unwalked thread id with an outgoing edge and walk it to surface the deadlock.
        foreach (var id in edges.Keys.OrderBy(x => x))
        {
            if (coveredRoots.Contains(id))
            {
                continue;
            }

            var chain = Walk(id, edges, threadsById);
            chains.Add(chain);
            foreach (var link in chain.Links)
            {
                coveredRoots.Add(link.WaitingThreadId);
            }
        }

        var ranked = chains
            .OrderByDescending(c => c.Length)
            .ThenByDescending(c => c.BlockedThreadCount)
            .ThenByDescending(c => c.IsCycle)
            .ThenByDescending(c => string.Equals(c.TerminalKind, TargetThreadPool, StringComparison.Ordinal))
            .ThenBy(c => c.RootThreadId)
            .Take(maxChains)
            .Select((c, index) => c with { Rank = index + 1 })
            .ToArray();

        var cycleCount = ranked.Count(c => c.IsCycle);
        var notes = new List<string>();
        if (ranked.Any(c => c.Links.Any(l => string.Equals(l.EdgeKind, AsyncEdge, StringComparison.Ordinal))))
        {
            notes.Add(AsyncOwnershipNote);
        }
        if (threadPoolStarved)
        {
            notes.Add(ThreadPoolStarvationNote);
        }

        return new WaitChainsView(
            View: "wait-chains",
            ThreadCount: threadsById.Count,
            EdgeCount: edgeCount,
            CycleCount: cycleCount,
            OpenChainCount: ranked.Length - cycleCount,
            ThreadPoolStarved: threadPoolStarved,
            Chains: ranked)
        {
            Notes = notes,
        };
    }

    private static WaitChain Walk(
        int rootThreadId,
        Dictionary<int, WaitEdge> edges,
        Dictionary<int, ManagedThread> threadsById)
    {
        var links = new List<WaitChainLink>();
        var notes = new List<string>();
        var pathSet = new HashSet<int>();
        var blockedThreads = new HashSet<int>();
        var current = rootThreadId;
        var isCycle = false;
        var terminalKind = TargetOwnerRunning;

        while (edges.TryGetValue(current, out var edge))
        {
            var thread = threadsById[current];
            pathSet.Add(current);
            blockedThreads.Add(current);

            var link = BuildLink(thread, edge);
            links.Add(link);
            if (link.Note is not null && !notes.Contains(link.Note))
            {
                notes.Add(link.Note);
            }

            if (!string.Equals(edge.EdgeKind, MonitorEdge, StringComparison.Ordinal))
            {
                terminalKind = edge.TargetKind;
                break;
            }

            var owner = edge.OwnerThreadId!.Value;
            if (pathSet.Contains(owner))
            {
                isCycle = true;
                terminalKind = TargetCycle;
                break;
            }

            if (!edges.ContainsKey(owner))
            {
                terminalKind = TargetOwnerRunning;
                break;
            }

            current = owner;
        }

        return new WaitChain(
            Rank: 0,
            IsCycle: isCycle,
            Length: links.Count,
            BlockedThreadCount: blockedThreads.Count,
            RootThreadId: rootThreadId,
            TerminalKind: terminalKind,
            Links: links)
        {
            Notes = notes,
        };
    }

    private static WaitChainLink BuildLink(ManagedThread thread, WaitEdge edge)
    {
        return new WaitChainLink(
            WaitingThreadId: thread.ManagedThreadId,
            WaitingOSThreadId: thread.OSThreadId,
            EdgeKind: edge.EdgeKind,
            WaitReason: edge.WaitReason,
            TargetKind: edge.TargetKind,
            TargetLabel: edge.TargetLabel)
        {
            OwnerThreadId = edge.OwnerThreadId,
            LockObjectAddress = edge.LockObjectAddress is { } addr
                ? "0x" + addr.ToString("x", CultureInfo.InvariantCulture)
                : null,
            LockObjectTypeFullName = edge.LockObjectTypeFullName,
            Note = edge.Note,
        };
    }

    private static WaitEdge? BuildMonitorEdge(
        int waiterId,
        ThreadSnapshotArtifact snapshot,
        Dictionary<int, ManagedThread> threadsById)
    {
        // Mirror ThreadDeadlockDetector's edge model: pick the contended lock this thread waits on
        // whose owner is a distinct, known thread. Deterministic tie-break (owner id, then address)
        // keeps the functional-graph successor stable across runs.
        var candidate = snapshot.Locks
            .Where(l => l.OwnerManagedThreadId > 0
                && l.OwnerManagedThreadId != waiterId
                && threadsById.ContainsKey(l.OwnerManagedThreadId)
                && l.WaitingManagedThreadIds.Contains(waiterId))
            .OrderBy(l => l.OwnerManagedThreadId)
            .ThenBy(l => l.ObjectAddress)
            .FirstOrDefault();

        if (candidate is null)
        {
            return null;
        }

        var owner = threadsById[candidate.OwnerManagedThreadId];
        var typeLabel = candidate.ObjectTypeFullName ?? "<unknown>";
        var reason = string.Create(
            CultureInfo.InvariantCulture,
            $"waiting to acquire {candidate.LockKind} on object 0x{candidate.ObjectAddress:x} ({typeLabel}) held by managed thread {owner.ManagedThreadId}");

        return new WaitEdge(
            EdgeKind: MonitorEdge,
            WaitReason: reason,
            TargetKind: TargetThread,
            TargetLabel: string.Create(CultureInfo.InvariantCulture, $"managed thread {owner.ManagedThreadId}"),
            OwnerThreadId: owner.ManagedThreadId,
            LockObjectAddress: candidate.ObjectAddress,
            LockObjectTypeFullName: candidate.ObjectTypeFullName,
            Note: null);
    }

    private static WaitEdge? BuildAsyncEdge(ManagedThread thread, bool threadPoolStarved)
    {
        var bucket = AsyncStallClassifier.ClassifyAsyncWait(thread);
        if (bucket is null)
        {
            return null;
        }

        // Sync-over-async (Task.Wait/.Result/GetResult) blocks a (usually pool) thread until a Task
        // completes. When the pool is starved that Task can never be scheduled — the chain genuinely
        // sinks in ThreadPool starvation. Otherwise the awaited Task's completer is indeterminate.
        if (string.Equals(bucket, AsyncStallClassifier.SyncOverAsync, StringComparison.Ordinal))
        {
            if (threadPoolStarved)
            {
                return new WaitEdge(
                    EdgeKind: ThreadPoolEdge,
                    WaitReason: "blocked sync-over-async waiting for a ThreadPool thread to complete a Task, but the pool is starved (no worker available)",
                    TargetKind: TargetThreadPool,
                    TargetLabel: "ThreadPool (starved)",
                    OwnerThreadId: null,
                    LockObjectAddress: null,
                    LockObjectTypeFullName: null,
                    Note: ThreadPoolStarvationNote);
            }

            return new WaitEdge(
                EdgeKind: AsyncEdge,
                WaitReason: "blocked sync-over-async (Task.Wait/.Result/GetResult) on an incomplete Task",
                TargetKind: TargetConstruct,
                TargetLabel: "Task",
                OwnerThreadId: null,
                LockObjectAddress: null,
                LockObjectTypeFullName: null,
                Note: AsyncOwnershipNote);
        }

        var (label, reason) = bucket switch
        {
            AsyncStallClassifier.SemaphoreAwait => ("SemaphoreSlim", "awaiting SemaphoreSlim.WaitAsync (gated by a semaphore release)"),
            AsyncStallClassifier.ChannelAwait => ("Channel", "awaiting a channel read (ChannelReader)"),
            AsyncStallClassifier.ChannelWriteBackpressure => ("Channel", "awaiting a bounded-channel write slot (backpressure)"),
            AsyncStallClassifier.TcsPending => ("TaskCompletionSource", "awaiting a TaskCompletionSource that has not been completed"),
            AsyncStallClassifier.Delay => ("Task.Delay", "awaiting a timer (Task.Delay) — time-based, not blocked on another worker"),
            _ => ("Task", "awaiting an incomplete Task (generic async continuation)"),
        };

        return new WaitEdge(
            EdgeKind: AsyncEdge,
            WaitReason: reason,
            TargetKind: TargetConstruct,
            TargetLabel: label,
            OwnerThreadId: null,
            LockObjectAddress: null,
            LockObjectTypeFullName: null,
            Note: AsyncOwnershipNote);
    }

    private static bool IsThreadPoolStarved(ThreadPoolSnapshot? threadPool)
    {
        if (threadPool is null || !threadPool.Initialized)
        {
            return false;
        }

        var workers = threadPool.Workers;
        var queues = threadPool.Queues;
        var hasPendingWork = threadPool.PendingWorkItems > 0
            || queues.GlobalQueueLength > 0
            || queues.LocalQueues.Sum(q => q.QueueLength) > 0;

        var noIdleWorkers = workers.Idle <= 0;
        var atMaxWorkers = workers.Max > 0
            && (workers.Current >= workers.Max || workers.Active >= workers.Max);

        return hasPendingWork && noIdleWorkers && atMaxWorkers;
    }

    private sealed record WaitEdge(
        string EdgeKind,
        string WaitReason,
        string TargetKind,
        string? TargetLabel,
        int? OwnerThreadId,
        ulong? LockObjectAddress,
        string? LockObjectTypeFullName,
        string? Note);
}
