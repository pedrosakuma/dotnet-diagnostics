namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Groups allocations by <b>type concentration</b>: does one type dominate the allocated bytes, or is
/// allocation spread thin across many types? Byte-weighted — the allocation analogue of CPU self-time
/// concentration. Says <i>which type</i> concentrates, never <i>why</i> it is allocated so heavily.
/// </summary>
public sealed class AllocationByTypeConcentrationProvider : ISignalProvider<AllocationSignalContext>
{
    /// <summary>Minimum top-1 type byte share for the profile to count as "concentrated".</summary>
    public const double MinTop1Share = 0.4;

    /// <summary>Floor on total bytes before a concentration is worth surfacing (avoids firing on a near-empty window).</summary>
    public const long MinTotalBytes = 64 * 1024;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(AllocationSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TotalBytes < MinTotalBytes || context.ByType is not { Count: > 0 } ranked)
        {
            yield break;
        }

        var top1Share = ranked[0].TotalBytes / (double)context.TotalBytes;
        if (top1Share < MinTop1Share)
        {
            yield break;
        }

        // "<unknown>" is a NativeAOT limitation placeholder (TypeName wasn't populated by the
        // runtime), not a real type dominating allocations — surfacing it as a concentration would
        // be noise, not information.
        if (string.Equals(ranked[0].TypeName, "<unknown>", StringComparison.Ordinal))
        {
            yield break;
        }

        var buckets = ranked
            .Take(MaxBuckets)
            .Select(t => new SignalBucket(t.TypeName, t.TotalBytes, "bytes", context.HandleId))
            .ToArray();

        var top1Percent = Math.Round(top1Share * 100.0, 1);
        yield return new SignalGroup(
            Signal: "allocations.by-type",
            Summary: $"Allocated bytes concentrate on one type: {ranked[0].TypeName} is {top1Percent:0.#}% ({ranked[0].TotalBytes:N0} bytes).",
            Salience: Math.Min(1.0, top1Share),
            Buckets: buckets,
            NextAction: new NextActionHint(
                "query_snapshot",
                "Walk the allocation call-site tree to see where the dominant type is being allocated.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "call-tree" }));
    }
}
