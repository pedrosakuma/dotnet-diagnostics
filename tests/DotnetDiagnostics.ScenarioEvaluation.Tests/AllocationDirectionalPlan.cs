namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AllocationDirectionalPlannedSide(
    string WorkloadIdentity,
    string Shape,
    int Workers,
    string Phase,
    long ExpectedPayloadBytesPerOperation,
    int WarmupMilliseconds,
    int RetainedBytes,
    bool DriveWorkload);

internal sealed record AllocationDirectionalPlanEntry(
    AllocationDirectionalScenario Scenario,
    string PairPrefix,
    string ComparisonAxis,
    AllocationDirectionalPlannedSide Baseline,
    AllocationDirectionalPlannedSide Current);

internal static class AllocationDirectionalCapturePlan
{
    public const int CaptureDurationSeconds = 4;
    public const int ArmDelayMilliseconds = 900;
    public const int WorkloadMilliseconds = 1500;
    public const int DefaultWarmupMilliseconds = 180;
    public const int TopN = AllocationDirectionalBounds.MaximumRankedRows;
    public const int AttemptTimeoutSeconds = 45;
    public const int McpCompletionTimeoutSeconds = 30;
    public const decimal MinimumThroughputMultiplier = 1.25m;

    public static int LiveMcpPairs =>
        Entries.Count * AllocationDirectionalBounds.RequiredPairs;

    public static int LiveMcpSides => LiveMcpPairs * 2;

    public static int ControlledProjectionPairs =>
        AllocationDirectionalBounds.RequiredPairs;

    public static int ControlledProjectionSides => ControlledProjectionPairs * 2;

    public static int TotalPairs => LiveMcpPairs + ControlledProjectionPairs;

    public static IReadOnlyDictionary<string, string> RuntimeEnvironment { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_gcServer"] = "0",
            ["DOTNET_TieredCompilation"] = "1",
            ["DOTNET_TC_QuickJit"] = "1",
            ["DOTNET_TieredPGO"] = "1",
        };

    public static IReadOnlyList<AllocationDirectionalPlanEntry> Entries { get; } =
    [
        Entry(
            AllocationDirectionalScenario.HealthyAa,
            "aa",
            "same-workload",
            Side("healthy-aa-primary-v1", "primary", 1, "steady", 288_000),
            Side("healthy-aa-primary-v1", "primary", 1, "steady", 288_000)),
        Entry(
            AllocationDirectionalScenario.TrueIncrease,
            "increase",
            "allocation-volume",
            Side("allocation-volume-v1", "low", 1, "steady", 64_000),
            Side("allocation-volume-v1", "increased", 1, "steady", 576_000)),
        Entry(
            AllocationDirectionalScenario.MixShiftStableTotal,
            "mix",
            "allocation-mix",
            Side("equal-payload-mix-v1", "primary", 1, "steady", 288_000),
            Side("equal-payload-mix-v1", "changed", 1, "steady", 288_000)),
        Entry(
            AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation,
            "throughput-aa",
            "same-load",
            Side("primary-throughput-v1", "primary", 2, "steady", 288_000),
            Side("primary-throughput-v1", "primary", 2, "aa", 288_000)),
        Entry(
            AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation,
            "throughput-intensity",
            "load-intensity",
            Side("primary-throughput-v1", "primary", 1, "steady", 288_000),
            Side("primary-throughput-v1", "primary", 2, "intensity", 288_000)),
        Entry(
            AllocationDirectionalScenario.FixedStrideBias,
            "stride-aa",
            "same-order",
            Side("equal-payload-order-v1", "periodic-a", 1, "steady", 192_000),
            Side("equal-payload-order-v1", "periodic-a", 1, "aa", 192_000)),
        Entry(
            AllocationDirectionalScenario.FixedStrideBias,
            "stride-reorder",
            "allocation-order",
            Side("equal-payload-order-v1", "periodic-a", 1, "steady", 192_000),
            Side("equal-payload-order-v1", "periodic-b", 1, "reorder", 192_000)),
        Entry(
            AllocationDirectionalScenario.LowerAllocationStableRetained,
            "lower-retained",
            "allocation-volume",
            Side(
                "retained-volume-v1",
                "increased",
                1,
                "steady",
                576_000,
                retainedBytes: 2 * 1024 * 1024),
            Side(
                "retained-volume-v1",
                "low",
                1,
                "steady",
                64_000,
                retainedBytes: 2 * 1024 * 1024)),
        Entry(
            AllocationDirectionalScenario.WarmupTransientConverges,
            "warmup-early",
            "phase",
            Side("warmup-v1", "primary", 1, "steady", 288_000),
            Side(
                "warmup-v1",
                "primary",
                1,
                "early",
                288_000,
                warmupMilliseconds: 0)),
        Entry(
            AllocationDirectionalScenario.WarmupTransientConverges,
            "warmup-converged",
            "phase",
            Side("warmup-v1", "primary", 1, "steady", 288_000),
            Side("warmup-v1", "primary", 1, "converged", 288_000)),
        Entry(
            AllocationDirectionalScenario.ChangedWorkload,
            "changed-workload",
            "workload-identity",
            Side("request-mix-a-v1", "primary", 1, "steady", 288_000),
            Side("request-mix-b-v1", "changed", 1, "steady", 288_000)),
        Entry(
            AllocationDirectionalScenario.MissingOrZeroWeight,
            "missing-denominator",
            "availability",
            Side("missing-denominator-v1", "primary", 1, "steady", 288_000),
            Side(
                "missing-denominator-v1",
                "primary",
                0,
                "missing",
                0,
                driveWorkload: false)),
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
        int workers,
        string phase,
        long expectedPayloadBytesPerOperation,
        int warmupMilliseconds = DefaultWarmupMilliseconds,
        int retainedBytes = 0,
        bool driveWorkload = true)
        => new(
            workloadIdentity,
            shape,
            workers,
            phase,
            expectedPayloadBytesPerOperation,
            warmupMilliseconds,
            retainedBytes,
            driveWorkload);
}
