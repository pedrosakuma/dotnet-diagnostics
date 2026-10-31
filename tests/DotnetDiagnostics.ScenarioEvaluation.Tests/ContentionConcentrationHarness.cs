using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ContentionConcentrationHarness
{
    public const string ProtocolId = "contention-concentration-characterization";
    public const string ProtocolVersion = "1";
    private const int MinimumCaptures = 4;

    public static ContentionConcentrationReport Evaluate(
        IReadOnlyList<ContentionCaptureEvidence> captures,
        string environment)
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        ValidateCaptures(captures);

        var scenarios = Enum.GetValues<ContentionConcentrationScenario>()
            .Select(scenario => EvaluateScenario(
                scenario,
                captures.Where(capture => capture.Scenario == scenario)
                    .OrderBy(capture => capture.CaptureId, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
        var go = scenarios.All(scenario => scenario.ExpectedBehaviorObserved);

        return new(
            SchemaVersion: 1,
            ProtocolId,
            ProtocolVersion,
            environment,
            captures.GroupBy(capture => capture.Scenario).Select(group => group.Count()).Distinct().Single(),
            captures.OrderBy(capture => capture.Scenario).ThenBy(capture => capture.CaptureId, StringComparer.Ordinal).ToArray(),
            scenarios,
            Verdict: go ? "GO" : "NO-GO",
            Conclusions:
            [
                "Wait-duration concentration changes the safe next drilldown when it is stable across independent captures and the inventory is complete.",
                "High concentration supports focusing collection on a lock and, when observed, its owner relationship; it does not establish why the lock is contended.",
                "Diffuse waiting keeps the investigation broad, a short burst requires time alignment and repetition, and a truncated inventory remains inconclusive.",
                "No events in bounded healthy captures means no contention was observed in those windows; it does not establish zero waiting outside them.",
            ],
            Limitations:
            [
                "Wilson intervals use one separately started capture in one fresh process run as the independent binary observation unit; EventPipe events are never counted as independent trials.",
                "Window values are descriptive nested evidence and do not increase the independent capture count.",
                "Concentration is descriptive prioritization evidence, not causal attribution or a performance verdict.",
                "The live EventPipe callback aggregates delivered events into bounded lock and window tables; runtime-side source loss is not independently observable in this validation contract.",
            ]);
    }

    public static void Write(string path, ContentionConcentrationReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, report, ContentionConcentrationJsonContext.Default.ContentionConcentrationReport);
    }

    public static ContentionConcentrationReport Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, ContentionConcentrationJsonContext.Default.ContentionConcentrationReport)
            ?? throw new InvalidDataException($"Contention characterization fixture '{path}' was empty.");
    }

    private static ContentionScenarioEvidence EvaluateScenario(
        ContentionConcentrationScenario scenario,
        ContentionCaptureEvidence[] captures)
    {
        var top1 = EvaluateQuantity(scenario, captures, "top1-wait-duration-share", capture => capture.Top1WaitDurationShare);
        var top3 = EvaluateQuantity(scenario, captures, "top3-wait-duration-share", capture => capture.Top3WaitDurationShare);
        var top1Mad = EvaluateQuantity(
            scenario,
            captures,
            "top1-wait-duration-share",
            capture => capture.Top1WaitDurationShare,
            ScenarioStatisticalMethodIds.MedianAbsoluteDeviation,
            minimum: MinimumCaptures);
        var ownerOverlap = EvaluateOwnerOverlap(scenario, captures);
        var activeWindowFractions = captures
            .Where(capture => capture.TotalWindowCount > 0)
            .Select(capture => capture.ActiveWindowCount / (double)capture.TotalWindowCount)
            .Order()
            .ToArray();
        double? medianActiveWindowFraction = activeWindowFractions.Length == 0
            ? null
            : Median(activeWindowFractions);
        var focus = SelectFocus(scenario, captures, top1, top3, top1Mad, ownerOverlap, medianActiveWindowFraction);
        var expected = ExpectedFocus(scenario) == focus
            && top1.Counts.IndependenceUnits <= captures.Length
            && (ownerOverlap is null || ownerOverlap.IndependenceUnit == ScenarioStatisticalIndependenceUnit.Capture);

        return new(
            scenario,
            captures.Length,
            captures.Count(capture => capture.TotalEvents > 0),
            top1,
            top3,
            top1Mad,
            ownerOverlap,
            medianActiveWindowFraction,
            focus,
            expected,
            captures.SelectMany(capture => capture.Limitations)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
    }

    private static ContentionDrilldownFocus SelectFocus(
        ContentionConcentrationScenario scenario,
        IReadOnlyList<ContentionCaptureEvidence> captures,
        ScenarioStatisticalEvidenceResult top1,
        ScenarioStatisticalEvidenceResult top3,
        ScenarioStatisticalEvidenceResult top1Mad,
        ScenarioStatisticalEvidenceResult? ownerOverlap,
        double? medianActiveWindowFraction)
    {
        if (scenario == ContentionConcentrationScenario.HealthyControl)
        {
            return captures.All(capture => capture.TotalEvents == 0)
                ? ContentionDrilldownFocus.BoundedCaptureObservedNoContention
                : ContentionDrilldownFocus.KeepContentionInvestigationBroad;
        }

        if (captures.Any(capture => capture.InventoryTruncated || capture.AggregationOverflow))
        {
            return ContentionDrilldownFocus.InconclusiveDueToTruncatedInventory;
        }

        if (scenario == ContentionConcentrationScenario.ShortBurst
            && medianActiveWindowFraction is <= 0.5)
        {
            return ContentionDrilldownFocus.TimeAlignAndRepeatCapture;
        }

        if (top1.Availability != ScenarioStatisticalAvailability.Available
            || top3.Availability != ScenarioStatisticalAvailability.Available
            || top1Mad.Availability != ScenarioStatisticalAvailability.Available)
        {
            return ContentionDrilldownFocus.KeepContentionInvestigationBroad;
        }

        if (top1.Estimate >= 0.65 && top3.Estimate >= 0.80 && top1Mad.Dispersion <= 0.15)
        {
            return HasObservedOwnerOverlap(ownerOverlap)
                ? ContentionDrilldownFocus.FocusDominantLockAndOwner
                : ContentionDrilldownFocus.FocusDominantLockOwnerUnavailable;
        }

        return ContentionDrilldownFocus.KeepContentionInvestigationBroad;
    }

    private static ContentionDrilldownFocus ExpectedFocus(ContentionConcentrationScenario scenario)
        => scenario switch
        {
            ContentionConcentrationScenario.DominantLock
                => ContentionDrilldownFocus.FocusDominantLockAndOwner,
            ContentionConcentrationScenario.DiffuseLocks
                => ContentionDrilldownFocus.KeepContentionInvestigationBroad,
            ContentionConcentrationScenario.OwnerUnavailable
                => ContentionDrilldownFocus.FocusDominantLockOwnerUnavailable,
            ContentionConcentrationScenario.ShortBurst
                => ContentionDrilldownFocus.TimeAlignAndRepeatCapture,
            ContentionConcentrationScenario.TruncatedInventory
                => ContentionDrilldownFocus.InconclusiveDueToTruncatedInventory,
            ContentionConcentrationScenario.HealthyControl
                => ContentionDrilldownFocus.BoundedCaptureObservedNoContention,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static bool HasObservedOwnerOverlap(ScenarioStatisticalEvidenceResult? ownerOverlap)
        => ownerOverlap?.Availability == ScenarioStatisticalAvailability.Available
            && ownerOverlap.Rate is { PositiveCount: > 0 };

    private static ScenarioStatisticalEvidenceResult EvaluateQuantity(
        ContentionConcentrationScenario scenario,
        IReadOnlyList<ContentionCaptureEvidence> captures,
        string quantityId,
        Func<ContentionCaptureEvidence, double?> selector,
        string methodId = ScenarioStatisticalMethodIds.WindowMedian,
        int minimum = MinimumCaptures)
    {
        var observations = captures
            .Select(capture => (Capture: capture, Value: selector(capture)))
            .Where(item => item.Value is not null)
            .ToArray();
        var attempts = captures.Select(capture => new ScenarioStatisticalAttempt(
            capture.CaptureId,
            "attempt-1",
            ScenarioStatisticalAttemptOutcome.Passed)).ToArray();
        var statisticalObservations = observations.Select(item => new ScenarioStatisticalObservation(
            $"{item.Capture.CaptureId}-{quantityId}",
            item.Capture.CaptureId,
            "attempt-1",
            "current-environment",
            item.Capture.CaptureId,
            null,
            item.Capture.CaptureId,
            item.Value!.Value,
            null,
            item.Capture.TotalEvents,
            0,
            ScenarioStatisticalRepresentation.Exact)).ToArray();
        var request = new ScenarioStatisticalEvidenceRequest(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            new ScenarioStatisticalProtocol(
                ProtocolId,
                ProtocolVersion,
                new ScenarioStatisticalMethod(methodId, 1),
                ScenarioStatisticalIndependenceUnit.Capture,
                minimum,
                AdmitLoss: false,
                AdmitApproximation: false),
            [new ScenarioStatisticalArm(
                "observed",
                ScenarioStatisticalArmRole.Observed,
                Identity(scenario, quantityId, "ratio"),
                attempts,
                statisticalObservations)],
            "observed");
        return ScenarioStatisticalEvidenceHarness.Evaluate(request);
    }

    private static ScenarioStatisticalEvidenceResult? EvaluateOwnerOverlap(
        ContentionConcentrationScenario scenario,
        IReadOnlyList<ContentionCaptureEvidence> captures)
    {
        var eligible = captures.Where(capture =>
            capture.OwnerMetadataAvailable && capture.OwnerOverlapObserved is not null).ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        var attempts = captures.Select(capture => new ScenarioStatisticalAttempt(
            capture.CaptureId,
            "attempt-1",
            ScenarioStatisticalAttemptOutcome.Passed)).ToArray();
        var observations = eligible.Select(capture => new ScenarioStatisticalObservation(
            $"{capture.CaptureId}-owner-overlap",
            capture.CaptureId,
            "attempt-1",
            "current-environment",
            capture.CaptureId,
            null,
            capture.CaptureId,
            capture.OwnerOverlapObserved!.Value ? 1 : 0,
            null,
            SourceEventCount: 1,
            LostOrDropped: 0,
            ScenarioStatisticalRepresentation.Exact)).ToArray();
        var request = new ScenarioStatisticalEvidenceRequest(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            new ScenarioStatisticalProtocol(
                ProtocolId,
                ProtocolVersion,
                new ScenarioStatisticalMethod(ScenarioStatisticalMethodIds.WilsonScoreRate, 1),
                ScenarioStatisticalIndependenceUnit.Capture,
                MinimumCaptures,
                AdmitLoss: false,
                AdmitApproximation: false,
                ConfidenceLevel: 0.95),
            [new ScenarioStatisticalArm(
                "observed",
                ScenarioStatisticalArmRole.Observed,
                Identity(scenario, "owner-overlap-observed", "binary"),
                attempts,
                observations)],
            "observed");
        return ScenarioStatisticalEvidenceHarness.Evaluate(request);
    }

    private static ScenarioStatisticalCompatibilityIdentity Identity(
        ContentionConcentrationScenario scenario,
        string quantityId,
        string unit)
        => new(
        [
            new("target.runtime-flavor", "CoreCLR"),
            new("target.runtime-version-policy", "major-minor"),
            new("target.os", "windows"),
            new("target.architecture", "x64"),
            new("target.process-run-id", "fresh-process-per-capture"),
            new("backend.id", "EventPipe+ClrMD"),
            new("backend.version", "1"),
            new("quantity.id", quantityId),
            new("quantity.unit", unit),
            new("quantity.aggregation", "capture-median"),
            new("quantity.normalization", "per-delivered-contention-wait-duration"),
            new("quantity.filters", $"scenario={ScenarioName(scenario)}"),
            new("quantity.representation", "exact"),
            new("capture.duration", "4s"),
            new("capture.windowing", "non-overlapping-1s"),
            new("capture.providers", "Microsoft-Windows-DotNETRuntime:Contention"),
            new("capture.sampling-period", "event-driven"),
            new("capture.retention", $"locks={ContentionConcentrationBounds.MaximumTrackedLocks};reported={ContentionConcentrationBounds.MaximumReportedLocks}"),
            new("capture.enrichment", "one-live-thread-snapshot"),
            new("workload.scenario", ScenarioName(scenario)),
            new("workload.version", "1"),
            new("workload.parameters", "bounded-shape-v1"),
            new("workload.concurrency", "scenario-defined"),
            new("workload.deployment", "fresh-local-process"),
            new("workload.variant", ScenarioName(scenario)),
            new("independence.unit", "capture"),
            new("independence.nesting", "events->windows->capture->fresh-process-run"),
            new("independence.pairing-key", "none"),
            new("comparison.axis", "workload.variant"),
        ]);

    private static string ScenarioName(ContentionConcentrationScenario scenario)
        => scenario switch
        {
            ContentionConcentrationScenario.DominantLock => "dominant-lock",
            ContentionConcentrationScenario.DiffuseLocks => "diffuse-locks",
            ContentionConcentrationScenario.OwnerUnavailable => "owner-unavailable",
            ContentionConcentrationScenario.ShortBurst => "short-burst",
            ContentionConcentrationScenario.TruncatedInventory => "truncated-inventory",
            ContentionConcentrationScenario.HealthyControl => "healthy-control",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static void ValidateCaptures(IReadOnlyList<ContentionCaptureEvidence> captures)
    {
        if (captures.Count == 0)
        {
            throw new InvalidDataException("Contention characterization requires captures.");
        }

        foreach (var scenario in Enum.GetValues<ContentionConcentrationScenario>())
        {
            var count = captures.Count(capture => capture.Scenario == scenario);
            if (count is < 1 or > ContentionConcentrationBounds.MaximumCapturesPerScenario)
            {
                throw new InvalidDataException(
                    $"Scenario '{ScenarioName(scenario)}' capture count {count} is outside [1, {ContentionConcentrationBounds.MaximumCapturesPerScenario}].");
            }
        }

        var counts = captures.GroupBy(capture => capture.Scenario).Select(group => group.Count()).Distinct().ToArray();
        if (counts.Length != 1)
        {
            throw new InvalidDataException("Every contention scenario must use the same bounded capture count.");
        }

        if (captures.Select(capture => capture.CaptureId).Distinct(StringComparer.Ordinal).Count() != captures.Count)
        {
            throw new InvalidDataException("Contention capture ids must be unique.");
        }
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            throw new InvalidOperationException("Median requires at least one value.");
        }

        var midpoint = values.Length / 2;
        return values.Length % 2 == 1
            ? values[midpoint]
            : (values[midpoint - 1] + values[midpoint]) / 2;
    }
}
