using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AllocationDirectionalBoundedCalibrationFixture(
    int SchemaVersion,
    string CalibrationId,
    string CalibrationVersion,
    string CreatedAtUtc,
    string Environment,
    string ProbeEvidenceSource,
    int RequiredIndependentPairs,
    AllocationDirectionalCaptureSettings ProbeSettings,
    AllocationDirectionalCaptureSettings OperatingSettings,
    IReadOnlyList<AllocationDirectionalObservation> Probes,
    IReadOnlyList<AllocationDirectionalPair> Pairs);

internal sealed record AllocationDirectionalBoundednessGate(
    bool Assessable,
    bool Passed,
    int ProbeAttempts,
    int ProbeAllocationCapFailures,
    int RequiredPairs,
    int RequiredPassedSides,
    int RequiredFailedSides,
    long MaximumObservedAllocationEvents,
    long AllocationEventOperatingLimit,
    decimal CaptureDurationFractionOfProbe,
    decimal WorkloadDurationFractionOfProbe,
    bool FrameCountAvailable,
    bool ModuleCountAvailable,
    bool FixedStrideShapesFeasible,
    bool PartialAttributionSourcesFeasible,
    string? AbstentionReason);

internal sealed record AllocationDirectionalBoundedCalibrationReport(
    string CalibrationId,
    string CalibrationVersion,
    bool Passed,
    AllocationDirectionalBoundednessGate Boundedness,
    AllocationDirectionalPracticalMovementGate Increase,
    AllocationDirectionalPracticalMovementGate Lower,
    IReadOnlyList<string> Notes);

internal static class AllocationDirectionalBoundedCalibrationPlan
{
    public const string CalibrationId =
        "allocation-directional-bounded-low-periodic";
    public const string CalibrationVersion = "6.0.0";
    public const string ProbeEvidenceFileName =
        "allocation-directional-bounded-calibration-v2.windows-x64.json";
    public const string ProbeEvidenceSha256 =
        "ae3b25b64da3e57316b129363e6ede44db4815457a4f260ac671c10bb25933ae";
    public const int AllocationFrameCap = 16_384;
    public const int AllocationEventCap = 100_000;
    public const decimal AllocationEventOperatingFraction = 0.75m;
    public const long AllocationEventOperatingLimit =
        (long)(AllocationEventCap * AllocationEventOperatingFraction);

    public static AllocationDirectionalCaptureSettings ProbeSettings { get; } =
        new(
            CaptureDurationSeconds: 4,
            ArmDelayMilliseconds: 900,
            WorkloadMilliseconds: 1_500,
            TopN: AllocationDirectionalBounds.MaximumRankedRows,
            AttemptTimeoutSeconds: 45,
            McpCompletionTimeoutSeconds: 30);

    public static AllocationDirectionalCaptureSettings OperatingSettings { get; } =
        new(
            CaptureDurationSeconds: 1,
            ArmDelayMilliseconds: 200,
            WorkloadMilliseconds: 700,
            TopN: AllocationDirectionalBounds.MaximumRankedRows,
            AttemptTimeoutSeconds: 30,
            McpCompletionTimeoutSeconds: 15);

    public static IReadOnlyList<AllocationDirectionalPlannedSide> Probes { get; } =
    [
        Side(
            "bounded-probe-low-v2",
            "low",
            "probe",
            64_000,
            warmupMilliseconds: 180),
        Side(
            "bounded-probe-periodic-a-v2",
            "periodic-a",
            "probe",
            192_000,
            warmupMilliseconds: 180),
        Side(
            "bounded-probe-periodic-b-v2",
            "periodic-b",
            "probe",
            192_000,
            warmupMilliseconds: 180),
    ];

