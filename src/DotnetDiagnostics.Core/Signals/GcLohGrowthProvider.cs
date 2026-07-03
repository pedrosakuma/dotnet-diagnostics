namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Surfaces <b>LOH size growth</b> across the collection window — a neutral trend signal, not a
/// diagnosis. Compares the first and last <c>GCHeapStats</c> samples in the window; a rising LOH
/// across the window is worth surfacing (it may indicate a leak, a legitimately large working set, or
/// a benign burst) without asserting which.
/// </summary>
public sealed class GcLohGrowthProvider : ISignalProvider<GcSignalContext>
{
    /// <summary>Minimum relative growth (last vs first sample) for the signal to be salient.</summary>
    public const double MinRelativeGrowth = 0.2;

    /// <summary>Minimum absolute growth in bytes, so a tiny heap growing from near-zero doesn't fire on relative share alone.</summary>
    public const long MinAbsoluteGrowthBytes = 1024 * 1024;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(GcSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HeapStats is not { Count: >= 2 } samples)
        {
            yield break;
        }

        var first = samples[0].LohSizeBytes;
        var last = samples[^1].LohSizeBytes;
        var growth = last - first;
        if (growth < MinAbsoluteGrowthBytes)
        {
            yield break;
        }

        var relativeGrowth = growth / (double)Math.Max(first, 1);
        if (relativeGrowth < MinRelativeGrowth)
        {
            yield break;
        }

        yield return new SignalGroup(
            Signal: "gc.loh-growth",
            Summary: $"LOH size grew {growth:N0} bytes across the window ({first:N0} -> {last:N0} bytes, +{Math.Round(relativeGrowth * 100.0, 1):0.#}%).",
            Salience: Math.Min(1.0, relativeGrowth),
            Buckets: new[] { new SignalBucket("LOH", growth, "bytes", context.HandleId) },
            NextAction: new NextActionHint(
                "query_snapshot",
                "Inspect the GC event timeline to see when LOH growth occurred, then correlate with an allocation sample.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "timeline" }));
    }
}
