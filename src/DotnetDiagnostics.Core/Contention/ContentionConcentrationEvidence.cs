using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Internal;

namespace DotnetDiagnostics.Core.Contention;

[JsonConverter(typeof(JsonStringEnumConverter<ContentionConcentrationAvailability>))]
public enum ContentionConcentrationAvailability
{
    [JsonStringEnumMemberName("available")]
    Available,

    [JsonStringEnumMemberName("insufficient_observations")]
    InsufficientObservations,

    [JsonStringEnumMemberName("incompatible")]
    Incompatible,

    [JsonStringEnumMemberName("loss_detected")]
    LossDetected,

    [JsonStringEnumMemberName("unsupported_approximation")]
    UnsupportedApproximation,

    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<ContentionConcentrationShape>))]
public enum ContentionConcentrationShape
{
    [JsonStringEnumMemberName("dominant")]
    Dominant,

    [JsonStringEnumMemberName("diffuse")]
    Diffuse,

    [JsonStringEnumMemberName("short_burst")]
    ShortBurst,

    [JsonStringEnumMemberName("truncated_inventory")]
    TruncatedInventory,

    [JsonStringEnumMemberName("bounded_capture_observed_no_contention")]
    BoundedCaptureObservedNoContention,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<ContentionQuantileRepresentation>))]
public enum ContentionQuantileRepresentation
{
    [JsonStringEnumMemberName("exact")]
    Exact,

    [JsonStringEnumMemberName("reservoir_approximate")]
    ReservoirApproximate,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

public sealed record ContentionConcentrationMethod(string Id, int Version);

public sealed record ContentionConcentrationCounts(
    int Captures,
    int ObservedEvents,
    int RetainedEvents,
    int RetentionDroppedEvents,
    int DistinctLocks,
    int RetainedLocks,
    int TotalWindows,
    int ActiveWindows,
    int IndependentCaptureUnits,
    long? DetectedSourceLossEvents);

public sealed record ContentionUnavailableField(string Field, string Reason);

public sealed record ContentionWaitDurationQuantiles(
    ContentionConcentrationAvailability Availability,
    string Unit,
    ContentionQuantileRepresentation Representation,
    double? P50,
    double? P95,
    double? Maximum,
    IReadOnlyList<ContentionUnavailableField> UnavailableFields);

public sealed record ContentionConfidenceInterval(double Lower, double Upper);

public sealed record ContentionOwnerOverlapEvidence(
    ContentionConcentrationAvailability Availability,
    ContentionConcentrationMethod Method,
    string IndependenceUnit,
    int PositiveCaptures,
    int CaptureCount,
    double? Rate,
    double? ConfidenceLevel,
    ContentionConfidenceInterval? Interval,
    IReadOnlyList<ContentionUnavailableField> UnavailableFields,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations);

public sealed record ContentionConcentrationEvidence(
    string Schema,
    ContentionConcentrationAvailability Availability,
    ContentionConcentrationMethod Method,
    string IndependenceUnit,
    ContentionConcentrationCounts Counts,
    double? Top1WaitDurationShare,
    double? Top3WaitDurationShare,
    double? ActiveWindowFraction,
    ContentionConcentrationShape Shape,
    ContentionWaitDurationQuantiles WaitDurationQuantiles,
    ContentionOwnerOverlapEvidence OwnerOverlap,
    IReadOnlyList<ContentionUnavailableField> UnavailableFields,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations)
{
    public const string SchemaV1 = "dotnet-diagnostics/contention-concentration-evidence/v1";
}

public sealed record ContentionOwnerOverlapObservation(
    string IndependenceUnit,
    int PositiveCaptures,
    int CaptureCount,
    double ConfidenceLevel = 0.95,
    long? DetectedLossEvents = null);

public sealed record ContentionConcentrationEvaluationContext(
    double WindowSeconds = 1,
    long? DetectedSourceLossEvents = null,
    ContentionOwnerOverlapObservation? OwnerOverlap = null);

public static class ContentionConcentrationEvaluator
{
    public const string MethodId = "bounded-lock-wait-duration-concentration";
    public const int MethodVersion = 1;
    public const string IndependenceUnit = "capture";
    public const double DominantTop1Threshold = 0.65;
    public const double DominantTop3Threshold = 0.80;
    public const double ShortBurstActiveWindowThreshold = 0.50;

    private const string WilsonMethodId = "wilson-score-rate";
    private const int WilsonMethodVersion = 1;

