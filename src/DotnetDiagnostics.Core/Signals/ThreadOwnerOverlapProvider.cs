namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Correlates the two thread-snapshot signal groupings by <b>thread identity</b> (#528): is the
/// owner of a contended lock (<see cref="ThreadByWaitTargetProvider"/>'s domain) itself parked in a
/// wait state (<see cref="ThreadByWaitStateProvider"/>'s domain)? Says <i>which thread appears in
/// both groupings</i>, never <i>why</i> — no lock-contention / blocking-chain naming, purely an
/// observed identity overlap over data the snapshot already carries.
/// </summary>
/// <remarks>
/// Reuses the same <see cref="ThreadWaitSignalContext"/> already fed to the by-wait-state and
/// by-wait-target providers — no additional capture. Produces nothing when no contended lock's
/// owner also appears among the blocked threads (the common, uncorrelated case).
/// </remarks>
public sealed class ThreadOwnerOverlapProvider : ISignalProvider<ThreadWaitSignalContext>
{
    /// <summary>Minimum waiting-thread count on a lock for its owner's overlap to be salient (mirrors <see cref="ThreadByWaitTargetProvider.MinWaitingThreadCount"/>).</summary>
    public const int MinWaitingThreadCount = 3;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(ThreadWaitSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Locks is not { Count: > 0 } || context.Threads is not { Count: > 0 })
        {
            yield break;
        }

        var blockedThreads = context.Threads
            .Where(t => t.IsLikelyBlocked)
            .ToDictionary(t => t.ManagedThreadId);

        if (blockedThreads.Count == 0)
        {
            yield break;
        }

        // Locks whose owner is *itself* among the blocked threads — the thread identity overlap
        // between the two thread-snapshot groupings.
        var overlapping = context.Locks
            .Where(l => l.WaitingThreadCount >= MinWaitingThreadCount && blockedThreads.ContainsKey(l.OwnerManagedThreadId))
            .OrderByDescending(l => l.WaitingThreadCount)
            .ToArray();

        if (overlapping.Length == 0)
        {
            yield break;
        }

        var top1 = overlapping[0];
        var ownerReason = blockedThreads[top1.OwnerManagedThreadId].InferredWaitReason;
        var reasonText = ownerReason is null ? string.Empty : $" ({ownerReason})";

        var buckets = overlapping
            .Take(MaxBuckets)
            .Select(l => new SignalBucket(
                $"thread {l.OwnerManagedThreadId} owns {(l.ObjectTypeFullName ?? "<unknown type>")} @ 0x{l.ObjectAddress:x}",
                l.WaitingThreadCount,
                "threads",
                context.HandleId))
            .ToArray();

        var salience = Math.Min(1.0, top1.WaitingThreadCount / (double)context.TotalThreads);

        yield return new SignalGroup(
            Signal: "correlation.thread-overlap",
            Summary: $"Thread {top1.OwnerManagedThreadId} appears in both thread groupings: it is itself in a wait state{reasonText} " +
                     $"while {top1.WaitingThreadCount} thread(s) wait on a lock it holds.",
            Salience: salience,
            Buckets: buckets,
            NextAction: new NextActionHint(
                "query_snapshot",
                "Inspect the lock graph for the overlapping owner thread's full waiter list.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "lock-graph" }));
    }
}
