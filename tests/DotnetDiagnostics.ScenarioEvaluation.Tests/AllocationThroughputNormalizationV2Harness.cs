using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

[JsonConverter(
    typeof(JsonStringEnumConverter<AllocationThroughputNormalizationV2Decision>))]
internal enum AllocationThroughputNormalizationV2Decision
{
    NoGo,
    NormalizationSurvives,
    Inconclusive,
}

internal sealed record AllocationThroughputNormalizationV2ControlResult(
    bool Assessable,
    bool WithinPracticalSanity,
    decimal AaEnvelope,
    AllocationDirectionalPairSummary AbsoluteRelativeDeltas,
    string? AbstentionReason);

internal sealed record AllocationThroughputNormalizationV2CohortResult(
    string CohortId,
    string OperationIdentity,
    string BaselineShape,
    string CurrentShape,
    int BaselinePauseMilliseconds,
    int BaselinePauseEveryOperations,
    bool Assessable,
    bool WorkloadManipulationEstablished,
    bool NormalizedQuantityEquivalent,
    bool MedianExceedsEquivalenceMargin,
    decimal InlierMedianAbsoluteRelativeDelta,
    decimal AaEnvelope,
    decimal EquivalenceMargin,
    AllocationDirectionalPairSummary ThroughputMultipliers,
    AllocationDirectionalPairSummary EffectAbsoluteRelativeDeltas,
    string Classification,
    string? AbstentionReason);

internal sealed record AllocationThroughputNormalizationV2Report(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolVersion,
    AllocationThroughputNormalizationV2Decision Decision,
    int AttemptedPairs,
    int AttemptedSides,
    int PassedSides,
    int FailedOrAbortedSides,
    long MaximumObservedAllocationEvents,
    AllocationThroughputNormalizationV2ControlResult HealthyUnchangedRateControl,
    IReadOnlyList<AllocationThroughputNormalizationV2CohortResult> Cohorts,
    bool BlindedEvaluationRan,
    IReadOnlyList<string> Limitations);

