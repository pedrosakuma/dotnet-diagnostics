using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ScenarioStatisticalEvidenceBounds
{
    public const int MaximumArms = 3;
    public const int MaximumIdentityDimensions = 64;
    public const int MaximumRepetitions = 64;
    public const int MaximumAttempts = 128;
    public const int MaximumCaptures = 256;
    public const int MaximumWindows = 1024;
    public const int MaximumObservations = 1024;
    public const int MaximumBootstrapResamples = 10_000;
    public const int MaximumAssumptions = 16;
    public const int MaximumLimitations = 32;
}

public static class ScenarioStatisticalMethodIds
{
    public const string WindowMedian = "window-median";
    public const string PairedDelta = "paired-delta";
    public const string MedianAbsoluteDeviation = "median-absolute-deviation";
    public const string WilsonScoreRate = "wilson-score-rate";
    public const string TheilSenSlope = "theil-sen-slope";
    public const string PercentileBootstrapInterval = "percentile-bootstrap-interval";
}

public enum ScenarioStatisticalAvailability
{
    [JsonStringEnumMemberName("available")]
    Available,

    [JsonStringEnumMemberName("insufficient_observations")]
    InsufficientObservations,

    [JsonStringEnumMemberName("incompatible")]
    Incompatible,

    [JsonStringEnumMemberName("loss_detected")]
    LossDetected,

    [JsonStringEnumMemberName("unsupported_approximation")]
    UnsupportedApproximation,

    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

public enum ScenarioStatisticalIndependenceUnit
{
    [JsonStringEnumMemberName("event")]
    Event,

    [JsonStringEnumMemberName("bounded_time_window")]
    BoundedTimeWindow,

    [JsonStringEnumMemberName("capture")]
    Capture,

    [JsonStringEnumMemberName("process_run")]
    ProcessRun,

    [JsonStringEnumMemberName("environment_allocation")]
    EnvironmentAllocation,
}

public enum ScenarioStatisticalArmRole
{
    [JsonStringEnumMemberName("observed")]
    Observed,

    [JsonStringEnumMemberName("baseline")]
    Baseline,

    [JsonStringEnumMemberName("candidate")]
    Candidate,

    [JsonStringEnumMemberName("control")]
    Control,
}

public enum ScenarioStatisticalAttemptOutcome
{
    [JsonStringEnumMemberName("passed")]
    Passed,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("aborted")]
    Aborted,
}

public enum ScenarioStatisticalRepresentation
{
    [JsonStringEnumMemberName("exact")]
    Exact,

    [JsonStringEnumMemberName("reservoir_sampled")]
    ReservoirSampled,

    [JsonStringEnumMemberName("projected")]
    Projected,

    [JsonStringEnumMemberName("truncated")]
    Truncated,
}

public enum ScenarioStatisticalPairOrder
{
    [JsonStringEnumMemberName("baseline_first")]
    BaselineFirst,

    [JsonStringEnumMemberName("candidate_first")]
    CandidateFirst,
}

public sealed record ScenarioStatisticalMethod(string Id, int Version);

public sealed record ScenarioStatisticalMethodDefinition(
    string Id,
    int Version,
    int MinimumInputCount,
    IReadOnlyList<ScenarioStatisticalIndependenceUnit> AllowedIndependenceUnits,
    bool RequiresPairing,
    IReadOnlyList<string> Produces,
    bool EmitsVerdict);

public sealed record ScenarioStatisticalProtocol(
    string Id,
    string Version,
    ScenarioStatisticalMethod Method,
    ScenarioStatisticalIndependenceUnit IndependenceUnit,
    int MinimumIndependenceUnits,
    bool AdmitLoss,
    bool AdmitApproximation,
    int? BootstrapResamples = null,
    double? ConfidenceLevel = null,
    string? ParentMethodId = null);

public sealed record ScenarioStatisticalIdentityDimension(string Name, string Value);

public sealed record ScenarioStatisticalCompatibilityIdentity(
    IReadOnlyList<ScenarioStatisticalIdentityDimension> Dimensions);

public sealed record ScenarioStatisticalAttempt(
    string RepetitionId,
    string AttemptId,
    ScenarioStatisticalAttemptOutcome Outcome,
    string? FailureKind = null,
    string? Detail = null);

