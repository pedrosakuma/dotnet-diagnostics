namespace DotnetDiagnostics.Core.Findings;

/// <summary>
/// Orders and caps a set of <see cref="Finding"/>s for surfacing. Ranking is a transparent
/// heuristic: primary key is <see cref="FindingSeverity"/> (most serious first), tie-broken by
/// descending <see cref="Finding.Confidence"/>. The surfaced set is capped so the payload stays
/// small (MCP tokens + human legibility); the full raw data always remains behind the drill-down
/// handles the findings reference.
/// </summary>
public static class FindingRanker
{
    /// <summary>Default cap on the number of surfaced findings.</summary>
    public const int DefaultMax = 5;

    /// <summary>
    /// Returns the findings ordered by (severity, confidence) descending, capped to
    /// <paramref name="max"/>. Returns an empty list when there is nothing to surface.
    /// </summary>
    public static IReadOnlyList<Finding> Rank(IEnumerable<Finding> findings, int max = DefaultMax)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (max < 1)
        {
            return Array.Empty<Finding>();
        }

        return findings
            .OrderBy(f => (int)f.Severity)          // Critical (0) first
            .ThenByDescending(f => f.Confidence)
            .Take(max)
            .ToArray();
    }
}