    public static IReadOnlyList<AllocationDirectionalPlanEntry> Entries { get; } =
    [
        Entry(
            AllocationDirectionalScenario.TrueIncrease,
            "bounded-v6-increase-aa",
            "same-workload",
            Side("bounded-allocation-volume-v6", "low-bounded-v4", "steady", 64_000),
            Side("bounded-allocation-volume-v6", "low-bounded-v4", "aa", 64_000)),
        Entry(
            AllocationDirectionalScenario.TrueIncrease,
            "bounded-v6-increase-effect",
            "allocation-volume",
            Side("bounded-allocation-volume-v6", "low-bounded-v4", "steady", 64_000),
            Side(
                "bounded-allocation-volume-v6",
                "increased-bounded-v4",
                "effect",
                576_000)),
        Entry(
            AllocationDirectionalScenario.LowerAllocationStableRetained,
            "bounded-v6-lower-aa",
            "same-workload",
            Side(
                "bounded-retained-volume-v6",
                "increased-bounded-v4",
                "steady",
                576_000,
                retainedBytes: 2 * 1024 * 1024),
            Side(
                "bounded-retained-volume-v6",
                "increased-bounded-v4",
                "aa",
                576_000,
                retainedBytes: 2 * 1024 * 1024)),
        Entry(
            AllocationDirectionalScenario.LowerAllocationStableRetained,
            "bounded-v6-lower-effect",
            "allocation-volume",
            Side(
                "bounded-retained-volume-v6",
                "increased-bounded-v4",
                "steady",
                576_000,
                retainedBytes: 2 * 1024 * 1024),
            Side(
                "bounded-retained-volume-v6",
                "low-bounded-v4",
                "effect",
                64_000,
                retainedBytes: 2 * 1024 * 1024)),
        Entry(
            AllocationDirectionalScenario.FixedStrideBias,
            "bounded-v6-stride-a-aa",
            "same-order-periodic-a",
            Side(
                "bounded-equal-payload-order-a-v6",
                "periodic-a-bounded-v4",
                "steady",
                192_000),
            Side(
                "bounded-equal-payload-order-a-v6",
                "periodic-a-bounded-v4",
                "aa",
                192_000)),
        Entry(
            AllocationDirectionalScenario.FixedStrideBias,
            "bounded-v6-stride-b-aa",
            "same-order-periodic-b",
            Side(
                "bounded-equal-payload-order-b-v6",
                "periodic-b-bounded-v4",
                "steady",
                192_000),
            Side(
                "bounded-equal-payload-order-b-v6",
                "periodic-b-bounded-v4",
                "aa",
                192_000)),
        Entry(
            AllocationDirectionalScenario.FixedStrideBias,
            "bounded-v6-stride-a-to-b",
            "allocation-order-a-to-b",
            Side(
                "bounded-equal-payload-reorder-v6",
                "periodic-a-bounded-v4",
                "steady",
                192_000),
            Side(
                "bounded-equal-payload-reorder-v6",
                "periodic-b-bounded-v4",
                "effect",
                192_000)),
        Entry(
            AllocationDirectionalScenario.FixedStrideBias,
            "bounded-v6-stride-b-to-a",
            "allocation-order-b-to-a",
            Side(
                "bounded-equal-payload-reorder-v6",
                "periodic-b-bounded-v4",
                "steady",
                192_000),
            Side(
                "bounded-equal-payload-reorder-v6",
                "periodic-a-bounded-v4",
                "effect",
                192_000)),
    ];

    private static AllocationDirectionalPlanEntry Entry(
        AllocationDirectionalScenario scenario,
        string pairPrefix,
        string comparisonAxis,
        AllocationDirectionalPlannedSide baseline,
        AllocationDirectionalPlannedSide current)
        => new(scenario, pairPrefix, comparisonAxis, baseline, current);

    private static AllocationDirectionalPlannedSide Side(
        string workloadIdentity,
        string shape,
        string phase,
        long expectedPayloadBytesPerOperation,
        int warmupMilliseconds = 180,
        int retainedBytes = 0)
        => new(
            workloadIdentity,
            shape,
            1,
            phase,
            expectedPayloadBytesPerOperation,
            warmupMilliseconds,
            retainedBytes,
            true);
}

