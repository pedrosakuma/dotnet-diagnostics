namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Groups threads by <b>wait target</b>: does one SyncBlock/monitor account for most of the waiting
/// threads across the snapshot? The finer, resolvable-only complement to
/// <see cref="ThreadByWaitStateProvider"/> — the byte-weighted-style analogue for locks. Says
/// <i>which object</i> threads converge on, never <i>why</i> (lock contention naming is left to the
/// consumer).
/// </summary>
/// <remarks>
/// <see cref="ThreadWaitSignalContext.Locks"/> can have no contended entries (nothing waiting on any
/// monitor), in which case this provider simply produces nothing.
/// </remarks>
public sealed class ThreadByWaitTargetProvider : ISignalProvider<ThreadWaitSignalContext>
{
    /// <summary>Minimum waiting-thread count on the top target for the signal to be salient.</summary>
    public const int MinWaitingThreadCount = 3;

    /// <summary>Minimum share of all lock-waiting threads converging on the top target for the signal to be salient.</summary>
    public const double MinShare = 0.4;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(ThreadWaitSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Locks is not { Count: > 0 })
        {
            yield break;
        }

        var ranked = context.Locks
            .Where(l => l.WaitingThreadCount > 0)
            .OrderByDescending(l => l.WaitingThreadCount)
            .ToArray();

        if (ranked.Length == 0)
        {
            yield break;
        }

        var totalWaiting = ranked.Sum(l => l.WaitingThreadCount);
        var top1 = ranked[0];
        var share = top1.WaitingThreadCount / (double)totalWaiting;
        if (top1.WaitingThreadCount < MinWaitingThreadCount || share < MinShare)
        {
            yield break;
        }

        var buckets = ranked
            .Take(MaxBuckets)
            .Select(l => new SignalBucket(TargetKey(l), l.WaitingThreadCount, "threads", context.HandleId))
            .ToArray();

        var percent = Math.Round(share * 100.0, 1);
        yield return new SignalGroup(
            Signal: "threads.by-wait-target",
            Summary: $"{top1.WaitingThreadCount} of {totalWaiting} lock-waiting threads ({percent:0.#}%) converge on one target: {TargetKey(top1)}.",
            Salience: Math.Min(1.0, share),
            Buckets: buckets,
            NextAction: new NextActionHint(
                "query_snapshot",
                "Inspect the lock graph for this target to see the owning thread and full waiter list.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "lock-graph" }));
    }

    private static string TargetKey(Threads.MonitorLockState l) =>
        $"{l.ObjectTypeFullName ?? "<unknown type>"} @ 0x{l.ObjectAddress:x}";
}
