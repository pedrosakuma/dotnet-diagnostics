namespace DotnetDiagnosticsMcp.Core.Counters;

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
        int intervalSeconds = 1,
        CancellationToken cancellationToken = default);
}
