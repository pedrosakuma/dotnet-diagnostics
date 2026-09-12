namespace DotnetDiagnostics.Core.ThreadPool;

public interface IThreadPoolCollector
{
    Task<ThreadPoolEventSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}

public sealed record ThreadPoolEventSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<ThreadPoolCountBucket> WorkerThreadTimeline,
    IReadOnlyList<ThreadPoolCountBucket> IocpThreadTimeline,
    IReadOnlyList<ThreadPoolHillClimbingSample> HillClimbing,
    IReadOnlyList<ThreadPoolWorkItemOrigin> WorkItemOrigins,
    ThreadPoolEffectiveSettings? EffectiveSettings,
    int TotalEnqueueEvents,
    int TotalDequeueEvents,
    IReadOnlyList<string> Notes,
    ThreadPoolEvidenceSummary? Evidence = null);

public sealed record ThreadPoolCountBucket(
    DateTimeOffset Timestamp,
    int Count,
    string? CountProvenance = null);

public sealed record ThreadPoolHillClimbingSample(
    DateTimeOffset Timestamp,
    string Reason,
    int? OldCount,
    int? NewCount,
    double? Throughput,
    string? ReasonProvenance = null,
    string? OldCountProvenance = null,
    string? NewCountProvenance = null);

public sealed record ThreadPoolEvidenceSummary(
    int HillClimbingEvents,
    int ConfirmedStarvationAdjustments,
    int ConfirmedCooperativeBlockingAdjustments,
    bool HasCompleteRuntimeReasonEvidence);

public sealed record ThreadPoolWorkItemOrigin(string Method, int Count);

public sealed record ThreadPoolEffectiveSettings(
    int WorkerMinThreads,
    int WorkerMaxThreads,
    int IocpMinThreads,
    int IocpMaxThreads);

public static class ThreadPoolEvidence
{
    public const string RuntimeObserved = "runtime-observed";
    public const string RuntimeUnrecognized = "runtime-unrecognized";
    public const string Missing = "missing";
    public const string CarriedForward = "carried-forward";
    public const string InferredFromDelta = "inferred-from-delta";
    public const string InferredFromNeighbor = "inferred-from-neighbor";

    public static bool IsConfirmedReason(ThreadPoolHillClimbingSample sample, string reason)
        => string.Equals(sample.Reason, reason, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sample.ReasonProvenance, RuntimeObserved, StringComparison.Ordinal);

    public static ThreadPoolEvidenceSummary Summarize(IReadOnlyList<ThreadPoolHillClimbingSample> samples)
        => new(
            samples.Count,
            samples.Count(static sample => IsConfirmedReason(sample, "Starvation")),
            samples.Count(static sample => IsConfirmedReason(sample, "CooperativeBlocking")),
            samples.Count > 0
                && samples.All(static sample => string.Equals(sample.ReasonProvenance, RuntimeObserved, StringComparison.Ordinal)));

    public static ThreadPoolEvidenceSummary? GetSummary(ThreadPoolEventSnapshot snapshot)
        => snapshot.Evidence
            ?? (snapshot.HillClimbing.Count > 0
                && snapshot.HillClimbing.All(static sample => sample.ReasonProvenance is not null)
                    ? Summarize(snapshot.HillClimbing)
                    : null);
}