internal static class AllocationThroughputNormalizationV2Harness
{
    public static AllocationThroughputNormalizationV2Report Evaluate(
        AllocationDirectionalFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        Validate(fixture);

        var observations = fixture.Pairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();
        var passedSides = observations.Count(observation =>
            observation.Outcome == AllocationDirectionalAttemptOutcome.Passed);
        var failedSides = observations.Length - passedSides;
        var maximumEvents = observations
            .Where(observation =>
                observation.Outcome == AllocationDirectionalAttemptOutcome.Passed)
            .Select(observation => observation.SampledAllocationTicks)
            .DefaultIfEmpty()
            .Max();
        var healthyGate =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateHealthyAa(
                PairsForEntry(
                    fixture,
                    AllocationThroughputNormalizationV2CapturePlan.HealthyControl));
        var healthySummary = healthyGate.AbsoluteRelativeDeltas;
        var healthyAssessable = healthyGate.Assessable
            && HasPoweredCell(healthySummary);
        var healthyWithinSanity = healthyAssessable
            && healthySummary.Median
                <= AllocationDirectionalCalibrationBounds
                    .PracticalRelativeEquivalenceFloor
            && healthySummary.Inliers.All(value =>
                value
                    <= AllocationDirectionalCalibrationBounds
                        .PracticalRelativeEquivalenceFloor);
        var healthy = new AllocationThroughputNormalizationV2ControlResult(
            healthyAssessable,
            healthyWithinSanity,
            healthyGate.AaEnvelope,
            healthySummary,
            healthyAssessable
                ? healthyWithinSanity
                    ? null
                    : healthyGate.AbstentionReason
                : "Healthy A/A requires five valid pairs and at least four retained non-outlier pairs.");
        var cohorts = AllocationThroughputNormalizationV2CapturePlan.Cohorts
            .Select(cohort => EvaluateCohort(fixture, cohort))
            .ToArray();
        var completeInventory = failedSides == 0;
        var withinOperatingLimit = maximumEvents
            <= AllocationThroughputNormalizationV2CapturePlan
                .AllocationEventOperatingLimit;
        var allAssessable = healthy.Assessable
            && healthy.WithinPracticalSanity
            && cohorts.All(cohort =>
                cohort.Assessable
                && cohort.WorkloadManipulationEstablished);
        var decision = !completeInventory
                || !withinOperatingLimit
                || !allAssessable
            ? AllocationThroughputNormalizationV2Decision.Inconclusive
            : cohorts.All(cohort =>
                cohort.MedianExceedsEquivalenceMargin)
                ? AllocationThroughputNormalizationV2Decision.NoGo
                : cohorts.All(cohort =>
                    cohort.NormalizedQuantityEquivalent)
                    ? AllocationThroughputNormalizationV2Decision
                        .NormalizationSurvives
                    : AllocationThroughputNormalizationV2Decision.Inconclusive;

        return new(
            SchemaVersion: 1,
            AllocationThroughputNormalizationV2Protocol.ProtocolId,
            AllocationThroughputNormalizationV2Protocol.ProtocolVersion,
            decision,
            fixture.Pairs.Count,
            observations.Length,
            passedSides,
            failedSides,
            maximumEvents,
            healthy,
            cohorts,
            BlindedEvaluationRan: false,
            [
                "This focused falsifier evaluates only estimated sampled bytes per logical operation under throughput-only pacing manipulation.",
                "Every A/A, effect, and healthy-control cell has five independently started pairs, pair-first median/raw MAD summaries, retained outliers, and a minimum of four inliers.",
                "The practical equivalence margin is max(workload-specific robust A/A envelope, 10%).",
                "Operations per second proves only the manipulation. Sampled bytes per second and allocation-event counts remain descriptive provenance.",
                "GCAllocationTick estimated bytes are fixed-stride sampled weights, not exact allocated bytes, retained bytes, object counts, severity, health, regression, or causality.",
                "Every side is a separately started process captured once through repository-built MCP over real stdio. Failures and aborts remain in denominators without retry or replacement.",
                "No blinded evaluation, weighted score, post-execution threshold tuning, product implementation, or downstream design issue is authorized by this protocol.",
            ]);
    }

    public static string SerializeReport(
        AllocationThroughputNormalizationV2Report report)
        => System.Text.Json.JsonSerializer.Serialize(
            report,
            AllocationDirectionalJson.Options);

    private static AllocationThroughputNormalizationV2CohortResult EvaluateCohort(
        AllocationDirectionalFixture fixture,
        AllocationThroughputNormalizationV2CohortPlan cohort)
    {
        var pairs = PairsForEntry(fixture, cohort.AaEntry)
            .Concat(PairsForEntry(fixture, cohort.EffectEntry))
            .ToArray();
        var gate =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateThroughput(
                pairs);
        var aaSummary = gate.Stability.AaAbsoluteRelativeDeltas;
        var effectSummary = gate.Stability.EffectAbsoluteRelativeDeltas;
        var throughputSummary = gate.ThroughputMultipliers;
        var assessable = gate.Assessable
            && HasPoweredCell(aaSummary)
            && HasPoweredCell(effectSummary)
            && HasPoweredCell(throughputSummary);
        var workloadManipulationEstablished = assessable
            && throughputSummary.Median
                > AllocationDirectionalCalibrationBounds
                    .MinimumThroughputMultiplier
            && throughputSummary.Inliers.All(value =>
                value
                    > AllocationDirectionalCalibrationBounds
                        .MinimumThroughputMultiplier);
        var normalizedQuantityEquivalent = assessable
            && effectSummary.Median <= gate.EquivalenceMargin
            && effectSummary.Inliers.All(value =>
                value <= gate.EquivalenceMargin);
        var inlierMedian = Median(
            effectSummary.Inliers);
        var medianExceeds = assessable
            && workloadManipulationEstablished
            && inlierMedian > gate.EquivalenceMargin;
        var classification = !assessable
                || !workloadManipulationEstablished
            ? "inconclusive"
            : medianExceeds
                ? "normalization-failed"
                : normalizedQuantityEquivalent
                    ? "within-equivalence-margin"
                    : "indeterminate-boundary-or-dispersion";
        return new(
            cohort.CohortId,
            cohort.BaselinePacing.OperationIdentity,
            cohort.BaselinePacing.Shape,
            cohort.CurrentPacing.Shape,
            cohort.BaselinePacing.PauseMilliseconds,
            cohort.BaselinePacing.PauseEveryOperations,
            assessable,
            workloadManipulationEstablished,
            normalizedQuantityEquivalent,
            medianExceeds,
            inlierMedian,
            gate.Stability.AaEnvelope,
            gate.EquivalenceMargin,
            throughputSummary,
            effectSummary,
            classification,
            assessable
                ? workloadManipulationEstablished
                    ? normalizedQuantityEquivalent
                        ? null
                        : gate.AbstentionReason
                    : "The workload manipulation itself was not established above 1.25x operations per second."
                : "Each A/A, effect, and throughput cell requires five valid pairs and at least four retained non-outlier pairs.");
    }

