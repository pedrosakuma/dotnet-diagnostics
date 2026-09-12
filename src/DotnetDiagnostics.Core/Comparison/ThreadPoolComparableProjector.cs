using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.ThreadPool;

namespace DotnetDiagnostics.Core.Comparison;

/// <summary>
/// Projects EventPipe ThreadPool snapshots into scalar signals only; rows are intentionally empty
/// so worker/starvation metrics drive the generic journey verdict.
/// </summary>
public sealed class ThreadPoolComparableProjector : IComparableProjector
{
    public string Kind => CollectionHandleKinds.ThreadPoolSnapshot;

    public bool CanProject(object artifact) => artifact is ThreadPoolEventSnapshot;

    public ComparableSnapshot Project(object artifact, string label)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact is not ThreadPoolEventSnapshot snapshot)
        {
            throw new ArgumentException($"Expected {nameof(ThreadPoolEventSnapshot)}, got {artifact.GetType().Name}.", nameof(artifact));
        }

        var metrics = new List<MetricValue>();
        var evidence = ThreadPoolEvidence.GetSummary(snapshot);

        if (evidence is not null)
        {
            Add(metrics, "starvationAdjustments", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Total, "count", evidence.ConfirmedStarvationAdjustments);
            Add(metrics, "cooperativeBlockingAdjustments", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Total, "count", evidence.ConfirmedCooperativeBlockingAdjustments);
            Add(metrics, "hillClimbingEvents", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Total, "count", evidence.HillClimbingEvents);
        }

        AddOptional(metrics, "latestWorkerThreadCount", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, "count", LatestCount(snapshot.WorkerThreadTimeline));
        AddOptional(metrics, "peakWorkerThreadCount", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, "count", PeakCount(snapshot.WorkerThreadTimeline));
        AddOptional(metrics, "latestIocpThreadCount", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, "count", LatestCount(snapshot.IocpThreadTimeline));
        AddOptional(metrics, "peakIocpThreadCount", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, "count", PeakCount(snapshot.IocpThreadTimeline));
        Add(metrics, "windowEnqueueDequeueDifference", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Total, "count", snapshot.TotalEnqueueEvents - snapshot.TotalDequeueEvents);
        Add(metrics, "totalEnqueueEvents", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Total, "count", snapshot.TotalEnqueueEvents);
        Add(metrics, "totalDequeueEvents", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Total, "count", snapshot.TotalDequeueEvents);
        Add(metrics, "workItemOriginCount", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Total, "count", snapshot.WorkItemOrigins.Count);
        Add(metrics, "durationSeconds", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Duration, "s", snapshot.Duration.TotalSeconds);

        if (snapshot.EffectiveSettings is { } settings)
        {
            Add(metrics, "workerMinThreads", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, "count", settings.WorkerMinThreads);
            Add(metrics, "workerMaxThreads", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, "count", settings.WorkerMaxThreads);
            Add(metrics, "iocpMinThreads", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, "count", settings.IocpMinThreads);
            Add(metrics, "iocpMaxThreads", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Point, "count", settings.IocpMaxThreads);
        }

        return new ComparableSnapshot(
            Schema: ComparableSnapshot.SchemaV1,
            Kind: Kind,
            Label: label,
            CapturedAt: snapshot.StartedAt,
            ProcessId: snapshot.ProcessId,
            Metrics: metrics,
            Rows: Array.Empty<ComparableRow>());
    }

    private static int? LatestCount(IReadOnlyList<ThreadPoolCountBucket> timeline)
        => timeline.LastOrDefault(static bucket => HasRuntimeObservedCount(bucket))?.Count;

    private static int? PeakCount(IReadOnlyList<ThreadPoolCountBucket> timeline)
    {
        var observedCounts = timeline
            .Where(static bucket => HasRuntimeObservedCount(bucket))
            .Select(static bucket => bucket.Count)
            .ToArray();
        return observedCounts.Length > 0 ? observedCounts.Max() : null;
    }

    private static bool HasRuntimeObservedCount(ThreadPoolCountBucket bucket)
        => string.Equals(bucket.CountProvenance, ThreadPoolEvidence.RuntimeObserved, StringComparison.Ordinal);

    private static void AddOptional(
        List<MetricValue> metrics,
        string name,
        MetricRole role,
        BetterDirection direction,
        MetricAggregation aggregation,
        string unit,
        double? value)
    {
        if (value.HasValue)
        {
            Add(metrics, name, role, direction, aggregation, unit, value.Value);
        }
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