internal static class AllocationDirectionalBoundedCalibrationSelector
{
    public static AllocationDirectionalBoundedCalibrationReport Evaluate(
        AllocationDirectionalBoundedCalibrationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var requiredObservations = fixture.Pairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();
        var passedSides = requiredObservations.Count(Passed);
        var failedSides = requiredObservations.Length - passedSides;
        var maximumEvents = requiredObservations
            .Where(Passed)
            .Select(observation => observation.SampledAllocationTicks)
            .DefaultIfEmpty()
            .Max();
        var probeCapFailures = fixture.Probes.Count(IsAllocationCapFailure);
        var expectedPairCount =
            AllocationDirectionalBoundedCalibrationPlan.Entries.Count
            * AllocationDirectionalBounds.RequiredPairs;
        var captureFraction =
            fixture.OperatingSettings.CaptureDurationSeconds
            / (decimal)fixture.ProbeSettings.CaptureDurationSeconds;
        var workloadFraction =
            fixture.OperatingSettings.WorkloadMilliseconds
            / (decimal)fixture.ProbeSettings.WorkloadMilliseconds;
        var fixedStride = fixture.Pairs.Where(pair =>
            pair.Scenario == AllocationDirectionalScenario.FixedStrideBias).ToArray();
        var fixedStrideFeasible =
            fixedStride.Length == AllocationDirectionalBounds.RequiredPairs * 4
            && fixedStride.All(pair =>
                AllocationDirectionalCalibrationSelector.IsValidPair(pair)
                && pair.Baseline.RequestedPayloadBytesPerOperation
                    == pair.Current.RequestedPayloadBytesPerOperation
                && pair.Baseline.Types.Count > 0
                && pair.Current.Types.Count > 0
                && pair.Baseline.Sites.Count > 0
                && pair.Current.Sites.Count > 0);
        var partialSources = fixture.Pairs.Where(pair =>
            pair.Scenario == AllocationDirectionalScenario.TrueIncrease
            && pair.Current.Phase == "effect").ToArray();
        var partialSourcesFeasible =
            partialSources.Length == AllocationDirectionalBounds.RequiredPairs
            && partialSources.All(pair =>
                AllocationDirectionalCalibrationSelector.IsValidPair(pair)
                && pair.Baseline.Types.Count > 0
                && pair.Current.Types.Count > 0
                && pair.Baseline.Sites.Count > 0
                && pair.Current.Sites.Count > 0);
        var assessable = fixture.Probes.Count
                == AllocationDirectionalBoundedCalibrationPlan.Probes.Count
            && fixture.Pairs.Count == expectedPairCount
            && MatchesPlan(fixture)
            && requiredObservations.Select(value => value.AttemptId)
                .Distinct(StringComparer.Ordinal).Count()
                == requiredObservations.Length
            && requiredObservations.Select(value => value.ProcessIdentity)
                .Where(value => value is not null)
                .Distinct(StringComparer.Ordinal).Count()
                == requiredObservations.Length;
        var boundednessPassed = assessable
            && failedSides == 0
            && maximumEvents
                <= AllocationDirectionalBoundedCalibrationPlan
                    .AllocationEventOperatingLimit
            && captureFraction <= 0.25m
            && workloadFraction <= 0.50m
            && probeCapFailures
                == AllocationDirectionalBoundedCalibrationPlan.Probes.Count
            && fixedStrideFeasible
            && partialSourcesFeasible;
        var boundedness = new AllocationDirectionalBoundednessGate(
            assessable,
            boundednessPassed,
            fixture.Probes.Count,
            probeCapFailures,
            fixture.Pairs.Count,
            passedSides,
            failedSides,
            maximumEvents,
            AllocationDirectionalBoundedCalibrationPlan.AllocationEventOperatingLimit,
            captureFraction,
            workloadFraction,
            FrameCountAvailable: false,
            ModuleCountAvailable: false,
            fixedStrideFeasible,
            partialSourcesFeasible,
            boundednessPassed
                ? null
                : assessable
                    ? "The operating inventory exceeded a boundedness, event-margin, shape-availability, or partial-source requirement."
                    : "The calibration did not retain the exact predeclared probe and required-pair inventory with unique attempts and processes.");
        var increase = AllocationDirectionalPracticalEquivalenceSelector.EvaluateMovement(
            fixture.Pairs.Where(pair =>
                pair.Scenario == AllocationDirectionalScenario.TrueIncrease).ToArray(),
            expectedIncrease: true);
        var lower = AllocationDirectionalPracticalEquivalenceSelector.EvaluateMovement(
            fixture.Pairs.Where(pair =>
                    pair.Scenario
                        == AllocationDirectionalScenario.LowerAllocationStableRetained)
                .ToArray(),
            expectedIncrease: false);
        return new(
            fixture.CalibrationId,
            fixture.CalibrationVersion,
            boundedness.Passed
                && increase.ExpectedMovementEstablished
                && lower.ExpectedMovementEstablished,
            boundedness,
            increase,
            lower,
            [
                "This is a non-final calibration/selector gate and not a directional-feasibility protocol.",
                "Every probe and required side starts once; failures remain retained and are never retried or replaced.",
                "The operating geometry is one second of capture and 700 ms of burst-paced workload: 25% of probe capture duration and 46.67% of probe workload duration.",
                "Successful MCP allocation envelopes expose allocation-event counts but not aggregate frame/module occupancy; frame and module counts are therefore explicitly unavailable rather than inferred.",
                "The observable allocation-event margin is capped at 75% of MaxAllocationEvents.",
                "MaxAllocationFrames remains 16,384 and is neither raised nor weakened.",
            ]);
    }

    public static string SerializeFixture(
        AllocationDirectionalBoundedCalibrationFixture fixture)
        => JsonSerializer.Serialize(fixture, AllocationDirectionalJson.Options);

