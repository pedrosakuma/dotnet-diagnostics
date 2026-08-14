using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.Comparison;

/// <summary>
/// Projects a <see cref="CpuSampleTraceArtifact"/> into key-set rows keyed by sampled method.
/// </summary>
public sealed class CpuSampleComparableProjector : IComparableProjector
{
    public string Kind => "cpu-sample";

    public bool CanProject(object artifact) => artifact is CpuSampleTraceArtifact;

    public ComparableSnapshot Project(object artifact, string label)
        => CpuSampleComparableProjection.Project(artifact, label, Kind);
}

/// <summary>
/// Projects a native allocation call tree into key-set rows keyed by native frame.
/// </summary>
public sealed class NativeAllocSampleComparableProjector : IComparableProjector
{
    public string Kind => "native-alloc-sample";

    public bool CanProject(object artifact) => artifact is CpuSampleTraceArtifact;

    public ComparableSnapshot Project(object artifact, string label)
        => CpuSampleComparableProjection.Project(artifact, label, Kind);
}

public static class CpuSampleComparableProjection
{
    public static ComparableSnapshot Project(object artifact, string label, string kind)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact is not CpuSampleTraceArtifact snapshot)
        {
            throw new ArgumentException($"Expected {nameof(CpuSampleTraceArtifact)}, got {artifact.GetType().Name}.", nameof(artifact));
        }

        var rows = ProjectRows(snapshot, kind);
        var metrics = string.Equals(kind, "cpu-sample", StringComparison.Ordinal)
            ? ProjectSelfSampleMetrics(snapshot)
            : Array.Empty<MetricValue>();
        return new ComparableSnapshot(
            Schema: ComparableSnapshot.SchemaV1,
            Kind: kind,
            Label: label,
            CapturedAt: snapshot.StartedAt,
            ProcessId: snapshot.ProcessId,
            Metrics: metrics,
            Rows: rows);
    }

    private static MetricValue[] ProjectSelfSampleMetrics(CpuSampleTraceArtifact artifact)
    {
        if (artifact.SelfSamples is not { } selfSamples)
        {
            return Array.Empty<MetricValue>();
        }

        var totalSamples = artifact.TotalSamples == 0 ? 1 : artifact.TotalSamples;
        return
        [
            Metric(
                "waitingSelfPercent",
                MetricRole.Primary,
                BetterDirection.Lower,
                MetricAggregation.Percent,
                MetricNormalization.SampleCount,
                "%",
                100.0 * selfSamples.WaitingSamples / totalSamples),
            Metric(
                "runningSelfPercent",
                MetricRole.Context,
                BetterDirection.Neutral,
                MetricAggregation.Percent,
                MetricNormalization.SampleCount,
                "%",
                100.0 * selfSamples.RunningSamples / totalSamples),
            Metric(
                "waitingSelfSamples",
                MetricRole.Context,
                BetterDirection.Neutral,
                MetricAggregation.Total,
                MetricNormalization.None,
                "samples",
                selfSamples.WaitingSamples),
            Metric(
                "runningSelfSamples",
                MetricRole.Context,
                BetterDirection.Neutral,
                MetricAggregation.Total,
                MetricNormalization.None,
                "samples",
                selfSamples.RunningSamples),
        ];
    }

    private static ComparableRow[] ProjectRows(CpuSampleTraceArtifact artifact, string kind)
    {
        var aggregates = BuildAggregates(artifact, kind);
        var totalSamples = artifact.TotalSamples == 0 ? 1 : artifact.TotalSamples;
        return aggregates.Values
            .OrderByDescending(row => row.ExclusiveSamples)
            .ThenBy(row => row.DisplayName, StringComparer.Ordinal)
            .Select(row =>
            {
                var exclusivePercent = 100.0 * row.ExclusiveSamples / totalSamples;
                var metrics = new List<MetricValue>
                {
                    Metric("exclusivePercent", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Percent, MetricNormalization.SampleCount, "%", exclusivePercent),
                    Metric("exclusiveSamples", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Total, MetricNormalization.None, "samples", row.ExclusiveSamples),
                    Metric("inclusiveSamples", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Total, MetricNormalization.None, "samples", row.InclusiveSamples),
                };
                if (row.HasSelfSampleClassification)
                {
                    metrics.Add(Metric(
                        "runningExclusiveSamples",
                        MetricRole.Context,
                        BetterDirection.Neutral,
                        MetricAggregation.Total,
                        MetricNormalization.None,
                        "samples",
                        row.RunningExclusiveSamples));
                    metrics.Add(Metric(
                        "waitingExclusiveSamples",
                        MetricRole.Secondary,
                        BetterDirection.Lower,
                        MetricAggregation.Total,
                        MetricNormalization.None,
                        "samples",
                        row.WaitingExclusiveSamples));
                }

                return new ComparableRow(row.Key, row.DisplayName, metrics);
            })
            .ToArray();
    }

    /// <summary>
    /// Typed pairwise projection shared with <see cref="ComparablePairwiseSampleDiff"/>: the same
    /// aggregation as <see cref="ProjectRows"/>, surfaced as the legacy
    /// <see cref="CpuDiffMetric"/> dictionary keyed by <see cref="MethodDiffKey"/>.
    /// </summary>
    public static Dictionary<MethodDiffKey, CpuDiffMetric> ProjectTyped(CpuSampleTraceArtifact artifact, string kind)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var aggregates = BuildAggregates(artifact, kind);
        var totalSamples = artifact.TotalSamples == 0 ? 1 : artifact.TotalSamples;
        var result = new Dictionary<MethodDiffKey, CpuDiffMetric>(ComparablePairwiseSampleDiff.MethodDiffKeyComparer.Instance);
        foreach (var row in aggregates.Values)
        {
            result[new MethodDiffKey(row.Symbol, row.Identity)] = new CpuDiffMetric(
                ExclusiveSamples: row.ExclusiveSamples,
                InclusiveSamples: row.InclusiveSamples,
                ExclusivePercent: Math.Round(100.0 * row.ExclusiveSamples / totalSamples, 2));
        }

        return result;
    }

    private static Dictionary<string, CpuAggregate> BuildAggregates(CpuSampleTraceArtifact artifact, string kind)
    {
        var aggregates = new Dictionary<string, CpuAggregate>(StringComparer.Ordinal);
        foreach (var node in Flatten(artifact.Root))
        {
            if (string.Equals(node.Frame.Method, "<root>", StringComparison.Ordinal)
                || (node.ExclusiveSamples <= 0 && node.InclusiveSamples <= 0))
            {
                continue;
            }

            var symbol = new SymbolRef(node.Frame.Module, node.Frame.Method);
            var identity = node.Identity ?? (artifact.MethodIdentities.TryGetValue(symbol, out var resolved) ? resolved : null);
            var key = ComparableKeyFactory.ForMethod(kind, symbol, identity);
            var matchId = key.ExactId ?? key.StableId;
            var selfSamples = string.Equals(kind, "cpu-sample", StringComparison.Ordinal)
                ? node.SelfSamples
                : null;
            if (aggregates.TryGetValue(matchId, out var existing))
            {
                aggregates[matchId] = existing with
                {
                    ExclusiveSamples = existing.ExclusiveSamples + node.ExclusiveSamples,
                    InclusiveSamples = Math.Max(existing.InclusiveSamples, node.InclusiveSamples),
                    RunningExclusiveSamples = existing.RunningExclusiveSamples + (selfSamples?.RunningSamples ?? 0),
                    WaitingExclusiveSamples = existing.WaitingExclusiveSamples + (selfSamples?.WaitingSamples ?? 0),
                    HasSelfSampleClassification = existing.HasSelfSampleClassification || selfSamples is not null,
                };
                continue;
            }

            aggregates[matchId] = new CpuAggregate(
                key,
                symbol.MethodFullName,
                symbol,
                identity,
                node.ExclusiveSamples,
                node.InclusiveSamples,
                selfSamples?.RunningSamples ?? 0,
                selfSamples?.WaitingSamples ?? 0,
                selfSamples is not null);
        }

        return aggregates;
    }

    private static IEnumerable<CallTreeNode> Flatten(CallTreeNode root)
    {
        var stack = new Stack<CallTreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in current.Children)
            {
                stack.Push(child);
            }
        }
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

    private sealed record CpuAggregate(
        ComparableKey Key,
        string DisplayName,
        SymbolRef Symbol,
        MethodIdentity? Identity,
        long ExclusiveSamples,
        long InclusiveSamples,
        long RunningExclusiveSamples,
        long WaitingExclusiveSamples,
        bool HasSelfSampleClassification);
}
