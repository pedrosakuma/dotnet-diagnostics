using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Gc;

namespace DotnetDiagnostics.Core.Collection;

/// <summary>
/// Correlates GC pause events with ActivitySource spans to identify spans impacted by garbage collection.
/// </summary>
public static class GcActivityCorrelator
{
    internal static string? Validate(ActivityCapture activity, GcSummary gc)
    {
        if (activity.ProcessId != gc.ProcessId) return "Activity and GC process IDs differ.";
        if (activity.ProcessStartedAt is { } a && gc.Suspension?.ProcessStartedAt is { } g && a != g)
            return "Activity and GC process lifetimes differ.";
        if (activity.Duration <= TimeSpan.Zero || gc.Duration <= TimeSpan.Zero ||
            activity.Duration > DateTimeOffset.MaxValue - activity.StartedAt ||
            gc.Duration > DateTimeOffset.MaxValue - gc.StartedAt)
            return "Invalid observation window.";
        var start = gc.Suspension?.ObservationStart ?? gc.StartedAt;
        var end = gc.Suspension?.ObservationEnd ?? gc.StartedAt + gc.Duration;
        if (end <= start || activity.StartedAt >= end || start >= activity.StartedAt + activity.Duration)
            return "Activity and GC observation windows do not overlap.";
        if (gc.Suspension?.Intervals.Any(p => p.StoppedAt < p.StartedAt || p.Reason is not (1 or 6)) == true)
            return "Invalid GC suspension interval evidence.";
        return null;
    }
    /// <summary>
    /// Correlates activities with GC events, returning spans that overlapped with GC pauses.
    /// </summary>
    public static GcOverlayResult Correlate(ActivityCapture activities, GcSummary gcSummary, int topN)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(gcSummary);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        if (Validate(activities, gcSummary) is { } invalid)
            throw new ArgumentException(invalid, nameof(gcSummary));
        var evidence = gcSummary.Suspension;
        var sortedGcEvents = (evidence is { IsAuthoritative: true } ? evidence.Intervals : [])
            .OrderBy(static gc => gc.StartedAt)
            .ToArray();
        var maxPauseDuration = sortedGcEvents.Length > 0 ? sortedGcEvents.Max(p => p.Duration) : TimeSpan.Zero;
        var activityLoss = activities.Retention?.DroppedMatchingActivities;
        var pauseLoss = evidence?.DroppedIntervals ?? 0;
        var projectedPauseLoss = evidence is null ? 0 : Math.Max(evidence.OutputOmittedIntervals,
            (int)Math.Min(int.MaxValue, Math.Max(0, evidence.ObservedIntervals - evidence.DroppedIntervals - evidence.Intervals.Count)));
        var correlationTruncated = activityLoss > 0 || pauseLoss > 0;
        var windowStart = evidence?.ObservationStart ?? gcSummary.StartedAt;
        var windowEnd = evidence?.ObservationEnd ?? gcSummary.StartedAt + gcSummary.Duration;
        var activityWindowEnd = activities.StartedAt + activities.Duration;
        windowStart = windowStart > activities.StartedAt ? windowStart : activities.StartedAt;
        windowEnd = windowEnd < activityWindowEnd ? windowEnd : activityWindowEnd;
        var invalidSpans = 0;
        var windowGaps = 0;
        var status = evidence?.Status ?? "legacy-unknown";
        if (windowEnd <= windowStart)
            throw new ArgumentException("Activity and GC observation windows do not overlap.", nameof(gcSummary));
        var topImpacted = new PriorityQueue<ImpactedActivity, ImpactedActivity>(
            Comparer<ImpactedActivity>.Create(static (left, right) => CompareImpactedAscending(left, right)));
        var impactedCount = 0;
        var totalGcOverlapMs = 0.0;

