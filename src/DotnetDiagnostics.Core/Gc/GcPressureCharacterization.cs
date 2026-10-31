using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Gc;

public static class GcPressureCharacterizationBounds
{
    public const int MaximumWindows = 8;
    public const int MinimumValidWindows = 5;
    public const int MinimumCooldownWindows = 2;
    public const int MaximumLimitations = 32;
}

[JsonConverter(typeof(JsonStringEnumConverter<GcPressureWindowPhase>))]
public enum GcPressureWindowPhase
{
    [JsonStringEnumMemberName("baseline")]
    Baseline,

    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("cooldown")]
    Cooldown,
}

[JsonConverter(typeof(JsonStringEnumConverter<GcPressureScenarioKind>))]
public enum GcPressureScenarioKind
{
    [JsonStringEnumMemberName("stable-healthy")]
    StableHealthy,

    [JsonStringEnumMemberName("transient-allocation-burst")]
    TransientAllocationBurst,

    [JsonStringEnumMemberName("sustained-allocation-pressure")]
    SustainedAllocationPressure,

    [JsonStringEnumMemberName("retained-growth")]
    RetainedGrowth,

    [JsonStringEnumMemberName("capture-loss")]
    CaptureLoss,
}

[JsonConverter(typeof(JsonStringEnumConverter<GcPressureRepresentation>))]
public enum GcPressureRepresentation
{
    [JsonStringEnumMemberName("exact")]
    Exact,

    [JsonStringEnumMemberName("reservoir_sampled")]
    ReservoirSampled,

    [JsonStringEnumMemberName("projected")]
    Projected,

    [JsonStringEnumMemberName("truncated")]
    Truncated,
}

[JsonConverter(typeof(JsonStringEnumConverter<GcPressureAvailability>))]
public enum GcPressureAvailability
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
}

[JsonConverter(typeof(JsonStringEnumConverter<GcPressureNextAction>))]
public enum GcPressureNextAction
{
    [JsonStringEnumMemberName("allocation-investigation")]
    AllocationInvestigation,

    [JsonStringEnumMemberName("retained-heap-investigation")]
    RetainedHeapInvestigation,

    [JsonStringEnumMemberName("continued-observation")]
    ContinuedObservation,

    [JsonStringEnumMemberName("justified-inconclusion")]
    JustifiedInconclusion,
}

public sealed record GcPressureWindow(
    int Index,
    GcPressureWindowPhase Phase,
    double LohSizeBytes,
    double Gen2CollectionsPerSecond,
    double PauseP95Milliseconds,
    double TimeInGcPercent,
    long SourceEvents,
    long LostOrDropped,
    GcPressureRepresentation Representation);

public sealed record GcPressureCapture(
    int SchemaVersion,
    string Id,
    GcPressureScenarioKind Scenario,
    string RuntimeVersion,
    string OperatingSystem,
    string Architecture,
    double WindowSeconds,
    IReadOnlyList<GcPressureWindow> Windows,
    IReadOnlyList<string> Notes);

public sealed record GcPressureCaptureIdentity(
    string CaptureId,
    string Scenario,
    string RuntimeVersion,
    string OperatingSystem,
    string Architecture,
    string Backend,
    double WindowSeconds,
    string Windowing,
    string IndependenceUnit);

public sealed record GcPressureStatisticCounts(
    int Windows,
    int IndependenceUnits,
    long SourceEvents,
    long LostOrDropped);

public sealed record GcPressureStatistic(
    GcPressureAvailability Availability,
    string? UnavailableReason,
    string MethodId,
    int MethodVersion,
    string IndependenceUnit,
    GcPressureStatisticCounts Counts,
    double? Estimate,
    double? Slope);

public sealed record GcPressureMetricEvidence(
    string QuantityId,
    string Unit,
    GcPressureStatistic Median,
    double? P95,
    GcPressureStatistic Slope);

