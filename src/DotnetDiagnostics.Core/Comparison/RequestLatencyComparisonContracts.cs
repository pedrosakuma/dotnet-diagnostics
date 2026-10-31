using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Comparison;

[JsonConverter(typeof(JsonStringEnumConverter<RequestLatencyEvidenceAvailability>))]
public enum RequestLatencyEvidenceAvailability
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
}

[JsonConverter(typeof(JsonStringEnumConverter<RequestLatencyRepresentation>))]
public enum RequestLatencyRepresentation
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

[JsonConverter(typeof(JsonStringEnumConverter<RequestLatencyWindowOutcome>))]
public enum RequestLatencyWindowOutcome
{
    [JsonStringEnumMemberName("complete")]
    Complete,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("aborted")]
    Aborted,
}

[JsonConverter(typeof(JsonStringEnumConverter<RequestLatencyPairOrder>))]
public enum RequestLatencyPairOrder
{
    [JsonStringEnumMemberName("baseline_first")]
    BaselineFirst,

    [JsonStringEnumMemberName("candidate_first")]
    CandidateFirst,
}

public sealed record RequestLatencyTargetIdentity(
    [property: JsonRequired] string RuntimeFlavor,
    [property: JsonRequired] string RuntimeVersionPolicy,
    [property: JsonRequired] string OperatingSystem,
    [property: JsonRequired] string Architecture,
    [property: JsonRequired] string ProcessRunId);

public sealed record RequestLatencyBackendIdentity(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Version);

public sealed record RequestLatencyQuantityIdentity(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Unit,
    [property: JsonRequired] string Aggregation,
    [property: JsonRequired] string Normalization,
    [property: JsonRequired] string Filters,
    [property: JsonRequired] RequestLatencyRepresentation Representation);

public sealed record RequestLatencyCaptureIdentity(
    [property: JsonRequired] string Duration,
    [property: JsonRequired] string Windowing,
    [property: JsonRequired] string Providers,
    [property: JsonRequired] string SamplingPeriod,
    [property: JsonRequired] string Retention,
    [property: JsonRequired] string Enrichment);

public sealed record RequestLatencyWorkloadIdentity(
    [property: JsonRequired] string Scenario,
    [property: JsonRequired] string Version,
    [property: JsonRequired] string Parameters,
    [property: JsonRequired] string Concurrency,
    [property: JsonRequired] string Deployment);

public sealed record RequestLatencyWindowIdentity(
    [property: JsonRequired] string IndependenceUnit,
    [property: JsonRequired] string Nesting,
    [property: JsonRequired] string PairingKey);

public sealed record RequestLatencyComparisonAxis(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Value);

public sealed record RequestLatencyCompatibilityIdentity(
    [property: JsonRequired] RequestLatencyTargetIdentity Target,
    [property: JsonRequired] RequestLatencyBackendIdentity Backend,
    [property: JsonRequired] RequestLatencyQuantityIdentity Quantity,
    [property: JsonRequired] RequestLatencyCaptureIdentity Capture,
    [property: JsonRequired] RequestLatencyWorkloadIdentity Workload,
    [property: JsonRequired] RequestLatencyWindowIdentity Window,
    [property: JsonRequired] RequestLatencyComparisonAxis ComparisonAxis);

public sealed record RequestLatencyWindow(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string PairKey,
    [property: JsonRequired] RequestLatencyPairOrder PairOrder,
    [property: JsonRequired] RequestLatencyWindowOutcome Outcome,
    [property: JsonRequired] int SourceCompletedCount,
    [property: JsonRequired] int ErrorCount,
    [property: JsonRequired] int CensoredCount,
    [property: JsonRequired] int MissingCompletionCount,
    [property: JsonRequired] long LostOrDropped,
    [property: JsonRequired] RequestLatencyRepresentation Representation,
    [property: JsonRequired] IReadOnlyList<double> RetainedLatenciesMilliseconds);

public sealed record RequestLatencyWindowSet(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] string Id,
    [property: JsonRequired] RequestLatencyCompatibilityIdentity Identity,
    [property: JsonRequired] IReadOnlyList<RequestLatencyWindow> Windows)
{
    public const string SchemaV1 = "dotnet-diagnostics/request-latency-window-set/v1";
}

public sealed record RequestLatencyIdentityMismatch(
    string Field,
    string? BaselineValue,
    string? CandidateValue,
    string Reason);

public sealed record RequestLatencySideCounts(
    int Windows,
    int CompleteWindows,
    int FailedWindows,
    int AbortedWindows,
    int SourceCompleted,
    int RetainedCompleted,
    int Errors,
    int Censored,
    int MissingCompletions,
    long LostOrDropped);

public sealed record RequestLatencyPairCounts(
    int Matched,
    int ValidIndependentComplete,
    int Unpaired,
    int PairOrderMismatches,
    int Incomplete,
    int Failed,
    int Aborted,
    int BelowMinimumCompletedRequests);

public sealed record RequestLatencyQuantileEstimate(
    RequestLatencyEvidenceAvailability Availability,
    double? ValueMilliseconds,
    int SourceCompletedCount,
    int RetainedCompletedCount,
    string? UnavailableReason);

public sealed record RequestLatencyInterval(
    double LowerMilliseconds,
    double UpperMilliseconds,
    double ConfidenceLevel);

public sealed record RequestLatencyUnavailableField(string Field, string Reason);

public sealed record RequestLatencyQuantileComparison(
    string Name,
    double Probability,
    RequestLatencyEvidenceAvailability Availability,
    RequestLatencyQuantileEstimate Baseline,
    RequestLatencyQuantileEstimate Candidate,
    double? AbsoluteDeltaMilliseconds,
    double? RelativeDelta,
    double? PairedWindowMedianDeltaMilliseconds,
    RequestLatencyInterval? PairedWindowBootstrapInterval,
    IReadOnlyList<RequestLatencyUnavailableField> UnavailableFields);

public sealed record RequestLatencyComparisonMethod(
    string Quantile,
    string PairedEstimate,
    string Interval,
    string IndependenceUnit,
    int MinimumCompletedRequestsPerWindow,
    int MinimumCompleteWindowPairs,
    int BootstrapResamples,
    double ConfidenceLevel);

public sealed record RequestLatencyCalculationProvenance(
    string OrderedInputSha256,
    string CompatibilityIdentitySha256,
    string SeedDerivation,
    string SeedSha256,
    string PrngAlgorithm,
    string QuantileRule);

public sealed record RequestLatencyComparisonResult(
    string Schema,
    RequestLatencyEvidenceAvailability Availability,
    string? UnavailableReason,
    RequestLatencyCompatibilityIdentity BaselineIdentity,
    RequestLatencyCompatibilityIdentity CandidateIdentity,
    IReadOnlyList<RequestLatencyIdentityMismatch> IdentityMismatches,
    RequestLatencySideCounts BaselineCounts,
    RequestLatencySideCounts CandidateCounts,
    RequestLatencyPairCounts PairCounts,
    RequestLatencyComparisonMethod Method,
    IReadOnlyList<RequestLatencyQuantileComparison> Quantiles,
    RequestLatencyCalculationProvenance Provenance,
    IReadOnlyList<string> Limitations)
{
    public const string SchemaV1 = "dotnet-diagnostics/request-latency-comparison/v1";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RequestLatencyWindowSet))]
[JsonSerializable(typeof(RequestLatencyComparisonResult))]
public sealed partial class RequestLatencyComparisonJsonContext : JsonSerializerContext;
