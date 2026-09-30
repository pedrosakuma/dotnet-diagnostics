using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Comparison;

internal static partial class HistoricalComparison
{
    private static bool Cpu(CpuSampleTraceArtifact left, CpuSampleTraceArtifact right, List<HistoricalMetric> output,
        List<HistoricalComparisonReason> reasons, HistoricalComparisonBudget budget)
    {
        var mismatch = !KnownEvidence(left.Evidence) || !KnownEvidence(right.Evidence) ||
            left.Evidence!.Backend != right.Evidence!.Backend || left.Evidence.Kind != right.Evidence.Kind;
        if (mismatch) reasons.Add(new("CpuEvidenceMismatch", "both", "evidence", "Explicit matching CPU backend and evidence semantics are required."));
        reasons.Add(new("SamplingNotEquivalentWork", "both", "normalization",
            "Counts and fractions describe retained samples, not CPU time. Sampling interval, workload and loss equivalence are not established."));
        var l = CpuRows(left, budget);
        var r = CpuRows(right, budget);
        foreach (var key in l.Keys.Union(r.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            budget.Visit();
            decimal? lv = l.TryGetValue(key, out var lvalue) ? lvalue : null;
            decimal? rv = r.TryGetValue(key, out var rvalue) ? rvalue : null;
            Add(output, key + "/exclusiveSamples", "samples", "retained-exclusive-count", "none",
                lv, rv, mismatch ? "CpuEvidenceMismatch" : null);
            Add(output, key + "/exclusiveFraction", "fraction", "retained-exclusive-fraction", "retained-total-samples",
                left.TotalSamples > 0 ? lv / left.TotalSamples : null,
                right.TotalSamples > 0 ? rv / right.TotalSamples : null,
                mismatch ? "CpuEvidenceMismatch" : left.TotalSamples <= 0 || right.TotalSamples <= 0 ? "SampleDenominatorUnavailable" : null);
            output[^1] = output[^1] with { LeftDenominator = left.TotalSamples, RightDenominator = right.TotalSamples };
        }
        return mismatch;

        static bool KnownEvidence(CpuSampleEvidence? evidence) => evidence?.Schema == CpuSampleEvidence.SchemaV1 &&
            (evidence.Backend, evidence.Kind) is
                (CpuSampleBackend.EventPipeSampleProfiler, CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits) or
                (CpuSampleBackend.LinuxPerf or CpuSampleBackend.WindowsEtw, CpuSampleEvidenceKind.OsOnCpuSamples);
    }

    private static Dictionary<string, decimal> CpuRows(CpuSampleTraceArtifact snapshot, HistoricalComparisonBudget budget)
    {
        var rows = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var pending = new Stack<CallTreeNode>();
        pending.Push(snapshot.Root);
        var visited = 0;
        while (pending.TryPop(out var node))
        {
            budget.Visit();
            if (++visited > MaximumRows || pending.Count + node.Children.Count > MaximumRows)
                HistoricalComparisonBudget.Limit("ComparisonSideRows");
            foreach (var child in node.Children) pending.Push(child);
            if (ReferenceEquals(node, snapshot.Root)) continue;
            if (node.ExclusiveSamples < 0 || snapshot.TotalSamples < 0)
                throw new CaptureStoreException(CaptureErrorCode.CorruptPackage, "NegativeSampleCount.");
            var symbol = new SymbolRef(node.Frame.Module, node.Frame.Method);
            var identity = node.Identity ?? (snapshot.MethodIdentities.TryGetValue(symbol, out var found) ? found : null);
            var comparable = ComparableKeyFactory.ForMethod("cpu-sample", symbol, identity);
            // Keep the closed signature with an exact metadata token: generic instantiations
            // sharing a token are not the same sampled method.
            var key = Key(comparable.ExactId, comparable.Module, identity?.ClosedSignature ?? symbol.MethodFullName);
            rows.TryGetValue(key, out var existing);
            rows[key] = existing + node.ExclusiveSamples;
        }
        if (rows.Values.Sum() > snapshot.TotalSamples)
            throw new CaptureStoreException(CaptureErrorCode.CorruptPackage, "ExclusiveSamplesExceedPopulation.");
        return rows;
    }

    private static bool Heap(HeapSnapshotArtifact left, HeapSnapshotArtifact right, List<HistoricalMetric> output,
        List<HistoricalComparisonReason> reasons, HistoricalComparisonBudget budget)
    {
        var mismatch = left.Origin != right.Origin || left.Runtime.Architecture != right.Runtime.Architecture ||
            !Enum.IsDefined(left.Origin) ||
            !Enum.TryParse<System.Runtime.InteropServices.Architecture>(left.Runtime.Architecture, true, out var architecture) ||
            !Enum.IsDefined(architecture);
        if (mismatch) reasons.Add(new("HeapOriginOrArchitectureMismatch", "both", "origin",
            "Matching heap origin and known architecture are required."));
        reasons.Add(new("RetainedTypeSubset", "both", "population",
            "Only retained type rows are compared; omission from a ranking is not proof of absence. Bytes are shallow per-type totals, not transitive retained size."));
        if (left.TopTypesByBytes.Count + left.TopTypesByInstances.Count > MaximumRows ||
            right.TopTypesByBytes.Count + right.TopTypesByInstances.Count > MaximumRows)
            HistoricalComparisonBudget.Limit("ComparisonSideRows");
        if (left.TopTypesByBytes.Concat(left.TopTypesByInstances).Concat(right.TopTypesByBytes).Concat(right.TopTypesByInstances)
            .Any(row => row.TotalBytes < 0 || row.InstanceCount < 0))
            throw new CaptureStoreException(CaptureErrorCode.CorruptPackage, "NegativeHeapQuantity.");
        if (left.Runtime.Version != right.Runtime.Version)
            reasons.Add(new("RuntimeVersionDiffers", "both", "runtime", "Runtime versions differ; runtime layout effects are not isolated from workload effects."));
        IReadOnlyList<HeapComparableType> l, r;
        try
        {
            l = HeapSnapshotComparableProjector.ProjectTypedByAvailableIdentity(left);
            r = HeapSnapshotComparableProjector.ProjectTypedByAvailableIdentity(right);
        }
        catch (OverflowException)
        {
            throw new CaptureStoreException(CaptureErrorCode.CorruptPackage, "HeapAggregateRangeExceeded.");
        }
        var matched = new HashSet<HeapComparableType>();
        foreach (var row in l)
        {
            budget.Visit(r.Count);
            var selection = HeapSnapshotComparableProjector.FindUniqueBestMatch(row.Identity, r);
            var candidate = selection.Match;
            var ambiguous = selection.Ambiguous;
            if (candidate is not null)
            {
                budget.Visit(l.Count);
                var reverse = HeapSnapshotComparableProjector.FindUniqueBestMatch(candidate.Identity, l);
                ambiguous |= reverse.Ambiguous;
                if (reverse.Match != row || reverse.Ambiguous) candidate = null;
            }
            if (candidate is not null) matched.Add(candidate);
            Append(row, candidate, ambiguous ? "AmbiguousTypeIdentity" : null);
        }
        foreach (var row in r)
            if (!matched.Contains(row)) Append(null, row, null);
        return mismatch;

        void Append(HeapComparableType? a, HeapComparableType? b, string? unavailable)
        {
            budget.Visit();
            var identity = (a ?? b)!.Identity;
            var key = Key(identity.ModuleVersionId?.ToString("D"),
                identity.MetadataToken?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                identity.ModuleName, identity.ModulePath, identity.TypeFullName);
            var reason = mismatch ? "HeapOriginOrArchitectureMismatch" : unavailable;
            Add(output, key + "/bytes", "bytes", "retained-type-shallow-bytes", "none", a?.Metric.TotalBytes, b?.Metric.TotalBytes, reason);
            Add(output, key + "/instances", "instances", "retained-type-instance-count", "none", a?.Metric.InstanceCount, b?.Metric.InstanceCount, reason);
        }
    }

    private static void Counters(CounterSnapshot left, CounterSnapshot right, List<HistoricalMetric> output,
        List<HistoricalComparisonReason> reasons, HistoricalComparisonBudget budget)
    {
        if (left.Counters.Count > MaximumRows || right.Counters.Count > MaximumRows)
            HistoricalComparisonBudget.Limit("ComparisonSideRows");
        if (left.Meters.Count > 0 || right.Meters.Count > 0)
            throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat, "MeterComparisonUnsupported.");
        var l = CounterRows(left);
        var r = CounterRows(right);
        reasons.Add(new("LastRetainedInterval", "both", "window",
            "Mean is the last retained interval mean; Sum is the last interval increment. Neither is a capture-wide mean or total; initial intervals can precede attachment."));
        foreach (var key in l.Keys.Union(r.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            budget.Visit();
            l.TryGetValue(key, out var a);
            r.TryGetValue(key, out var b);
            var reason = a is null || b is null ? "MissingObservation" :
                a.Kind != b.Kind ? "AggregationMismatch" :
                string.IsNullOrWhiteSpace(a.Unit) || string.IsNullOrWhiteSpace(b.Unit) ? "UnitUnknown" :
                a.Unit != b.Unit ? "UnitMismatch" :
                Number(a.Value) is null || Number(b.Value) is null ? "NumericRangeExceeded" : null;
            var kind = (a ?? b)!.Kind;
            var unit = a is not null && b is not null && a.Unit != b.Unit ? null : (a ?? b)!.Unit;
            Add(output, key + "/value", unit,
                reason == "AggregationMismatch" ? "incompatible" : Aggregation(kind), "none",
                a is null ? null : Number(a.Value), b is null ? null : Number(b.Value), reason);
            output[^1] = output[^1] with
            {
                LeftUnit = a?.Unit, RightUnit = b?.Unit,
                LeftAggregation = a is null ? null : Aggregation(a.Kind),
                RightAggregation = b is null ? null : Aggregation(b.Kind),
            };
            if (a?.Kind == CounterKind.Sum || b?.Kind == CounterKind.Sum)
            {
                var ar = Rate(a);
                var br = Rate(b);
                Add(output, key + "/rate", unit is null ? null : unit + "/s",
                    "last-interval-rate", "actual-eventcounter-interval-seconds", ar, br,
                    reason ?? (ar is null || br is null ? "IntervalMetadataUnavailable" : null));
                output[^1] = output[^1] with
                {
                    LeftDenominator = a?.IntervalSec is { } ai ? Number(ai) : null,
                    RightDenominator = b?.IntervalSec is { } bi ? Number(bi) : null,
                    LeftUnit = a?.Unit is { } au ? au + "/s" : null,
                    RightUnit = b?.Unit is { } bu ? bu + "/s" : null,
                };
            }
        }

        static Dictionary<string, CounterValue> CounterRows(CounterSnapshot snapshot)
        {
            var values = new Dictionary<string, CounterValue>(StringComparer.Ordinal);
            foreach (var row in snapshot.Counters)
            {
                if (row.Kind is not (CounterKind.Mean or CounterKind.Sum))
                    throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat, "UnsupportedCounterAggregation.");
                if (!values.TryAdd(Key(row.Provider, row.Name), row))
                    throw new CaptureStoreException(CaptureErrorCode.CorruptPackage, "DuplicateCounterIdentity.");
            }
            return values;
        }
        static string Aggregation(CounterKind kind) => kind == CounterKind.Mean ? "last-interval-mean" : "last-interval-increment";
        static decimal? Rate(CounterValue? value)
        {
            if (value is null || !CounterValueNormalization.TryGetRate(value, out _)) return null;
            var interval = Number(value.IntervalSec!.Value);
            var increment = Number(value.Value);
            if (interval is null or <= 0 || increment is null) return null;
            try { return increment / interval; }
            catch (OverflowException) { return null; }
        }
    }
}
