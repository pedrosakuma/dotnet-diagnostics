namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public enum ThreadPoolBacklogNextAction
{
    CollectWaitsAndStacksNow,
    TreatBurstAsResolved,
    InvestigateOtherSignals,
    ContinueObservation,
    RecaptureDegradedEvidence,
}

public enum ThreadPoolStarvationSupport
{
    ConfirmedByRuntimeReason,
    NotEstablished,
    InconclusiveDegradedEvidence,
}

public sealed record ThreadPoolBacklogProtocol(
    string Id,
    string Version,
    double BacklogLevel,
    double HighWorkerLevel,
    double RequiredBacklogWindowProportion,
    double RequiredDurationAboveLevelSeconds,
    int MinimumWindows,
    int DrainTailWindows);

public sealed record ThreadPoolBacklogReason(
    string? Value,
    string Provenance,
    string? CaptureId,
    double? ObservedAtSeconds);

public sealed record ThreadPoolBacklogWindow(
    string Id,
    double StartSeconds,
    double DurationSeconds,
    double QueueLength,
    double WorkerCount,
    double? QueueP95,
    long SourceEventCount,
    long LostOrDropped,
    ScenarioStatisticalRepresentation Representation);

public sealed record ThreadPoolBacklogScenarioCase(
    string Id,
    ThreadPoolBacklogProtocol Protocol,
    ThreadPoolBacklogReason Reason,
    IReadOnlyList<ThreadPoolBacklogWindow> Windows,
    ThreadPoolBacklogExpected Expected);

public sealed record ThreadPoolBacklogExpected(
    ThreadPoolBacklogNextAction NextAction,
    ThreadPoolStarvationSupport StarvationSupport,
    double? QueueMedian,
    double? QueueP95Median,
    double? QueueSlope,
    double? WorkerMedian,
    double? WorkerSlope,
    int? BacklogWindows,
    double? BacklogWindowProportion,
    double? DurationAboveLevelSeconds,
    bool? DrainedAtTail);

public sealed record ThreadPoolBacklogReplayFixture(
    int SchemaVersion,
    string Id,
    IReadOnlyList<ThreadPoolBacklogScenarioCase> Cases);

public sealed record ThreadPoolBacklogAnalysis(
    ThreadPoolBacklogNextAction NextAction,
    ThreadPoolStarvationSupport StarvationSupport,
    ThreadPoolBacklogReason Reason,
    ScenarioStatisticalEvidenceResult QueueMedian,
    ScenarioStatisticalEvidenceResult? QueueP95Median,
    ScenarioStatisticalEvidenceResult QueueSlope,
    ScenarioStatisticalEvidenceResult WorkerMedian,
    ScenarioStatisticalEvidenceResult WorkerSlope,
    int? BacklogWindows,
    double? BacklogWindowProportion,
    double? DurationAboveLevelSeconds,
    bool? DrainedAtTail);
