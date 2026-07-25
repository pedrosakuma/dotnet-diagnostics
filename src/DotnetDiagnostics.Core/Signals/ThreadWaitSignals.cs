using DotnetDiagnostics.Core.Threads;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Runs the registered thread-wait <see cref="ISignalProvider{TContext}"/>s and returns a ranked,
/// capped set of <see cref="SignalGroup"/>s — the salient "vector" the engine forwards instead of the
/// full thread + lock lists. Diagnosis-agnostic: surfaces where threads concentrate by wait state and
/// by wait target (and, via #528, where those two groupings overlap by thread identity), never why
/// (no lock-contention / sync-over-async naming).
/// </summary>
public static class ThreadWaitSignals
{
    private const int MaxBuckets = 5;
    private const int MaxWaitStateCandidates = 16;
    private const int MaxOwnerOverlapCandidates = 64;

    private static readonly ISignalProvider<ThreadWaitSignalContext>[] Providers =
        {
            new ThreadByWaitStateProvider(),
            new ThreadByWaitTargetProvider(),
            new ThreadOwnerOverlapProvider(),
        };

    /// <summary>
    /// Derives compatible signals from a thread snapshot with bounded aggregation workspace.
    /// </summary>
    public static IReadOnlyList<SignalGroup> Detect(ThreadSnapshotArtifact snapshot, string handleId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(handleId);

        var signals = new List<SignalGroup>(Providers.Length);
        AddWaitStateSignal(snapshot, handleId, signals);
        var topLocks = AddWaitTargetSignal(snapshot, handleId, signals);
        AddOwnerOverlapSignal(snapshot, handleId, topLocks, signals);
        return SignalRanker.Rank(signals);
    }

    /// <summary>Runs every registered provider over the context and ranks the union by salience.</summary>
    public static IReadOnlyList<SignalGroup> Detect(ThreadWaitSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SignalRanker.Rank(Providers.SelectMany(p => p.Detect(context)));
    }

    private static void AddWaitStateSignal(
        ThreadSnapshotArtifact snapshot,
        string handleId,
        List<SignalGroup> signals)
    {
        if (snapshot.Threads.Count == 0)
        {
            return;
        }

        var candidateKeys = new string?[MaxWaitStateCandidates];
        var candidateCounts = new int[MaxWaitStateCandidates];
        foreach (var thread in snapshot.Threads)
        {
            if (!thread.IsLikelyBlocked || string.IsNullOrWhiteSpace(thread.InferredWaitReason))
            {
                continue;
            }

            var reason = thread.InferredWaitReason;
            var matchIndex = -1;
            var emptyIndex = -1;
            for (var index = 0; index < candidateKeys.Length; index++)
            {
                if (string.Equals(candidateKeys[index], reason, StringComparison.Ordinal))
                {
                    matchIndex = index;
                    break;
                }
                if (emptyIndex < 0 && candidateKeys[index] is null)
                {
                    emptyIndex = index;
                }
            }

            if (matchIndex >= 0)
            {
                candidateCounts[matchIndex]++;
            }
            else if (emptyIndex >= 0)
            {
                candidateKeys[emptyIndex] = reason;
                candidateCounts[emptyIndex] = 1;
            }
            else
            {
                for (var index = 0; index < candidateKeys.Length; index++)
                {
                    candidateCounts[index]--;
                    if (candidateCounts[index] == 0)
                    {
                        candidateKeys[index] = null;
                    }
                }
            }
        }

        if (candidateKeys.All(static key => key is null))
        {
            return;
        }

        Array.Clear(candidateCounts);
        foreach (var thread in snapshot.Threads)
        {
            if (!thread.IsLikelyBlocked || thread.InferredWaitReason is not { } reason)
            {
                continue;
            }

            for (var index = 0; index < candidateKeys.Length; index++)
            {
                if (string.Equals(candidateKeys[index], reason, StringComparison.Ordinal))
                {
                    candidateCounts[index]++;
                    break;
                }
            }
        }

        var ranked = candidateKeys
            .Select((key, index) => (Key: key, Count: candidateCounts[index]))
            .Where(static candidate => candidate.Key is not null)
            .OrderByDescending(static candidate => candidate.Count)
            .ThenBy(static candidate => candidate.Key, StringComparer.Ordinal)
            .Take(MaxBuckets)
            .ToArray();
        var top = ranked[0];
        var share = top.Count / (double)snapshot.Threads.Count;
        if (top.Count < ThreadByWaitStateProvider.MinThreadCount || share < ThreadByWaitStateProvider.MinShare)
        {
            return;
        }

        var percent = Math.Round(share * 100.0, 1);
        signals.Add(new SignalGroup(
            Signal: "threads.by-wait-state",
            Summary: $"{top.Count} of {snapshot.Threads.Count} threads ({percent:0.#}%) are parked in the same wait state: {top.Key}.",
            Salience: Math.Min(1.0, share),
            Buckets: ranked.Select(pair => new SignalBucket(pair.Key!, pair.Count, "threads", handleId)).ToArray(),
            NextAction: new NextActionHint(
                "query_snapshot",
                "Drill into the blocked threads to see where they converge.",
                new Dictionary<string, object?> { ["handle"] = handleId, ["view"] = "top-blocked" })));
    }

