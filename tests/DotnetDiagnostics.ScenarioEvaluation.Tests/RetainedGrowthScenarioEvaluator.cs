using System.Globalization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class RetainedGrowthScenarioEvaluator
{
    private const int SchemaVersion = 1;
    private const string ComparisonAxis = "workload.phase";

    public static RetainedGrowthScenarioResult Evaluate(RetainedGrowthScenarioEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Validate(evidence);

        var retainedBytes = EvaluatePaired(
            evidence,
            "retained-type-bytes",
            "bytes",
            observation => FindType(observation, evidence.TargetType)?.Bytes);
        var retainedInstances = EvaluatePaired(
            evidence,
            "retained-type-instances",
            "instances",
            observation => FindType(observation, evidence.TargetType)?.Instances);
        var pairChanges = BuildPairChangeRequest(evidence);
        var repeatedChange = EvaluateSingle(
            evidence,
            pairChanges,
            ScenarioStatisticalMethodIds.WindowMedian,
            "retained-type-byte-change",
            "bytes",
            RetainedGrowthScenarioBounds.MinimumRobustRuns);
        var spread = EvaluateSingle(
            evidence,
            pairChanges,
            ScenarioStatisticalMethodIds.MedianAbsoluteDeviation,
            "retained-type-byte-change",
            "bytes",
            RetainedGrowthScenarioBounds.MinimumRobustRuns);
        var interval = EvaluateSingle(
            evidence,
            pairChanges,
            ScenarioStatisticalMethodIds.PercentileBootstrapInterval,
            "retained-type-byte-change",
            "bytes",
            RetainedGrowthScenarioBounds.MinimumRobustRuns,
            bootstrapResamples: RetainedGrowthScenarioBounds.BootstrapResamples,
            confidenceLevel: 0.95,
            parentMethodId: ScenarioStatisticalMethodIds.WindowMedian);
        var persistence = EvaluateSingle(
            evidence,
            BuildPersistenceObservations(evidence),
            ScenarioStatisticalMethodIds.WilsonScoreRate,
            "positive-retained-growth-persistence",
            "binary",
            minimum: 1,
            confidenceLevel: 0.95);
        var stabilization = EvaluateSingle(
            evidence,
            BuildStabilizationObservations(evidence),
            ScenarioStatisticalMethodIds.WilsonScoreRate,
            "post-gc-stabilization",
            "binary",
            minimum: 1,
            confidenceLevel: 0.95);
        var concentrationInput = BuildConcentrationObservations(evidence);
        var concentration = EvaluateSingle(
            evidence,
            concentrationInput.Observations,
            ScenarioStatisticalMethodIds.WindowMedian,
            "largest-common-growing-type-byte-share",
            "ratio",
            minimum: 1);
        var allocationTraffic = EvaluateSingle(
            evidence,
            BuildAllocationTrafficObservations(evidence),
            ScenarioStatisticalMethodIds.WindowMedian,
            "allocation-traffic-estimated-bytes",
            "bytes",
            minimum: 1);
        var rootPaths = SummarizeRootPaths(evidence);
        var inducedGen2 = evidence.Runs.Sum(run =>
            run.Baseline.CaptureInducedGen2Collections
            + run.Candidate.CaptureInducedGen2Collections
            + run.Persistence.CaptureInducedGen2Collections);
        var support = DeterminePositiveEvidence(
            retainedBytes,
            retainedInstances,
            repeatedChange,
            persistence,
            stabilization,
            concentration,
            evidence);
        var limitations = BuildLimitations(evidence, rootPaths, inducedGen2);

        return new(
            SchemaVersion,
            evidence.Id,
            evidence.Variant,
            support,
            retainedBytes,
            retainedInstances,
            repeatedChange,
            spread,
            interval,
            persistence,
            stabilization,
            concentration,
            allocationTraffic,
            concentrationInput.TopGrowingTypes,
            rootPaths,
            inducedGen2,
            limitations);
    }

    private static ScenarioStatisticalEvidenceResult EvaluatePaired(
        RetainedGrowthScenarioEvidence evidence,
        string quantityId,
        string unit,
        Func<RetainedGrowthHeapObservation, long?> selector)
    {
        var baselineAttempts = new List<ScenarioStatisticalAttempt>(evidence.Runs.Count);
        var candidateAttempts = new List<ScenarioStatisticalAttempt>(evidence.Runs.Count);
        var baselineObservations = new List<ScenarioStatisticalObservation>(evidence.Runs.Count);
        var candidateObservations = new List<ScenarioStatisticalObservation>(evidence.Runs.Count);

        foreach (var run in evidence.Runs)
        {
            var baseline = selector(run.Baseline);
            var candidate = selector(run.Candidate);
            var passed = baseline is not null
                && candidate is not null
                && run.Baseline.CaptureCompleted
                && run.Candidate.CaptureCompleted
                && run.Baseline.PositiveRetentionSupported
                && run.Candidate.PositiveRetentionSupported;
            var outcome = passed
                ? ScenarioStatisticalAttemptOutcome.Passed
                : ScenarioStatisticalAttemptOutcome.Failed;
            var detail = passed ? null : "required_positive_type_evidence_unavailable";
            baselineAttempts.Add(new(run.RunId, "attempt-1", outcome, detail, detail));
            candidateAttempts.Add(new(run.RunId, "attempt-1", outcome, detail, detail));
            if (!passed)
            {
                continue;
            }

            baselineObservations.Add(Observation(
                run,
                run.Baseline,
                "baseline",
                baseline!.Value,
                run.PairOrder,
                $"pair-{run.RunId}"));
            candidateObservations.Add(Observation(
                run,
                run.Candidate,
                "candidate",
                candidate!.Value,
                run.PairOrder,
                $"pair-{run.RunId}"));
        }

        var protocol = Protocol(
            $"retained-growth-{quantityId}-paired-v1",
            ScenarioStatisticalMethodIds.PairedDelta,
            minimum: 1);
        var request = new ScenarioStatisticalEvidenceRequest(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            protocol,
            [
                new ScenarioStatisticalArm(
                    "baseline",
                    ScenarioStatisticalArmRole.Baseline,
                    Identity(evidence.BaselineCompatibility, quantityId, unit, "baseline"),
                    baselineAttempts,
                    baselineObservations),
                new ScenarioStatisticalArm(
                    "candidate",
                    ScenarioStatisticalArmRole.Candidate,
                    Identity(evidence.CandidateCompatibility, quantityId, unit, "candidate"),
                    candidateAttempts,
                    candidateObservations),
            ]);
        return ScenarioStatisticalEvidenceHarness.Evaluate(request);
    }

    private static List<DerivedObservation> BuildPairChangeRequest(
        RetainedGrowthScenarioEvidence evidence)
    {
        var result = new List<DerivedObservation>(evidence.Runs.Count);
        foreach (var run in evidence.Runs)
        {
            var baseline = FindType(run.Baseline, evidence.TargetType);
            var candidate = FindType(run.Candidate, evidence.TargetType);
            var passed = baseline is not null
                && candidate is not null
                && run.Baseline.CaptureCompleted
                && run.Candidate.CaptureCompleted
                && run.Baseline.PositiveRetentionSupported
                && run.Candidate.PositiveRetentionSupported;
            result.Add(new(
                run,
                passed ? candidate!.Bytes - baseline!.Bytes : null,
                passed,
                CombinedRepresentation(run.Baseline, run.Candidate),
                run.Baseline.LostOrDropped + run.Candidate.LostOrDropped));
        }

        return result;
    }

    private static List<DerivedObservation> BuildPersistenceObservations(
        RetainedGrowthScenarioEvidence evidence)
    {
        var result = new List<DerivedObservation>(evidence.Runs.Count);
        foreach (var run in evidence.Runs)
        {
            var baseline = FindType(run.Baseline, evidence.TargetType);
            var candidate = FindType(run.Candidate, evidence.TargetType);
            var persistence = FindType(run.Persistence, evidence.TargetType);
            var passed = baseline is not null
                && candidate is not null
                && persistence is not null
                && run.Baseline.CaptureCompleted
                && run.Candidate.CaptureCompleted
                && run.Persistence.CaptureCompleted
                && run.Baseline.PositiveRetentionSupported
                && run.Candidate.PositiveRetentionSupported
                && run.Persistence.PositiveRetentionSupported;
            double? value = null;
            if (passed)
            {
                var positiveGrowth = candidate!.Bytes > baseline!.Bytes
                    && candidate.Instances > baseline.Instances;
                var persisted = persistence!.Bytes >= candidate.Bytes * 0.95
                    && persistence.Instances >= candidate.Instances;
                value = positiveGrowth && persisted ? 1 : 0;
            }

            result.Add(new(
                run,
                value,
                passed,
                CombinedRepresentation(run.Baseline, run.Candidate, run.Persistence),
                run.Baseline.LostOrDropped
                    + run.Candidate.LostOrDropped
                    + run.Persistence.LostOrDropped));
        }

        return result;
    }

    private static DerivedObservation[] BuildStabilizationObservations(
        RetainedGrowthScenarioEvidence evidence)
        => evidence.Runs.Select(run => new DerivedObservation(
            run,
            run.Candidate.PostGcStabilized && run.Persistence.PostGcStabilized ? 1 : 0,
            run.Candidate.CaptureCompleted && run.Persistence.CaptureCompleted,
            CombinedRepresentation(run.Candidate, run.Persistence),
            run.Candidate.LostOrDropped + run.Persistence.LostOrDropped)).ToArray();

    private static ConcentrationInput BuildConcentrationObservations(
        RetainedGrowthScenarioEvidence evidence)
    {
        var observations = new List<DerivedObservation>(evidence.Runs.Count);
        var topTypes = new List<string>(evidence.Runs.Count);
        foreach (var run in evidence.Runs)
        {
            var baseline = run.Baseline.Types.ToDictionary(type => type.TypeName, StringComparer.Ordinal);
            var candidate = run.Candidate.Types.ToDictionary(type => type.TypeName, StringComparer.Ordinal);
            var positive = baseline.Keys.Intersect(candidate.Keys, StringComparer.Ordinal)
                .Select(type => (Type: type, Delta: candidate[type].Bytes - baseline[type].Bytes))
                .Where(item => item.Delta > 0)
                .OrderByDescending(item => item.Delta)
                .ThenBy(item => item.Type, StringComparer.Ordinal)
                .ToArray();
            var total = positive.Sum(item => item.Delta);
            var passed = total > 0
                && run.Baseline.CaptureCompleted
                && run.Candidate.CaptureCompleted
                && run.Baseline.PositiveRetentionSupported
                && run.Candidate.PositiveRetentionSupported;
            if (passed)
            {
                topTypes.Add(positive[0].Type);
            }

            observations.Add(new(
                run,
                passed ? (double)positive[0].Delta / total : null,
                passed,
                CombinedRepresentation(run.Baseline, run.Candidate),
                run.Baseline.LostOrDropped + run.Candidate.LostOrDropped));
        }

        return new(
            observations,
            topTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static DerivedObservation[] BuildAllocationTrafficObservations(
        RetainedGrowthScenarioEvidence evidence)
        => evidence.Runs.Select(run => new DerivedObservation(
            run,
            run.AllocationTraffic?.SampledBytes,
            run.AllocationTraffic is not null,
            run.AllocationTraffic?.Representation ?? ScenarioStatisticalRepresentation.Exact,
            run.AllocationTraffic?.LostOrDropped ?? 0)).ToArray();

    private static ScenarioStatisticalEvidenceResult EvaluateSingle(
        RetainedGrowthScenarioEvidence evidence,
        IReadOnlyList<DerivedObservation> values,
        string methodId,
        string quantityId,
        string unit,
        int minimum,
        int? bootstrapResamples = null,
        double? confidenceLevel = null,
        string? parentMethodId = null)
    {
        var attempts = values.Select(value => new ScenarioStatisticalAttempt(
            value.Run.RunId,
            "attempt-1",
            value.Passed
                ? ScenarioStatisticalAttemptOutcome.Passed
                : ScenarioStatisticalAttemptOutcome.Failed,
            value.Passed ? null : "required_evidence_unavailable",
            value.Passed ? null : "required_evidence_unavailable")).ToArray();
        var observations = values
            .Where(value => value.Passed && value.Value is not null)
            .Select(value => new ScenarioStatisticalObservation(
                $"{quantityId}-{value.Run.RunId}",
                value.Run.RunId,
                "attempt-1",
                $"environment-{value.Run.RunId}",
                $"derived-{quantityId}-{value.Run.RunId}",
                null,
                value.Run.RunId,
                value.Value!.Value,
                null,
                SourceEventCount: 1,
                value.LostOrDropped,
                value.Representation))
            .ToArray();
        var protocol = Protocol(
            $"retained-growth-{quantityId}-{methodId}-v1",
            methodId,
            minimum,
            bootstrapResamples,
            confidenceLevel,
            parentMethodId);
        var request = new ScenarioStatisticalEvidenceRequest(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            protocol,
            [
                new ScenarioStatisticalArm(
                    "observed",
                    ScenarioStatisticalArmRole.Observed,
                    Identity(
                        evidence.CandidateCompatibility,
                        quantityId,
                        unit,
                        evidence.Variant.ToString().ToLowerInvariant()),
                    attempts,
                    observations),
            ],
            "observed");
        return ScenarioStatisticalEvidenceHarness.Evaluate(request);
    }

    private static ScenarioStatisticalProtocol Protocol(
        string id,
        string methodId,
        int minimum,
        int? bootstrapResamples = null,
        double? confidenceLevel = null,
        string? parentMethodId = null)
        => new(
            id,
            "1",
            new ScenarioStatisticalMethod(methodId, 1),
            ScenarioStatisticalIndependenceUnit.ProcessRun,
            minimum,
            AdmitLoss: false,
            AdmitApproximation: false,
            bootstrapResamples,
            confidenceLevel,
            parentMethodId);

    private static ScenarioStatisticalCompatibilityIdentity Identity(
        RetainedGrowthCompatibility compatibility,
        string quantityId,
        string unit,
        string phase)
        => new(
        [
            new("target.runtime-flavor", compatibility.RuntimeFlavor),
            new("target.runtime-version-policy", compatibility.RuntimeVersionPolicy),
            new("target.os", compatibility.Os),
            new("target.architecture", compatibility.Architecture),
            new("target.process-run-id", $"per-observation-{phase}"),
            new("backend.id", compatibility.BackendId),
            new("backend.version", compatibility.BackendVersion),
            new("quantity.id", quantityId),
            new("quantity.unit", unit),
            new("quantity.aggregation", "one-value-per-process-run"),
            new("quantity.normalization", "none"),
            new("quantity.filters", "common-observed-types"),
            new("quantity.representation", "exact"),
            new("capture.duration", compatibility.CaptureDuration),
            new("capture.windowing", compatibility.CaptureWindowing),
            new("capture.providers", compatibility.CaptureProviders),
            new("capture.sampling-period", compatibility.CaptureSamplingPeriod),
            new("capture.retention", compatibility.CaptureRetention),
            new("capture.enrichment", compatibility.CaptureEnrichment),
            new("workload.scenario", compatibility.WorkloadScenario),
            new("workload.version", compatibility.WorkloadVersion),
            new("workload.parameters", compatibility.WorkloadParameters),
            new("workload.concurrency", compatibility.WorkloadConcurrency),
            new("workload.deployment", compatibility.WorkloadDeployment),
            new(ComparisonAxis, phase),
            new("independence.unit", "process_run"),
            new("independence.nesting", "type-rows->captures->paired-captures->process-runs"),
            new("independence.pairing-key", "process-run"),
            new("comparison.axis", ComparisonAxis),
        ]);

    private static ScenarioStatisticalObservation Observation(
        RetainedGrowthScenarioRun run,
        RetainedGrowthHeapObservation capture,
        string arm,
        double value,
        ScenarioStatisticalPairOrder order,
        string pairKey)
        => new(
            $"{arm}-{run.RunId}",
            run.RunId,
            "attempt-1",
            $"environment-{run.RunId}",
            capture.CaptureId,
            null,
            run.RunId,
            value,
            null,
            SourceEventCount: capture.Types.Count,
            capture.LostOrDropped,
            capture.Representation,
            pairKey,
            order);

    private static RetainedGrowthTypeObservation? FindType(
        RetainedGrowthHeapObservation observation,
        string typeName)
        => observation.Types.SingleOrDefault(type =>
            string.Equals(type.TypeName, typeName, StringComparison.Ordinal));

    private static ScenarioStatisticalRepresentation CombinedRepresentation(
        params RetainedGrowthHeapObservation[] captures)
        => captures.Select(capture => capture.Representation).Max();

    private static RetainedGrowthRootPathSummary SummarizeRootPaths(
        RetainedGrowthScenarioEvidence evidence)
    {
        var captures = evidence.Runs
            .SelectMany(run => new[] { run.Baseline, run.Candidate, run.Persistence })
            .ToArray();
        var available = captures.Count(capture =>
            capture.RootPathAvailability == RetainedGrowthRootPathAvailability.Available
            && capture.RootPathCount is > 0);
        var availability = available == captures.Length
            ? RetainedGrowthRootPathAvailability.Available
            : captures.Any(capture =>
                capture.RootPathAvailability == RetainedGrowthRootPathAvailability.IncompleteEvidence)
                ? RetainedGrowthRootPathAvailability.IncompleteEvidence
                : RetainedGrowthRootPathAvailability.UnavailableByMechanism;
        return new(availability, available, captures.Length);
    }

    private static RetainedGrowthPositiveEvidence DeterminePositiveEvidence(
        ScenarioStatisticalEvidenceResult bytes,
        ScenarioStatisticalEvidenceResult instances,
        ScenarioStatisticalEvidenceResult repeatedChange,
        ScenarioStatisticalEvidenceResult persistence,
        ScenarioStatisticalEvidenceResult stabilization,
        ScenarioStatisticalEvidenceResult concentration,
        RetainedGrowthScenarioEvidence evidence)
    {
        var required = new[]
        {
            bytes.Availability,
            instances.Availability,
            repeatedChange.Availability,
            persistence.Availability,
            stabilization.Availability,
        };
        if (required.Any(availability => availability != ScenarioStatisticalAvailability.Available)
            || concentration.Availability is ScenarioStatisticalAvailability.Incompatible
                or ScenarioStatisticalAvailability.LossDetected
                or ScenarioStatisticalAvailability.UnsupportedApproximation
            || evidence.Runs.SelectMany(run => new[] { run.Baseline, run.Candidate, run.Persistence })
                .Any(capture => !capture.CaptureCompleted
                    || capture.LostOrDropped > 0
                    || capture.Representation != ScenarioStatisticalRepresentation.Exact))
        {
            return RetainedGrowthPositiveEvidence.Inconclusive;
        }

        return bytes.Delta?.Absolute > 0
            && instances.Delta?.Absolute > 0
            && repeatedChange.Estimate > 0
            && persistence.Rate?.Value == 1
            && stabilization.Rate?.Value == 1
            && concentration.Availability == ScenarioStatisticalAvailability.Available
            && concentration.Estimate >= 0.5
                ? RetainedGrowthPositiveEvidence.Supported
                : RetainedGrowthPositiveEvidence.NotEstablished;
    }

    private static List<string> BuildLimitations(
        RetainedGrowthScenarioEvidence evidence,
        RetainedGrowthRootPathSummary rootPaths,
        int inducedGen2)
    {
        var limitations = new List<string>
        {
            "Positive retained-growth evidence does not establish a memory leak, defect, cause, or future trajectory.",
            "Allocation traffic is reported separately and is not treated as retained heap.",
            "A control that does not establish positive retained growth does not prove absence of retention.",
            "The known-shape workload intentionally retains objects; retention is not automatically an application defect.",
        };
        if (inducedGen2 > 0)
        {
            limitations.Add(
                $"{inducedGen2.ToString(CultureInfo.InvariantCulture)} Gen2 collection(s) were induced across heap captures; capture and explicit stabilization can change the observed heap.");
        }
        if (rootPaths.Availability != RetainedGrowthRootPathAvailability.Available)
        {
            limitations.Add(
                "GC root/path attribution was unavailable or incomplete; type growth alone does not identify the retaining owner.");
        }
        if (evidence.Runs.Count < RetainedGrowthScenarioBounds.MinimumRobustRuns)
        {
            limitations.Add(
                $"Robust spread and interval require at least {RetainedGrowthScenarioBounds.MinimumRobustRuns} independent process runs in this scenario protocol.");
        }

        return limitations;
    }

    private static void Validate(RetainedGrowthScenarioEvidence evidence)
    {
        if (evidence.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported retained-growth scenario schema {evidence.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(evidence.Id)
            || string.IsNullOrWhiteSpace(evidence.TargetType))
        {
            throw new InvalidDataException("Scenario id and target type are required.");
        }
        if (evidence.Runs.Count is 0 or > RetainedGrowthScenarioBounds.MaximumRuns)
        {
            throw new InvalidDataException(
                $"Scenario must contain 1..{RetainedGrowthScenarioBounds.MaximumRuns} runs.");
        }
        if (evidence.Runs.Select(run => run.RunId).Distinct(StringComparer.Ordinal).Count()
            != evidence.Runs.Count)
        {
            throw new InvalidDataException("Process-run identities must be unique.");
        }
        foreach (var capture in evidence.Runs.SelectMany(run =>
                     new[] { run.Baseline, run.Candidate, run.Persistence }))
        {
            if (capture.Types.Count > RetainedGrowthScenarioBounds.MaximumTypesPerCapture)
            {
                throw new InvalidDataException(
                    $"Capture '{capture.CaptureId}' exceeds the type-row bound.");
            }
            if (capture.Types.Select(type => type.TypeName).Distinct(StringComparer.Ordinal).Count()
                != capture.Types.Count)
            {
                throw new InvalidDataException(
                    $"Capture '{capture.CaptureId}' contains duplicate type rows.");
            }
            if (capture.TotalHeapBytes < 0
                || capture.LostOrDropped < 0
                || capture.CaptureInducedGen2Collections < 0
                || capture.Types.Any(type => type.Bytes < 0 || type.Instances < 0))
            {
                throw new InvalidDataException(
                    $"Capture '{capture.CaptureId}' contains a negative count.");
            }
        }
    }

    private sealed record DerivedObservation(
        RetainedGrowthScenarioRun Run,
        double? Value,
        bool Passed,
        ScenarioStatisticalRepresentation Representation,
        long LostOrDropped);

    private sealed record ConcentrationInput(
        IReadOnlyList<DerivedObservation> Observations,
        IReadOnlyList<string> TopGrowingTypes);
}