    public static ContentionConcentrationEvidence Evaluate(
        ContentionSnapshot snapshot,
        ContentionConcentrationEvaluationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        context ??= new ContentionConcentrationEvaluationContext();

        var events = snapshot.Events ?? [];
        var retainedLocks = events
            .Where(static item => item.LockId != 0 || item.AssociatedObjectId != 0)
            .Select(static item => (item.LockId, item.AssociatedObjectId))
            .Distinct()
            .Count();
        var unknownIdentityWaitEvents = events.Count(static item =>
            item.LockId == 0
            && item.AssociatedObjectId == 0
            && item.Duration > TimeSpan.Zero);
        var retentionDropped = Math.Max(0, snapshot.TotalEvents - events.Count);
        var timing = EvaluateTiming(snapshot, events, context.WindowSeconds);
        var counts = new ContentionConcentrationCounts(
            Captures: 1,
            ObservedEvents: snapshot.TotalEvents,
            RetainedEvents: events.Count,
            RetentionDroppedEvents: retentionDropped,
            DistinctLocks: snapshot.DistinctMonitors,
            RetainedLocks: retainedLocks,
            TotalWindows: timing.TotalWindows,
            ActiveWindows: timing.ActiveWindows,
            IndependentCaptureUnits: 1,
            DetectedSourceLossEvents: context.DetectedSourceLossEvents);
        var unavailable = new List<ContentionUnavailableField>();
        var limitations = new List<string>
        {
            "Concentration prioritizes the next contention drilldown; it does not establish cause, severity, regression, starvation, or workload health.",
            "One separately started capture is the independence unit. Events and one-second windows are nested descriptive evidence and do not increase the independent count.",
            "A single capture does not establish concentration stability; repeat compatible captures before treating the observed shape as stable.",
        };
        var assumptions = new[]
        {
            "Retained event durations and lock identities represent the delivered contention population when the retained inventory is complete.",
            "The workload and capture settings are sufficiently stable for the bounded capture to guide the next drilldown.",
        };

        var invalidReason = Validate(snapshot, events, context, timing);
        var availability = ContentionConcentrationAvailability.Available;
        var shape = ContentionConcentrationShape.Unavailable;
        double? top1 = null;
        double? top3 = null;
        double? activeWindowFraction = timing.Fraction;

        if (invalidReason is not null)
        {
            availability = ContentionConcentrationAvailability.Incompatible;
            unavailable.Add(new("top1WaitDurationShare", invalidReason));
            unavailable.Add(new("top3WaitDurationShare", invalidReason));
            limitations.Add($"Concentration is unavailable because {invalidReason.Replace('_', ' ')}.");
        }
        else if (context.DetectedSourceLossEvents is > 0)
        {
            availability = ContentionConcentrationAvailability.LossDetected;
            unavailable.Add(new("top1WaitDurationShare", "source_loss_detected"));
            unavailable.Add(new("top3WaitDurationShare", "source_loss_detected"));
            limitations.Add(
                $"Detected source loss affected {context.DetectedSourceLossEvents.Value} event(s); concentration over the delivered subset is not reported.");
        }
        else if (retentionDropped > 0)
        {
            availability = ContentionConcentrationAvailability.UnsupportedApproximation;
            shape = ContentionConcentrationShape.TruncatedInventory;
            unavailable.Add(new("top1WaitDurationShare", "retained_event_inventory_truncated"));
            unavailable.Add(new("top3WaitDurationShare", "retained_event_inventory_truncated"));
            limitations.Add(
                $"The snapshot retained {events.Count} of {snapshot.TotalEvents} observed event(s); longest-event retention cannot support an unbiased lock concentration share.");
        }
        else if (unknownIdentityWaitEvents > 0)
        {
            availability = ContentionConcentrationAvailability.UnsupportedApproximation;
            unavailable.Add(new("top1WaitDurationShare", "lock_identity_unavailable"));
            unavailable.Add(new("top3WaitDurationShare", "lock_identity_unavailable"));
            limitations.Add(
                $"{unknownIdentityWaitEvents} retained positive-wait event(s) had no lock or associated-object identity; unidentified waits are not combined into a synthetic dominant lock.");
        }
        else if (snapshot.TotalEvents == 0)
        {
            availability = ContentionConcentrationAvailability.InsufficientObservations;
            shape = ContentionConcentrationShape.BoundedCaptureObservedNoContention;
            activeWindowFraction = timing.TotalWindows > 0 ? 0 : null;
            unavailable.Add(new("top1WaitDurationShare", "no_contention_events_observed"));
            unavailable.Add(new("top3WaitDurationShare", "no_contention_events_observed"));
            limitations.Add(
                "No contention events were observed in this bounded capture; this does not establish zero waiting outside the window or workload health.");
        }
        else
        {
            var byLock = events
                .Where(static item => item.LockId != 0 || item.AssociatedObjectId != 0)
                .GroupBy(static item => (item.LockId, item.AssociatedObjectId))
                .Select(static group => group.Sum(item => item.Duration.TotalMilliseconds))
                .OrderByDescending(static duration => duration)
                .ToArray();
            var totalWait = byLock.Sum();
            if (!double.IsFinite(totalWait) || totalWait <= 0)
            {
                availability = ContentionConcentrationAvailability.InsufficientObservations;
                unavailable.Add(new("top1WaitDurationShare", "positive_wait_duration_not_observed"));
                unavailable.Add(new("top3WaitDurationShare", "positive_wait_duration_not_observed"));
                limitations.Add(
                    "Contention events were observed, but their bounded wait-duration denominator was not positive; missing concentration is unavailable, not zero.");
            }
            else
            {
                top1 = byLock[0] / totalWait;
                top3 = byLock.Take(3).Sum() / totalWait;
                shape = activeWindowFraction switch
                {
                    null => ContentionConcentrationShape.Unavailable,
                    <= ShortBurstActiveWindowThreshold => ContentionConcentrationShape.ShortBurst,
                    _ when top1 >= DominantTop1Threshold && top3 >= DominantTop3Threshold
                        => ContentionConcentrationShape.Dominant,
                    _ => ContentionConcentrationShape.Diffuse,
                };
            }
        }

        if (context.DetectedSourceLossEvents is null)
        {
            unavailable.Add(new("counts.detectedSourceLossEvents", "source_loss_not_observable"));
            limitations.Add(
                "Runtime-side EventPipe source loss is not observable from this snapshot; a missing loss count is unavailable, not zero.");
        }

        if (activeWindowFraction is null)
        {
            unavailable.Add(new("activeWindowFraction", timing.UnavailableReason ?? "capture_timing_unavailable"));
        }

        return new ContentionConcentrationEvidence(
            ContentionConcentrationEvidence.SchemaV1,
            availability,
            new ContentionConcentrationMethod(MethodId, MethodVersion),
            IndependenceUnit,
            counts,
            top1,
            top3,
            activeWindowFraction,
            shape,
            EvaluateQuantiles(snapshot, context.DetectedSourceLossEvents),
            EvaluateOwnerOverlap(context.OwnerOverlap),
            unavailable,
            assumptions,
            limitations);
    }