    public static string SerializeReport(
        AllocationDirectionalBoundedCalibrationReport report)
        => JsonSerializer.Serialize(report, AllocationDirectionalJson.Options);

    private static bool Passed(AllocationDirectionalObservation observation)
        => observation.Outcome == AllocationDirectionalAttemptOutcome.Passed;

    private static bool MatchesPlan(
        AllocationDirectionalBoundedCalibrationFixture fixture)
    {
        if (fixture.SchemaVersion != 1
            || !string.Equals(
                fixture.CalibrationId,
                AllocationDirectionalBoundedCalibrationPlan.CalibrationId,
                StringComparison.Ordinal)
            || !string.Equals(
                fixture.CalibrationVersion,
                AllocationDirectionalBoundedCalibrationPlan.CalibrationVersion,
                StringComparison.Ordinal)
            || fixture.RequiredIndependentPairs
                != AllocationDirectionalBounds.RequiredPairs
            || fixture.ProbeSettings
                != AllocationDirectionalBoundedCalibrationPlan.ProbeSettings
            || fixture.OperatingSettings
                != AllocationDirectionalBoundedCalibrationPlan.OperatingSettings
            || !string.Equals(
                fixture.ProbeEvidenceSource,
                $"{AllocationDirectionalBoundedCalibrationPlan.ProbeEvidenceFileName}; sha256={AllocationDirectionalBoundedCalibrationPlan.ProbeEvidenceSha256}",
                StringComparison.Ordinal)
            || fixture.Probes.Count
                != AllocationDirectionalBoundedCalibrationPlan.Probes.Count)
        {
            return false;
        }

        for (var index = 0; index < fixture.Probes.Count; index++)
        {
            var observation = fixture.Probes[index];
            var planned = AllocationDirectionalBoundedCalibrationPlan.Probes[index];
            if (!MatchesProbe(observation, planned, fixture.ProbeSettings))
            {
                return false;
            }
        }

        var pairIndex = 0;
        foreach (var entry in AllocationDirectionalBoundedCalibrationPlan.Entries)
        {
            for (var repetition = 1;
                 repetition <= AllocationDirectionalBounds.RequiredPairs;
                 repetition++)
            {
                if (pairIndex >= fixture.Pairs.Count
                    || !MatchesPair(
                        fixture.Pairs[pairIndex],
                        entry,
                        repetition,
                        fixture.OperatingSettings))
                {
                    return false;
                }

                pairIndex++;
            }
        }

        return pairIndex == fixture.Pairs.Count;
    }

    private static bool MatchesProbe(
        AllocationDirectionalObservation observation,
        AllocationDirectionalPlannedSide planned,
        AllocationDirectionalCaptureSettings settings)
        => observation.Workers == planned.Workers
            && observation.CaptureDurationSeconds
                == settings.CaptureDurationSeconds
            && string.Equals(
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
                StringComparison.Ordinal);

    private static bool MatchesPair(
        AllocationDirectionalPair pair,
        AllocationDirectionalPlanEntry entry,
        int repetition,
        AllocationDirectionalCaptureSettings settings)
        => string.Equals(
                pair.PairId,
                $"{entry.PairPrefix}-{repetition:D2}",
                StringComparison.Ordinal)
            && pair.Scenario == entry.Scenario
            && string.Equals(
                pair.ComparisonAxis,
                entry.ComparisonAxis,
                StringComparison.Ordinal)
            && MatchesOperatingSide(pair.Baseline, entry.Baseline, settings)
            && MatchesOperatingSide(pair.Current, entry.Current, settings);

    private static bool MatchesOperatingSide(
        AllocationDirectionalObservation observation,
        AllocationDirectionalPlannedSide planned,
        AllocationDirectionalCaptureSettings settings)
        => observation.Workers == planned.Workers
            && observation.CaptureDurationSeconds
                == settings.CaptureDurationSeconds
            && observation.RequestedPayloadBytesPerOperation
                == planned.ExpectedPayloadBytesPerOperation
            && observation.RetainedBytes == planned.RetainedBytes
            && string.Equals(
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
                StringComparison.Ordinal);

    private static bool IsAllocationCapFailure(
        AllocationDirectionalObservation observation)
        => observation.Outcome == AllocationDirectionalAttemptOutcome.Failed
            && observation.AttemptEvidence is
            {
                Stage: AllocationDirectionalAttemptStage.McpResponse,
                FailureCategory: AllocationDirectionalFailureCategory.McpIsError,
                ErrorKind: "ResourceLimitExceeded",
                ErrorCode: "allocation.capture.limit",
            };
}
