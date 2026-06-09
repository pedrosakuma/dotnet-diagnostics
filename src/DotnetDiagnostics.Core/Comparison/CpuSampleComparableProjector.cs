using DotnetDiagnostics.Core.CpuSampling;
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

internal static class CpuSampleComparableProjection
{
    public static ComparableSnapshot Project(object artifact, string label, string kind)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact is not CpuSampleTraceArtifact snapshot)
        {
            throw new ArgumentException($"Expected {nameof(CpuSampleTraceArtifact)}, got {artifact.GetType().Name}.", nameof(artifact));
        }

        var rows = ProjectRows(snapshot, kind);
        return new ComparableSnapshot(
            Schema: ComparableSnapshot.SchemaV1,
            Kind: kind,
            Label: label,
            CapturedAt: snapshot.StartedAt,
            ProcessId: snapshot.ProcessId,
            Metrics: Array.Empty<MetricValue>(),
            Rows: rows);
    }

    private static ComparableRow[] ProjectRows(CpuSampleTraceArtifact artifact, string kind)
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
            if (aggregates.TryGetValue(matchId, out var existing))
            {
                aggregates[matchId] = existing with
                {
                    ExclusiveSamples = existing.ExclusiveSamples + node.ExclusiveSamples,
                    InclusiveSamples = Math.Max(existing.InclusiveSamples, node.InclusiveSamples),
                };
                continue;
            }

            aggregates[matchId] = new CpuAggregate(key, symbol.MethodFullName, node.ExclusiveSamples, node.InclusiveSamples);
        }

        var totalSamples = artifact.TotalSamples == 0 ? 1 : artifact.TotalSamples;
        return aggregates.Values
            .OrderByDescending(row => row.ExclusiveSamples)
            .ThenBy(row => row.DisplayName, StringComparer.Ordinal)
            .Select(row =>
            {
                var exclusivePercent = 100.0 * row.ExclusiveSamples / totalSamples;
                return new ComparableRow(
                    row.Key,
                    row.DisplayName,
                    new[]
                    {
                        Metric("exclusivePercent", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Percent, MetricNormalization.SampleCount, "%", exclusivePercent),
                        Metric("exclusiveSamples", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Total, MetricNormalization.None, "samples", row.ExclusiveSamples),
                        Metric("inclusiveSamples", MetricRole.Context, BetterDirection.Neutral, MetricAggregation.Total, MetricNormalization.None, "samples", row.InclusiveSamples),
                    });
            })
            .ToArray();
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

    private sealed record CpuAggregate(ComparableKey Key, string DisplayName, long ExclusiveSamples, long InclusiveSamples);
}
