namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AllocationThroughputV2Pacing(
    string Shape,
    string OperationIdentity,
    int PauseMilliseconds,
    int PauseEveryOperations);

internal sealed record AllocationThroughputNormalizationV2CohortPlan(
    string CohortId,
    AllocationThroughputV2Pacing BaselinePacing,
    AllocationThroughputV2Pacing CurrentPacing,
    AllocationDirectionalPlanEntry AaEntry,
    AllocationDirectionalPlanEntry EffectEntry);

internal static class AllocationThroughputNormalizationV2CapturePlan
{
    public const int RequiredPairs = 5;
    public const int MinimumInlierPairs = 4;
    public const int CaptureDurationSeconds = 1;
    public const int ArmDelayMilliseconds = 200;
    public const int WorkloadMilliseconds = 700;
    public const int DefaultWarmupMilliseconds = 180;
    public const int TopN = AllocationDirectionalBounds.MaximumRankedRows;
    public const int AttemptTimeoutSeconds = 30;
    public const int McpCompletionTimeoutSeconds = 15;
    public const long AllocationEventOperatingLimit =
        AllocationDirectionalBoundedCalibrationPlan.AllocationEventOperatingLimit;

    public static AllocationDirectionalCaptureSettings CaptureSettings { get; } =
        new(
            CaptureDurationSeconds,
            ArmDelayMilliseconds,
            WorkloadMilliseconds,
            TopN,
            AttemptTimeoutSeconds,
            McpCompletionTimeoutSeconds);

    public static IReadOnlyDictionary<string, string> RuntimeEnvironment { get; } =
        AllocationDirectionalV4CapturePlan.RuntimeEnvironment;

    public static IReadOnlyList<AllocationThroughputNormalizationV2CohortPlan>
        Cohorts { get; } =
    [
        Cohort(
            "primary-every-operation",
            "throughput-powered-primary-v2",
            "throughput-v2-primary",
            new("primary-paced", "PrimaryOperation", 1, 1),
            new("primary", "PrimaryOperation", 0, 0),
            288_000),
        Cohort(
            "low-every-eight-operations",
            "throughput-powered-low-v2",
            "throughput-v2-low",
            new("low-bounded-v4", "LowOperation", 1, 8),
            new("low", "LowOperation", 0, 0),
            64_000),
    ];

    public static AllocationDirectionalPlanEntry HealthyControl { get; } =
        Entry(
            AllocationDirectionalScenario.HealthyAa,
            "throughput-v2-healthy-control",
            "unchanged-rate",
            Side(
                "throughput-powered-healthy-v2",
                "primary",
                "steady",
                288_000),
            Side(
                "throughput-powered-healthy-v2",
                "primary",
                "aa",
                288_000));

    public static IReadOnlyList<AllocationDirectionalPlanEntry> Entries { get; } =
        Cohorts.SelectMany(cohort => new[]
            {
                cohort.AaEntry,
                cohort.EffectEntry,
            })
            .Append(HealthyControl)
            .ToArray();

    public static int TotalPairs =>
        Entries.Count * RequiredPairs;

    public static int LiveMcpSides => TotalPairs * 2;

    public static void ValidateConstruction()
    {
        if (Cohorts.Count < 2
            || Cohorts.Select(cohort => cohort.CohortId)
                .Distinct(StringComparer.Ordinal).Count() != Cohorts.Count
            || Entries.Select(entry => entry.PairPrefix)
                .Distinct(StringComparer.Ordinal).Count() != Entries.Count)
        {
            throw new InvalidDataException(
                "The throughput-normalization plan requires distinct cohorts and pair prefixes.");
        }

        foreach (var cohort in Cohorts)
        {
            if (cohort.BaselinePacing == cohort.CurrentPacing
                || cohort.BaselinePacing.PauseMilliseconds <= 0
                || cohort.BaselinePacing.PauseEveryOperations <= 0
                || cohort.CurrentPacing.PauseMilliseconds != 0
                || cohort.CurrentPacing.PauseEveryOperations != 0
                || !string.Equals(
                    cohort.BaselinePacing.OperationIdentity,
                    cohort.CurrentPacing.OperationIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Cohort '{cohort.CohortId}' does not isolate an independently parameterized pacing-only manipulation.");
            }

            ValidateEqualAllocationPair(cohort.AaEntry, requireSameShape: true);
            ValidateEqualAllocationPair(
                cohort.EffectEntry,
                requireSameShape: false);
            if (!string.Equals(
                    cohort.AaEntry.Baseline.Shape,
                    cohort.BaselinePacing.Shape,
                    StringComparison.Ordinal)
                || !string.Equals(
                    cohort.AaEntry.Current.Shape,
                    cohort.BaselinePacing.Shape,
                    StringComparison.Ordinal)
                || !string.Equals(
                    cohort.EffectEntry.Baseline.Shape,
                    cohort.BaselinePacing.Shape,
                    StringComparison.Ordinal)
                || !string.Equals(
                    cohort.EffectEntry.Current.Shape,
                    cohort.CurrentPacing.Shape,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Cohort '{cohort.CohortId}' plan shapes do not match its reviewed pacing definition.");
            }
        }

        ValidateEqualAllocationPair(HealthyControl, requireSameShape: true);
    }

    private static AllocationThroughputNormalizationV2CohortPlan Cohort(
        string cohortId,
        string workloadIdentity,
        string pairPrefix,
        AllocationThroughputV2Pacing baselinePacing,
        AllocationThroughputV2Pacing currentPacing,
        long payloadBytesPerOperation)
        => new(
            cohortId,
            baselinePacing,
            currentPacing,
            Entry(
                AllocationDirectionalScenario
                    .ThroughputIncreaseStablePerOperation,
                $"{pairPrefix}-aa",
                $"{cohortId}-aa",
                Side(
                    workloadIdentity,
                    baselinePacing.Shape,
                    "steady",
                    payloadBytesPerOperation),
                Side(
                    workloadIdentity,
                    baselinePacing.Shape,
                    "aa",
                    payloadBytesPerOperation)),
            Entry(
                AllocationDirectionalScenario
                    .ThroughputIncreaseStablePerOperation,
                $"{pairPrefix}-effect",
                $"{cohortId}-effect",
                Side(
                    workloadIdentity,
                    baselinePacing.Shape,
                    "steady",
                    payloadBytesPerOperation),
                Side(
                    workloadIdentity,
                    currentPacing.Shape,
                    "effect",
                    payloadBytesPerOperation)));

    private static void ValidateEqualAllocationPair(
        AllocationDirectionalPlanEntry entry,
        bool requireSameShape)
    {
        if (!string.Equals(
                entry.Baseline.WorkloadIdentity,
                entry.Current.WorkloadIdentity,
                StringComparison.Ordinal)
            || entry.Baseline.ExpectedPayloadBytesPerOperation
                != entry.Current.ExpectedPayloadBytesPerOperation
            || entry.Baseline.Workers != entry.Current.Workers
            || entry.Baseline.RetainedBytes != entry.Current.RetainedBytes
            || entry.Baseline.DriveWorkload != entry.Current.DriveWorkload
            || requireSameShape
                && !string.Equals(
                    entry.Baseline.Shape,
                    entry.Current.Shape,
                    StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Pair prefix '{entry.PairPrefix}' violates the equal-allocation construction.");
        }
    }

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
        long expectedPayloadBytesPerOperation)
        => new(
            workloadIdentity,
            shape,
            Workers: 1,
            phase,
            expectedPayloadBytesPerOperation,
            DefaultWarmupMilliseconds,
            RetainedBytes: 0,
            DriveWorkload: true);
}
