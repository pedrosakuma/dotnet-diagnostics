using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ContentionConcentrationBounds
{
    public const int MaximumCapturesPerScenario = 12;
    public const int MaximumTrackedLocks = 32;
    public const int MaximumReportedLocks = 8;
    public const int MaximumWindowsPerCapture = 8;
}

public enum ContentionConcentrationScenario
{
    [JsonStringEnumMemberName("dominant_lock")]
    DominantLock,

    [JsonStringEnumMemberName("diffuse_locks")]
    DiffuseLocks,

    [JsonStringEnumMemberName("owner_unavailable")]
    OwnerUnavailable,

    [JsonStringEnumMemberName("short_burst")]
    ShortBurst,

    [JsonStringEnumMemberName("truncated_inventory")]
    TruncatedInventory,

    [JsonStringEnumMemberName("healthy_control")]
    HealthyControl,
}

public enum ContentionDrilldownFocus
{
    [JsonStringEnumMemberName("focus_dominant_lock_and_owner")]
    FocusDominantLockAndOwner,

    [JsonStringEnumMemberName("focus_dominant_lock_owner_unavailable")]
    FocusDominantLockOwnerUnavailable,

    [JsonStringEnumMemberName("keep_contention_investigation_broad")]
    KeepContentionInvestigationBroad,

    [JsonStringEnumMemberName("time_align_and_repeat_capture")]
    TimeAlignAndRepeatCapture,

    [JsonStringEnumMemberName("inconclusive_due_to_truncated_inventory")]
    InconclusiveDueToTruncatedInventory,

    [JsonStringEnumMemberName("bounded_capture_observed_no_contention")]
    BoundedCaptureObservedNoContention,
}

public sealed record ContentionLockWaitEvidence(
    string LockKey,
    int EventCount,
    double TotalWaitMilliseconds,
    double Share);

public sealed record ContentionWindowEvidence(
    int WindowIndex,
    int EventCount,
    double TotalWaitMilliseconds,
    double Top1WaitDurationShare,
    double Top3WaitDurationShare);

public sealed record ContentionCaptureEvidence(
    string CaptureId,
    ContentionConcentrationScenario Scenario,
    int ProcessId,
    string RuntimeVersion,
    string OperatingSystem,
    double CaptureDurationSeconds,
    int TotalEvents,
    int DistinctLocks,
    double TotalWaitMilliseconds,
    double P50WaitMilliseconds,
    double P95WaitMilliseconds,
    double MaxWaitMilliseconds,
    double? Top1WaitDurationShare,
    double? Top3WaitDurationShare,
    int ReportedLockCount,
    bool InventoryTruncated,
    bool AggregationOverflow,
    bool OwnerMetadataAvailable,
    bool? OwnerOverlapObserved,
    int ActiveWindowCount,
    int TotalWindowCount,
    IReadOnlyList<ContentionLockWaitEvidence> TopLocks,
    IReadOnlyList<ContentionWindowEvidence> Windows,
    IReadOnlyList<string> Limitations);

public sealed record ContentionScenarioEvidence(
    ContentionConcentrationScenario Scenario,
    int CaptureCount,
    int CapturesWithContention,
    ScenarioStatisticalEvidenceResult Top1Median,
    ScenarioStatisticalEvidenceResult Top3Median,
    ScenarioStatisticalEvidenceResult Top1CaptureMad,
    ScenarioStatisticalEvidenceResult? OwnerOverlapRate,
    double? MedianActiveWindowFraction,
    ContentionDrilldownFocus DrilldownFocus,
    bool ExpectedBehaviorObserved,
    IReadOnlyList<string> Limitations);

public sealed record ContentionConcentrationReport(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolVersion,
    string Environment,
    int CapturesPerScenario,
    IReadOnlyList<ContentionCaptureEvidence> Captures,
    IReadOnlyList<ContentionScenarioEvidence> Scenarios,
    string Verdict,
    IReadOnlyList<string> Conclusions,
    IReadOnlyList<string> Limitations);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ContentionConcentrationReport))]
internal sealed partial class ContentionConcentrationJsonContext : JsonSerializerContext;
