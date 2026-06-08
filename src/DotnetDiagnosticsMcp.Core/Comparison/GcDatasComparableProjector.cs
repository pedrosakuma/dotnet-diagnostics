using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Gc;

namespace DotnetDiagnosticsMcp.Core.Comparison;

/// <summary>
/// Projects a <see cref="GcDatasSnapshot"/> into the headline DATAS tuning metrics
/// (heap-count churn, throughput-cost %, budgets). DATAS is inherently a time-evolving
/// adaptation, so these metrics are most useful as an N-way trend (did the heap count settle
/// or keep oscillating?). Pure metric kind — no key-set rows.
/// </summary>
public sealed class GcDatasComparableProjector : IComparableProjector
{
    public string Kind => CollectionHandleKinds.GcDatas;

    public bool CanProject(object artifact) => artifact is GcDatasSnapshot;

    public ComparableSnapshot Project(object artifact, string label)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact is not GcDatasSnapshot snapshot)
        {
            throw new ArgumentException($"Expected {nameof(GcDatasSnapshot)}, got {artifact.GetType().Name}.", nameof(artifact));
        }

        var overview = GcDatasQueryDispatcher.RenderOverview(snapshot, string.Empty).Data
            ?? throw new InvalidOperationException("DATAS overview projection returned no data.");

        var metrics = new List<MetricValue>();

        Add(metrics, "heapCountChanges", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Total, unit: "count",
            value: overview.HeapCountChanges);
        Add(metrics, "meanMedianThroughputCostPercent", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Percent, unit: "%",
            value: overview.MeanMedianThroughputCostPercent);
        Add(metrics, "maxMedianThroughputCostPercent", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Percent, unit: "%",
            value: overview.MaxMedianThroughputCostPercent);
        Add(metrics, "minHeapCount", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, unit: "count",
            value: overview.MinHeapCount);
        Add(metrics, "maxHeapCount", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, unit: "count",
            value: overview.MaxHeapCount);
        Add(metrics, "meanGen0BudgetMB", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, unit: "MB",
            value: overview.MeanGen0BudgetMB);
        Add(metrics, "meanSohStableSizeMB", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, unit: "MB",
            value: overview.MeanSohStableSizeMB);
        Add(metrics, "sampleCount", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Total, unit: "count",
            value: overview.SampleCount);
        Add(metrics, "tuningEventCount", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Total, unit: "count",
            value: overview.TuningEventCount);

        return new ComparableSnapshot(
            Schema: ComparableSnapshot.SchemaV1,
            Kind: Kind,
            Label: label,
            CapturedAt: snapshot.StartedAt,
            ProcessId: snapshot.ProcessId,
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
        double? value)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v))
        {
            return;
        }

        metrics.Add(new MetricValue(
            new MetricDefinition(name, role, direction, aggregation, MetricNormalization.None, unit),
            Math.Round(v, 4)));
    }
}