        foreach (var activity in activities.Activities)
        {
            if (!activity.StoppedAt.HasValue) continue; // Skip incomplete spans

            var activityStart = activity.StartedAt;
            var activityEnd = activity.StoppedAt.Value;
            if (activityEnd < activityStart || activity.Duration != activityEnd - activityStart)
            {
                invalidSpans++;
                continue;
            }
            if (activityEnd == activityStart) { invalidSpans++; continue; }
            var hasGap = activityStart < windowStart || activityEnd > windowEnd;
            if (hasGap) windowGaps++;
            var clipStart = activityStart > windowStart ? activityStart : windowStart;
            var clipEnd = activityEnd < windowEnd ? activityEnd : windowEnd;

            var overlappingGcEvents = new List<GcOverlapEvent>();
            var intervals = new List<(DateTimeOffset Start, DateTimeOffset Stop)>();
            var lowerBound = LowerBound(sortedGcEvents, activityStart > DateTimeOffset.MinValue + maxPauseDuration
                ? activityStart - maxPauseDuration : DateTimeOffset.MinValue);
            var upperBound = LowerBound(sortedGcEvents, activityEnd);

            for (var gcIndex = lowerBound; gcIndex < upperBound; gcIndex++)
            {
                var gc = sortedGcEvents[gcIndex];
                var gcStart = gc.StartedAt;
                var gcEnd = gc.StoppedAt;

                // Check for overlap: [activityStart, activityEnd] ∩ [gcStart, gcEnd]
                if (gcStart < activityEnd && gcEnd > activityStart)
                {
                    // Calculate overlap duration
                    var overlapStart = gcStart > clipStart ? gcStart : clipStart;
                    var overlapEnd = gcEnd < clipEnd ? gcEnd : clipEnd;
                    var overlapMs = (overlapEnd - overlapStart).TotalMilliseconds;

                    if (overlapMs > 0)
                    {
                        intervals.Add((overlapStart, overlapEnd));
                        if (overlappingGcEvents.Count < 100)
                            overlappingGcEvents.Add(new GcOverlapEvent(
                                null, gc.Reason.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                "runtime-suspension", gc.Duration.TotalMilliseconds, overlapMs));
                    }
                }
            }

            if (overlappingGcEvents.Count > 0 && activity.Duration.HasValue)
            {
                var durationMs = activity.Duration.Value.TotalMilliseconds;
                var totalOverlapMs = TimeSpan.FromTicks(UtcIntervalUnion.Measure(clipStart, clipEnd, intervals).CoveredTicks).TotalMilliseconds;
                var gcPausePercent = (totalOverlapMs / durationMs) * 100;
                var impactedActivity = new ImpactedActivity(
                    activity.SourceName,
                    activity.OperationName,
                    activity.Id,
                    activity.TraceId,
                    activity.SpanId,
                    durationMs,
                    totalOverlapMs,
                    gcPausePercent,
                    overlappingGcEvents,
                    GcPauseIsLowerBound: pauseLoss > 0 || projectedPauseLoss > 0 || hasGap,
                    OutputOmittedPauseDetails: Math.Max(0, intervals.Count - overlappingGcEvents.Count));
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
            evidence is { IsAuthoritative: true } ? totalGcOverlapMs : null,
            gcSummary.TotalCollections,
            evidence is { IsAuthoritative: true } ? evidence.TotalSuspensionTime?.TotalMilliseconds : null,
            evidence?.Intervals.Count ?? 0,
            (int)Math.Min(int.MaxValue, pauseLoss),
            correlationTruncated,
            status != "no-detected-loss" ? status
                : activityLoss is null ? "unknown-activity-retention"
                : projectedPauseLoss > 0 ? "projected-pause-details"
                : correlationTruncated ? "retained-prefix"
                : windowGaps > 0 ? "window-intersection" : "full-window",
            (correlationTruncated || projectedPauseLoss > 0) && evidence is { IsAuthoritative: true },
            orderedTopImpacted,
            status,
            activityLoss.HasValue ? activityLoss == 0 ? "complete-retained-selection" : "incomplete" : "unknown",
            activities.ProcessStartedAt.HasValue && evidence?.ProcessStartedAt is not null ? "matched" : "unknown",
            invalidSpans,
            windowGaps,
            Math.Max(0, impactedCount - orderedTopImpacted.Count),
            projectedPauseLoss);
    }

    private static int LowerBound(GcSuspensionInterval[] events, DateTimeOffset timestamp)
    {
        var low = 0;
        var high = events.Length;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (events[mid].StartedAt < timestamp)
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
    int? Generation,
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
    IReadOnlyList<GcOverlapEvent> GcEvents,
    bool GcPauseIsLowerBound,
    int OutputOmittedPauseDetails = 0);

/// <summary>
/// Result of correlating authoritative retained suspension intervals with activity spans.
/// Aggregate measurements remain separate from detail-scoped correlation values.
/// </summary>
/// <remarks>RetainedGcEvents counts raw suspension rows, including unreliable evidence; not the number used for correlation.</remarks>
public sealed record GcOverlayResult(
    int TotalActivities,
    int CompletedActivities,
    int ImpactedCount,
    int ReturnedCount,
    double? TotalGcOverlapMs,
    int TotalGcCollections,
    double? TotalGcPauseMs,
    int RetainedGcEvents,
    int DroppedGcEvents,
    bool CorrelationTruncated,
    string CorrelationScope,
    bool CorrelationValuesAreLowerBounds,
    IReadOnlyList<ImpactedActivity> ImpactedActivities,
    string MeasurementStatus = "legacy-unknown",
    string CandidateSelection = "unknown",
    string LifetimeCompatibility = "unknown",
    int InvalidOrZeroDurationSpans = 0,
    int WindowGapSpans = 0,
    int OutputOmittedActivities = 0,
    int InputOmittedPauseIntervals = 0);
