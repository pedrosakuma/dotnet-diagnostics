namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Rolls exceptions up by <b>type × throw-site</b> — a neutral "where does this exception originate"
/// grouping. It says <i>which frame</i> a given exception type is thrown from (e.g.
/// <c>FormatException @ MyApp.Parsing.Parse(...)</c>) without saying <i>why</i>: the consumer infers
/// the cause from the site and drills in, rather than the engine naming a bug.
/// </summary>
/// <remarks>
/// Requires resolved managed stacks (<see cref="ExceptionSignalContext.ThrowSites"/>), which only the
/// crash-guard collector provides — the standard exception stream carries no stack. Live EventPipe
/// stack resolution is best-effort, so shares are relative to
/// <see cref="ExceptionSignalContext.ThrowSiteSampleTotal"/> (the events that carried a stack), not
/// the exact total, and the roll-up simply produces nothing when no stacks were resolved.
/// </remarks>
public sealed class ExceptionByThrowSiteProvider : ISignalProvider<ExceptionSignalContext>
{
    /// <summary>Minimum top-site share (of stack-resolved events) for the roll-up to be salient.</summary>
    public const double MinTopSiteShare = 0.3;

    /// <summary>Floor on stack-resolved events before a throw-site roll-up is worth surfacing.</summary>
    public const long MinSampleTotal = 3;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(ExceptionSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ThrowSiteSampleTotal < MinSampleTotal || context.ThrowSites is not { Count: > 0 } sites)
        {
            yield break;
        }

        var ranked = sites
            .Where(s => s.Count > 0)
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.ExceptionType, StringComparer.Ordinal)
            .ThenBy(s => s.ThrowSite, StringComparer.Ordinal)
            .ToArray();

        if (ranked.Length == 0)
        {
            yield break;
        }

        var topShare = ranked[0].Count / (double)context.ThrowSiteSampleTotal;
        if (topShare < MinTopSiteShare)
        {
            yield break;
        }

        var buckets = ranked
            .Take(MaxBuckets)
            .Select(s => new SignalBucket($"{s.ExceptionType} @ {s.ThrowSite}", s.Count, "exceptions", context.HandleId))
            .ToArray();

        var topPercent = Math.Round(topShare * 100.0, 1);
        var drillTopN = Math.Max(MaxBuckets, context.RetainedEventCount);
        yield return new SignalGroup(
            Signal: "exceptions.by-throw-site",
            Summary: $"Exceptions concentrate at one throw-site: {ranked[0].ExceptionType} @ {ranked[0].ThrowSite} ({topPercent:0.#}% of stack-resolved events).",
            Salience: Math.Min(1.0, topShare),
            Buckets: buckets,
            NextAction: new NextActionHint(
                "query_snapshot",
                "Drill into the crash-guard snapshot's retained exception events to read the managed stacks behind the dominant throw-site.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "exceptions", ["topN"] = drillTopN }));
    }
}
