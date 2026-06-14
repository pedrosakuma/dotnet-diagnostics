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
        var latestWorker = LatestCount(snapshot.WorkerThreadTimeline);
        var peakWorker = PeakCount(snapshot.WorkerThreadTimeline);
        var latestIocp = LatestCount(snapshot.IocpThreadTimeline);
        var peakIocp = PeakCount(snapshot.IocpThreadTimeline);
        var starvationAdjustments = snapshot.HillClimbing.Count(static sample => string.Equals(sample.Reason, "Starvation", StringComparison.OrdinalIgnoreCase));
        var pendingWorkItemsEstimate = Math.Max(0, snapshot.TotalEnqueueEvents - snapshot.TotalDequeueEvents);

        Add(metrics, "starvationAdjustments", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Total, "count", starvationAdjustments);
        Add(metrics, "pendingWorkItemsEstimate", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Point, "count", pendingWorkItemsEstimate);
        Add(metrics, "latestWorkerThreadCount", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Point, "count", latestWorker);
        Add(metrics, "peakWorkerThreadCount", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Point, "count", peakWorker);
        Add(metrics, "latestIocpThreadCount", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Point, "count", latestIocp);
        Add(metrics, "peakIocpThreadCount", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Point, "count", peakIocp);
        Add(metrics, "hillClimbingEvents", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Total, "count", snapshot.HillClimbing.Count);
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

    private static int LatestCount(IReadOnlyList<ThreadPoolCountBucket> timeline)
        => timeline.Count > 0 ? timeline[^1].Count : 0;

    private static int PeakCount(IReadOnlyList<ThreadPoolCountBucket> timeline)
        => timeline.Count > 0 ? timeline.Max(static bucket => bucket.Count) : 0;

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
