using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.Comparison;

/// <summary>
/// Backward-compatible typed pairwise (N=2 <c>baselineHandle</c>) diff over the comparable
/// projectors. Replaces the retired <c>SampleDiffer</c>: the artifact→row projection now lives in
/// the comparable projector classes (single owner, shared with the N-ary journey path), and this
/// adapter keeps the legacy <see cref="SampleDiff{TKey, TMetric}"/> envelope shape — same verdict
/// vocabulary, added/removed/changed bucketing, and per-row typed metrics — for clients that rely
/// on the typed pairwise output.
/// </summary>
public static class ComparablePairwiseSampleDiff
{
    public static SampleDiff<MethodDiffKey, CpuDiffMetric> Compare(
        CpuSampleTraceArtifact baseline,
        string baselineHandle,
        CpuSampleTraceArtifact current,
        string currentHandle,
        double minDeltaPct,
        int topN)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ValidateThresholds(minDeltaPct, topN);

        var notes = new List<string>();
        if (baseline.ProcessId != current.ProcessId)
        {
            notes.Add($"Comparison spans different runs/processes: baseline pid {baseline.ProcessId}, current pid {current.ProcessId}.");
        }

        return BuildDiff(
            kind: "cpu-sample",
            baselineHandle,
            currentHandle,
            minDeltaPct,
            topN,
            baseline: CpuSampleComparableProjection.ProjectTyped(baseline, "cpu-sample"),
            current: CpuSampleComparableProjection.ProjectTyped(current, "cpu-sample"),
            primaryMetric: static metric => metric.ExclusivePercent,
            notes);
    }

    public static SampleDiff<TypeIdentity, HeapDiffMetric> Compare(
        HeapSnapshotArtifact baseline,
        string baselineHandle,
        HeapSnapshotArtifact current,
        string currentHandle,
        double minDeltaPct,
        int topN)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ValidateThresholds(minDeltaPct, topN);

        var notes = new List<string>();
        if (baseline.ProcessId != current.ProcessId)
        {
            notes.Add($"Comparison spans different runs/processes: baseline pid {baseline.ProcessId}, current pid {current.ProcessId}.");
        }

        return BuildDiff(
            kind: "heap-snapshot",
            baselineHandle,
            currentHandle,
            minDeltaPct,
            topN,
            baseline: HeapSnapshotComparableProjector.ProjectTyped(baseline),
            current: HeapSnapshotComparableProjector.ProjectTyped(current),
            primaryMetric: static metric => metric.TotalBytes,
            notes);
    }

    public static SampleDiff<TypeIdentity, AllocationDiffMetric> Compare(
        AllocationSample baseline,
        string baselineHandle,
        AllocationSample current,
        string currentHandle,
        double minDeltaPct,
        int topN)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ValidateThresholds(minDeltaPct, topN);

        var notes = new List<string>();
        if (baseline.ProcessId != current.ProcessId)
        {
            notes.Add($"Comparison spans different runs/processes: baseline pid {baseline.ProcessId}, current pid {current.ProcessId}.");
        }
        if (baseline.Duration != current.Duration)
        {
            notes.Add($"Allocation diff normalized totals to per-second rates because durations differ ({baseline.Duration.TotalSeconds:F1}s → {current.Duration.TotalSeconds:F1}s).");
        }

        return BuildDiff(
            kind: "allocation-sample",
            baselineHandle,
            currentHandle,
            minDeltaPct,
            topN,
            baseline: AllocationSampleComparableProjector.ProjectTyped(baseline),
            current: AllocationSampleComparableProjector.ProjectTyped(current),
            primaryMetric: static metric => metric.BytesPerSecond,
            notes);
    }

    internal static SampleDiff<TKey, TMetric> BuildDiff<TKey, TMetric>(
        string kind,
        string baselineHandle,
        string currentHandle,
        double minDeltaPct,
        int topN,
        IReadOnlyDictionary<TKey, TMetric> baseline,
        IReadOnlyDictionary<TKey, TMetric> current,
        Func<TMetric, double> primaryMetric,
        List<string> notes)
        where TKey : notnull
        where TMetric : class
    {
        var baselineMedian = Median(baseline.Values.Select(primaryMetric));
        var overlapCount = baseline.Keys.Count(current.ContainsKey);

        var addedAll = current
            .Where(kv => !baseline.ContainsKey(kv.Key))
            .Select(kv => new DiffRow<TKey, TMetric>(
                kv.Key,
                Baseline: default,
                Current: kv.Value,
                DeltaAbs: Math.Round(primaryMetric(kv.Value), 2),
                DeltaPct: 100,
                Direction: "added"))
            .OrderByDescending(row => MetricForSort(row.Current, primaryMetric))
            .ToArray();

        var removedAll = baseline
            .Where(kv => !current.ContainsKey(kv.Key))
            .Select(kv => new DiffRow<TKey, TMetric>(
                kv.Key,
                Baseline: kv.Value,
                Current: default,
                DeltaAbs: Math.Round(-primaryMetric(kv.Value), 2),
                DeltaPct: -100,
                Direction: "removed"))
            .OrderByDescending(row => MetricForSort(row.Baseline, primaryMetric))
            .ToArray();

        var changedAll = baseline
            .Where(kv => current.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var baselineMetric = kv.Value;
                var currentMetric = current[kv.Key];
                var baselinePrimary = primaryMetric(baselineMetric);
                var currentPrimary = primaryMetric(currentMetric);
                var deltaAbs = Math.Round(currentPrimary - baselinePrimary, 2);
                var deltaPct = PercentDelta(baselinePrimary, currentPrimary);
                return new DiffRow<TKey, TMetric>(
                    kv.Key,
                    Baseline: baselineMetric,
                    Current: currentMetric,
                    DeltaAbs: deltaAbs,
                    DeltaPct: deltaPct,
                    Direction: deltaAbs >= 0 ? "up" : "down");
            })
            .Where(row => Math.Abs(row.DeltaPct) >= minDeltaPct)
            .OrderByDescending(row => Math.Abs(row.DeltaPct))
            .ThenByDescending(row => Math.Abs(row.DeltaAbs))
            .ToArray();

        if (overlapCount == 0)
        {
            notes.Add("No overlapping symbols/types between baseline and current handles; verdict forced to no_change.");
        }

        var regressionSignal = overlapCount > 0 && (
            changedAll.Any(row => row.Direction == "up") ||
            addedAll.Any(row => MetricForSort(row.Current, primaryMetric) > baselineMedian));

        var improvementSignal = overlapCount > 0 && (
            changedAll.Any(row => row.Direction == "down") ||
            removedAll.Any(row => MetricForSort(row.Baseline, primaryMetric) > baselineMedian));

        var verdict = overlapCount == 0
            ? "no_change"
            : regressionSignal && improvementSignal ? "mixed"
            : regressionSignal ? "regression"
            : improvementSignal ? "improvement"
            : "no_change";

        return new SampleDiff<TKey, TMetric>(
            Kind: kind,
            BaselineHandle: baselineHandle,
            CurrentHandle: currentHandle,
            MinDeltaPct: minDeltaPct,
            TotalAdded: addedAll.Length,
            TotalRemoved: removedAll.Length,
            TotalChanged: changedAll.Length,
            Added: addedAll.Take(topN).ToArray(),
            Removed: removedAll.Take(topN).ToArray(),
            Changed: changedAll.Take(topN).ToArray(),
            Verdict: verdict)
        {
            Notes = notes.Count > 0 ? notes : null,
        };
    }

    private static double MetricForSort<TMetric>(TMetric? metric, Func<TMetric, double> primaryMetric)
        where TMetric : class
        => metric is null ? 0 : primaryMetric(metric);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? Math.Round((ordered[middle - 1] + ordered[middle]) / 2, 2)
            : Math.Round(ordered[middle], 2);
    }

    private static double PercentDelta(double baseline, double current)
    {
        if (baseline == 0)
        {
            return current == 0 ? 0 : 100;
        }

        return Math.Round(((current - baseline) / Math.Abs(baseline)) * 100, 2);
    }

    private static void ValidateThresholds(double minDeltaPct, int topN)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minDeltaPct);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);
    }

    internal sealed class MethodDiffKeyComparer : IEqualityComparer<MethodDiffKey>
    {
        public static MethodDiffKeyComparer Instance { get; } = new();

        public bool Equals(MethodDiffKey? x, MethodDiffKey? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }
            if (x is null || y is null)
            {
                return false;
            }

            var xHasKey = TryGetMethodKey(x.Identity, out var left);
            var yHasKey = TryGetMethodKey(y.Identity, out var right);
            if (xHasKey || yHasKey)
            {
                return xHasKey && yHasKey && left == right;
            }

            return EqualityComparer<SymbolRef>.Default.Equals(x.Symbol, y.Symbol);
        }

        public int GetHashCode(MethodDiffKey obj)
            => TryGetMethodKey(obj.Identity, out var key)
                ? HashCode.Combine(key.ModuleVersionId, key.MetadataToken)
                : obj.Symbol.GetHashCode();
    }

    internal sealed class TypeIdentityComparer : IEqualityComparer<TypeIdentity>
    {
        public static TypeIdentityComparer Instance { get; } = new();

        public bool Equals(TypeIdentity? x, TypeIdentity? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }
            if (x is null || y is null)
            {
                return false;
            }

            var xHasKey = TryGetTypeKey(x, out var left);
            var yHasKey = TryGetTypeKey(y, out var right);
            if (xHasKey || yHasKey)
            {
                return xHasKey && yHasKey && left == right;
            }

            return string.Equals(x.TypeFullName, y.TypeFullName, StringComparison.Ordinal);
        }

        public int GetHashCode(TypeIdentity obj)
            => TryGetTypeKey(obj, out var key)
                ? HashCode.Combine(key.ModuleVersionId, key.MetadataToken)
                : StringComparer.Ordinal.GetHashCode(obj.TypeFullName);
    }

    private static bool TryGetMethodKey(MethodIdentity? identity, out (Guid ModuleVersionId, int MetadataToken) key)
    {
        if (identity is { ModuleVersionId: Guid mvid, MetadataToken: int token })
        {
            key = (mvid, token);
            return true;
        }

        key = default;
        return false;
    }

    private static bool TryGetTypeKey(TypeIdentity? identity, out (Guid ModuleVersionId, int MetadataToken) key)
    {
        if (identity is { ModuleVersionId: Guid mvid, MetadataToken: int token })
        {
            key = (mvid, token);
            return true;
        }

        key = default;
        return false;
    }
}
