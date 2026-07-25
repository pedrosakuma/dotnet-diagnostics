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

        var baselineByType = HeapSnapshotComparableProjector.ProjectTypedByAvailableIdentity(baseline);
        var currentByType = HeapSnapshotComparableProjector.ProjectTypedByAvailableIdentity(current);

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
        // objects now?". Correlate by the strongest shared type identity, never by an ambiguous
        // display name shared by multiple modules.
        var retentionByType = IndexRetentionPaths(current.RetentionPaths, currentByType.Keys, notes);
        if (current.RetentionPaths is null || current.RetentionPaths.Count == 0)
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

            retentionByType.TryGetValue(identity, out var paths);
            var projectedPaths = paths?
                .Take(1)
                .Select(HeapSnapshotQueryDispatcher.ProjectRetentionPath)
                .ToArray();
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
                RetentionPaths = projectedPaths,
                TotalRetentionPaths = paths?.Count,
                OmittedRetentionPaths = paths is null ? null : paths.Count - (projectedPaths?.Length ?? 0),
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

    private static Dictionary<TypeIdentity, IReadOnlyList<RetentionPath>> IndexRetentionPaths(
        IReadOnlyList<RetentionPath>? paths,
        IEnumerable<TypeIdentity> currentIdentities,
        List<string> notes)
    {
        var identities = currentIdentities.ToArray();
        var index = new Dictionary<TypeIdentity, List<RetentionPath>>(HeapSnapshotComparableProjector.AvailableTypeIdentityComparer.Instance);
        var ambiguousByName = new Dictionary<string, int>(StringComparer.Ordinal);
        if (paths is not null)
        {
            foreach (var path in paths)
            {
                var candidates = identities
                    .Where(identity => string.Equals(identity.TypeFullName, path.TargetTypeFullName, StringComparison.Ordinal))
                    .ToArray();
                var matching = path.TargetIdentity is { } targetIdentity
                    ? candidates.Where(identity => MatchesAvailableIdentity(identity, targetIdentity)).ToArray()
                    : Array.Empty<TypeIdentity>();

                TypeIdentity? matchedIdentity = matching.Length == 1
                    ? matching[0]
                    : path.TargetIdentity is null && candidates.Length == 1
                        ? candidates[0]
                        : null;
                if (matchedIdentity is null)
                {
                    if (candidates.Length > 1)
                    {
                        ambiguousByName[path.TargetTypeFullName] =
                            ambiguousByName.GetValueOrDefault(path.TargetTypeFullName) + 1;
                    }
                    continue;
                }

                if (!index.TryGetValue(matchedIdentity, out var list))
                {
                    list = new List<RetentionPath>();
                    index[matchedIdentity] = list;
                }
                list.Add(path);
            }
        }

        foreach (var (typeName, count) in ambiguousByName.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            notes.Add(
                $"Skipped {count} retention path(s) for '{typeName}' because multiple current type identities share that full name and the retention evidence did not uniquely match MVID/token/module identity; no name-only correlation was applied.");
        }

        return index.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<RetentionPath>)pair.Value,
            HeapSnapshotComparableProjector.AvailableTypeIdentityComparer.Instance);
    }

    private static bool MatchesAvailableIdentity(TypeIdentity current, TypeIdentity retained)
    {
        if (!string.Equals(current.TypeFullName, retained.TypeFullName, StringComparison.Ordinal))
        {
            return false;
        }

        if (retained.ModuleVersionId is { } moduleVersionId)
        {
            return current.ModuleVersionId == moduleVersionId &&
                   (retained.MetadataToken is not { } token || current.MetadataToken == token);
        }

        if (!string.IsNullOrWhiteSpace(retained.ModulePath))
        {
            return string.Equals(current.ModulePath, retained.ModulePath, StringComparison.OrdinalIgnoreCase) &&
                   (retained.MetadataToken is not { } token || current.MetadataToken == token);
        }

        if (!string.IsNullOrWhiteSpace(retained.ModuleName))
        {
            return string.Equals(current.ModuleName, retained.ModuleName, StringComparison.OrdinalIgnoreCase) &&
                   (retained.MetadataToken is not { } token || current.MetadataToken == token);
        }

        return false;
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
