using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Comparison;

[JsonConverter(typeof(JsonStringEnumConverter<CpuHotspotEvidenceAvailability>))]
public enum CpuHotspotEvidenceAvailability
{
    [JsonStringEnumMemberName("available")]
    Available,

    [JsonStringEnumMemberName("insufficient_observations")]
    InsufficientObservations,

    [JsonStringEnumMemberName("incompatible")]
    Incompatible,

    [JsonStringEnumMemberName("unsupported_approximation")]
    UnsupportedApproximation,
}

[JsonConverter(typeof(JsonStringEnumConverter<CpuHotspotRepresentation>))]
public enum CpuHotspotRepresentation
{
    [JsonStringEnumMemberName("exact")]
    Exact,

    [JsonStringEnumMemberName("projected")]
    Projected,

    [JsonStringEnumMemberName("truncated")]
    Truncated,
}

[JsonConverter(typeof(JsonStringEnumConverter<CpuHotspotPairOrder>))]
public enum CpuHotspotPairOrder
{
    [JsonStringEnumMemberName("baseline_first")]
    BaselineFirst,

    [JsonStringEnumMemberName("candidate_first")]
    CandidateFirst,
}

public sealed record CpuHotspotMethodObservation(
    [property: JsonRequired] string MethodId,
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] long InclusiveSamples);

public sealed record CpuHotspotCaptureProfile(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] int ProcessId,
    [property: JsonRequired] DateTimeOffset StartedAt,
    [property: JsonRequired] double DurationSeconds,
    [property: JsonRequired] long TotalSamples,
    [property: JsonRequired] long UnresolvedExclusiveSamples,
    [property: JsonRequired] CpuSampleBackend Backend,
    [property: JsonRequired] CpuSampleEvidenceKind EvidenceKind,
    [property: JsonRequired] CpuHotspotRepresentation Representation,
    [property: JsonRequired] int TotalMethods,
    [property: JsonRequired] int OmittedMethods,
    [property: JsonRequired] IReadOnlyList<CpuHotspotMethodObservation> Methods)
{
    public const string SchemaV1 = "dotnet-diagnostics/cpu-hotspot-capture/v1";
}

public sealed record CpuHotspotTargetIdentity(
    [property: JsonRequired] string RuntimeFlavor,
    [property: JsonRequired] string RuntimeVersionPolicy,
    [property: JsonRequired] string OperatingSystem,
    [property: JsonRequired] string Architecture);

public sealed record CpuHotspotBackendIdentity(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Version,
    [property: JsonRequired] CpuSampleEvidenceKind EvidenceKind);

public sealed record CpuHotspotQuantityIdentity(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Unit,
    [property: JsonRequired] string Aggregation,
    [property: JsonRequired] string Normalization,
    [property: JsonRequired] string Filters,
    [property: JsonRequired] string SelectedMethodId,
    [property: JsonRequired] CpuHotspotRepresentation Representation);

public sealed record CpuHotspotCaptureIdentity(
    [property: JsonRequired] double DurationSeconds,
    [property: JsonRequired] string SamplingPeriod,
    [property: JsonRequired] string Retention,
    [property: JsonRequired] string Enrichment);

public sealed record CpuHotspotWorkloadIdentity(
    [property: JsonRequired] string Scenario,
    [property: JsonRequired] string Version,
    [property: JsonRequired] string Parameters,
    [property: JsonRequired] int OfferedLoad,
    [property: JsonRequired] string Deployment);

public sealed record CpuHotspotIndependenceIdentity(
    [property: JsonRequired] string Unit,
    [property: JsonRequired] string Nesting,
    [property: JsonRequired] string PairingKey);

public sealed record CpuHotspotComparisonAxis(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Value);

public sealed record CpuHotspotCompatibilityIdentity(
    [property: JsonRequired] CpuHotspotTargetIdentity Target,
    [property: JsonRequired] CpuHotspotBackendIdentity Backend,
    [property: JsonRequired] CpuHotspotQuantityIdentity Quantity,
    [property: JsonRequired] CpuHotspotCaptureIdentity Capture,
    [property: JsonRequired] CpuHotspotWorkloadIdentity Workload,
    [property: JsonRequired] CpuHotspotIndependenceIdentity Independence,
    [property: JsonRequired] CpuHotspotComparisonAxis ComparisonAxis);

public sealed record CpuHotspotCaptureObservation(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string PairKey,
    [property: JsonRequired] CpuHotspotPairOrder PairOrder,
    [property: JsonRequired] string ProcessRunId,
    [property: JsonRequired] CpuHotspotCaptureProfile Capture);

public sealed record CpuHotspotCaptureSet(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] string Id,
    [property: JsonRequired] CpuHotspotCompatibilityIdentity Identity,
    [property: JsonRequired] IReadOnlyList<CpuHotspotCaptureObservation> Captures)
{
    public const string SchemaV1 = "dotnet-diagnostics/cpu-hotspot-capture-set/v1";
}

