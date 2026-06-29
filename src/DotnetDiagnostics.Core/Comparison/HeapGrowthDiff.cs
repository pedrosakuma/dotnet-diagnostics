using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;

namespace DotnetDiagnostics.Core.Comparison;

/// <summary>
/// Retention-aware <b>live heap growth</b> diff (issue #463 — Phase 15 A1). Given two
/// <see cref="HeapSnapshotArtifact"/> captures taken N seconds apart (an earlier
/// <c>baseline</c> and a later <c>current</c>), it ranks the managed types that
/// <i>grew</i> by retained bytes / instance count and — for the top growers — surfaces the
/// retention paths recorded on the <c>current</c> snapshot so the LLM can answer
/// "which types grew, and what's holding them?" in a single drill-down.
/// </summary>
/// <remarks>
/// Reuses <see cref="HeapSnapshotComparableProjector.ProjectTyped"/> (the same per-type
/// aggregation the pairwise <c>view="diff"</c> path uses) so the two surfaces never disagree
/// on which types are present or how their bytes/instances are counted. Unlike the generic
/// pairwise diff — which ranks <c>Changed</c> rows by percentage and buries large-but-modest-%
/// leaks — this view ranks strictly by absolute growth in the requested dimension, which is the
/// signal that matters for a steady-state leak hunt.
/// </remarks>
public static class HeapGrowthDiff
{
    /// <summary>Ranking dimension: order growers by retained-byte growth.</summary>
    public const string RankByBytes = "bytes";

    /// <summary>Ranking dimension: order growers by instance-count growth.</summary>
    public const string RankByInstances = "instances";

    /// <summary>
    /// Builds the growth view. <paramref name="rankBy"/> is normalized internally and must be
    /// <see cref="RankByBytes"/> or <see cref="RankByInstances"/> (validated by the caller).
    /// Only types whose growth in the ranking dimension is positive <i>and</i> whose percentage
    /// growth meets <paramref name="minDeltaPct"/> are surfaced; the list is truncated to
    /// <paramref name="topN"/> rows after ranking.
    /// </summary>
    public static HeapGrowthResult Build(
        HeapSnapshotArtifact baseline,
        string baselineHandle,
        HeapSnapshotArtifact current,
        string currentHandle,
        string rankBy,
        double minDeltaPct,
        int topN)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentHandle);
        ArgumentOutOfRangeException.ThrowIfNegative(minDeltaPct);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        var rankByInstances = string.Equals(rankBy?.Trim(), RankByInstances, StringComparison.OrdinalIgnoreCase);
        var normalizedRank = rankByInstances ? RankByInstances : RankByBytes;

        var baselineByType = HeapSnapshotComparableProjector.ProjectTyped(baseline);
        var currentByType = HeapSnapshotComparableProjector.ProjectTyped(current);

        var notes = new List<string>();
        if (baseline.ProcessId != current.ProcessId)
        {
            notes.Add($"Comparison spans different runs/processes: baseline pid {baseline.ProcessId}, current pid {current.ProcessId}. Per-type deltas may be meaningless across a restart.");
        }

        if (current.CapturedAt < baseline.CapturedAt)
        {
            notes.Add($"Current snapshot '{currentHandle}' was captured before baseline '{baselineHandle}'; pass the EARLIER capture as baselineHandle for a meaningful growth diff.");
        }

        var overlapCount = baselineByType.Keys.Count(currentByType.ContainsKey);
        if (overlapCount == 0)
        {
            notes.Add("No overlapping types between baseline and current snapshots; growth is reported as all-new allocations.");
        }

        // Retention paths recorded on the *current* snapshot answer "what's holding the grown
        // objects now?". Index them by target type so we can attach to each grower cheaply.
        var retentionByType = IndexRetentionPaths(current.RetentionPaths);
        if (retentionByType.Count == 0)
        {
            notes.Add("Current snapshot carries no retention paths; re-run inspect_heap(source=\"live\", includeRetentionPaths=true) to populate \"what's holding them\" for the top growers.");
        }

        var growers = new List<HeapTypeGrowth>();
        foreach (var (identity, currentMetric) in currentByType)
        {
            baselineByType.TryGetValue(identity, out var baselineMetric);
            var baselineBytes = baselineMetric?.TotalBytes ?? 0;
            var baselineInstances = baselineMetric?.InstanceCount ?? 0;
            var bytesDelta = currentMetric.TotalBytes - baselineBytes;
            var instancesDelta = currentMetric.InstanceCount - baselineInstances;

            var rankingDelta = rankByInstances ? instancesDelta : bytesDelta;
            if (rankingDelta <= 0)
            {
                continue;
            }

            var bytesPct = PercentDelta(baselineBytes, currentMetric.TotalBytes);
            var instancesPct = PercentDelta(baselineInstances, currentMetric.InstanceCount);
            var rankingPct = rankByInstances ? instancesPct : bytesPct;
            if (rankingPct < minDeltaPct)
            {
                continue;
            }

            retentionByType.TryGetValue(identity.TypeFullName, out var paths);
            growers.Add(new HeapTypeGrowth(
                identity.TypeFullName,
                identity.ModuleName,
                baselineBytes,
                currentMetric.TotalBytes,
                bytesDelta,
                bytesPct,
                baselineInstances,
                currentMetric.InstanceCount,
                instancesDelta,
                instancesPct,
                IsNew: baselineMetric is null)
            {
                Identity = identity,
                RetentionPaths = paths,
            });
        }

        var ranked = growers
            .OrderByDescending(g => rankByInstances ? g.InstancesDelta : g.BytesDelta)
            .ThenByDescending(g => rankByInstances ? g.BytesDelta : g.InstancesDelta)
            .ThenBy(g => g.TypeFullName, StringComparer.Ordinal)
            .Take(topN)
            .ToArray();

        var totalGrowthBytes = current.Heap.TotalBytes - baseline.Heap.TotalBytes;
        // A leak is suspected whenever managed types retained more bytes/instances than the
        // baseline; the process-wide heap total is noisier (GC timing) so it stays informational.
        var verdict = growers.Count > 0 ? "leak_suspected" : "stable";

        return new HeapGrowthResult(
            baselineHandle,
            currentHandle,
            current.ProcessId,
            baseline.CapturedAt,
            current.CapturedAt,
            current.CapturedAt - baseline.CapturedAt,
            normalizedRank,
            minDeltaPct,
            baseline.Heap.TotalBytes,
            current.Heap.TotalBytes,
            totalGrowthBytes,
            growers.Count,
            ranked,
            verdict)
        {
            Notes = notes.Count > 0 ? notes : null,
        };
    }

    private static Dictionary<string, IReadOnlyList<RetentionPath>> IndexRetentionPaths(IReadOnlyList<RetentionPath>? paths)
    {
        var index = new Dictionary<string, List<RetentionPath>>(StringComparer.Ordinal);
        if (paths is not null)
        {
            foreach (var path in paths)
            {
                if (!index.TryGetValue(path.TargetTypeFullName, out var list))
                {
                    list = new List<RetentionPath>();
                    index[path.TargetTypeFullName] = list;
                }

                list.Add(path);
            }
        }

        return index.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<RetentionPath>)kv.Value, StringComparer.Ordinal);
    }

    private static double PercentDelta(long baseline, long current)
    {
        if (baseline == 0)
        {
            return current == 0 ? 0 : 100;
        }

        return Math.Round(((double)(current - baseline) / Math.Abs(baseline)) * 100, 2);
    }
}