public sealed record GcPressurePersistenceEvidence(
    int PressureWindows,
    int AllocationPressureWindows,
    int LongestAllocationPressureRun);

public sealed record GcPressureTimingObservation(
    string FirstSignal,
    int FirstWindow,
    string SecondSignal,
    int SecondWindow,
    string Interpretation);

public sealed record GcPressureCharacterizationResult(
    int SchemaVersion,
    int ProtocolVersion,
    GcPressureCaptureIdentity Identity,
    GcPressureNextAction NextAction,
    string Reason,
    GcPressurePersistenceEvidence? Persistence,
    bool? PostLoadStabilized,
    GcPressureMetricEvidence LohSize,
    GcPressureMetricEvidence Gen2Rate,
    GcPressureMetricEvidence PauseDuration,
    GcPressureMetricEvidence SuspensionTime,
    IReadOnlyList<GcPressureTimingObservation> TimingObservations,
    IReadOnlyList<string> Limitations);

public static class GcPressureCharacterizer
{
    public const int CurrentCaptureSchemaVersion = 1;
    public const int CurrentResultSchemaVersion = 1;
    public const int CurrentProtocolVersion = 1;

    private const double LohPressureDeltaBytes = 16 * 1024 * 1024;
    private const double RetainedLohSlopeBytesPerWindow = 1024 * 1024;
    private const double Gen2PressureRate = 0.5;
    private const double PausePressureMilliseconds = 2;
    private const double SuspensionPressurePercent = 5;
    private const double StabilizedLohDeltaBytes = 8 * 1024 * 1024;
    private const int PersistentAllocationWindows = 3;