    private static AllocationDirectionalPair[] PairsForEntry(
        AllocationDirectionalFixture fixture,
        AllocationDirectionalPlanEntry entry)
        => fixture.Pairs.Where(pair =>
                pair.PairId.StartsWith(
                    $"{entry.PairPrefix}-",
                    StringComparison.Ordinal))
            .ToArray();

    private static void Validate(AllocationDirectionalFixture fixture)
    {
        AllocationThroughputNormalizationV2CapturePlan.ValidateConstruction();
        if (fixture.SchemaVersion != AllocationDirectionalJson.CurrentSchemaVersion
            || !string.Equals(
                fixture.ProtocolId,
                AllocationThroughputNormalizationV2Protocol.ProtocolId,
                StringComparison.Ordinal)
            || !string.Equals(
                fixture.ProtocolVersion,
                AllocationThroughputNormalizationV2Protocol.ProtocolVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                fixture.SourceBaseCommit,
                AllocationThroughputNormalizationV2Protocol.SourceBaseCommit,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fixture.ProtocolSha256)
            || !string.Equals(
                fixture.ProtocolSha256,
                fixture.CanonicalProtocolSha256,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fixture.Environment)
            || fixture.Pairs.Count
                != AllocationThroughputNormalizationV2CapturePlan.TotalPairs
            || fixture.Pairs.Select(pair => pair.PairId)
                .Distinct(StringComparer.Ordinal).Count() != fixture.Pairs.Count)
        {
            throw new InvalidDataException(
                "The throughput-normalization fixture identity or inventory is invalid.");
        }

        var pairIndex = 0;
        foreach (var entry in
                 AllocationThroughputNormalizationV2CapturePlan.Entries)
        {
            for (var repetition = 1;
                 repetition <=
                 AllocationThroughputNormalizationV2CapturePlan.RequiredPairs;
                 repetition++)
            {
                var pair = fixture.Pairs[pairIndex++];
                if (!string.Equals(
                        pair.PairId,
                        $"{entry.PairPrefix}-{repetition:D2}",
                        StringComparison.Ordinal)
                    || pair.Scenario != entry.Scenario
                    || !string.Equals(
                        pair.ComparisonAxis,
                        entry.ComparisonAxis,
                        StringComparison.Ordinal)
                    || !MatchesSide(pair.Baseline, entry.Baseline)
                    || !MatchesSide(pair.Current, entry.Current))
                {
                    throw new InvalidDataException(
                        "The throughput-normalization fixture does not match the frozen pair order and side plan.");
                }

                ValidateEqualAllocationInvariant(pair);
            }
        }

        if (pairIndex != fixture.Pairs.Count)
        {
            throw new InvalidDataException(
                "The throughput-normalization fixture contains unregistered pairs.");
        }

        var observations = fixture.Pairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();
        if (observations.Length
                != AllocationThroughputNormalizationV2CapturePlan.LiveMcpSides
            || observations.Any(observation =>
                observation.Origin != AllocationDirectionalOrigin.LiveMcp)
            || observations.Select(observation => observation.AttemptId)
                .Distinct(StringComparer.Ordinal).Count() != observations.Length
            || observations.Select(observation => observation.CaptureId)
                .Any(string.IsNullOrWhiteSpace)
            || observations.Select(observation => observation.CaptureId)
                .Distinct(StringComparer.Ordinal).Count() != observations.Length
            || observations.Select(observation => observation.ProcessIdentity)
                .Any(string.IsNullOrWhiteSpace)
            || observations.Select(observation => observation.ProcessIdentity)
                .Distinct(StringComparer.Ordinal).Count() != observations.Length
            || observations.Any(observation =>
                observation.QuantityEvidence is null
                || observation.Types.Count
                    > AllocationDirectionalBounds.MaximumRankedRows
                || observation.Sites.Count
                    > AllocationDirectionalBounds.MaximumRankedRows))
        {
            throw new InvalidDataException(
                "The throughput-normalization observations violate identity, provenance, or bounded-geometry requirements.");
        }

        foreach (var observation in observations)
        {
            var evidence = observation.AttemptEvidence
                ?? throw new InvalidDataException(
                    "Every throughput-normalization observation requires attempt evidence.");
            if (observation.Outcome == AllocationDirectionalAttemptOutcome.Passed)
            {
                if (evidence.FailureCategory is not null
                    || evidence.Stage != AllocationDirectionalAttemptStage.Completed
                    || evidence.IsError != false)
                {
                    throw new InvalidDataException(
                        "A passed throughput-normalization side has non-truthful success provenance.");
                }
            }
            else if (evidence.FailureCategory is null)
            {
                throw new InvalidDataException(
                    "A failed or aborted throughput-normalization side lacks a truthful failure category.");
            }
        }
    }

