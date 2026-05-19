namespace DotnetDiagnosticsMcp.Core.Counters;

/// <summary>Kind of <see cref="CounterValue"/>: a mean over the interval, or a sum of increments.</summary>
public enum CounterKind
{
    Mean,
    Sum,
}

/// <summary>A single counter sample reported by an EventCounters EventSource.</summary>
public sealed record CounterValue(
    string Provider,
    string Name,
    string DisplayName,
    double Value,
    CounterKind Kind,
    string? Unit = null);

/// <summary>Final aggregation returned by <see cref="ICounterCollector"/>.</summary>
public sealed record CounterSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<CounterValue> Counters);
