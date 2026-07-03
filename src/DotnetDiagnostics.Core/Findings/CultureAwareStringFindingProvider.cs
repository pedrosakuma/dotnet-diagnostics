using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Findings;

/// <summary>
/// Detects culture-aware string operations dominating a hot path (#516): when the hottest CPU
/// self-time frame sits inside the ICU/NLS collation or casing engine
/// (<c>System.Globalization.CompareInfo.Icu*</c>/<c>Nls*</c>, <c>System.Globalization.TextInfo.Icu*</c>/<c>Nls*</c>),
/// the most likely cause is an accidental culture-sensitive comparer / <c>ToLower</c> / <c>ToUpper</c>
/// on a lookup or key that should be ordinal.
/// </summary>
/// <remarks>
/// This is invisible to static analysis — <c>StringComparison.InvariantCultureIgnoreCase</c> reads
/// fine in source; only runtime sampling shows ICU dominating self-time. It is keyed off
/// <b>self-time</b> (exclusive) rather than inclusive, because the culprit is a leaf that burns CPU
/// building collation hashes, while its inclusive ancestors are innocuous plumbing (the trap the
/// <c>culture-lookup</c> case study describes). The global self-time leader
/// (<see cref="CpuFindingContext.TopSelfTime"/>) is consulted so a deep hot leaf that falls outside
/// the inline, inclusive-ranked hotspot cap is still caught.
/// </remarks>
public sealed class CultureAwareStringFindingProvider : IFindingProvider<CpuFindingContext>
{
    /// <summary>Minimum exclusive-sample share a culture-aware frame must hold to be considered genuinely hot.</summary>
    public const double HotExclusiveShare = 0.20;

    /// <inheritdoc/>
    public IEnumerable<Finding> Detect(CpuFindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TotalSamples <= 0)
        {
            yield break;
        }

        var minExclusive = context.TotalSamples * HotExclusiveShare;

        // The global self-time leader is the strongest signal; fall back to scanning the inline
        // hotspot list so the finding still fires even when TopSelfTime is unavailable.
        var candidates = context.Hotspots.AsEnumerable();
        if (context.TopSelfTime is not null)
        {
            candidates = candidates.Prepend(context.TopSelfTime);
        }

        var hot = candidates
            .Where(h => h.ExclusiveSamples >= minExclusive && IsCultureAwareFrame(h.Frame.Method))
            .OrderByDescending(h => h.ExclusiveSamples)
            .FirstOrDefault();
        if (hot is null)
        {
            yield break;
        }

        var exclusivePercent = hot.ExclusiveSamples * 100.0 / context.TotalSamples;

        yield return new Finding(
            Pattern: "culture-aware-string-op",
            Severity: FindingSeverity.High,
            Confidence: 0.8,
            Title:
                "A culture-aware string operation (ICU/NLS collation) dominates CPU self-time — a classic sign of " +
                "an accidental culture-sensitive comparer / ToLower / ToUpper on a hot lookup that should be ordinal.",
            Evidence: new[]
            {
                new FindingEvidence(
                    Kind: "frame",
                    Description: $"{hot.Frame.Method} holds {exclusivePercent:0.#}% of exclusive (self-time) samples.",
                    Handle: context.HandleId,
                    Value: Math.Round(exclusivePercent, 1),
                    Unit: "%"),
            },
            SuggestedFix:
                "Use StringComparison.Ordinal / OrdinalIgnoreCase (or StringComparer.Ordinal[IgnoreCase] for dictionary/set " +
                "keys) on hot lookups; reserve culture-aware comparison for user-facing display/sorting.",
            NextAction: new NextActionHint(
                "query_snapshot",
                "Walk the call tree to find the user frame that owns the culture-aware call.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "call-tree", ["maxDepth"] = 12, ["maxNodes"] = 200 }));
    }

    private static bool IsCultureAwareFrame(string method) =>
        (method.Contains("System.Globalization.CompareInfo.Icu", StringComparison.Ordinal)
            || method.Contains("System.Globalization.CompareInfo.Nls", StringComparison.Ordinal)
            || method.Contains("System.Globalization.TextInfo.Icu", StringComparison.Ordinal)
            || method.Contains("System.Globalization.TextInfo.Nls", StringComparison.Ordinal));
}
