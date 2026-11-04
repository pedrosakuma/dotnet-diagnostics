using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

[JsonConverter(
    typeof(JsonStringEnumConverter<AllocationThroughputNormalizationDecision>))]
internal enum AllocationThroughputNormalizationDecision
{
    NoGo,
    NormalizationSurvives,
    Inconclusive,
}

internal sealed record AllocationThroughputNormalizationControlResult(
    bool Assessable,
    bool WithinPracticalSanity,
    decimal AaEnvelope,
    AllocationDirectionalPairSummary AbsoluteRelativeDeltas,
    string? AbstentionReason);

internal sealed record AllocationThroughputNormalizationCohortResult(
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

internal sealed record AllocationThroughputNormalizationReport(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolVersion,
    AllocationThroughputNormalizationDecision Decision,
    int AttemptedPairs,
    int AttemptedSides,
    int PassedSides,
    int FailedOrAbortedSides,
    long MaximumObservedAllocationEvents,
    AllocationThroughputNormalizationControlResult HealthyUnchangedRateControl,
    IReadOnlyList<AllocationThroughputNormalizationCohortResult> Cohorts,
    bool BlindedEvaluationRan,
    IReadOnlyList<string> Limitations);

internal static class AllocationThroughputNormalizationHarness
{
    public static AllocationThroughputNormalizationReport Evaluate(
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
                    AllocationThroughputNormalizationCapturePlan.HealthyControl));
        var healthy = new AllocationThroughputNormalizationControlResult(
            healthyGate.Assessable,
            healthyGate.WithinPracticalSanity,
            healthyGate.AaEnvelope,
            healthyGate.AbsoluteRelativeDeltas,
            healthyGate.AbstentionReason);
        var cohorts = AllocationThroughputNormalizationCapturePlan.Cohorts
            .Select(cohort => EvaluateCohort(fixture, cohort))
            .ToArray();
        var completeInventory = failedSides == 0;
        var withinOperatingLimit = maximumEvents
            <= AllocationThroughputNormalizationCapturePlan
                .AllocationEventOperatingLimit;
        var allAssessable = healthy.Assessable
            && healthy.WithinPracticalSanity
            && cohorts.All(cohort =>
                cohort.Assessable
                && cohort.WorkloadManipulationEstablished);
        var decision = !completeInventory
                || !withinOperatingLimit
                || !allAssessable
            ? AllocationThroughputNormalizationDecision.Inconclusive
            : cohorts.All(cohort =>
                cohort.MedianExceedsEquivalenceMargin)
                ? AllocationThroughputNormalizationDecision.NoGo
                : cohorts.All(cohort =>
                    cohort.NormalizedQuantityEquivalent)
                    ? AllocationThroughputNormalizationDecision
                        .NormalizationSurvives
                    : AllocationThroughputNormalizationDecision.Inconclusive;

        return new(
            SchemaVersion: 1,
            AllocationThroughputNormalizationProtocol.ProtocolId,
            AllocationThroughputNormalizationProtocol.ProtocolVersion,
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
                "Every cohort has its own pair-first A/A envelope, median/raw MAD summaries, retained outliers, minimum three pairs, and minimum two inliers.",
                "The practical equivalence margin is max(workload-specific robust A/A envelope, 10%).",
                "Operations per second proves only the manipulation. Sampled bytes per second and allocation-event counts remain descriptive provenance.",
                "GCAllocationTick estimated bytes are fixed-stride sampled weights, not exact allocated bytes, retained bytes, object counts, severity, health, regression, or causality.",
                "Every side is a separately started process captured once through repository-built MCP over real stdio. Failures and aborts remain in denominators without retry or replacement.",
                "No blinded evaluation, weighted score, post-execution threshold tuning, product implementation, or downstream design issue is authorized by this protocol.",
            ]);
    }

    public static string SerializeReport(
        AllocationThroughputNormalizationReport report)
        => System.Text.Json.JsonSerializer.Serialize(
            report,
            AllocationDirectionalJson.Options);

    private static AllocationThroughputNormalizationCohortResult EvaluateCohort(
        AllocationDirectionalFixture fixture,
        AllocationThroughputNormalizationCohortPlan cohort)
    {
        var pairs = PairsForEntry(fixture, cohort.AaEntry)
            .Concat(PairsForEntry(fixture, cohort.EffectEntry))
            .ToArray();
        var gate =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateThroughput(
                pairs);
        var inlierMedian = Median(
            gate.Stability.EffectAbsoluteRelativeDeltas.Inliers);
        var medianExceeds = gate.Assessable
            && gate.WorkloadManipulationEstablished
            && gate.Stability.EffectAbsoluteRelativeDeltas.HasRequiredInliers
            && inlierMedian > gate.EquivalenceMargin;
        var classification = !gate.Assessable
                || !gate.WorkloadManipulationEstablished
            ? "inconclusive"
            : medianExceeds
                ? "normalization-failed"
                : gate.EstimatedBytesPerOperationEquivalent
                    ? "within-equivalence-margin"
                    : "indeterminate-boundary-or-dispersion";
        return new(
            cohort.CohortId,
            cohort.BaselinePacing.OperationIdentity,
            cohort.BaselinePacing.Shape,
            cohort.CurrentPacing.Shape,
            cohort.BaselinePacing.PauseMilliseconds,
            cohort.BaselinePacing.PauseEveryOperations,
            gate.Assessable,
            gate.WorkloadManipulationEstablished,
            gate.EstimatedBytesPerOperationEquivalent,
            medianExceeds,
            inlierMedian,
            gate.Stability.AaEnvelope,
            gate.EquivalenceMargin,
            gate.ThroughputMultipliers,
            gate.Stability.EffectAbsoluteRelativeDeltas,
            classification,
            gate.AbstentionReason);
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
        AllocationThroughputNormalizationCapturePlan.ValidateConstruction();
        if (fixture.SchemaVersion != AllocationDirectionalJson.CurrentSchemaVersion
            || !string.Equals(
                fixture.ProtocolId,
                AllocationThroughputNormalizationProtocol.ProtocolId,
                StringComparison.Ordinal)
            || !string.Equals(
                fixture.ProtocolVersion,
                AllocationThroughputNormalizationProtocol.ProtocolVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                fixture.SourceBaseCommit,
                AllocationThroughputNormalizationProtocol.SourceBaseCommit,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fixture.ProtocolSha256)
            || !string.Equals(
                fixture.ProtocolSha256,
                fixture.CanonicalProtocolSha256,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fixture.Environment)
            || fixture.Pairs.Count
                != AllocationThroughputNormalizationCapturePlan.TotalPairs
            || fixture.Pairs.Select(pair => pair.PairId)
                .Distinct(StringComparer.Ordinal).Count() != fixture.Pairs.Count)
        {
            throw new InvalidDataException(
                "The throughput-normalization fixture identity or inventory is invalid.");
        }

        var pairIndex = 0;
        foreach (var entry in
                 AllocationThroughputNormalizationCapturePlan.Entries)
        {
            for (var repetition = 1;
                 repetition <= AllocationDirectionalBounds.RequiredPairs;
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
                != AllocationThroughputNormalizationCapturePlan.LiveMcpSides
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
                == AllocationThroughputNormalizationCapturePlan
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
}
