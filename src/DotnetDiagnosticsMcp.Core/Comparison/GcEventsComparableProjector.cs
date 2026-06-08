using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Gc;

namespace DotnetDiagnosticsMcp.Core.Comparison;

/// <summary>
/// Projects a <see cref="GcSummary"/> into scalar GC health metrics (collection volume,
/// pause budget, and per-generation counts). Pure metric kind — no key-set rows.
/// </summary>
public sealed class GcEventsComparableProjector : IComparableProjector
{
    public string Kind => CollectionHandleKinds.GcEvents;

    public bool CanProject(object artifact) => artifact is GcSummary;

    public ComparableSnapshot Project(object artifact, string label)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact is not GcSummary summary)
        {
            throw new ArgumentException($"Expected {nameof(GcSummary)}, got {artifact.GetType().Name}.", nameof(artifact));
        }

        var metrics = new List<MetricValue>();
        var durationSeconds = summary.Duration.TotalSeconds;
        var totalPauseMs = summary.TotalPauseTime.TotalMilliseconds;

        Add(metrics, "totalCollections", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Total, "count",
            summary.TotalCollections);
        Add(metrics, "totalPauseTimeMs", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Duration, "ms",
            totalPauseMs);
        Add(metrics, "pauseTimePercent", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Percent, "%",
            durationSeconds <= 0 ? 0 : 100.0 * summary.TotalPauseTime.TotalSeconds / durationSeconds);
        Add(metrics, "maxPauseTimeMs", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Duration, "ms",
            summary.MaxPauseTime.TotalMilliseconds);
        Add(metrics, "durationSeconds", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Duration, "s",
            durationSeconds);
        Add(metrics, "eventCount", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Total, "count",
            summary.Events.Count);

        var generationCounts = summary.Generations.ToDictionary(g => g.Generation, g => g.Count);
        for (var generation = 0; generation <= 2; generation++)
        {
            generationCounts.TryGetValue(generation, out var count);
            Add(metrics, $"gen{generation}Collections", MetricRole.Secondary, BetterDirection.Lower,
                MetricAggregation.Total, "count", count);
        }

        foreach (var generation in summary.Generations.Select(g => g.Generation).Where(g => g > 2).Order())
        {
            Add(metrics, $"gen{generation}Collections", MetricRole.Secondary, BetterDirection.Lower,
                MetricAggregation.Total, "count", generationCounts[generation]);
        }

        return new ComparableSnapshot(
            Schema: ComparableSnapshot.SchemaV1,
            Kind: Kind,
            Label: label,
            CapturedAt: summary.StartedAt,
            ProcessId: summary.ProcessId,
            Metrics: metrics,
            Rows: Array.Empty<ComparableRow>());
    }

    private static void Add(
        List<MetricValue> metrics,
        string name,
        MetricRole role,
        BetterDirection direction,
        MetricAggregation aggregation,
        string unit,
        double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        metrics.Add(new MetricValue(
            new MetricDefinition(name, role, direction, aggregation, MetricNormalization.None, unit),
            Math.Round(value, 4)));
    }
}