    private static bool MatchesSide(
        AllocationDirectionalObservation observation,
        AllocationDirectionalPlannedSide planned)
        => string.Equals(
                observation.WorkloadIdentity,
                planned.WorkloadIdentity,
                StringComparison.Ordinal)
            && string.Equals(
                observation.Shape,
                planned.Shape,
                StringComparison.Ordinal)
            && string.Equals(
                observation.Phase,
                planned.Phase,
                StringComparison.Ordinal)
            && observation.Workers == planned.Workers
            && observation.CaptureDurationSeconds
                == AllocationThroughputNormalizationV2CapturePlan
                    .CaptureDurationSeconds
            && (observation.Outcome != AllocationDirectionalAttemptOutcome.Passed
                || observation.RequestedPayloadBytesPerOperation
                    == planned.ExpectedPayloadBytesPerOperation
                && observation.RetainedBytes == planned.RetainedBytes);

    private static void ValidateEqualAllocationInvariant(
        AllocationDirectionalPair pair)
    {
        if (!string.Equals(
                pair.Baseline.WorkloadIdentity,
                pair.Current.WorkloadIdentity,
                StringComparison.Ordinal)
            || pair.Baseline.Workers != pair.Current.Workers
            || pair.Baseline.CaptureDurationSeconds
                != pair.Current.CaptureDurationSeconds)
        {
            throw new InvalidDataException(
                $"Pair '{pair.PairId}' violates the equal-allocation, worker, capture, or payload invariant.");
        }

        if (pair.Baseline.Outcome == AllocationDirectionalAttemptOutcome.Passed
            && pair.Current.Outcome == AllocationDirectionalAttemptOutcome.Passed
            && (pair.Baseline.RequestedPayloadBytesPerOperation
                    != pair.Current.RequestedPayloadBytesPerOperation
                || pair.Baseline.RetainedBytes != pair.Current.RetainedBytes))
        {
            throw new InvalidDataException(
                $"Passed pair '{pair.PairId}' violates the equal-allocation payload or retained-value invariant.");
        }
    }

    private static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static bool HasPoweredCell(
        AllocationDirectionalPairSummary summary)
        => summary.PairCount
                >= AllocationThroughputNormalizationV2CapturePlan.RequiredPairs
            && summary.Inliers.Count
                >= AllocationThroughputNormalizationV2CapturePlan
                    .MinimumInlierPairs;
}
