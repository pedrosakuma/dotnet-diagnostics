using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class GcPressureCharacterizationHarness
{
    public const int CurrentSchemaVersion = 1;
    private const double LohPressureDeltaBytes = 16 * 1024 * 1024;
    private const double RetainedLohSlopeBytesPerWindow = 1024 * 1024;
    private const double Gen2PressureRate = 0.5;
    private const double PausePressureMilliseconds = 2;
    private const double TimeInGcPressurePercent = 5;
    private const double StabilizedLohDeltaBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static GcPressureCharacterizationResult Evaluate(GcPressureCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Validate(capture);

        var loh = EvaluateMetric(capture, "gc.loh-size", "bytes", window => window.LohSizeBytes);
        var gen2 = EvaluateMetric(
            capture,
            "gc.gen2-collections-rate",
            "collections/s",
            window => window.Gen2CollectionsPerSecond);
        var pause = EvaluateMetric(
            capture,
            "gc.pause-duration-p95",
            "ms",
            window => window.PauseP95Milliseconds);
        var timeInGc = EvaluateMetric(
            capture,
            "gc.time-in-gc",
            "%",
            window => window.TimeInGcPercent);

        var limitations = new List<string>
        {
            "Window summaries are descriptive observations from non-overlapping bounded captures.",
            "Cross-signal timing is retained as observation only and never establishes causality.",
            "LOH growth does not establish a leak; retained-heap investigation requires separate graph/root evidence coordinated with #924.",
            "Capture startup, forced workload shape, and GC observation can affect the measured target.",
        };
        limitations.AddRange(capture.Notes);

        var unavailable = new[] { loh, gen2, pause, timeInGc }
            .Where(metric => metric.Median.Availability != ScenarioStatisticalAvailability.Available
                || metric.Slope.Availability != ScenarioStatisticalAvailability.Available
                || metric.P95 is null)
            .ToArray();
        if (unavailable.Length > 0)
        {
            limitations.AddRange(unavailable.Select(metric =>
                $"{metric.QuantityId} is unavailable: median={metric.Median.Availability}; slope={metric.Slope.Availability}; p95={(metric.P95 is null ? "unavailable" : "available")}."));
            return Result(
                capture,
                GcPressureNextAction.JustifiedInconclusion,
                "One or more required window statistics are unavailable because evidence is insufficient, lost, truncated, or incompatible.",
                pressureWindows: 0,
                postLoadStabilized: null,
                loh,
                gen2,
                pause,
                timeInGc,
                [],
                limitations);
        }

        var baseline = capture.Windows
            .Where(window => window.Phase == GcPressureWindowPhase.Baseline)
            .OrderBy(window => window.Index)
            .First();
        var pressureWindows = capture.Windows.Count(window => IsPressureWindow(window, baseline));
        var gcPressureWindows = capture.Windows.Count(IsGcPressureWindow);
        var postLoadStabilized = PostLoadStabilized(capture.Windows, baseline);
        var timing = TimingObservations(capture.Windows, baseline);
        var lohSlope = loh.Slope.Slope!.Value;

        if (gcPressureWindows >= 3)
        {
            return Result(
                capture,
                GcPressureNextAction.AllocationInvestigation,
                "Gen2, pause, or time-in-GC pressure persists across at least three bounded windows; prioritize allocation investigation even when LOH maxima remain elevated after churn.",
                pressureWindows,
                postLoadStabilized,
                loh,
                gen2,
                pause,
                timeInGc,
                timing,
                limitations);
        }

        if (lohSlope >= RetainedLohSlopeBytesPerWindow && postLoadStabilized == false)
        {
            return Result(
                capture,
                GcPressureNextAction.RetainedHeapInvestigation,
                "LOH size has a persistent positive robust slope and does not recover during the bounded cooldown; inspect retained heap graph/root evidence before any retention conclusion.",
                pressureWindows,
                postLoadStabilized,
                loh,
                gen2,
                pause,
                timeInGc,
                timing,
                limitations);
        }

        if (pressureWindows <= 2 && postLoadStabilized != false)
        {
            return Result(
                capture,
                GcPressureNextAction.ContinuedObservation,
                pressureWindows == 0
                    ? "The bounded windows remain stable without objective GC/LOH pressure."
                    : "The bounded pressure is transient and returns to the baseline envelope during cooldown.",
                pressureWindows,
                postLoadStabilized,
                loh,
                gen2,
                pause,
                timeInGc,
                timing,
                limitations);
        }

        return Result(
            capture,
            GcPressureNextAction.JustifiedInconclusion,
            "The available signals do not satisfy the predeclared allocation, retained-growth, or recovered-transient shapes.",
            pressureWindows,
            postLoadStabilized,
            loh,
            gen2,
            pause,
            timeInGc,
            timing,
            limitations);
    }

    public static GcPressureReplayFixture ReadFixture(string path)
    {
        using var stream = File.OpenRead(path);
        var fixture = JsonSerializer.Deserialize<GcPressureReplayFixture>(stream, JsonOptions)
            ?? throw new InvalidDataException($"GC pressure fixture '{path}' is empty.");
        if (fixture.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported GC pressure fixture schema version {fixture.SchemaVersion}.");
        }

        return fixture;
    }

    public static string Serialize(GcPressureCharacterizationResult result)
        => JsonSerializer.Serialize(result, JsonOptions);

    public static string SerializeCapture(GcPressureCapture capture)
        => JsonSerializer.Serialize(capture, JsonOptions);

    private static GcPressureMetricEvidence EvaluateMetric(
        GcPressureCapture capture,
        string quantityId,
        string unit,
        Func<GcPressureWindow, double> selector)
    {
        var request = Request(capture, quantityId, unit, selector);
        var median = ScenarioStatisticalEvidenceHarness.Evaluate(request);
        var slope = ScenarioStatisticalEvidenceHarness.Evaluate(request with
        {
            Protocol = request.Protocol with
            {
                Id = $"{quantityId}-slope",
                Method = new ScenarioStatisticalMethod(ScenarioStatisticalMethodIds.TheilSenSlope, 1),
            },
        });
        double? p95 = median.Availability == ScenarioStatisticalAvailability.Available
            ? QuantileR7(capture.Windows.Select(selector).Order().ToArray(), 0.95)
            : null;
        return new(quantityId, unit, median, p95, slope);
    }

    private static ScenarioStatisticalEvidenceRequest Request(
        GcPressureCapture capture,
        string quantityId,
        string unit,
        Func<GcPressureWindow, double> selector)
    {
        var captureRepresentation = capture.Windows.Any(window =>
            window.LostOrDropped > 0
            || window.Representation != ScenarioStatisticalRepresentation.Exact)
            ? ScenarioStatisticalRepresentation.Truncated
            : ScenarioStatisticalRepresentation.Exact;
        var attempts = new[]
        {
            new ScenarioStatisticalAttempt(
                "process-run-1",
                "attempt-1",
                ScenarioStatisticalAttemptOutcome.Passed),
        };
        var observations = capture.Windows.Select(window => new ScenarioStatisticalObservation(
            $"{quantityId}-window-{window.Index}",
            "process-run-1",
            "attempt-1",
            "local-environment-1",
            capture.Id,
            $"window-{window.Index}",
            $"window-{window.Index}",
            selector(window),
            window.Index,
            window.SourceEvents,
            window.LostOrDropped,
            captureRepresentation)).ToArray();
        return new(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            new ScenarioStatisticalProtocol(
                $"{quantityId}-window-median",
                "1",
                new ScenarioStatisticalMethod(ScenarioStatisticalMethodIds.WindowMedian, 1),
                ScenarioStatisticalIndependenceUnit.BoundedTimeWindow,
                GcPressureCharacterizationBounds.MinimumValidWindows,
                AdmitLoss: false,
                AdmitApproximation: false),
            [
                new ScenarioStatisticalArm(
                    "observed",
                    ScenarioStatisticalArmRole.Observed,
                    Identity(capture, quantityId, unit),
                    attempts,
                    observations),
            ],
            "observed");
    }

    private static ScenarioStatisticalCompatibilityIdentity Identity(
        GcPressureCapture capture,
        string quantityId,
        string unit)
        => new(
        [
            new("target.runtime-flavor", "CoreCLR"),
            new("target.runtime-version-policy", capture.RuntimeVersion),
            new("target.os", capture.OperatingSystem),
            new("target.architecture", capture.Architecture),
            new("target.process-run-id", capture.Id),
            new("backend.id", "EventPipe"),
            new("backend.version", "runtime-gc-v2+system-runtime-counters-v1"),
            new("quantity.id", quantityId),
            new("quantity.unit", unit),
            new("quantity.aggregation", "one-bounded-window-summary"),
            new("quantity.normalization", quantityId.Contains("rate", StringComparison.Ordinal) ? "per-observed-second" : "none"),
            new("quantity.filters", "CoreCLR GC plus System.Runtime"),
            new("quantity.representation", capture.Windows.All(window => window.Representation == ScenarioStatisticalRepresentation.Exact) ? "exact" : "truncated"),
            new("capture.duration", FormattableString.Invariant($"{capture.WindowSeconds:0.###}s")),
            new("capture.windowing", "non-overlapping-separately-started-captures"),
            new("capture.providers", "Microsoft-Windows-DotNETRuntime:GC;System.Runtime"),
            new("capture.sampling-period", "1s-counters"),
            new("capture.retention", $"windows<={GcPressureCharacterizationBounds.MaximumWindows};gc-details<={GcPressureCharacterizationBounds.NormalGcDetailCap}"),
            new("capture.enrichment", "none"),
            new("workload.scenario", capture.Scenario.ToString()),
            new("workload.version", "1"),
            new("workload.parameters", "bounded-known-shape"),
            new("workload.concurrency", "scenario-defined"),
            new("workload.deployment", "fresh-local-process"),
            new("independence.unit", "bounded_time_window"),
            new("independence.nesting", "events->windows->one-process-run->one-environment-allocation"),
            new("independence.pairing-key", "not-paired"),
            new("comparison.axis", "workload.scenario"),
        ]);

    private static bool IsPressureWindow(GcPressureWindow window, GcPressureWindow baseline)
        => window.LohSizeBytes - baseline.LohSizeBytes >= LohPressureDeltaBytes
            || IsGcPressureWindow(window);

    private static bool IsGcPressureWindow(GcPressureWindow window)
        => window.Gen2CollectionsPerSecond >= Gen2PressureRate
            || window.PauseP95Milliseconds >= PausePressureMilliseconds
            || window.TimeInGcPercent >= TimeInGcPressurePercent;

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
            && window.Gen2CollectionsPerSecond < Gen2PressureRate
            && window.PauseP95Milliseconds < PausePressureMilliseconds
            && window.TimeInGcPercent < TimeInGcPressurePercent);
    }

    private static IReadOnlyList<GcPressureTimingObservation> TimingObservations(
        IReadOnlyList<GcPressureWindow> windows,
        GcPressureWindow baseline)
    {
        var firstLoh = windows.FirstOrDefault(window =>
            window.LohSizeBytes - baseline.LohSizeBytes >= LohPressureDeltaBytes);
        var firstGc = windows.FirstOrDefault(window =>
            window.Gen2CollectionsPerSecond >= Gen2PressureRate
            || window.PauseP95Milliseconds >= PausePressureMilliseconds
            || window.TimeInGcPercent >= TimeInGcPressurePercent);
        if (firstLoh is null || firstGc is null)
        {
            return [];
        }

        return
        [
            new(
                "loh-elevation",
                firstLoh.Index,
                "gc-pressure",
                firstGc.Index,
                "Temporal co-observation only; ordering and co-occurrence do not establish causality."),
        ];
    }

    private static GcPressureCharacterizationResult Result(
        GcPressureCapture capture,
        GcPressureNextAction action,
        string reason,
        int pressureWindows,
        bool? postLoadStabilized,
        GcPressureMetricEvidence loh,
        GcPressureMetricEvidence gen2,
        GcPressureMetricEvidence pause,
        GcPressureMetricEvidence timeInGc,
        IReadOnlyList<GcPressureTimingObservation> timing,
        IReadOnlyList<string> limitations)
        => new(
            CurrentSchemaVersion,
            capture.Id,
            action,
            reason,
            pressureWindows,
            postLoadStabilized,
            loh,
            gen2,
            pause,
            timeInGc,
            timing,
            limitations.Take(ScenarioStatisticalEvidenceBounds.MaximumLimitations).ToArray());

    private static void Validate(GcPressureCapture capture)
    {
        if (capture.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported GC pressure capture schema version {capture.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(capture.Id)
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
        if (capture.Windows.Select(window => window.Index).Distinct().Count() != capture.Windows.Count
            || capture.Windows.Any(window => window.Index < 0
                || window.SourceEvents < 0
                || window.LostOrDropped < 0
                || !AllFinite(
                    window.LohSizeBytes,
                    window.Gen2CollectionsPerSecond,
                    window.PauseP95Milliseconds,
                    window.TimeInGcPercent)))
        {
            throw new InvalidDataException("GC pressure capture contains an invalid window.");
        }
        if (capture.Windows.Count(window => window.Phase == GcPressureWindowPhase.Baseline) != 1)
        {
            throw new InvalidDataException("GC pressure capture requires exactly one baseline window.");
        }
        if (capture.Windows.Count(window => window.Phase == GcPressureWindowPhase.Cooldown)
            < GcPressureCharacterizationBounds.MinimumCooldownWindows)
        {
            throw new InvalidDataException(
                $"GC pressure capture requires at least {GcPressureCharacterizationBounds.MinimumCooldownWindows} cooldown windows.");
        }
    }

    private static double QuantileR7(double[] sortedValues, double probability)
    {
        if (sortedValues.Length == 0)
        {
            return double.NaN;
        }
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

    private static bool AllFinite(params double[] values)
        => values.All(double.IsFinite);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
