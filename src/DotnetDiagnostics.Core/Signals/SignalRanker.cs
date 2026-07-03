namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Orders and caps a set of <see cref="SignalGroup"/>s for surfacing. Ranking is a transparent
/// heuristic: most <b>salient</b> first (descending <see cref="SignalGroup.Salience"/>). The surfaced
/// set is capped so the payload stays small (MCP tokens + human legibility); the full raw data always
/// remains behind the drill-down handles the buckets reference.
/// </summary>
public static class SignalRanker
{
    /// <summary>Default cap on the number of surfaced signal groups.</summary>
    public const int DefaultMax = 5;

    /// <summary>
    /// Returns the signal groups ordered by descending salience, capped to <paramref name="max"/>.
    /// Returns an empty list when there is nothing to surface.
    /// </summary>
    public static IReadOnlyList<SignalGroup> Rank(IEnumerable<SignalGroup> signals, int max = DefaultMax)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (max < 1)
        {
            return Array.Empty<SignalGroup>();
        }

        return signals
            .OrderByDescending(s => s.Salience)
            .Take(max)
            .ToArray();
    }
}
