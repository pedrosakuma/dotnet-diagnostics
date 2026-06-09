using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.Comparison;

/// <summary>
/// Projects a <see cref="CounterSnapshot"/> into a flat set of named scalar metrics
/// (EventCounters + Meter instruments). Direction is <see cref="BetterDirection.Neutral"/>
/// because "up" is not generically good or bad for an arbitrary counter — the differ reports
/// the delta and lets the reader judge. Pure metric kind — no key-set rows.
/// </summary>
public sealed class CountersComparableProjector : IComparableProjector
{
    public string Kind => CollectionHandleKinds.Counters;

    public bool CanProject(object artifact) => artifact is CounterSnapshot;

    public ComparableSnapshot Project(object artifact, string label)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact is not CounterSnapshot snapshot)
        {
            throw new ArgumentException($"Expected {nameof(CounterSnapshot)}, got {artifact.GetType().Name}.", nameof(artifact));
        }

        // Dedupe by metric name (last wins); a flat metric set must have unique names for the differ.
        var metrics = new Dictionary<string, MetricValue>(StringComparer.Ordinal);

        foreach (var counter in snapshot.Counters)
        {
            var name = $"counter:{counter.Provider}/{counter.Name}";
            var aggregation = counter.Kind == CounterKind.Sum ? MetricAggregation.Total : MetricAggregation.Point;
            AddOrReplace(metrics, name, BetterDirection.Neutral, aggregation, counter.Unit, counter.Value);
        }

        foreach (var meter in snapshot.Meters)
        {
            var prefix = $"meter:{meter.Meter}/{meter.Instrument}{FormatTags(meter.Tags)}";
            if (meter.Histogram is { } h)
            {
                AddOrReplace(metrics, $"{prefix}/p50", BetterDirection.Neutral, MetricAggregation.Point, meter.Unit, h.P50);
                AddOrReplace(metrics, $"{prefix}/p95", BetterDirection.Neutral, MetricAggregation.Point, meter.Unit, h.P95);
                AddOrReplace(metrics, $"{prefix}/p99", BetterDirection.Neutral, MetricAggregation.Point, meter.Unit, h.P99);
                continue;
            }

            // Rate counters publish both a cumulative value and a per-second rate; emit both so the
            // duration-normalized signal isn't lost. Gauges publish LastValue only.
            if (meter.LastValue is { } last)
            {
                AddOrReplace(metrics, prefix, BetterDirection.Neutral, MetricAggregation.Point, meter.Unit, last);
            }

            if (meter.Rate is { } rate)
            {
                AddOrReplace(metrics, $"{prefix}/rate", BetterDirection.Neutral, MetricAggregation.Rate, meter.Unit, rate);
            }
        }

        return new ComparableSnapshot(
            Schema: ComparableSnapshot.SchemaV1,
            Kind: Kind,
            Label: label,
            CapturedAt: snapshot.StartedAt,
            ProcessId: snapshot.ProcessId,
            Metrics: metrics.Values.OrderBy(m => m.Definition.Name, StringComparer.Ordinal).ToArray(),
            Rows: Array.Empty<ComparableRow>());
    }

    private static string FormatTags(IReadOnlyDictionary<string, string?> tags)
    {
        if (tags.Count == 0)
        {
            return string.Empty;
        }

        // Escape delimiters so distinct tag sets never collapse to the same metric name (which
        // would silently overwrite a series). A null value is rendered as a bare key (no '='),
        // distinct from an empty value ("key=").
        var pairs = tags
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value is null
                ? Escape(kv.Key)
                : $"{Escape(kv.Key)}={Escape(kv.Value)}");
        return $"[{string.Join(",", pairs)}]";
    }

    private static string Escape(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);

    private static void AddOrReplace(
        Dictionary<string, MetricValue> metrics,
        string name,
        BetterDirection direction,
        MetricAggregation aggregation,
        string? unit,
        double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        metrics[name] = new MetricValue(
            new MetricDefinition(name, MetricRole.Context, direction, aggregation, MetricNormalization.None, unit),
            Math.Round(value, 4));
    }
}
