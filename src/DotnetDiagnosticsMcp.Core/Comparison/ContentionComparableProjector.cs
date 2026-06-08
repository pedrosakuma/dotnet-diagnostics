using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Contention;

namespace DotnetDiagnosticsMcp.Core.Comparison;

/// <summary>
/// Projects monitor contention events into call-site key rows so before/after journeys can compare
/// contention duration at stable lock sites.
/// </summary>
public sealed class ContentionComparableProjector : IComparableProjector
{
    public string Kind => CollectionHandleKinds.ContentionSnapshot;

    public bool CanProject(object artifact) => artifact is ContentionSnapshot;

    public ComparableSnapshot Project(object artifact, string label)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact is not ContentionSnapshot snapshot)
        {
            throw new ArgumentException($"Expected {nameof(ContentionSnapshot)}, got {artifact.GetType().Name}.", nameof(artifact));
        }

        var rows = snapshot.Events
            .GroupBy(static item => new { item.CallSiteMethod, item.CallSiteModule })
            .Select(static group =>
            {
                var totalDuration = group.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration);
                var maxDuration = group.Max(static item => item.Duration);
                var distinctMonitors = group.Select(static item => item.LockId).Where(static lockId => lockId != 0).Distinct().Count();
                var module = group.Key.CallSiteModule;
                var method = group.Key.CallSiteMethod;
                var stableId = $"{module}\u001f{method}";
                var displayName = string.IsNullOrWhiteSpace(module) ? method : $"{module}!{method}";

                return new ComparableRow(
                    new ComparableKey("contention-callsite", stableId, Module: module, MethodName: method),
                    displayName,
                    new[]
                    {
                        Metric("totalContentionDurationMs", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Duration, "ms", totalDuration.TotalMilliseconds),
                        Metric("eventCount", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Total, "count", group.Count()),
                        Metric("maxContentionDurationMs", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Duration, "ms", maxDuration.TotalMilliseconds),
                        Metric("distinctMonitors", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Total, "count", distinctMonitors),
                    });
            })
            .OrderByDescending(static row => row.Metrics[0].Value)
            .ThenBy(static row => row.DisplayName, StringComparer.Ordinal)
            .ToArray();

        var metrics = new[]
        {
            Metric("totalContentionDurationMs", MetricRole.Context, BetterDirection.Lower, MetricAggregation.Duration, "ms", snapshot.TotalContentionDuration.TotalMilliseconds),
            Metric("totalEvents", MetricRole.Context, BetterDirection.Lower, MetricAggregation.Total, "count", snapshot.TotalEvents),
            Metric("distinctMonitors", MetricRole.Context, BetterDirection.Lower, MetricAggregation.Total, "count", snapshot.DistinctMonitors),
            Metric("maxContentionDurationMs", MetricRole.Context, BetterDirection.Lower, MetricAggregation.Duration, "ms", snapshot.MaxContentionDuration.TotalMilliseconds),
            Metric("durationSeconds", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Duration, "s", snapshot.Duration.TotalSeconds),
        };

        return new ComparableSnapshot(
            Schema: ComparableSnapshot.SchemaV1,
            Kind: Kind,
            Label: label,
            CapturedAt: snapshot.StartedAt,
            ProcessId: snapshot.ProcessId,
            Metrics: metrics,
            Rows: rows);
    }

    private static MetricValue Metric(
        string name,
        MetricRole role,
        BetterDirection direction,
        MetricAggregation aggregation,
        string unit,
        double value)
        => new(
            new MetricDefinition(name, role, direction, aggregation, MetricNormalization.None, unit),
            Math.Round(value, 4));
}