    public static JsonSerializerOptions CreateJsonOptions(bool writeIndented = true)
        => new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = writeIndented,
        };

    public static GcPressureCharacterizationResult Evaluate(GcPressureCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ValidateStructuralBounds(capture);
        var identity = Identity(capture);

        if (capture.SchemaVersion != CurrentCaptureSchemaVersion)
        {
            return IncompatibleLegacy(identity, capture.SchemaVersion);
        }

        var availability = DetermineAvailability(capture);
        var loh = EvaluateMetric(capture, "gc.loh-size", "bytes", window => window.LohSizeBytes, availability);
        var gen2 = EvaluateMetric(
            capture,
            "gc.gen2-collections-rate",
            "collections/s",
            window => window.Gen2CollectionsPerSecond,
            availability);
        var pause = EvaluateMetric(
            capture,
            "gc.pause-duration-p95",
            "ms",
            window => window.PauseP95Milliseconds,
            availability);
        var suspension = EvaluateMetric(
            capture,
            "gc.suspension-time",
            "%",
            window => window.TimeInGcPercent,
            availability);

        var limitations = BaseLimitations(capture);
        if (availability.Status != GcPressureAvailability.Available)
        {
            limitations.Add(
                $"Required statistics are unavailable: {availability.Status} ({availability.Reason}).");
            return Result(
                identity,
                GcPressureNextAction.JustifiedInconclusion,
                "Required bounded-window evidence is unavailable; no investigation route can be selected.",
                null,
                null,
                loh,
                gen2,
                pause,
                suspension,
                [],
                limitations);
        }

        var baseline = capture.Windows.Single(window => window.Phase == GcPressureWindowPhase.Baseline);
        var ordered = capture.Windows.OrderBy(window => window.Index).ToArray();
        var pressureWindows = ordered.Count(window => IsPressureWindow(window, baseline));
        var allocationPressureWindows = ordered.Count(IsAllocationPressureWindow);
        var longestAllocationPressureRun = LongestRun(ordered, IsAllocationPressureWindow);
        var persistence = new GcPressurePersistenceEvidence(
            pressureWindows,
            allocationPressureWindows,
            longestAllocationPressureRun);
        var postLoadStabilized = PostLoadStabilized(ordered, baseline);
        var timing = TimingObservations(ordered, baseline);
        var lohSlope = loh.Slope.Slope!.Value;

        if (longestAllocationPressureRun >= PersistentAllocationWindows)
        {
            return Result(
                identity,
                GcPressureNextAction.AllocationInvestigation,
                "Allocation-pressure signals persist across at least three consecutive bounded windows; prioritize allocation investigation even when a later LOH maximum remains after churn.",
                persistence,
                postLoadStabilized,
                loh,
                gen2,
                pause,
                suspension,
                timing,
                limitations);
        }

        if (lohSlope >= RetainedLohSlopeBytesPerWindow && postLoadStabilized == false)
        {
            return Result(
                identity,
                GcPressureNextAction.RetainedHeapInvestigation,
                "LOH size has a persistent positive robust slope and does not recover during the bounded cooldown; inspect retained heap graph/root evidence before drawing a retention conclusion.",
                persistence,
                postLoadStabilized,
                loh,
                gen2,
                pause,
                suspension,
                timing,
                limitations);
        }

        if (pressureWindows <= 2 && postLoadStabilized != false)
        {
            return Result(
                identity,
                GcPressureNextAction.ContinuedObservation,
                pressureWindows == 0
                    ? "The bounded windows remain stable without objective GC/LOH pressure."
                    : "The bounded pressure is transient and returns to the baseline envelope during cooldown.",
                persistence,
                postLoadStabilized,
                loh,
                gen2,
                pause,
                suspension,
                timing,
                limitations);
        }

        return Result(
            identity,
            GcPressureNextAction.JustifiedInconclusion,
            "The available signals do not satisfy the predeclared allocation-pressure, retained-growth, or recovered-transient geometry.",
            persistence,
            postLoadStabilized,
            loh,
            gen2,
            pause,
            suspension,
            timing,
            limitations);
    }

    public static GcPressureCharacterizationResult IncompatibleLegacy(
        string? captureId,
        int? sourceSchemaVersion)
    {
        var identity = new GcPressureCaptureIdentity(
            string.IsNullOrWhiteSpace(captureId) ? "legacy-artifact" : captureId,
            "unknown",
            "unknown",
            "unknown",
            "unknown",
            "unknown",
            0,
            "unknown",
            "bounded_time_window");
        return IncompatibleLegacy(identity, sourceSchemaVersion);
    }

    private static GcPressureCharacterizationResult IncompatibleLegacy(
        GcPressureCaptureIdentity identity,
        int? sourceSchemaVersion)
    {
        var reason = sourceSchemaVersion is null
            ? "legacy_contract_missing"
            : $"unsupported_capture_schema_{sourceSchemaVersion.Value}";
        var unavailable = new AvailabilityState(GcPressureAvailability.Incompatible, reason);
        var counts = new GcPressureStatisticCounts(0, 0, 0, 0);
        var loh = UnavailableMetric("gc.loh-size", "bytes", unavailable, counts);
        var gen2 = UnavailableMetric("gc.gen2-collections-rate", "collections/s", unavailable, counts);
        var pause = UnavailableMetric("gc.pause-duration-p95", "ms", unavailable, counts);
        var suspension = UnavailableMetric("gc.suspension-time", "%", unavailable, counts);
        return Result(
            identity,
            GcPressureNextAction.JustifiedInconclusion,
            "The artifact does not contain the current GC pressure contract and is not reinterpreted with current scenario geometry.",
            null,
            null,
            loh,
            gen2,
            pause,
            suspension,
            [],
            [$"Source artifact compatibility is unavailable: {reason}."]);
    }

    private static GcPressureMetricEvidence EvaluateMetric(
        GcPressureCapture capture,
        string quantityId,
        string unit,
        Func<GcPressureWindow, double> selector,
        AvailabilityState availability)
    {
        var counts = Counts(capture);
        if (availability.Status != GcPressureAvailability.Available)
        {
            return UnavailableMetric(quantityId, unit, availability, counts);
        }

        var values = capture.Windows.Select(selector).Order().ToArray();
        return new(
            quantityId,
            unit,
            new(
                GcPressureAvailability.Available,
                null,
                "window-median",
                1,
                "bounded_time_window",
                counts,
                Median(values),
                null),
            QuantileR7(values, 0.95),
            new(
                GcPressureAvailability.Available,
                null,
                "theil-sen-slope",
                1,
                "bounded_time_window",
                counts,
                null,
                TheilSenSlope(capture.Windows, selector)));
    }

    private static GcPressureMetricEvidence UnavailableMetric(
        string quantityId,
        string unit,
        AvailabilityState availability,
        GcPressureStatisticCounts counts)
        => new(
            quantityId,
            unit,
            new(
                availability.Status,
                availability.Reason,
                "window-median",
                1,
                "bounded_time_window",
                counts,
                null,
                null),
            null,
            new(
                availability.Status,
                availability.Reason,
                "theil-sen-slope",
                1,
                "bounded_time_window",
                counts,
                null,
                null));

    private static AvailabilityState DetermineAvailability(GcPressureCapture capture)
    {
        if (capture.Windows.Count(window => window.Phase == GcPressureWindowPhase.Baseline) != 1)
        {
            return new(GcPressureAvailability.Incompatible, "exactly_one_baseline_required");
        }

        if (capture.Windows.Any(window => window.LostOrDropped > 0))
        {
            return new(GcPressureAvailability.LossDetected, "source_loss_detected");
        }

        if (capture.Windows.Any(window => window.Representation == GcPressureRepresentation.Truncated))
        {
            return new(GcPressureAvailability.LossDetected, "truncated_representation");
        }

        if (capture.Windows.Any(window => window.Representation != GcPressureRepresentation.Exact))
        {
            return new(GcPressureAvailability.UnsupportedApproximation, "approximate_representation_not_admitted");
        }

        if (capture.Windows.Count(window => window.Phase == GcPressureWindowPhase.Cooldown)
            < GcPressureCharacterizationBounds.MinimumCooldownWindows)
        {
            return new(GcPressureAvailability.InsufficientObservations, "minimum_cooldown_windows_not_met");
        }

        if (capture.Windows.Count < GcPressureCharacterizationBounds.MinimumValidWindows)
        {
            return new(GcPressureAvailability.InsufficientObservations, "minimum_valid_windows_not_met");
        }

        return new(GcPressureAvailability.Available, null);
    }

    private static void ValidateStructuralBounds(GcPressureCapture capture)
    {
        if (capture.Windows is null
            || capture.Notes is null
            || string.IsNullOrWhiteSpace(capture.Id)
            || !Enum.IsDefined(capture.Scenario)
            || string.IsNullOrWhiteSpace(capture.RuntimeVersion)
            || string.IsNullOrWhiteSpace(capture.OperatingSystem)
            || string.IsNullOrWhiteSpace(capture.Architecture))
        {
            throw new InvalidDataException("GC pressure capture identity is incomplete.");
        }

        if (capture.WindowSeconds <= 0
            || capture.Windows.Count is < 1 or > GcPressureCharacterizationBounds.MaximumWindows)
        {
            throw new InvalidDataException("GC pressure capture exceeds the bounded window protocol.");
        }

        if (capture.Windows.Any(static window => window is null)
            || capture.Windows.Select(window => window.Index).Distinct().Count() != capture.Windows.Count
            || capture.Windows.Any(window => window.Index < 0
                || window.SourceEvents < 0
                || window.LostOrDropped < 0
                || !Enum.IsDefined(window.Phase)
                || !Enum.IsDefined(window.Representation)
                || window.LohSizeBytes < 0
                || window.Gen2CollectionsPerSecond < 0
                || window.PauseP95Milliseconds < 0
                || window.TimeInGcPercent is < 0 or > 100
                || !AllFinite(
                    window.LohSizeBytes,
                    window.Gen2CollectionsPerSecond,
                    window.PauseP95Milliseconds,
                    window.TimeInGcPercent))
            || capture.Notes.Any(string.IsNullOrWhiteSpace)
            || !AggregateCountsFitLong(capture.Windows))
        {
            throw new InvalidDataException("GC pressure capture contains an invalid window.");
        }
    }

    private static GcPressureCaptureIdentity Identity(GcPressureCapture capture)
        => new(
            capture.Id,
            ScenarioName(capture.Scenario),
            capture.RuntimeVersion,
            capture.OperatingSystem,
            capture.Architecture,
            "EventPipe/runtime-gc-v2+system-runtime-counters-v1",
            capture.WindowSeconds,
            "non-overlapping-separately-started-captures",
            "bounded_time_window");

    private static GcPressureStatisticCounts Counts(GcPressureCapture capture)
        => new(
            capture.Windows.Count,
            capture.Windows.Count,
            capture.Windows.Sum(window => window.SourceEvents),
            capture.Windows.Sum(window => window.LostOrDropped));

    private static List<string> BaseLimitations(GcPressureCapture capture)
    {
        var limitations = new List<string>
        {
            "Thresholds are named scenario geometry and are not universal production defaults.",
            "Window summaries are descriptive observations from non-overlapping bounded captures.",
            "Cross-signal timing is retained as observation only and never establishes causality.",
            "LOH growth does not identify a retaining cause; retained-heap investigation requires separate graph/root evidence.",
            "Capture startup, workload shape, and GC observation can affect the measured target.",
        };
        limitations.AddRange(capture.Notes);
        return limitations;
    }

    private static bool IsPressureWindow(GcPressureWindow window, GcPressureWindow baseline)
        => window.LohSizeBytes - baseline.LohSizeBytes >= LohPressureDeltaBytes
            || IsAllocationPressureWindow(window);

    private static bool IsAllocationPressureWindow(GcPressureWindow window)
        => window.Gen2CollectionsPerSecond >= Gen2PressureRate
            || window.PauseP95Milliseconds >= PausePressureMilliseconds
            || window.TimeInGcPercent >= SuspensionPressurePercent;

    private static int LongestRun(
        IReadOnlyList<GcPressureWindow> windows,
        Func<GcPressureWindow, bool> predicate)
    {
        var longest = 0;
        var current = 0;
        foreach (var window in windows)
        {
            current = predicate(window) ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        return longest;
    }

    private static bool? PostLoadStabilized(
        IReadOnlyList<GcPressureWindow> windows,
        GcPressureWindow baseline)
    {
        var cooldown = windows
            .Where(window => window.Phase == GcPressureWindowPhase.Cooldown)
            .OrderBy(window => window.Index)
            .ToArray();
        if (cooldown.Length < GcPressureCharacterizationBounds.MinimumCooldownWindows)
        {
            return null;
        }

        return cooldown.All(window =>
            window.LohSizeBytes - baseline.LohSizeBytes < StabilizedLohDeltaBytes
            && !IsAllocationPressureWindow(window));
    }

    private static IReadOnlyList<GcPressureTimingObservation> TimingObservations(
        IReadOnlyList<GcPressureWindow> windows,
        GcPressureWindow baseline)
    {
        var firstLoh = windows.FirstOrDefault(window =>
            window.LohSizeBytes - baseline.LohSizeBytes >= LohPressureDeltaBytes);
        var firstGc = windows.FirstOrDefault(IsAllocationPressureWindow);
        if (firstLoh is null || firstGc is null)
        {
            return [];
        }

        return
        [
            new(
                "loh-elevation",
                firstLoh.Index,
                "allocation-pressure",
                firstGc.Index,
                "Temporal co-observation only; ordering and co-occurrence do not establish causality."),
        ];
    }

    private static GcPressureCharacterizationResult Result(
        GcPressureCaptureIdentity identity,
        GcPressureNextAction action,
        string reason,
        GcPressurePersistenceEvidence? persistence,
        bool? postLoadStabilized,
        GcPressureMetricEvidence loh,
        GcPressureMetricEvidence gen2,
        GcPressureMetricEvidence pause,
        GcPressureMetricEvidence suspension,
        IReadOnlyList<GcPressureTimingObservation> timing,
        IReadOnlyList<string> limitations)
        => new(
            CurrentResultSchemaVersion,
            CurrentProtocolVersion,
            identity,
            action,
            reason,
            persistence,
            postLoadStabilized,
            loh,
            gen2,
            pause,
            suspension,
            timing,
            BoundLimitations(limitations));

    private static double Median(double[] sortedValues)
    {
        var middle = sortedValues.Length / 2;
        return sortedValues.Length % 2 == 1
            ? sortedValues[middle]
            : sortedValues[middle - 1] / 2d + sortedValues[middle] / 2d;
    }

    private static double QuantileR7(double[] sortedValues, double probability)
    {
        if (sortedValues.Length == 1)
        {
            return sortedValues[0];
        }

        var position = (sortedValues.Length - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var fraction = position - lower;
        return sortedValues[lower] + fraction * (sortedValues[upper] - sortedValues[lower]);
    }

    private static double TheilSenSlope(
        IReadOnlyList<GcPressureWindow> windows,
        Func<GcPressureWindow, double> selector)
    {
        var ordered = windows.OrderBy(window => window.Index).ToArray();
        var slopes = new List<double>(checked(ordered.Length * (ordered.Length - 1) / 2));
        for (var left = 0; left < ordered.Length; left++)
        {
            for (var right = left + 1; right < ordered.Length; right++)
            {
                var delta = ordered[right].Index - ordered[left].Index;
                if (delta == 0)
                {
                    continue;
                }

                var slope = (selector(ordered[right]) - selector(ordered[left])) / delta;
                if (double.IsFinite(slope))
                {
                    slopes.Add(slope);
                }
            }
        }

        slopes.Sort();
        return Median(slopes.ToArray());
    }

    private static bool AllFinite(params double[] values)
        => values.All(double.IsFinite);

    private static bool AggregateCountsFitLong(IReadOnlyList<GcPressureWindow> windows)
    {
        long sourceEvents = 0;
        long lostOrDropped = 0;
        foreach (var window in windows)
        {
            if (window.SourceEvents > long.MaxValue - sourceEvents
                || window.LostOrDropped > long.MaxValue - lostOrDropped)
            {
                return false;
            }

            sourceEvents += window.SourceEvents;
            lostOrDropped += window.LostOrDropped;
        }

        return true;
    }

    private static string[] BoundLimitations(IReadOnlyList<string> limitations)
    {
        if (limitations.Count <= GcPressureCharacterizationBounds.MaximumLimitations)
        {
            return limitations.ToArray();
        }

        return
        [
            .. limitations.Take(GcPressureCharacterizationBounds.MaximumLimitations - 1),
            $"Additional limitations omitted after the {GcPressureCharacterizationBounds.MaximumLimitations}-entry output bound.",
        ];
    }

    private static string ScenarioName(GcPressureScenarioKind scenario)
        => scenario switch
        {
            GcPressureScenarioKind.StableHealthy => "stable-healthy",
            GcPressureScenarioKind.TransientAllocationBurst => "transient-allocation-burst",
            GcPressureScenarioKind.SustainedAllocationPressure => "sustained-allocation-pressure",
            GcPressureScenarioKind.RetainedGrowth => "retained-growth",
            GcPressureScenarioKind.CaptureLoss => "capture-loss",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private sealed record AvailabilityState(
        GcPressureAvailability Status,
        string? Reason);
}
