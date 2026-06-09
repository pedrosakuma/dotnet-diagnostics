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

        var impacted = new List<ImpactedActivity>();

        foreach (var activity in activities.Activities)
        {
            if (!activity.StoppedAt.HasValue) continue; // Skip incomplete spans

            var activityStart = activity.StartedAt;
            var activityEnd = activity.StoppedAt.Value;

            var overlappingGcEvents = new List<GcOverlapEvent>();
            var totalOverlapMs = 0.0;

            foreach (var gc in gcSummary.Events)
            {
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

                impacted.Add(new ImpactedActivity(
                    activity.SourceName,
                    activity.OperationName,
                    activity.Id,
                    activity.TraceId,
                    activity.SpanId,
                    durationMs,
                    totalOverlapMs,
                    gcPausePercent,
                    overlappingGcEvents));
            }
        }

        // Sort by GC impact (highest pause percent first) and take topN
        var topImpacted = impacted
            .OrderByDescending(i => i.GcPausePercent)
            .Take(topN)
            .ToList();

        var totalGcOverlapMs = impacted.Sum(i => i.GcPauseMs);

        return new GcOverlayResult(
            activities.TotalActivities,
            activities.CompletedActivities,
            impacted.Count,
            topImpacted.Count,
            totalGcOverlapMs,
            gcSummary.TotalCollections,
            gcSummary.TotalPauseTime.TotalMilliseconds,
            topImpacted);
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