    private static string? Validate(
        ContentionSnapshot snapshot,
        IReadOnlyList<ContentionEventSample> events,
        ContentionConcentrationEvaluationContext context,
        TimingResult timing)
    {
        if (snapshot.TotalEvents < 0
            || snapshot.DistinctMonitors < 0
            || snapshot.Duration <= TimeSpan.Zero
            || events.Count > snapshot.TotalEvents
            || context.DetectedSourceLossEvents is < 0)
        {
            return "invalid_capture_counts";
        }

        if (!double.IsFinite(context.WindowSeconds) || context.WindowSeconds <= 0)
        {
            return "nonfinite_or_nonpositive_window";
        }

        if (timing.UnavailableReason == "window_count_overflow")
        {
            return timing.UnavailableReason;
        }

        foreach (var item in events)
        {
            var milliseconds = item.Duration.TotalMilliseconds;
            if (!double.IsFinite(milliseconds) || milliseconds < 0)
            {
                return "nonfinite_or_negative_wait_duration";
            }
        }

        return null;
    }

    private static TimingResult EvaluateTiming(
        ContentionSnapshot snapshot,
        IReadOnlyList<ContentionEventSample> events,
        double windowSeconds)
    {
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0 || snapshot.Duration <= TimeSpan.Zero)
        {
            return new TimingResult(0, 0, null, "capture_timing_unavailable");
        }

        var rawWindowCount = Math.Ceiling(snapshot.Duration.TotalSeconds / windowSeconds);
        if (!double.IsFinite(rawWindowCount) || rawWindowCount > int.MaxValue)
        {
            return new TimingResult(0, 0, null, "window_count_overflow");
        }

