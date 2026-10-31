using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class GcPressureCharacterizationBounds
{
    public const int MaximumWindows = 8;
    public const int MinimumValidWindows = 5;
    public const int MinimumCooldownWindows = 2;
    public const int LiveWindowCount = 6;
    public const int LiveWindowSeconds = 3;
    public const int NormalGcDetailCap = 512;
    public const int LossGcDetailCap = 1;
}

public enum GcPressureScenarioKind
{
    [JsonStringEnumMemberName("stable-healthy")]
    StableHealthy,

    [JsonStringEnumMemberName("transient-allocation-burst")]
    TransientAllocationBurst,

    [JsonStringEnumMemberName("sustained-allocation-pressure")]
    SustainedAllocationPressure,

    [JsonStringEnumMemberName("retained-growth")]
    RetainedGrowth,

    [JsonStringEnumMemberName("capture-loss")]
    CaptureLoss,
}

public enum GcPressureWindowPhase
{
    [JsonStringEnumMemberName("baseline")]
    Baseline,

    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("cooldown")]
    Cooldown,
}

public enum GcPressureNextAction
{
    [JsonStringEnumMemberName("allocation-investigation")]
    AllocationInvestigation,

    [JsonStringEnumMemberName("retained-heap-investigation")]
    RetainedHeapInvestigation,

    [JsonStringEnumMemberName("continued-observation")]
    ContinuedObservation,

    [JsonStringEnumMemberName("justified-inconclusion")]
    JustifiedInconclusion,
}

public sealed record GcPressureWindow(
    int Index,
    GcPressureWindowPhase Phase,
    double LohSizeBytes,
    double Gen2CollectionsPerSecond,
    double PauseP95Milliseconds,
    double TimeInGcPercent,
    long SourceEvents,
    long LostOrDropped,
    ScenarioStatisticalRepresentation Representation);

public sealed record GcPressureCapture(
    int SchemaVersion,
    string Id,
    GcPressureScenarioKind Scenario,
    string RuntimeVersion,
    string OperatingSystem,
    string Architecture,
    double WindowSeconds,
    IReadOnlyList<GcPressureWindow> Windows,
    IReadOnlyList<string> Notes);

public sealed record GcPressureMetricEvidence(
    string QuantityId,
    string Unit,
    ScenarioStatisticalEvidenceResult Median,
    double? P95,
    ScenarioStatisticalEvidenceResult Slope);

public sealed record GcPressureTimingObservation(
    string FirstSignal,
    int FirstWindow,
    string SecondSignal,
    int SecondWindow,
    string Interpretation);

public sealed record GcPressureCharacterizationResult(
    int SchemaVersion,
    string CaptureId,
    GcPressureNextAction NextAction,
    string Reason,
    int PressureWindows,
    bool? PostLoadStabilized,
    GcPressureMetricEvidence LohSize,
    GcPressureMetricEvidence Gen2Rate,
    GcPressureMetricEvidence PauseDuration,
    GcPressureMetricEvidence TimeInGc,
    IReadOnlyList<GcPressureTimingObservation> TimingObservations,
    IReadOnlyList<string> Limitations);

public sealed record GcPressureReplayFixture(
    int SchemaVersion,
    IReadOnlyList<GcPressureReplayScenario> Scenarios);

public sealed record GcPressureReplayScenario(
    string Id,
    GcPressureCapture Capture,
    GcPressureNextAction ExpectedAction,
    bool? ExpectedPostLoadStabilized,
    int MinimumPressureWindows);
