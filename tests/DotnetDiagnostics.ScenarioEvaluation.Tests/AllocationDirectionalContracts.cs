using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class AllocationDirectionalBounds
{
    public const int RequiredPairs = 3;
    public const int MaximumPairs = 64;
    public const int MaximumRankedRows = 16;
    public const int MaximumFixtureBytes = 1024 * 1024;
    public const int MaximumRawResponseBytes = 4 * 1024 * 1024;
    public const int MaximumErrorKindLength = 64;
    public const int MaximumErrorCodeLength = 96;
    public const int MaximumOperationIdLength = 96;
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

[JsonConverter(typeof(JsonStringEnumConverter<AllocationDirectionalAttemptStage>))]
public enum AllocationDirectionalAttemptStage
{
    ProcessStart,
    ProcessReadiness,
    Warmup,
    Workload,
    McpTransport,
    McpResponse,
    Projection,
    Invariant,
    Cleanup,
    Completed,
}

[JsonConverter(typeof(JsonStringEnumConverter<AllocationDirectionalFailureCategory>))]
public enum AllocationDirectionalFailureCategory
{
    ProcessStart,
    ProcessReadiness,
    Warmup,
    Workload,
    McpTransport,
    McpIsError,
    McpErrorEnvelope,
    MalformedEnvelope,
    WrongSchema,
    AllocationUnavailable,
    MissingRequiredField,
    FieldTypeMismatch,
    DenominatorInvariant,
    RetainedValueInvariant,
    Timeout,
    Cancellation,
    Cleanup,
    UnexpectedHarnessException,
}

[JsonConverter(typeof(JsonStringEnumConverter<AllocationDirectionalCleanupState>))]
public enum AllocationDirectionalCleanupState
{
    NotStarted,
    Succeeded,
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<AllocationDirectionalRawRetentionState>))]
public enum AllocationDirectionalRawRetentionState
{
    NotAvailable,
    DigestOnly,
}

public sealed record AllocationDirectionalRankedRow(
    string Key,
    long EstimatedBytes,
    long AllocationTicks);

public sealed record AllocationDirectionalRawResponseEvidence(
    string Sha256,
    int ByteCount,
    AllocationDirectionalRawRetentionState RetentionState,
    bool Truncated,
    bool Redacted);

public sealed record AllocationDirectionalAttemptEvidence(
    string AttemptId,
    string WorkloadIdentity,
    AllocationDirectionalAttemptStage Stage,
    string OperationId,
    AllocationDirectionalFailureCategory? FailureCategory,
    bool? IsError,
    string? SchemaIdentity,
    string? ErrorKind,
    string ErrorDetailState,
    int ErrorDetailByteCount,
    AllocationDirectionalRawResponseEvidence? RawResponse,
    string? ExceptionType,
    AllocationDirectionalCleanupState CleanupState)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }
}

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
    IReadOnlyList<string> Provenance)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AllocationDirectionalAttemptEvidence? AttemptEvidence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaptureId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessIdentity { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComparisonRole { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AllocationDirectionalQuantityEvidence? QuantityEvidence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeAvailability { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SiteAvailability { get; init; }
}

public sealed record AllocationDirectionalQuantityEvidence(
    string Quantity,
    string Unit,
    string BaselineCurrentSemantics,
    string DenominatorQuantity,
    string DenominatorUnit,
    long DenominatorValue,
    string Normalization,
    bool LoadSensitive,
    string Availability,
    string? AbstentionReason,
    IReadOnlyList<string> Assumptions);

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
    string CanonicalProtocolSha256,
    IReadOnlyList<string> ProvenanceCorrections,
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
    IReadOnlyList<string> Findings)
{
    public bool Assessable { get; init; }
}

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
    IReadOnlyList<string> Limitations)
{
    public bool BlindedEvaluationRan { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlindedEvaluationResult { get; init; }
}
