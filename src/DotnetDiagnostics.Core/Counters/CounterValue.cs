namespace DotnetDiagnostics.Core.Counters;

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
    string? Unit = null)
{
    /// <summary>Actual EventCounter increment interval from the payload, in seconds.</summary>
    public double? IntervalSec { get; init; }

    /// <summary>Time scale the producer uses when displaying an Increment as a rate.</summary>
    public TimeSpan? DisplayRateTimeScale { get; init; }
}

internal static class CounterValueNormalization
{
    internal static bool TryGetRate(CounterValue counter, out double rate)
    {
        if (counter.Kind != CounterKind.Sum
            || counter.IntervalSec is not double intervalSec
            || intervalSec <= 0
            || !double.IsFinite(intervalSec)
            || counter.DisplayRateTimeScale is not { } scale
            || scale <= TimeSpan.Zero)
        {
            rate = 0;
            return false;
        }

        rate = counter.Value / intervalSec;
        return double.IsFinite(rate);
    }

    internal static bool HasRateMetadata(CounterValue counter)
        => counter.IntervalSec.HasValue || counter.DisplayRateTimeScale.HasValue;

    internal static string? RateUnit(CounterValue counter)
    {
        if (counter.Unit is null)
        {
            return null;
        }

        return $"{counter.Unit}/s";
    }
}

/// <summary>Percentile snapshot reconstituted from a Meter histogram payload.</summary>
public sealed record HistogramSnapshot(
    long Count,
    double Sum,
    double P50,
    double P95,
    double P99);

/// <summary>A single Meter time series emitted via System.Diagnostics.Metrics.</summary>
public sealed record MeterInstrumentValue(
    string Meter,
    string Instrument,
    string? Unit,
    string Kind,
    IReadOnlyDictionary<string, string?> Tags,
    double? LastValue,
    double? Rate,
    HistogramSnapshot? Histogram);

/// <summary>Final aggregation returned by <see cref="ICounterCollector"/>.</summary>
public sealed record CounterSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<CounterValue> Counters,
    IReadOnlyList<MeterInstrumentValue> Meters,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Logical processor count reported once by the target's <c>System.Runtime/ProcessorCount</c>
    /// EventSource event when the provider is enabled. Null when the event was unavailable.
    /// </summary>
    public int? ProcessorCount { get; init; }

    /// <summary>
    /// The first-observed value for each counter present in <see cref="Counters"/> (<see cref="Counters"/>
    /// itself holds the last-observed value per key). Lets the signal-grouping layer (#527) compute an
    /// intra-window delta/trend without a second collection pass. <c>null</c> when the collector didn't
    /// populate it (e.g. older callers, or a window too short to observe more than one tick).
    /// </summary>
    public IReadOnlyList<CounterValue>? FirstCounters { get; init; }

    /// <summary>
    /// The maximum-observed value for each counter present in <see cref="Counters"/>, tracked across every
    /// tick of the observation window (<see cref="Counters"/> itself holds only the last-observed value per
    /// key). This is what makes transient churn — e.g. a Gen2/LOH counter that peaks mid-window and is
    /// already collected back down to (near) zero by the final tick — observable at all (see #858).
    /// <c>null</c> when the collector didn't populate it (e.g. older callers).
    /// </summary>
    public IReadOnlyList<CounterValue>? MaxCounters { get; init; }
}
