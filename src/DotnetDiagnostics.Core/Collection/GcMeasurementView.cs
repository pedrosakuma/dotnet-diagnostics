using DotnetDiagnostics.Core.Gc;

namespace DotnetDiagnostics.Core.Collection;

/// <summary>Versioned query projection: collection elapsed is never presented as a measured pause.</summary>
/// <remarks>RetainedPauseIntervals counts raw retained rows, including unreliable evidence; not the number used or returned.</remarks>
public sealed record GcMeasurementView(
    int MeasurementVersion,
    string MeasurementStatus,
    int TotalCollections,
    TimeSpan CollectionElapsedTime,
    int RetainedCollections,
    int DroppedCollections,
    int RetainedPauseIntervals,
    GcSuspensionEvidence? Evidence,
    object Data);

public sealed record GcCollectionTimelineEntry(int Index, DateTimeOffset Timestamp, int Generation,
    string Type, string Reason, TimeSpan CollectionElapsedDuration, TimeSpan GapSincePreviousStart);

public sealed record GcGenerationElapsedStats(string Bucket, int Count, TimeSpan TotalElapsed,
    TimeSpan MeanElapsed, TimeSpan MaxElapsed)
{
    public string SuspensionAttribution { get; init; } = "unassociated";
}

internal static class GcMeasurementProjection
{
    internal static GcMeasurementView Render(GcSummary g, string view, int topN, object? heapStats = null)
    {
        var evidence = g.Suspension;
        var pauses = evidence is { IsAuthoritative: true } ? evidence.Intervals : [];
        var returnedIntervals = view.Equals("longestPauses", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(topN, pauses.Count) : 0;
        object data = view.ToLowerInvariant() switch
        {
            "events" or "timeline" => Timeline(g, topN),
            "longestpauses" => pauses.OrderByDescending(p => p.Duration).Take(topN).ToArray(),
            "pausehistogram" => evidence is not { IsAuthoritative: true } ? Array.Empty<GcPauseBucket>()
                : new[] { 1, 10, 100, 1000, int.MaxValue }.Select((bound, i) =>
                new GcPauseBucket($"<{bound}ms", bound, pauses.Count(p => p.Duration.TotalMilliseconds < bound &&
                    p.Duration.TotalMilliseconds >= (i == 0 ? 0 : Math.Pow(10, i - 1))))).ToArray(),
            "bygeneration" => g.Events.GroupBy(e => e.Type == "BackgroundGC" ? "background" : $"gen{e.Generation}")
                .Select(group => new GcGenerationElapsedStats(group.Key, group.Count(),
                    TimeSpan.FromTicks(group.Sum(e => e.CollectionElapsedDuration.Ticks)),
                    TimeSpan.FromTicks(group.Sum(e => e.CollectionElapsedDuration.Ticks) / group.Count()),
                    group.Max(e => e.CollectionElapsedDuration)))
                .OrderBy(e => e.Bucket == "background" ? "z" : e.Bucket, StringComparer.Ordinal).ToArray(),
            "heap-stats" or "heapstats" => heapStats!,
            _ => new { g.Generations, g.CollectionElapsedTime, LegacyPauseFields = "collection-elapsed-not-suspension" },
        };
        return new(2, g.PauseMeasurementStatus, g.TotalCollections, g.CollectionElapsedTime,
            g.Events.Count, g.DroppedEvents, evidence?.Intervals.Count ?? 0,
            evidence is null ? null : evidence with
            {
                Intervals = [],
                OutputOmittedIntervals = (int)Math.Min(int.MaxValue,
                    (long)evidence.OutputOmittedIntervals + evidence.Intervals.Count - returnedIntervals),
            }, data);
    }

    private static GcCollectionTimelineEntry[] Timeline(GcSummary g, int topN)
    {
        var events = g.Events.OrderBy(e => e.Timestamp).Take(topN).ToArray();
        return events.Select((e, i) => new GcCollectionTimelineEntry(i, e.Timestamp, e.Generation, e.Type,
            e.Reason, e.CollectionElapsedDuration, i == 0 ? TimeSpan.Zero : e.Timestamp - events[i - 1].Timestamp)).ToArray();
    }
}
