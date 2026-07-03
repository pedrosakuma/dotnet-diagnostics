using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Findings;

/// <summary>
/// Detects catastrophic regex backtracking (#388): when the hottest CPU frames sit inside the regex
/// engine (<c>System.Text.RegularExpressions.RegexRunner</c> / <c>RegexInterpreter</c>), the most
/// likely cause is backtracking on attacker- or user-controlled input.
/// </summary>
/// <remarks>
/// This is the reference adopter for the findings layer (#515): the detection thresholds are the
/// same ones previously baked into <c>DiagnosticTools.TryBuildRegexBacktrackingHint</c> — a regex
/// frame must carry a meaningful inclusive-sample share so a low-ranked incidental regex call (common
/// in any app) never trips the warning.
/// </remarks>
public sealed class RegexBacktrackingFindingProvider : IFindingProvider<CpuFindingContext>
{
    /// <summary>Minimum inclusive-sample share a regex frame must hold to be considered genuinely hot.</summary>
    public const double HotInclusiveShare = 0.20;

    /// <inheritdoc/>
    public IEnumerable<Finding> Detect(CpuFindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TotalSamples <= 0)
        {
            yield break;
        }

        var minInclusive = context.TotalSamples * HotInclusiveShare;
        var hot = context.Hotspots.FirstOrDefault(h =>
            h.InclusiveSamples >= minInclusive
            && (h.Frame.Method.Contains("System.Text.RegularExpressions.RegexRunner", StringComparison.Ordinal)
                || h.Frame.Method.Contains("System.Text.RegularExpressions.RegexInterpreter", StringComparison.Ordinal)));
        if (hot is null)
        {
            yield break;
        }

        var inclusivePercent = hot.InclusiveSamples * 100.0 / context.TotalSamples;

        yield return new Finding(
            Pattern: "regex-backtracking",
            Severity: FindingSeverity.High,
            Confidence: 0.8,
            Title:
                "Hot frames are inside the regex engine (RegexRunner / RegexInterpreter) — a classic sign of " +
                "catastrophic backtracking on user-controlled input.",
            Evidence: new[]
            {
                new FindingEvidence(
                    Kind: "frame",
                    Description: $"{hot.Frame.Method} holds {inclusivePercent:0.#}% of inclusive samples.",
                    Handle: context.HandleId,
                    Value: Math.Round(inclusivePercent, 1),
                    Unit: "%"),
            },
            SuggestedFix:
                "Mitigate with a [GeneratedRegex] source-generated regex, a match timeout, and an input-length bound.",
            NextAction: new NextActionHint(
                "query_snapshot",
                "Walk the call tree to find the calling pattern.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "call-tree", ["maxDepth"] = 12, ["maxNodes"] = 200 }));
    }
}