        var totalWindows = Math.Max(1, (int)rawWindowCount);
        var active = new HashSet<int>();
        var outsideCaptureWindow = false;
        var boundaryToleranceSeconds = Math.Min(0.25, windowSeconds / 4);
        var durationSeconds = snapshot.Duration.TotalSeconds;
        foreach (var item in events)
        {
            var offsetSeconds = (item.StoppedAt - snapshot.StartedAt).TotalSeconds;
            if (!double.IsFinite(offsetSeconds)
                || offsetSeconds < -boundaryToleranceSeconds
                || offsetSeconds > durationSeconds + boundaryToleranceSeconds)
            {
                outsideCaptureWindow = true;
                continue;
            }

            var clampedOffsetSeconds = Math.Clamp(offsetSeconds, 0, durationSeconds);
            var index = (int)Math.Floor(clampedOffsetSeconds / windowSeconds);
            if (index >= totalWindows)
            {
                index = totalWindows - 1;
            }

            if (index >= 0 && index < totalWindows)
            {
                active.Add(index);
            }
            else
            {
                outsideCaptureWindow = true;
            }
        }

        if (outsideCaptureWindow)
        {
            return new TimingResult(
                totalWindows,
                active.Count,
                null,
                "event_timestamps_outside_capture_window");
        }

        return new TimingResult(
            totalWindows,
            active.Count,
            active.Count / (double)totalWindows,
            null);
    }

    private static ContentionWaitDurationQuantiles EvaluateQuantiles(
        ContentionSnapshot snapshot,
        long? detectedSourceLossEvents)
    {
        if (snapshot.TotalEvents == 0)
        {
            return UnavailableQuantiles(
                ContentionConcentrationAvailability.InsufficientObservations,
                "no_contention_events_observed");
        }

        if (detectedSourceLossEvents is > 0)
        {
            return UnavailableQuantiles(
                ContentionConcentrationAvailability.LossDetected,
                "source_loss_detected");
        }

        var values = new[]
        {
            snapshot.P50ContentionDuration.TotalMilliseconds,
            snapshot.P95ContentionDuration.TotalMilliseconds,
            snapshot.MaxContentionDuration.TotalMilliseconds,
        };
        if (values.Any(static value => !double.IsFinite(value) || value < 0))
        {
            return UnavailableQuantiles(
                ContentionConcentrationAvailability.Incompatible,
                "nonfinite_or_negative_wait_quantile");
        }

        return new ContentionWaitDurationQuantiles(
            ContentionConcentrationAvailability.Available,
            "milliseconds",
            snapshot.TotalEvents > BoundedPercentileSampler.ExactSampleCapacity
                ? ContentionQuantileRepresentation.ReservoirApproximate
                : ContentionQuantileRepresentation.Exact,
            values[0],
            values[1],
            values[2],
            []);
    }

    private static ContentionWaitDurationQuantiles UnavailableQuantiles(
        ContentionConcentrationAvailability availability,
        string reason)
        => new(
            availability,
            "milliseconds",
            ContentionQuantileRepresentation.Unavailable,
            null,
            null,
            null,
            [
                new("waitDurationQuantiles.p50", reason),
                new("waitDurationQuantiles.p95", reason),
                new("waitDurationQuantiles.maximum", reason),
            ]);