public sealed record ScenarioStatisticalObservation(
    string Id,
    string RepetitionId,
    string AttemptId,
    string EnvironmentAllocationId,
    string CaptureId,
    string? WindowId,
    string IndependenceUnitId,
    double Value,
    double? X,
    long SourceEventCount,
    long LostOrDropped,
    ScenarioStatisticalRepresentation Representation,
    string? PairKey = null,
    ScenarioStatisticalPairOrder? PairOrder = null);

public sealed record ScenarioStatisticalArm(
    string Id,
    ScenarioStatisticalArmRole Role,
    ScenarioStatisticalCompatibilityIdentity CompatibilityIdentity,
    IReadOnlyList<ScenarioStatisticalAttempt> Attempts,
    IReadOnlyList<ScenarioStatisticalObservation> Observations);

public sealed record ScenarioStatisticalEvidenceRequest(
    int SchemaVersion,
    ScenarioStatisticalProtocol Protocol,
    IReadOnlyList<ScenarioStatisticalArm> Arms,
    string? SelectedArmId = null);

public sealed record ScenarioStatisticalCounts(
    long SourceEvents,
    int Windows,
    int Captures,
    int Repetitions,
    int Attempts,
    int SuccessfulAttempts,
    int FailedAttempts,
    int AbortedAttempts,
    int IndependenceUnits,
    int AcceptedObservations,
    int ExcludedNonfinite,
    int CollapsedCorrelatedDuplicates,
    int IncompletePairs,
    long LostOrDropped);

public sealed record ScenarioStatisticalDelta(
    double BaselineEstimate,
    double CandidateEstimate,
    double Absolute,
    double? Relative);

public sealed record ScenarioStatisticalRate(
    int PositiveCount,
    int ObservationCount,
    double Value);

public sealed record ScenarioStatisticalInterval(
    double Lower,
    double Upper,
    double ConfidenceLevel,
    double? ParentEstimate = null);

public sealed record ScenarioStatisticalUnavailableField(string Field, string Reason);

public sealed record ScenarioStatisticalIdentityMismatch(
    string? ArmId,
    string Dimension,
    string? BaselineValue,
    string? CandidateValue,
    string Reason);

public sealed record ScenarioStatisticalComparedIdentity(
    string ArmId,
    ScenarioStatisticalArmRole Role,
    ScenarioStatisticalCompatibilityIdentity Identity);

public sealed record ScenarioStatisticalCompatibilityComparison(
    IReadOnlyList<ScenarioStatisticalComparedIdentity> Arms,
    IReadOnlyList<ScenarioStatisticalIdentityMismatch> Mismatches);

public sealed record ScenarioStatisticalCalculationProvenance(
    string OrderedInputSha256,
    string CompatibilityIdentitySha256,
    string? SeedDerivation,
    string? SeedSha256,
    string? PrngAlgorithm,
    string? QuantileRule,
    int? ResamplesRequested,
    int? ResamplesCompleted,
    int FailedResamples,
    IReadOnlyList<ScenarioStatisticalPairProvenance> OrderedPairs);

public sealed record ScenarioStatisticalPairProvenance(
    string PairKey,
    ScenarioStatisticalPairOrder PairOrder);

public sealed record ScenarioStatisticalEvidenceResult(
    int SchemaVersion,
    ScenarioStatisticalAvailability Availability,
    ScenarioStatisticalMethod Method,
    ScenarioStatisticalIndependenceUnit IndependenceUnit,
    ScenarioStatisticalCounts Counts,
    double? Estimate,
    ScenarioStatisticalDelta? Delta,
    double? Dispersion,
    double? Slope,
    ScenarioStatisticalRate? Rate,
    ScenarioStatisticalInterval? Interval,
    IReadOnlyList<ScenarioStatisticalUnavailableField> UnavailableFields,
    ScenarioStatisticalCompatibilityComparison? Compatibility,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations,
    ScenarioStatisticalCalculationProvenance CalculationProvenance);

public sealed record ScenarioStatisticalReplayFixture(
    int SchemaVersion,
    string Id,
    ScenarioStatisticalEvidenceRequest Request,
    ScenarioStatisticalReplayExpectation Expected);

public sealed record ScenarioStatisticalReplayExpectation(
    ScenarioStatisticalAvailability Availability,
    int IndependenceUnits,
    int Windows,
    int Captures,
    int Attempts,
    int FailedAttempts,
    int AbortedAttempts,
    double? Estimate,
    double? AbsoluteDelta,
    double? IntervalLower,
    double? IntervalUpper,
    string? SeedSha256,
    int? ResamplesRequested,
    string? UnavailableReason,
    string OrderedInputSha256);
