using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;

namespace DotnetDiagnostics.Core.Comparison;

/// <summary>
/// Projects an <see cref="AllocationSampleArtifact"/> into key-set rows keyed by allocated type.
/// </summary>
public sealed class AllocationSampleComparableProjector : IComparableProjector
{
    public string Kind => "allocation-sample";

    public bool CanProject(object artifact) => artifact is AllocationSampleArtifact;

    public ComparableSnapshot Project(object artifact, string label)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact is not AllocationSampleArtifact sampleArtifact)
        {
            throw new ArgumentException($"Expected {nameof(AllocationSampleArtifact)}, got {artifact.GetType().Name}.", nameof(artifact));
        }

        var sample = sampleArtifact.Summary;
        var rows = ProjectRows(sample, Kind);
        return new ComparableSnapshot(
            Schema: ComparableSnapshot.SchemaV1,
            Kind: Kind,
            Label: label,
            CapturedAt: sample.StartedAt,
            ProcessId: sample.ProcessId,
            Metrics: Array.Empty<MetricValue>(),
            Rows: rows);
    }

    private static ComparableRow[] ProjectRows(AllocationSample sample, string kind)
    {
        var aggregates = BuildAggregates(sample, kind);
        return aggregates.Values
            .OrderByDescending(row => row.BytesPerSecond)
            .ThenBy(row => row.DisplayName, StringComparer.Ordinal)
            .Select(row => new ComparableRow(
                row.Key,
                row.DisplayName,
                new[]
                {
                    Metric("bytesPerSecond", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Rate, MetricNormalization.DurationSeconds, "bytes/s", row.BytesPerSecond),
                    Metric("allocCountPerSecond", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Rate, MetricNormalization.DurationSeconds, "allocations/s", row.AllocCountPerSecond),
                    Metric("totalBytes", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Total, MetricNormalization.None, "bytes", row.TotalBytes),
                    Metric("allocCount", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Total, MetricNormalization.None, "count", row.AllocCount),
                    Metric("durationSeconds", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Duration, MetricNormalization.None, "s", row.DurationSeconds),
                }))
            .ToArray();
    }

    /// <summary>
    /// Typed pairwise projection shared with <see cref="ComparablePairwiseSampleDiff"/>: the same
    /// aggregation as <see cref="ProjectRows"/>, surfaced as the legacy
    /// <see cref="AllocationDiffMetric"/> dictionary keyed by <see cref="TypeIdentity"/>.
    /// </summary>
    public static Dictionary<TypeIdentity, AllocationDiffMetric> ProjectTyped(AllocationSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var aggregates = BuildAggregates(sample, "allocation-sample");
        var result = new Dictionary<TypeIdentity, AllocationDiffMetric>(ComparablePairwiseSampleDiff.TypeIdentityComparer.Instance);
        foreach (var row in aggregates.Values)
        {
            result[row.Identity] = new AllocationDiffMetric(
                TotalBytes: row.TotalBytes,
                AllocCount: row.AllocCount,
                BytesPerSecond: row.BytesPerSecond,
                AllocCountPerSecond: row.AllocCountPerSecond,
                DurationSeconds: row.DurationSeconds);
        }

        return result;
    }

    private static Dictionary<string, AllocationAggregate> BuildAggregates(AllocationSample sample, string kind)
    {
        var aggregates = new Dictionary<string, AllocationAggregate>(StringComparer.Ordinal);
        foreach (var stat in sample.TopByBytes.Concat(sample.TopByCount))
        {
            var identity = stat.Identity ?? new TypeIdentity(stat.TypeName);
            var key = ComparableKeyFactory.ForType(kind, identity, stat.TypeName, identity.ModuleName);
            var metric = new AllocationAggregate(
                key,
                stat.TypeName,
                identity,
                TotalBytes: stat.TotalBytes,
                AllocCount: stat.EventCount,
                BytesPerSecond: PerSecond(stat.TotalBytes, sample.Duration),
                AllocCountPerSecond: PerSecond(stat.EventCount, sample.Duration),
                DurationSeconds: Math.Round(sample.Duration.TotalSeconds, 2));

            var matchId = key.ExactId ?? key.StableId;
            if (aggregates.TryGetValue(matchId, out var existing))
            {
                aggregates[matchId] = existing with
                {
                    TotalBytes = Math.Max(existing.TotalBytes, metric.TotalBytes),
                    AllocCount = Math.Max(existing.AllocCount, metric.AllocCount),
                    BytesPerSecond = Math.Max(existing.BytesPerSecond, metric.BytesPerSecond),
                    AllocCountPerSecond = Math.Max(existing.AllocCountPerSecond, metric.AllocCountPerSecond),
                    DurationSeconds = metric.DurationSeconds,
                };
                continue;
            }

            aggregates[matchId] = metric;
        }

        return aggregates;
    }

    private static double PerSecond(long value, TimeSpan duration)
        => Math.Round(duration.TotalSeconds <= 0 ? 0 : value / duration.TotalSeconds, 2);

    private static MetricValue Metric(
        string name,
        MetricRole role,
        BetterDirection direction,
        MetricAggregation aggregation,
        MetricNormalization normalizedBy,
        string unit,
        double value)
        => new(new MetricDefinition(name, role, direction, aggregation, normalizedBy, unit), Math.Round(value, 4));

    private sealed record AllocationAggregate(
        ComparableKey Key,
        string DisplayName,
        TypeIdentity Identity,
        long TotalBytes,
        long AllocCount,
        double BytesPerSecond,
        double AllocCountPerSecond,
        double DurationSeconds);
}