    private static ContentionOwnerOverlapEvidence EvaluateOwnerOverlap(
        ContentionOwnerOverlapObservation? observation)
    {
        var method = new ContentionConcentrationMethod(WilsonMethodId, WilsonMethodVersion);
        const string capture = IndependenceUnit;
        if (observation is null)
        {
            return new(
                ContentionConcentrationAvailability.NotApplicable,
                method,
                capture,
                0,
                0,
                null,
                null,
                null,
                [new("ownerOverlap", "independent_capture_observation_not_provided")],
                ["Wilson owner-overlap intervals require one binary observation per separately started capture."],
                ["Contention events, owners attached to individual events, and nested time windows are not independent Wilson trials."]);
        }

        if (!string.Equals(observation.IndependenceUnit, capture, StringComparison.Ordinal)
            || observation.CaptureCount < 0
            || observation.PositiveCaptures < 0
            || observation.PositiveCaptures > observation.CaptureCount
            || observation.DetectedLossEvents is < 0
            || !double.IsFinite(observation.ConfidenceLevel)
            || observation.ConfidenceLevel <= 0
            || observation.ConfidenceLevel >= 1)
        {
            return new(
                ContentionConcentrationAvailability.Incompatible,
                method,
                observation.IndependenceUnit,
                observation.PositiveCaptures,
                observation.CaptureCount,
                null,
                null,
                null,
                [new("ownerOverlap", "invalid_or_nonindependent_owner_observation")],
                ["Wilson owner-overlap intervals require one binary observation per separately started capture."],
                ["The supplied owner-overlap observation was not a valid capture-level binomial population."]);
        }

        if (observation.DetectedLossEvents is > 0)
        {
            return new(
                ContentionConcentrationAvailability.LossDetected,
                method,
                capture,
                observation.PositiveCaptures,
                observation.CaptureCount,
                null,
                null,
                null,
                [new("ownerOverlap", "owner_observation_loss_detected")],
                ["Wilson owner-overlap intervals require one binary observation per separately started capture."],
                [$"Detected loss affected {observation.DetectedLossEvents.Value} owner-observation event(s)."]);
        }

        if (observation.CaptureCount == 0)
        {
            return new(
                ContentionConcentrationAvailability.InsufficientObservations,
                method,
                capture,
                0,
                0,
                null,
                null,
                null,
                [new("ownerOverlap", "minimum_independent_captures_not_met")],
                ["Wilson owner-overlap intervals require one binary observation per separately started capture."],
                ["No independent capture-level owner observations were supplied."]);
        }

        var rate = observation.PositiveCaptures / (double)observation.CaptureCount;
        var interval = Wilson(
            observation.PositiveCaptures,
            observation.CaptureCount,
            observation.ConfidenceLevel);
        return new(
            ContentionConcentrationAvailability.Available,
            method,
            capture,
            observation.PositiveCaptures,
            observation.CaptureCount,
            rate,
            observation.ConfidenceLevel,
            interval,
            [],
            [
                "Each separately started capture contributes at most one binary owner-overlap observation.",
                "The Wilson interval describes capture-level binomial uncertainty and does not establish a causal owner relationship.",
            ],
            []);
    }

    private static ContentionConfidenceInterval Wilson(int positive, int count, double confidenceLevel)
    {
        var z = confidenceLevel switch
        {
            0.90 => 1.6448536269514722,
            0.95 => 1.959963984540054,
            0.99 => 2.5758293035489004,
            _ => InverseStandardNormal(0.5 + confidenceLevel / 2),
        };
        var proportion = positive / (double)count;
        var zSquared = z * z;
        var denominator = 1 + zSquared / count;
        var center = (proportion + zSquared / (2 * count)) / denominator;
        var margin = z / denominator
            * Math.Sqrt(proportion * (1 - proportion) / count + zSquared / (4d * count * count));
        return new(
            Math.Max(0, center - margin),
            Math.Min(1, center + margin));
    }

    private static double InverseStandardNormal(double probability)
    {
        double[] a =
        [
            -3.969683028665376e+01,
            2.209460984245205e+02,
            -2.759285104469687e+02,
            1.383577518672690e+02,
            -3.066479806614716e+01,
            2.506628277459239e+00,
        ];
        double[] b =
        [
            -5.447609879822406e+01,
            1.615858368580409e+02,
            -1.556989798598866e+02,
            6.680131188771972e+01,
            -1.328068155288572e+01,
        ];
        double[] c =
        [
            -7.784894002430293e-03,
            -3.223964580411365e-01,
            -2.400758277161838e+00,
            -2.549732539343734e+00,
            4.374664141464968e+00,
            2.938163982698783e+00,
        ];
        double[] d =
        [
            7.784695709041462e-03,
            3.224671290700398e-01,
            2.445134137142996e+00,
            3.754408661907416e+00,
        ];
        const double lower = 0.02425;
        const double upper = 1 - lower;

        if (probability < lower)
        {
            var q = Math.Sqrt(-2 * Math.Log(probability));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        if (probability <= upper)
        {
            var q = probability - 0.5;
            var r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q
                / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }

        var tail = Math.Sqrt(-2 * Math.Log(1 - probability));
        return -(((((c[0] * tail + c[1]) * tail + c[2]) * tail + c[3]) * tail + c[4]) * tail + c[5])
            / ((((d[0] * tail + d[1]) * tail + d[2]) * tail + d[3]) * tail + 1);
    }

    private sealed record TimingResult(
        int TotalWindows,
        int ActiveWindows,
        double? Fraction,
        string? UnavailableReason);
}
