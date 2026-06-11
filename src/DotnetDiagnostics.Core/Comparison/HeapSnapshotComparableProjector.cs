using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;

namespace DotnetDiagnostics.Core.Comparison;

/// <summary>
/// Projects a <see cref="HeapSnapshotArtifact"/> into key-set rows keyed by managed type.
/// </summary>
public sealed class HeapSnapshotComparableProjector : IComparableProjector
{
    public string Kind => "heap-snapshot";

    public bool CanProject(object artifact) => artifact is HeapSnapshotArtifact;

    public ComparableSnapshot Project(object artifact, string label)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact is not HeapSnapshotArtifact snapshot)
        {
            throw new ArgumentException($"Expected {nameof(HeapSnapshotArtifact)}, got {artifact.GetType().Name}.", nameof(artifact));
        }

        var rows = ProjectRows(snapshot, Kind);
        return new ComparableSnapshot(
            Schema: ComparableSnapshot.SchemaV1,
            Kind: Kind,
            Label: label,
            CapturedAt: snapshot.CapturedAt,
            ProcessId: snapshot.ProcessId,
            Metrics: Array.Empty<MetricValue>(),
            Rows: rows);
    }

    private static ComparableRow[] ProjectRows(HeapSnapshotArtifact snapshot, string kind)
    {
        var aggregates = BuildAggregates(snapshot, kind);
        return aggregates.Values
            .OrderByDescending(row => row.TotalBytes)
            .ThenBy(row => row.DisplayName, StringComparer.Ordinal)
            .Select(row => new ComparableRow(
                row.Key,
                row.DisplayName,
                new[]
                {
                    Metric("totalBytes", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Total, MetricNormalization.None, "bytes", row.TotalBytes),
                    Metric("instanceCount", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Total, MetricNormalization.None, "count", row.InstanceCount),
                }))
            .ToArray();
    }

    /// <summary>
    /// Typed pairwise projection shared with <see cref="ComparablePairwiseSampleDiff"/>: the same
    /// aggregation as <see cref="ProjectRows"/>, surfaced as the legacy
    /// <see cref="HeapDiffMetric"/> dictionary keyed by <see cref="TypeIdentity"/>.
    /// </summary>
    public static Dictionary<TypeIdentity, HeapDiffMetric> ProjectTyped(HeapSnapshotArtifact snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var aggregates = BuildAggregates(snapshot, "heap-snapshot");
        var result = new Dictionary<TypeIdentity, HeapDiffMetric>(ComparablePairwiseSampleDiff.TypeIdentityComparer.Instance);
        foreach (var row in aggregates.Values)
        {
            result[row.Identity] = new HeapDiffMetric(
                TotalBytes: row.TotalBytes,
                InstanceCount: row.InstanceCount);
        }

        return result;
    }

    private static Dictionary<string, HeapAggregate> BuildAggregates(HeapSnapshotArtifact snapshot, string kind)
    {
        var aggregates = new Dictionary<string, HeapAggregate>(StringComparer.Ordinal);
        foreach (var stat in snapshot.TopTypesByBytes.Concat(snapshot.TopTypesByInstances))
        {
            var identity = stat.Identity ?? new TypeIdentity(stat.TypeFullName) { ModuleName = stat.ModuleName };
            var key = ComparableKeyFactory.ForType(kind, identity, stat.TypeFullName, stat.ModuleName);
            var matchId = key.ExactId ?? key.StableId;
            if (aggregates.TryGetValue(matchId, out var existing))
            {
                aggregates[matchId] = existing with
                {
                    TotalBytes = Math.Max(existing.TotalBytes, stat.TotalBytes),
                    InstanceCount = Math.Max(existing.InstanceCount, stat.InstanceCount),
                };
                continue;
            }

            aggregates[matchId] = new HeapAggregate(key, stat.TypeFullName, identity, stat.TotalBytes, stat.InstanceCount);
        }

        return aggregates;
    }

    private static MetricValue Metric(
        string name,
        MetricRole role,
        BetterDirection direction,
        MetricAggregation aggregation,
        MetricNormalization normalizedBy,
        string unit,
        double value)
        => new(new MetricDefinition(name, role, direction, aggregation, normalizedBy, unit), Math.Round(value, 4));

    private sealed record HeapAggregate(ComparableKey Key, string DisplayName, TypeIdentity Identity, long TotalBytes, long InstanceCount);
}
