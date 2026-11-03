using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class AllocationDirectionalBounds
{
    public const int RequiredPairs = 3;
    public const int MaximumPairs = 48;
    public const int MaximumRankedRows = 16;
    public const int MaximumFixtureBytes = 1024 * 1024;
    public const decimal MaximumAllowedAaRelativeDelta = 0.25m;
    public const decimal MaximumAllowedAaShareMovement = 0.25m;
}

[JsonConverter(typeof(JsonStringEnumConverter<AllocationDirectionalScenario>))]
public enum AllocationDirectionalScenario
{
    TrueIncrease,
    MixShiftStableTotal,
    ThroughputIncreaseStablePerOperation,
    LowerAllocationStableRetained,
    WarmupTransientConverges,
    ChangedWorkload,
    FixedStrideBias,
    PartialAttribution,
    MissingOrZeroWeight,
    HealthyAa,
}

[JsonConverter(typeof(JsonStringEnumConverter<AllocationDirectionalOrigin>))]
public enum AllocationDirectionalOrigin
{
    LiveMcp,
    ControlledProjection,
}

[JsonConverter(typeof(JsonStringEnumConverter<AllocationDirectionalAttemptOutcome>))]
public enum AllocationDirectionalAttemptOutcome
{
    Passed,
    Failed,
    Aborted,
}

[JsonConverter(typeof(JsonStringEnumConverter<AllocationDirectionalDecision>))]
public enum AllocationDirectionalDecision
{
    Go,
    NoGo,
    Inconclusive,
}

public sealed record AllocationDirectionalRankedRow(
    string Key,
    long EstimatedBytes,
    long AllocationTicks);

public sealed record AllocationDirectionalObservation(
    string AttemptId,
    AllocationDirectionalAttemptOutcome Outcome,
    AllocationDirectionalOrigin Origin,
    string WorkloadIdentity,
    string Shape,
    string Phase,
    int Workers,
    double CaptureDurationSeconds,
    long Operations,
    long RequestedPayloadBytesPerOperation,
    long RetainedBytes,
    long SampledEstimatedBytes,
    long SampledAllocationTicks,
    string AttributionState,
    IReadOnlyList<AllocationDirectionalRankedRow> Types,
    IReadOnlyList<AllocationDirectionalRankedRow> Sites,
    string? FailureCategory,
    IReadOnlyList<string> Provenance);

public sealed record AllocationDirectionalPair(
    string PairId,
    AllocationDirectionalScenario Scenario,
    string ComparisonAxis,
    AllocationDirectionalObservation Baseline,
    AllocationDirectionalObservation Current);

public sealed record AllocationDirectionalFixture(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolVersion,
    string ProtocolSha256,
    string FrozenAtUtc,
    string SourceBaseCommit,
    string Environment,
    IReadOnlyList<AllocationDirectionalPair> Pairs);

public sealed record AllocationDirectionalScenarioResult(
    AllocationDirectionalScenario Scenario,
    int AttemptedPairs,
    int ValidPairs,
    bool ExpectedBehaviorObserved,
    bool UnsafeDirectionalEvidenceObserved,
    IReadOnlyList<string> Findings);

public sealed record AllocationDirectionalStudyReport(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolVersion,
    AllocationDirectionalDecision Decision,
    decimal AaMaximumAbsoluteEstimatedBytesPerOperationRelativeDelta,
    decimal AaMaximumTypeShareMovement,
    decimal AaMaximumSiteShareMovement,
    bool NumericGatePassed,
    bool BlindedEvaluationRequired,
    IReadOnlyList<AllocationDirectionalScenarioResult> Scenarios,
    IReadOnlyList<string> Limitations);
