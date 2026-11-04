namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AllocationThroughputPacing(
    string Shape,
    string OperationIdentity,
    int PauseMilliseconds,
    int PauseEveryOperations);

internal sealed record AllocationThroughputNormalizationCohortPlan(
    string CohortId,
    AllocationThroughputPacing BaselinePacing,
    AllocationThroughputPacing CurrentPacing,
    AllocationDirectionalPlanEntry AaEntry,
    AllocationDirectionalPlanEntry EffectEntry);

internal static class AllocationThroughputNormalizationCapturePlan
{
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

    public static IReadOnlyList<AllocationThroughputNormalizationCohortPlan>
        Cohorts { get; } =
    [
        Cohort(
            "primary-every-operation",
            "throughput-falsifier-primary-v1",
            "throughput-v1-primary",
            new("primary-paced", "PrimaryOperation", 1, 1),
            new("primary", "PrimaryOperation", 0, 0),
            288_000),
        Cohort(
            "low-every-eight-operations",
            "throughput-falsifier-low-v1",
            "throughput-v1-low",
            new("low-bounded-v4", "LowOperation", 1, 8),
            new("low", "LowOperation", 0, 0),
            64_000),
    ];

    public static AllocationDirectionalPlanEntry HealthyControl { get; } =
        Entry(
            AllocationDirectionalScenario.HealthyAa,
            "throughput-v1-healthy-control",
            "unchanged-rate",
            Side(
                "throughput-falsifier-healthy-v1",
                "primary",
                "steady",
                288_000),
            Side(
                "throughput-falsifier-healthy-v1",
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
        Entries.Count * AllocationDirectionalBounds.RequiredPairs;

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

    private static AllocationThroughputNormalizationCohortPlan Cohort(
        string cohortId,
        string workloadIdentity,
        string pairPrefix,
        AllocationThroughputPacing baselinePacing,
        AllocationThroughputPacing currentPacing,
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
