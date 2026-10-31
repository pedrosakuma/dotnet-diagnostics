using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class AllocationHotspotBounds
{
    public const int MaximumCapturesPerScenario = 8;
    public const int MaximumRankedRows = 16;
    public const int TopK = 3;
    public const int MinimumStableCaptures = 4;
    public const int MaximumFixtureBytes = 512 * 1024;
}

public enum AllocationHotspotScenario
{
    [JsonStringEnumMemberName("stable_hotspot")]
    StableHotspot,

    [JsonStringEnumMemberName("low_sample")]
    LowSample,

    [JsonStringEnumMemberName("raw_empty")]
    RawEmpty,

    [JsonStringEnumMemberName("raw_nonzero_no_stack")]
    RawNonzeroNoStack,

    [JsonStringEnumMemberName("usable_stack_zero_weight")]
    UsableStackZeroWeight,

    [JsonStringEnumMemberName("changed_mix")]
    ChangedMix,

    [JsonStringEnumMemberName("invalid_handle")]
    InvalidHandle,

    [JsonStringEnumMemberName("repeated_aa")]
    RepeatedAa,
}

public enum AllocationAttributionState
{
    [JsonStringEnumMemberName("raw_empty")]
    RawEmpty,

    [JsonStringEnumMemberName("no_stack")]
    NoStack,

    [JsonStringEnumMemberName("zero_weight")]
    ZeroWeight,

    [JsonStringEnumMemberName("partial")]
    Partial,

    [JsonStringEnumMemberName("complete")]
    Complete,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

public enum AllocationHandleState
{
    [JsonStringEnumMemberName("available")]
    Available,

    [JsonStringEnumMemberName("invalid_or_expired")]
    InvalidOrExpired,
}

public enum AllocationHotspotAssessment
{
    [JsonStringEnumMemberName("focused_investigation_supported")]
    FocusedInvestigationSupported,

    [JsonStringEnumMemberName("insufficient_capture_count")]
    InsufficientCaptureCount,

    [JsonStringEnumMemberName("bounded_capture_observed_no_ticks")]
    BoundedCaptureObservedNoTicks,

    [JsonStringEnumMemberName("stack_attribution_unavailable")]
    StackAttributionUnavailable,

    [JsonStringEnumMemberName("byte_weighted_ranking_unavailable")]
    ByteWeightedRankingUnavailable,

    [JsonStringEnumMemberName("changed_mix_detected")]
    ChangedMixDetected,

    [JsonStringEnumMemberName("retained_attribution_unavailable")]
    RetainedAttributionUnavailable,

    [JsonStringEnumMemberName("repeated_control_reproducible")]
    RepeatedControlReproducible,

    [JsonStringEnumMemberName("not_reproducible")]
    NotReproducible,
}

public enum AllocationEvidenceOrigin
{
    [JsonStringEnumMemberName("deterministic_fixture")]
    DeterministicFixture,

    [JsonStringEnumMemberName("live_capture")]
    LiveCapture,

    [JsonStringEnumMemberName("controlled_projection")]
    ControlledProjection,
}

public sealed record AllocationRankedEvidence(
    string Key,
    int Rank,
    long EstimatedBytes,
    long AllocationTicks);

public sealed record AllocationHotspotCapture(
    string CaptureId,
    AllocationHotspotScenario Scenario,
    string WorkloadVariant,
    AllocationEvidenceOrigin Origin,
    string RuntimeVersion,
    string OperatingSystem,
    string Architecture,
    double DurationSeconds,
    AllocationHandleState HandleState,
    long RawAllocationTicks,
    long RawEstimatedBytes,
    long AttributedAllocationTicks,
    long AttributedEstimatedBytes,
    IReadOnlyList<AllocationRankedEvidence> Types,
    IReadOnlyList<AllocationRankedEvidence> Stacks,
    IReadOnlyList<string> Limitations);

public sealed record AllocationTopKStability(
    int ComparisonCount,
    double? MedianMembershipJaccard,
    double? MinimumMembershipJaccard,
    double? MedianSharedRankCorrelation,
    double? MinimumSharedRankCorrelation,
    int ComparisonsWithRankCorrelation);

public sealed record AllocationScenarioAnalysis(
    AllocationHotspotScenario Scenario,
    int CaptureCount,
    AllocationAttributionState AttributionState,
    ScenarioStatisticalEvidenceResult EstimatedBytesMedian,
    ScenarioStatisticalEvidenceResult EstimatedBytesMad,
    ScenarioStatisticalEvidenceResult AllocationTicksMedian,
    ScenarioStatisticalEvidenceResult AllocationTicksMad,
    double? EstimatedBytesRelativeMad,
    double? AllocationTicksRelativeMad,
    double? MedianAttributedByteShare,
    double? MedianUnattributedByteShare,
    double? MedianAttributedTickShare,
    AllocationTopKStability TypeTopK,
    AllocationTopKStability StackTopK,
    AllocationHotspotAssessment Assessment,
    bool ExpectedBehaviorObserved,
    IReadOnlyList<string> Limitations);

public sealed record AllocationHotspotContrast(
    string Id,
    AllocationHotspotScenario BaselineScenario,
    AllocationHotspotScenario CandidateScenario,
    ScenarioStatisticalEvidenceResult EstimatedBytesPairedDelta,
    ScenarioStatisticalEvidenceResult AllocationTicksPairedDelta,
    AllocationTopKStability TypeTopK,
    AllocationTopKStability StackTopK,
    bool TopTypeChanged,
    bool TopStackChanged,
    bool ExpectedBehaviorObserved,
    IReadOnlyList<string> Limitations);

public sealed record AllocationHotspotStudyReport(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolVersion,
    string Environment,
    IReadOnlyList<AllocationHotspotCapture> Captures,
    IReadOnlyList<AllocationScenarioAnalysis> Scenarios,
    AllocationHotspotContrast RepeatedAaContrast,
    AllocationHotspotContrast ChangedMixContrast,
    string Verdict,
    IReadOnlyList<string> Conclusions,
    IReadOnlyList<string> Limitations);

public sealed record AllocationHotspotStudyFixture(
    int SchemaVersion,
    string Id,
    string Environment,
    IReadOnlyList<AllocationHotspotCapture> Captures,
    string ExpectedVerdict);
