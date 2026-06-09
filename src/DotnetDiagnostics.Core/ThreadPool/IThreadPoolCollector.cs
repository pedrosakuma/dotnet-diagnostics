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
    IReadOnlyList<string> Notes);

public sealed record ThreadPoolCountBucket(DateTimeOffset Timestamp, int Count);

public sealed record ThreadPoolHillClimbingSample(
    DateTimeOffset Timestamp,
    string Reason,
    int? OldCount,
    int? NewCount,
    double? Throughput);

public sealed record ThreadPoolWorkItemOrigin(string Method, int Count);

public sealed record ThreadPoolEffectiveSettings(
    int WorkerMinThreads,
    int WorkerMaxThreads,
    int IocpMinThreads,
    int IocpMaxThreads);