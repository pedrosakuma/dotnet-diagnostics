using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class CpuHotspotMovementBounds
{
    public const int MaximumCases = 8;
    public const int MaximumCapturesPerArm = 12;
    public const int MaximumMethodsPerCapture = 16;
    public const int MinimumCompletePairs = 4;
    public const long MinimumSamplesPerCapture = 80;
    public const int TopK = 2;
    public const double MaximumUnresolvedShare = 0.35;
    public const double MaximumPairedUnresolvedShareDelta = 0.20;
    public const double MinimumMovementShareDelta = 0.07;
    public const double MinimumSeparationBeyondAaRange = 0.05;
}

public enum CpuHotspotMovementVerdict
{
    [JsonStringEnumMemberName("go")]
    Go,

    [JsonStringEnumMemberName("no_go")]
    NoGo,
}

public enum CpuHotspotNextAction
{
    [JsonStringEnumMemberName("movement_observed_collect_drilldown")]
    MovementObservedCollectDrilldown,

    [JsonStringEnumMemberName("no_movement_established")]
    NoMovementEstablished,

    [JsonStringEnumMemberName("inconclusive")]
    Inconclusive,
}

public sealed record CpuHotspotMethodEvidence(
    string MethodId,
    long InclusiveSamples);

public sealed record CpuHotspotCaptureEvidence(
    string Id,
    string PairKey,
    ScenarioStatisticalPairOrder PairOrder,
    string ProcessRunId,
    string RuntimeFlavor,
    string RuntimeVersionPolicy,
    string OperatingSystem,
    string Architecture,
    string Backend,
    string EvidenceKind,
    double DurationSeconds,
    string SamplingPeriod,
    string WorkloadScenario,
    string WorkloadVersion,
    string WorkloadVariant,
    int OfferedLoad,
    long TotalSamples,
    long UnresolvedExclusiveSamples,
    ScenarioStatisticalRepresentation Representation,
    IReadOnlyList<CpuHotspotMethodEvidence> Methods);

public sealed record CpuHotspotArm(
    string Id,
    ScenarioStatisticalArmRole Role,
    IReadOnlyList<CpuHotspotCaptureEvidence> Captures);

public sealed record CpuHotspotMovementCase(
    string Id,
    string SelectedMethodId,
    CpuHotspotArm Baseline,
    CpuHotspotArm Candidate);

public sealed record CpuHotspotMovementFixture(
    int SchemaVersion,
    string Id,
    IReadOnlyList<CpuHotspotMovementCase> Cases);

public sealed record CpuHotspotCaptureDistribution(
    double? Median,
    double? FirstQuartile,
    double? ThirdQuartile,
    double? InterquartileRange,
    double? Minimum,
    double? Maximum);

public sealed record CpuHotspotRankStability(
    int CompletePairs,
    double? MedianTopKOverlap,
    double? SameSelectedRankFraction,
    double? MedianAbsoluteSelectedRankDelta);

public sealed record CpuHotspotCompatibilityMismatch(
    string PairKey,
    string Dimension,
    string? BaselineValue,
    string? CandidateValue,
    string Reason);

public sealed record CpuHotspotMovementCaseResult(
    string Id,
    ScenarioStatisticalAvailability Availability,
    string? UnavailableReason,
    IReadOnlyList<CpuHotspotCompatibilityMismatch> CompatibilityMismatches,
    IReadOnlyList<CpuHotspotCaptureEvidence> BaselineCaptures,
    IReadOnlyList<CpuHotspotCaptureEvidence> CandidateCaptures,
    ScenarioStatisticalEvidenceResult? BaselineMedianShare,
    ScenarioStatisticalEvidenceResult? CandidateMedianShare,
    ScenarioStatisticalEvidenceResult? PairedShareDelta,
    CpuHotspotCaptureDistribution BaselineShareDistribution,
    CpuHotspotCaptureDistribution CandidateShareDistribution,
    CpuHotspotCaptureDistribution BaselineUnresolvedDistribution,
    CpuHotspotCaptureDistribution CandidateUnresolvedDistribution,
    IReadOnlyList<double> PairedShareDeltas,
    CpuHotspotRankStability RankStability,
    CpuHotspotNextAction NextAction,
    IReadOnlyList<string> Limitations);

public sealed record CpuHotspotMovementReport(
    int SchemaVersion,
    string Id,
    CpuHotspotMovementVerdict Verdict,
    IReadOnlyList<CpuHotspotMovementCaseResult> Cases,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Limitations);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CpuHotspotMovementFixture))]
[JsonSerializable(typeof(CpuHotspotMovementReport))]
internal sealed partial class CpuHotspotMovementJsonContext : JsonSerializerContext;
