namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Surfaces an unusually high <b>share of gen2 collections</b> among all collections observed — a
/// neutral magnitude signal, not a diagnosis. Generational GC is designed so gen0 collections
/// dominate and gen2 (full, compacting) collections are rare; an elevated gen2 share is worth
/// surfacing without asserting a cause.
/// </summary>
public sealed class GcGen2ShareProvider : ISignalProvider<GcSignalContext>
{
    /// <summary>Minimum gen2 share of total collections for the signal to be salient.</summary>
    public const double MinShare = 0.3;

    /// <summary>Floor on total collections before a gen2 share is worth surfacing (avoids firing on 1-2 collections).</summary>
    public const int MinTotalCollections = 3;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(GcSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TotalCollections < MinTotalCollections || context.Generations is not { Count: > 0 })
        {
            yield break;
        }

        var gen2Count = context.Generations.Where(g => g.Generation == 2).Sum(g => g.Count);
        if (gen2Count == 0)
        {
            yield break;
        }

        var share = gen2Count / (double)context.TotalCollections;
        if (share < MinShare)
        {
            yield break;
        }

        var percent = Math.Round(share * 100.0, 1);
        yield return new SignalGroup(
            Signal: "gc.gen2-share",
            Summary: $"Gen2 collections are {percent:0.#}% of all collections ({gen2Count}/{context.TotalCollections}).",
            Salience: Math.Min(1.0, share),
            Buckets: new[] { new SignalBucket("gen2", gen2Count, "collections", context.HandleId) },
            NextAction: new NextActionHint(
                "query_snapshot",
                "Break the collections down by generation and inspect the timeline.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "byGeneration" }));
    }
}