public sealed record CpuHotspotIdentityMismatch(
    string Field,
    string? PairKey,
    string? BaselineValue,
    string? CandidateValue,
    string Reason);

public sealed record CpuHotspotSideCounts(
    int Captures,
    long TotalSamples,
    long UnresolvedExclusiveSamples,
    int BelowMinimumSamples,
    int MissingSelectedMethod,
    int ApproximateCaptures);

public sealed record CpuHotspotPairCounts(
    int Matched,
    int ValidIndependentComplete,
    int Unpaired,
    int PairOrderMismatches,
    int ProcessRunMismatches,
    int ProcessIdMismatches,
    int OverlappingCaptureIntervals,
    int DurationMismatches,
    int BackendMismatches,
    int EvidenceKindMismatches);

public sealed record CpuHotspotDistribution(
    double? Median,
    double? FirstQuartile,
    double? ThirdQuartile,
    double? InterquartileRange,
    double? Minimum,
    double? Maximum);

public sealed record CpuHotspotCaptureMeasurement(
    string Id,
    string PairKey,
    CpuHotspotPairOrder PairOrder,
    string ProcessRunId,
    int ProcessId,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    long TotalSamples,
    long UnresolvedExclusiveSamples,
    double? UnresolvedExclusiveShare,
    long? SelectedMethodInclusiveSamples,
    double? SelectedMethodInclusiveShare,
    int? SelectedMethodRank,
    CpuSampleBackend Backend,
    CpuSampleEvidenceKind EvidenceKind,
    CpuHotspotRepresentation Representation,
    int RetainedMethods,
    int OmittedMethods);

public sealed record CpuHotspotPairedShareDelta(
    string PairKey,
    CpuHotspotPairOrder PairOrder,
    double BaselineShare,
    double CandidateShare,
    double CandidateMinusBaseline);

public sealed record CpuHotspotPairRankEvidence(
    string PairKey,
    double TopKOverlap,
    int BaselineSelectedRank,
    int CandidateSelectedRank,
    int CandidateMinusBaselineRank,
    int AbsoluteRankDelta);

public sealed record CpuHotspotRankStability(
    int TopK,
    int CompletePairs,
    double MedianTopKOverlap,
    double SameSelectedRankFraction,
    double MedianAbsoluteSelectedRankDelta,
    IReadOnlyList<CpuHotspotPairRankEvidence> Pairs);

public sealed record CpuHotspotComparisonMethod(
    string CaptureEstimate,
    string CaptureSpread,
    string PairedEstimate,
    string TopKMembership,
    string SelectedRank,
    string IndependenceUnit,
    int MinimumCompletePairs,
    long MinimumSamplesPerCapture,
    int MaximumMethodsPerCapture,
    int TopK,
    double MaximumUnresolvedShare,
    double MaximumPairedUnresolvedShareDelta,
    string QuantileRule);

public sealed record CpuHotspotUnavailableField(string Field, string Reason);

public sealed record CpuHotspotCalculationProvenance(
    string OrderedInputSha256,
    string CompatibilityIdentitySha256,
    IReadOnlyList<string> OrderedPairKeys);

public sealed record CpuHotspotComparisonResult(
    string Schema,
    CpuHotspotEvidenceAvailability Availability,
    string? UnavailableReason,
    CpuHotspotCompatibilityIdentity BaselineIdentity,
    CpuHotspotCompatibilityIdentity CandidateIdentity,
    IReadOnlyList<CpuHotspotIdentityMismatch> IdentityMismatches,
    CpuHotspotSideCounts BaselineCounts,
    CpuHotspotSideCounts CandidateCounts,
    CpuHotspotPairCounts PairCounts,
    CpuHotspotComparisonMethod Method,
    IReadOnlyList<CpuHotspotCaptureMeasurement> BaselineCaptures,
    IReadOnlyList<CpuHotspotCaptureMeasurement> CandidateCaptures,
    CpuHotspotDistribution BaselineSelectedShare,
    CpuHotspotDistribution CandidateSelectedShare,
    CpuHotspotDistribution BaselineUnresolvedShare,
    CpuHotspotDistribution CandidateUnresolvedShare,
    IReadOnlyList<CpuHotspotPairedShareDelta> PairedShareDeltas,
    double? MedianPairedShareDelta,
    CpuHotspotRankStability? RankStability,
    IReadOnlyList<CpuHotspotUnavailableField> UnavailableFields,
    CpuHotspotCalculationProvenance Provenance,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations)
{
    public const string SchemaV1 = "dotnet-diagnostics/cpu-hotspot-comparison/v1";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CpuHotspotCaptureSet))]
[JsonSerializable(typeof(CpuHotspotComparisonResult))]
public sealed partial class CpuHotspotComparisonJsonContext : JsonSerializerContext;