    private static List<LockCandidate> AddWaitTargetSignal(
        ThreadSnapshotArtifact snapshot,
        string handleId,
        List<SignalGroup> signals)
    {
        var topLocks = new List<LockCandidate>(MaxOwnerOverlapCandidates);
        long totalWaiting = 0;
        foreach (var monitor in snapshot.Locks)
        {
            if (monitor.WaitingThreadCount <= 0)
            {
                continue;
            }

            totalWaiting += monitor.WaitingThreadCount;
            InsertRanked(topLocks, new LockCandidate(monitor), MaxOwnerOverlapCandidates);
        }

        if (topLocks.Count == 0)
        {
            return topLocks;
        }

        var top = topLocks[0].Lock;
        var share = top.WaitingThreadCount / (double)totalWaiting;
        if (top.WaitingThreadCount >= ThreadByWaitTargetProvider.MinWaitingThreadCount
            && share >= ThreadByWaitTargetProvider.MinShare)
        {
            var percent = Math.Round(share * 100.0, 1);
            signals.Add(new SignalGroup(
                Signal: "threads.by-wait-target",
                Summary: $"{top.WaitingThreadCount} of {totalWaiting} lock-waiting threads ({percent:0.#}%) converge on one target: {TargetKey(top)}.",
                Salience: Math.Min(1.0, share),
                Buckets: topLocks
                    .Take(MaxBuckets)
                    .Select(candidate => new SignalBucket(
                        TargetKey(candidate.Lock),
                        candidate.Lock.WaitingThreadCount,
                        "threads",
                        handleId))
                    .ToArray(),
                NextAction: LockNextAction(handleId, top.ObjectAddress)));
        }

        return topLocks;
    }

    private static void AddOwnerOverlapSignal(
        ThreadSnapshotArtifact snapshot,
        string handleId,
        IReadOnlyList<LockCandidate> topLocks,
        List<SignalGroup> signals)
    {
        if (topLocks.Count == 0 || snapshot.Threads.Count == 0)
        {
            return;
        }

        var ownerReasons = new string?[topLocks.Count];
        foreach (var thread in snapshot.Threads)
        {
            if (!thread.IsLikelyBlocked)
            {
                continue;
            }

            for (var index = 0; index < topLocks.Count; index++)
            {
                if (topLocks[index].Lock.OwnerManagedThreadId == thread.ManagedThreadId)
                {
                    ownerReasons[index] = thread.InferredWaitReason ?? string.Empty;
                }
            }
        }

        var overlapping = new List<LockCandidate>(MaxBuckets);
        for (var index = 0; index < topLocks.Count; index++)
        {
            var candidate = topLocks[index];
            if (ownerReasons[index] is not null
                && candidate.Lock.WaitingThreadCount >= ThreadOwnerOverlapProvider.MinWaitingThreadCount)
            {
                overlapping.Add(candidate with { OwnerWaitReason = ownerReasons[index] });
                if (overlapping.Count == MaxBuckets)
                {
                    break;
                }
            }
        }

        if (overlapping.Count == 0)
        {
            return;
        }

        var top = overlapping[0];
        var ownerReason = string.IsNullOrEmpty(top.OwnerWaitReason) ? string.Empty : $" ({top.OwnerWaitReason})";
        signals.Add(new SignalGroup(
            Signal: "correlation.thread-overlap",
            Summary: $"Thread {top.Lock.OwnerManagedThreadId} appears in both thread groupings: it is itself in a wait state{ownerReason} " +
                     $"while {top.Lock.WaitingThreadCount} thread(s) wait on a lock it holds.",
            Salience: Math.Min(1.0, top.Lock.WaitingThreadCount / (double)snapshot.Threads.Count),
            Buckets: overlapping
                .Select(candidate => new SignalBucket(
                    $"thread {candidate.Lock.OwnerManagedThreadId} owns {TargetKey(candidate.Lock)}",
                    candidate.Lock.WaitingThreadCount,
                    "threads",
                    handleId))
                .ToArray(),
            NextAction: LockNextAction(handleId, top.Lock.ObjectAddress)));
    }

    private static void InsertRanked(List<LockCandidate> target, LockCandidate candidate, int limit)
    {
        var index = target.BinarySearch(candidate, LockCandidateComparer.Instance);
        if (index < 0)
        {
            index = ~index;
        }
        if (index >= limit)
        {
            return;
        }

        target.Insert(index, candidate);
        if (target.Count > limit)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static string TargetKey(MonitorLockState monitor)
        => $"{monitor.ObjectTypeFullName ?? "<unknown type>"} @ 0x{monitor.ObjectAddress:x}";

    private static NextActionHint LockNextAction(string handleId, ulong address)
        => new(
            "query_snapshot",
            "Inspect this lock's owner and first bounded waiter page; continue with nextWaiterCursor when present.",
            new Dictionary<string, object?>
            {
                ["handle"] = handleId,
                ["view"] = "lock-graph",
                ["address"] = $"0x{address:x}",
                ["offset"] = 0,
            });

    private readonly record struct LockCandidate(MonitorLockState Lock)
    {
        public string? OwnerWaitReason { get; init; }
    }

    private sealed class LockCandidateComparer : IComparer<LockCandidate>
    {
        public static LockCandidateComparer Instance { get; } = new();

        public int Compare(LockCandidate x, LockCandidate y)
        {
            var result = y.Lock.WaitingThreadCount.CompareTo(x.Lock.WaitingThreadCount);
            return result != 0 ? result : x.Lock.ObjectAddress.CompareTo(y.Lock.ObjectAddress);
        }
    }
}
