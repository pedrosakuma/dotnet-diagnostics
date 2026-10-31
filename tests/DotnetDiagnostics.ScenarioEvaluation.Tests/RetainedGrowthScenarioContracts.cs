using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class RetainedGrowthScenarioBounds
{
    public const int MaximumRuns = 8;
    public const int MaximumCapturesPerRun = 3;
    public const int MaximumTypesPerCapture = 4_096;
    public const int MinimumRobustRuns = 3;
    public const int BootstrapResamples = 512;
}

public enum RetainedGrowthScenarioVariant
{
    [JsonStringEnumMemberName("retained_growth")]
    RetainedGrowth,

    [JsonStringEnumMemberName("allocation_churn")]
    AllocationChurn,

    [JsonStringEnumMemberName("stable_heap")]
    StableHeap,

    [JsonStringEnumMemberName("degraded")]
    Degraded,
}

public enum RetainedGrowthPositiveEvidence
{
    [JsonStringEnumMemberName("supported")]
    Supported,

    [JsonStringEnumMemberName("not_established")]
    NotEstablished,

    [JsonStringEnumMemberName("inconclusive")]
    Inconclusive,
}

public enum RetainedGrowthRootPathAvailability
{
    [JsonStringEnumMemberName("available")]
    Available,

    [JsonStringEnumMemberName("unavailable_by_mechanism")]
    UnavailableByMechanism,

    [JsonStringEnumMemberName("incomplete_evidence")]
    IncompleteEvidence,
}

public sealed record RetainedGrowthCompatibility(
    string RuntimeFlavor,
    string RuntimeVersionPolicy,
    string Os,
    string Architecture,
    string BackendId,
    string BackendVersion,
    string CaptureDuration,
    string CaptureWindowing,
    string CaptureProviders,
    string CaptureSamplingPeriod,
    string CaptureRetention,
    string CaptureEnrichment,
    string WorkloadScenario,
    string WorkloadVersion,
    string WorkloadParameters,
    string WorkloadConcurrency,
    string WorkloadDeployment);

public sealed record RetainedGrowthTypeObservation(
    string TypeName,
    long Bytes,
    long Instances);

public sealed record RetainedGrowthHeapObservation(
    string CaptureId,
    long TotalHeapBytes,
    IReadOnlyList<RetainedGrowthTypeObservation> Types,
    bool PostGcStabilized,
    int CaptureInducedGen2Collections,
    bool CaptureCompleted,
    long LostOrDropped,
    ScenarioStatisticalRepresentation Representation,
    bool PositiveRetentionSupported,
    bool AbsenceSupported,
    RetainedGrowthRootPathAvailability RootPathAvailability,
    int? RootPathCount);

public sealed record RetainedGrowthAllocationTraffic(
    long SampledBytes,
    long SampleEvents,
    long LostOrDropped,
    ScenarioStatisticalRepresentation Representation);

public sealed record RetainedGrowthScenarioRun(
    string RunId,
    ScenarioStatisticalPairOrder PairOrder,
    RetainedGrowthHeapObservation Baseline,
    RetainedGrowthHeapObservation Candidate,
    RetainedGrowthHeapObservation Persistence,
    RetainedGrowthAllocationTraffic? AllocationTraffic);

public sealed record RetainedGrowthScenarioEvidence(
    int SchemaVersion,
    string Id,
    RetainedGrowthScenarioVariant Variant,
    string TargetType,
    RetainedGrowthCompatibility BaselineCompatibility,
    RetainedGrowthCompatibility CandidateCompatibility,
    IReadOnlyList<RetainedGrowthScenarioRun> Runs);

public sealed record RetainedGrowthWorkloadTruth(
    RetainedGrowthScenarioVariant Variant,
    int SeedRetainedObjects,
    int WorkloadAllocatedObjects,
    int PayloadBytes,
    int ExpectedRetainedObjectsAfterWorkload,
    bool IntentionalRetention);

public sealed record RetainedGrowthRootPathSummary(
    RetainedGrowthRootPathAvailability Availability,
    int AvailableCaptures,
    int TotalCaptures);

public sealed record RetainedGrowthScenarioResult(
    int SchemaVersion,
    string ScenarioId,
    RetainedGrowthScenarioVariant Variant,
    RetainedGrowthPositiveEvidence PositiveRetainedGrowthEvidence,
    ScenarioStatisticalEvidenceResult RetainedBytesDelta,
    ScenarioStatisticalEvidenceResult RetainedInstancesDelta,
    ScenarioStatisticalEvidenceResult RepeatedRetainedByteChange,
    ScenarioStatisticalEvidenceResult RepeatedRetainedByteSpread,
    ScenarioStatisticalEvidenceResult RepeatedRetainedByteInterval,
    ScenarioStatisticalEvidenceResult PersistenceRate,
    ScenarioStatisticalEvidenceResult PostGcStabilizationRate,
    ScenarioStatisticalEvidenceResult GrowingTypeConcentration,
    ScenarioStatisticalEvidenceResult AllocationTraffic,
    IReadOnlyList<string> TopGrowingTypes,
    RetainedGrowthRootPathSummary RootPathEvidence,
    int CaptureInducedGen2Collections,
    IReadOnlyList<string> Limitations);

public sealed record RetainedGrowthReplayFixture(
    int SchemaVersion,
    string Id,
    RetainedGrowthWorkloadTruth Truth,
    RetainedGrowthScenarioEvidence Evidence,
    RetainedGrowthReplayExpectation Expected);

public sealed record RetainedGrowthReplayExpectation(
    RetainedGrowthPositiveEvidence PositiveRetainedGrowthEvidence,
    ScenarioStatisticalAvailability RetainedBytesAvailability,
    ScenarioStatisticalAvailability RetainedInstancesAvailability,
    ScenarioStatisticalAvailability RepeatedChangeAvailability,
    ScenarioStatisticalAvailability SpreadAvailability,
    ScenarioStatisticalAvailability IntervalAvailability,
    ScenarioStatisticalAvailability PersistenceAvailability,
    ScenarioStatisticalAvailability ConcentrationAvailability,
    RetainedGrowthRootPathAvailability RootPathAvailability,
    double? RetainedBytesAbsoluteDelta,
    double? RetainedInstancesAbsoluteDelta,
    double? RepeatedChangeEstimate,
    double? PersistenceRate,
    bool NoLeakVerdict);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RetainedGrowthReplayFixture))]
[JsonSerializable(typeof(RetainedGrowthScenarioResult))]
internal sealed partial class RetainedGrowthScenarioJsonContext : JsonSerializerContext;
