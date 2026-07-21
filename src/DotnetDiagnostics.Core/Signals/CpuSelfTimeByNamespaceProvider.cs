namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Rolls CPU self-time up by <b>namespace</b> — a neutral "where does the CPU concentrate" grouping.
/// It says <i>where</i> (e.g. <c>System.Globalization</c>, <c>System.Text.RegularExpressions</c>,
/// <c>MyApp.Services</c>) without saying <i>what</i>: the consumer infers "culture-aware comparison"
/// or "regex" or "app code" from the namespace and drills in, rather than the engine naming a bug.
/// </summary>
/// <remarks>
/// Requires the full per-method self-time ranking (<see cref="CpuSignalContext.SelfTimeRanked"/>),
/// which only the Resource path provides — the inline hotspot list is inclusive-capped and would bias
/// the roll-up. Exclusive samples sum exactly by namespace, so summing per-method exclusive grouped by
/// namespace equals a tree-wide namespace aggregation.
/// </remarks>
public sealed class CpuSelfTimeByNamespaceProvider : ISignalProvider<CpuSignalContext>
{
    /// <summary>Minimum top-namespace self-time share for the roll-up to be salient.</summary>
    public const double MinTopNamespaceShare = 0.20;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(CpuSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TotalSamples <= 0 || context.SelfTimeRanked is not { Count: > 0 } ranked)
        {
            yield break;
        }

        var totalSelfSamples = context.OverallSelfSamples?.RunningSamples ?? context.TotalSamples;
        if (totalSelfSamples <= 0)
        {
            yield break;
        }

        var byNamespace = ranked
            .GroupBy(m => m.Namespace, StringComparer.Ordinal)
            .Select(g => (Namespace: g.Key, Exclusive: g.Sum(m => m.SelfSamples?.RunningSamples ?? m.ExclusiveSamples)))
            .Where(g => g.Exclusive > 0)
            .OrderByDescending(g => g.Exclusive)
            .ToArray();

        if (byNamespace.Length == 0)
        {
            yield break;
        }

        var topShare = byNamespace[0].Exclusive / (double)totalSelfSamples;
        if (topShare < MinTopNamespaceShare)
        {
            yield break;
        }

        var buckets = byNamespace
            .Take(MaxBuckets)
            .Select(g => new SignalBucket(g.Namespace, Math.Round(g.Exclusive * 100.0 / totalSelfSamples, 1), "%", context.HandleId))
            .ToArray();

        yield return new SignalGroup(
            Signal: "cpu.self-time.by-namespace",
            Summary: $"CPU self-time concentrates in namespace {byNamespace[0].Namespace} ({buckets[0].Magnitude:0.#}%).",
            Salience: Math.Min(1.0, topShare),
            Buckets: buckets,
            NextAction: new NextActionHint(
                "query_snapshot",
                "Rank the hot namespace's methods by self-time (exclusive) and walk the call tree.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "top-methods", ["rankBy"] = "exclusive" }));
    }
}
