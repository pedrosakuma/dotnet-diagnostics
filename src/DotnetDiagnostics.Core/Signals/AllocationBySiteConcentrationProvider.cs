namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Groups allocations by <b>call-site concentration</b>: does one call site (leaf allocating frame)
/// account for most allocated bytes? Byte-weighted — the allocation analogue of CPU self-time
/// concentration. Says <i>which frame</i> allocates the most, never <i>why</i>.
/// </summary>
/// <remarks>
/// <see cref="AllocationSignalContext.BySite"/> can be empty (e.g. NativeAOT frames that resolve to
/// raw addresses only), in which case this provider simply produces nothing.
/// </remarks>
public sealed class AllocationBySiteConcentrationProvider : ISignalProvider<AllocationSignalContext>
{
    /// <summary>Minimum top-1 site byte share for the profile to count as "concentrated".</summary>
    public const double MinTop1Share = 0.4;

    /// <summary>Floor on total bytes before a concentration is worth surfacing (avoids firing on a near-empty window).</summary>
    public const long MinTotalBytes = 64 * 1024;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(AllocationSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TotalBytes < MinTotalBytes || context.BySite is not { Count: > 0 } ranked)
        {
            yield break;
        }

        var top1Share = ranked[0].TotalBytes / (double)context.TotalBytes;
        if (top1Share < MinTop1Share)
        {
            yield break;
        }

        var buckets = ranked
            .Take(MaxBuckets)
            .Select(s => new SignalBucket(s.Frame.Method, s.TotalBytes, "bytes", context.HandleId))
            .ToArray();

        var top1Percent = Math.Round(top1Share * 100.0, 1);
        yield return new SignalGroup(
            Signal: "allocations.by-site",
            Summary: $"Allocated bytes concentrate at one call site: {ranked[0].Frame.Method} is {top1Percent:0.#}% ({ranked[0].TotalBytes:N0} bytes).",
            Salience: Math.Min(1.0, top1Share),
            Buckets: buckets,
            NextAction: new NextActionHint(
                "query_snapshot",
                "Walk the allocation call-site tree to confirm where the dominant call site sits in the caller chain.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "call-tree" }));
    }
}
