namespace DotnetDiagnostics.Core.Counters;

/// <summary>
/// Collects EventCounters from a target process over a fixed time window and returns the latest
/// value seen per counter, aggregated across the requested providers.
/// </summary>
public interface ICounterCollector
{
    Task<CounterSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? providers = null,
        IReadOnlyList<string>? meters = null,
        int intervalSeconds = 1,
        int maxInstrumentTimeSeries = 1000,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Collects the ordinary counter snapshot plus caller-declared bounded ThreadPool backlog evidence.
/// </summary>
public interface IThreadPoolBacklogCollector : ICounterCollector
{
    Task<CounterSnapshot> CollectWithThreadPoolBacklogAsync(
        int processId,
        TimeSpan duration,
        DotnetDiagnostics.Core.ThreadPool.ThreadPoolBacklogProtocol protocol,
        IReadOnlyList<string>? providers = null,
        IReadOnlyList<string>? meters = null,
        int intervalSeconds = 1,
        int maxInstrumentTimeSeries = 1000,
        CancellationToken cancellationToken = default);
}
