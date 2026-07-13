using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Gc;

namespace DotnetDiagnostics.Core.Collection;

/// <summary>
/// Correlates GC pause events with ActivitySource spans to identify spans impacted by garbage collection.
/// </summary>
public static class GcActivityCorrelator
{
    /// <summary>
    /// Correlates activities with GC events, returning spans that overlapped with GC pauses.
    /// </summary>
    public static GcOverlayResult Correlate(ActivityCapture activities, GcSummary gcSummary, int topN)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(gcSummary);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        var sortedGcEvents = gcSummary.Events
            .OrderBy(static gc => gc.Timestamp)
            .ToArray();
        var maxPauseDuration = gcSummary.MaxPauseTime > TimeSpan.Zero
            ? gcSummary.MaxPauseTime
            : TimeSpan.Zero;
        var topImpacted = new PriorityQueue<ImpactedActivity, ImpactedActivity>(
            Comparer<ImpactedActivity>.Create(static (left, right) => CompareImpactedAscending(left, right)));
        var impactedCount = 0;
        var totalGcOverlapMs = 0.0;

        foreach (var activity in activities.Activities)
        {
            if (!activity.StoppedAt.HasValue) continue; // Skip incomplete spans

            var activityStart = activity.StartedAt;
            var activityEnd = activity.StoppedAt.Value;

            var overlappingGcEvents = new List<GcOverlapEvent>();
            var totalOverlapMs = 0.0;
            var windowStart = activityStart - maxPauseDuration;
            var lowerBound = LowerBound(sortedGcEvents, windowStart);
            var upperBound = LowerBound(sortedGcEvents, activityEnd);

            for (var gcIndex = lowerBound; gcIndex < upperBound; gcIndex++)
            {
                var gc = sortedGcEvents[gcIndex];
                var gcStart = gc.Timestamp;
                var gcEnd = gc.Timestamp + gc.PauseDuration;

                // Check for overlap: [activityStart, activityEnd] ∩ [gcStart, gcEnd]
                if (gcStart < activityEnd && gcEnd > activityStart)
                {
                    // Calculate overlap duration
                    var overlapStart = gcStart > activityStart ? gcStart : activityStart;
                    var overlapEnd = gcEnd < activityEnd ? gcEnd : activityEnd;
                    var overlapMs = (overlapEnd - overlapStart).TotalMilliseconds;

                    if (overlapMs > 0)
                    {
                        totalOverlapMs += overlapMs;
                        overlappingGcEvents.Add(new GcOverlapEvent(
                            gc.Generation,
                            gc.Reason,
                            gc.Type,
                            gc.PauseDuration.TotalMilliseconds,
                            overlapMs));
                    }
                }
            }

            if (overlappingGcEvents.Count > 0 && activity.Duration.HasValue)
            {
                var durationMs = activity.Duration.Value.TotalMilliseconds;
                var gcPausePercent = durationMs > 0 ? (totalOverlapMs / durationMs) * 100 : 0;
                var impactedActivity = new ImpactedActivity(
                    activity.SourceName,
                    activity.OperationName,
                    activity.Id,
                    activity.TraceId,
                    activity.SpanId,
                    durationMs,
                    totalOverlapMs,
                    gcPausePercent,
                    overlappingGcEvents);
                impactedCount++;
                totalGcOverlapMs += impactedActivity.GcPauseMs;
                topImpacted.Enqueue(impactedActivity, impactedActivity);
                if (topImpacted.Count > topN)
                {
                    topImpacted.Dequeue();
                }
            }
        }

        var orderedTopImpacted = topImpacted.UnorderedItems
            .Select(static item => item.Element)
            .OrderByDescending(static item => item.GcPausePercent)
            .ThenByDescending(static item => item.GcPauseMs)
            .ThenBy(static item => item.SourceName, StringComparer.Ordinal)
            .ThenBy(static item => item.OperationName, StringComparer.Ordinal)
            .ThenBy(static item => item.ActivityId, StringComparer.Ordinal)
            .ToList();

        return new GcOverlayResult(
            activities.TotalActivities,
            activities.CompletedActivities,
            impactedCount,
            orderedTopImpacted.Count,
            totalGcOverlapMs,
            gcSummary.TotalCollections,
            gcSummary.TotalPauseTime.TotalMilliseconds,
            orderedTopImpacted);
    }

    private static int LowerBound(GcEvent[] events, DateTimeOffset timestamp)
    {
        var low = 0;
        var high = events.Length;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (events[mid].Timestamp < timestamp)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static int CompareImpactedAscending(ImpactedActivity left, ImpactedActivity right)
    {
        var byPercent = left.GcPausePercent.CompareTo(right.GcPausePercent);
        if (byPercent != 0)
        {
            return byPercent;
        }

        var byPause = left.GcPauseMs.CompareTo(right.GcPauseMs);
        if (byPause != 0)
        {
            return byPause;
        }

        var bySource = string.CompareOrdinal(right.SourceName, left.SourceName);
        if (bySource != 0)
        {
            return bySource;
        }

        var byOperation = string.CompareOrdinal(right.OperationName, left.OperationName);
        if (byOperation != 0)
        {
            return byOperation;
        }

        return string.CompareOrdinal(right.ActivityId, left.ActivityId);
    }
}

/// <summary>A GC event that overlapped with a span.</summary>
public sealed record GcOverlapEvent(
    int Generation,
    string Reason,
    string Type,
    double PauseDurationMs,
    double OverlapMs);

/// <summary>An activity span that was impacted by GC pauses.</summary>
public sealed record ImpactedActivity(
    string SourceName,
    string OperationName,
    string ActivityId,
    string? TraceId,
    string? SpanId,
    double DurationMs,
    double GcPauseMs,
    double GcPausePercent,
    IReadOnlyList<GcOverlapEvent> GcEvents);

/// <summary>Result of correlating GC events with activity spans.</summary>
public sealed record GcOverlayResult(
    int TotalActivities,
    int CompletedActivities,
    int ImpactedCount,
    int ReturnedCount,
    double TotalGcOverlapMs,
    int TotalGcCollections,
    double TotalGcPauseMs,
    IReadOnlyList<ImpactedActivity> ImpactedActivities);
