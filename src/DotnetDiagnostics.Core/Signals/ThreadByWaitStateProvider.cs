namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Groups threads by <b>wait state</b>: how many threads are parked in the same kind of wait
/// (<c>Monitor.Enter (contended)</c>, <c>Thread.Sleep</c>, <c>Socket I/O</c>, …), inferred from each
/// thread's top frame. Says <i>how many threads share a wait state</i>, never <i>why</i> — lock
/// contention / sync-over-async naming is left to the consumer.
/// </summary>
public sealed class ThreadByWaitStateProvider : ISignalProvider<ThreadWaitSignalContext>
{
    /// <summary>Minimum thread count in the top wait-state bucket for the signal to be salient.</summary>
    public const int MinThreadCount = 3;

    /// <summary>Minimum share of all threads sharing the top wait state for the signal to be salient.</summary>
    public const double MinShare = 0.3;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(ThreadWaitSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TotalThreads == 0 || context.Threads is not { Count: > 0 })
        {
            yield break;
        }

        var ranked = context.Threads
            .Where(t => t.IsLikelyBlocked && !string.IsNullOrWhiteSpace(t.InferredWaitReason))
            .GroupBy(t => t.InferredWaitReason!, StringComparer.Ordinal)
            .Select(g => (Reason: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ToArray();

        if (ranked.Length == 0)
        {
            yield break;
        }

        var (topReason, topCount) = ranked[0];
        var share = topCount / (double)context.TotalThreads;
        if (topCount < MinThreadCount || share < MinShare)
        {
            yield break;
        }

        var buckets = ranked
            .Take(MaxBuckets)
            .Select(g => new SignalBucket(g.Reason, g.Count, "threads", context.HandleId))
            .ToArray();

        var percent = Math.Round(share * 100.0, 1);
        yield return new SignalGroup(
            Signal: "threads.by-wait-state",
            Summary: $"{topCount} of {context.TotalThreads} threads ({percent:0.#}%) are parked in the same wait state: {topReason}.",
            Salience: Math.Min(1.0, share),
            Buckets: buckets,
            NextAction: new NextActionHint(
                "query_snapshot",
                "Drill into the blocked threads to see where they converge.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "top-blocked" }));
    }
}
