namespace DotnetDiagnosticsMcp.Core.Comparison;

/// <summary>
/// Compares 2..N <see cref="ComparableSnapshot"/>s of the same kind into a
/// <see cref="SnapshotJourneyDiff"/>. The same engine serves both entry points (handle diff on
/// the server, file compare on the CLI) and both axes: ordered captures (one process over time →
/// trend) and unordered captures (replicas/pods → dispersion / outlier detection).
/// </summary>
public static class SnapshotDiffer
{
    private const string Improvement = "improvement";
    private const string Regression = "regression";
    private const string Mixed = "mixed";
    private const string NoChange = "no_change";
    private const string NoOverlap = "no_overlap";
    private const string Incomparable = "incomparable";
    private const string Uniform = "uniform";
    private const string Dispersed = "dispersed";

    private const string Improved = "improved";
    private const string Regressed = "regressed";
    private const string Flat = "flat";
    private const string NotApplicable = "n/a";

    private const double DispersionCvThreshold = 0.1;

    public static SnapshotJourneyDiff Compare(
        IReadOnlyList<ComparableSnapshot> snapshots,
        JourneyMode mode = JourneyMode.Trend,
        double minDeltaPct = 0,
        int topN = 25)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentOutOfRangeException.ThrowIfNegative(minDeltaPct);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        var labels = snapshots.Select(s => s.Label).ToArray();

        if (snapshots.Count < 2)
        {
            return Empty(snapshots.Count > 0 ? snapshots[0].Kind : "unknown", mode, labels, Incomparable,
                "Need at least two snapshots to compare.");
        }

        var kind = snapshots[0].Kind;
        if (snapshots.Any(s => !string.Equals(s.Kind, kind, StringComparison.Ordinal)))
        {
            return Empty(kind, mode, labels, Incomparable,
                $"Snapshots are of mixed kinds ({string.Join(", ", snapshots.Select(s => s.Kind).Distinct())}); only same-kind comparison is supported.");
        }

        var notes = new List<string>();
        if (snapshots.Select(s => s.ProcessId).Distinct().Count() > 1)
        {
            notes.Add("Comparison spans different process ids; treat as cross-run.");
        }

        var isKeySet = snapshots.Any(s => s.Rows.Count > 0);
        var metricSeries = BuildMetricSeries(snapshots, mode, minDeltaPct, notes);
        var (keyMatrix, keySetPrimaryDir) = isKeySet
            ? BuildKeyMatrix(snapshots, mode, minDeltaPct, topN, notes)
            : (Array.Empty<KeyMatrixRow>(), (BetterDirection?)null);

        if (mode == JourneyMode.Dispersion)
        {
            var dispVerdict = DispersionVerdict(metricSeries, keyMatrix);
            return new SnapshotJourneyDiff(kind, mode, labels, dispVerdict, metricSeries, keyMatrix, Pairwise: null, notes);
        }

        var pairwise = BuildPairwise(snapshots, isKeySet, keySetPrimaryDir, minDeltaPct);
        var verdict = pairwise.Headline.Verdict;
        return new SnapshotJourneyDiff(kind, mode, labels, verdict, metricSeries, keyMatrix, pairwise, notes);
    }

    private static SnapshotJourneyDiff Empty(
        string kind, JourneyMode mode, IReadOnlyList<string> labels, string verdict, string note)
        => new(kind, mode, labels, verdict, Array.Empty<MetricSeries>(), Array.Empty<KeyMatrixRow>(), null, new[] { note });

    // ---- Metric series ----------------------------------------------------------------------

    private static List<MetricSeries> BuildMetricSeries(
        IReadOnlyList<ComparableSnapshot> snapshots, JourneyMode mode, double minDeltaPct, List<string> notes)
    {
        var lookups = snapshots.Select(MetricLookup).ToArray();

        // Stable union of metric names in first-seen order.
        var order = new List<string>();
        var defs = new Dictionary<string, MetricDefinition>(StringComparer.Ordinal);
        foreach (var lookup in lookups)
        {
            foreach (var kv in lookup)
            {
                if (defs.TryAdd(kv.Key, kv.Value.Definition))
                {
                    order.Add(kv.Key);
                }
                else if (!string.Equals(defs[kv.Key].Unit, kv.Value.Definition.Unit, StringComparison.Ordinal))
                {
                    var note = $"Metric '{kv.Key}' has inconsistent units across captures; comparing raw values.";
                    if (!notes.Contains(note))
                    {
                        notes.Add(note);
                    }
                }
            }
        }

        var series = new List<MetricSeries>(order.Count);
        foreach (var name in order)
        {
            var def = defs[name];
            var values = lookups
                .Select(l => l.TryGetValue(name, out var mv) ? mv.Value : (double?)null)
                .ToArray();

            var first = values[0];
            var last = values[^1];
            double? deltaAbs = null;
            double? deltaPct = null;
            var direction = NotApplicable;
            if (first is double f && last is double t)
            {
                deltaAbs = Math.Round(t - f, 4);
                deltaPct = PercentDelta(f, t);
                direction = Direction(def.BetterDirection, f, t, deltaPct.Value, minDeltaPct);
            }
            else if (values.Any(v => v is null))
            {
                var note = $"Metric '{name}' is absent from one or more captures.";
                if (!notes.Contains(note))
                {
                    notes.Add(note);
                }
            }

            var trend = mode == JourneyMode.Trend ? ClassifyTrend(values) : MetricTrend.Insufficient;
            var dispersion = mode == JourneyMode.Dispersion ? Dispersion(values) : null;

            series.Add(new MetricSeries(def, values, deltaAbs, deltaPct, direction, trend, dispersion));
        }

        return series;
    }

    private static Dictionary<string, MetricValue> MetricLookup(ComparableSnapshot snapshot)
    {
        var map = new Dictionary<string, MetricValue>(StringComparer.Ordinal);
        foreach (var metric in snapshot.Metrics)
        {
            map[metric.Definition.Name] = metric; // last wins; projectors already dedupe
        }

        return map;
    }

    // ---- Key matrix -------------------------------------------------------------------------

    private static (IReadOnlyList<KeyMatrixRow> Rows, BetterDirection? PrimaryDir) BuildKeyMatrix(
        IReadOnlyList<ComparableSnapshot> snapshots, JourneyMode mode, double minDeltaPct, int topN, List<string> notes)
    {
        var lookups = snapshots.Select(s => KeyLookup(s, notes)).ToArray();

        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var display = new Dictionary<string, (ComparableKey Key, string Name)>(StringComparer.Ordinal);
        BetterDirection? primaryDir = null;

        foreach (var lookup in lookups)
        {
            foreach (var kv in lookup)
            {
                if (seen.Add(kv.Key))
                {
                    order.Add(kv.Key);
                    display[kv.Key] = (kv.Value.Key, kv.Value.Display);
                }

                primaryDir ??= kv.Value.Direction;
            }
        }

        var rows = new List<KeyMatrixRow>(order.Count);
        foreach (var id in order)
        {
            var values = lookups
                .Select(l => l.TryGetValue(id, out var entry) ? entry.Value : (double?)null)
                .ToArray();

            var first = values[0];
            var last = values[^1];
            double? deltaAbs = null;
            double? deltaPct = null;
            var direction = NotApplicable;
            if (first is double fv && last is double tv)
            {
                deltaAbs = Math.Round(tv - fv, 4);
                deltaPct = PercentDelta(fv, tv);
                direction = Direction(primaryDir ?? BetterDirection.Lower, fv, tv, deltaPct.Value, minDeltaPct);
            }

            var (key, name) = display[id];
            rows.Add(new KeyMatrixRow(key, name, values, deltaAbs, deltaPct, direction));
        }

        var total = rows.Count;
        var sorted = mode == JourneyMode.Dispersion
            ? rows
                .OrderByDescending(r => Dispersion(r.Values.ToArray())?.CoefficientOfVariation ?? -1)
                .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
                .Take(topN)
                .ToArray()
            : rows
                .OrderByDescending(r => r.DeltaAbs.HasValue ? Math.Abs(r.DeltaAbs.Value) : double.MaxValue)
                .ThenByDescending(r => r.Values.LastOrDefault(v => v.HasValue) ?? 0)
                .Take(topN)
                .ToArray();

        if (total > topN)
        {
            notes.Add(mode == JourneyMode.Dispersion
                ? $"Key matrix truncated to top {topN} of {total} rows by coefficient of variation."
                : $"Key matrix truncated to top {topN} of {total} rows by |first→last| delta.");
        }

        return (sorted, primaryDir);
    }

    private static Dictionary<string, (double Value, ComparableKey Key, string Display, BetterDirection Direction)> KeyLookup(
        ComparableSnapshot snapshot, List<string> notes)
    {
        var map = new Dictionary<string, (double, ComparableKey, string, BetterDirection)>(StringComparer.Ordinal);
        foreach (var row in snapshot.Rows)
        {
            var primary = PrimaryMetric(row.Metrics);
            if (primary is null)
            {
                continue;
            }

            var id = KeyMatchId(row.Key);
            if (!map.TryAdd(id, (primary.Value, row.Key, row.DisplayName, primary.Definition.BetterDirection)))
            {
                var note = $"Duplicate key '{id}' within a capture; keeping first occurrence.";
                if (!notes.Contains(note))
                {
                    notes.Add(note);
                }
            }
        }

        return map;
    }

    private static string KeyMatchId(ComparableKey key)
        => key.ExactId ?? key.StableId;

    private static MetricValue? PrimaryMetric(IReadOnlyList<MetricValue> metrics)
    {
        if (metrics.Count == 0)
        {
            return null;
        }

        return metrics.FirstOrDefault(m => m.Definition.Role == MetricRole.Primary)
            ?? metrics.FirstOrDefault(m => m.Definition.Role == MetricRole.Secondary)
            ?? metrics[0];
    }

    // ---- Verdicts ---------------------------------------------------------------------------

    private static PairwiseJourney BuildPairwise(
        IReadOnlyList<ComparableSnapshot> snapshots, bool isKeySet, BetterDirection? keySetDir, double minDeltaPct)
    {
        var n = snapshots.Count;
        var headline = new PairwiseComparison("first→last", 0, n - 1,
            Verdict2(snapshots[0], snapshots[^1], isKeySet, keySetDir, minDeltaPct));

        var baselineEach = new List<PairwiseComparison>(n - 1);
        for (var i = 1; i < n; i++)
        {
            baselineEach.Add(new PairwiseComparison($"first→{i}", 0, i,
                Verdict2(snapshots[0], snapshots[i], isKeySet, keySetDir, minDeltaPct)));
        }

        var adjacent = new List<PairwiseComparison>(n - 1);
        for (var i = 0; i < n - 1; i++)
        {
            adjacent.Add(new PairwiseComparison($"{i}→{i + 1}", i, i + 1,
                Verdict2(snapshots[i], snapshots[i + 1], isKeySet, keySetDir, minDeltaPct)));
        }

        return new PairwiseJourney(headline, baselineEach, adjacent);
    }

    private static string Verdict2(
        ComparableSnapshot from, ComparableSnapshot to, bool isKeySet, BetterDirection? keySetDir, double minDeltaPct)
        => isKeySet
            ? KeySetVerdict(from, to, keySetDir ?? BetterDirection.Lower, minDeltaPct)
            : MetricVerdict(from, to, minDeltaPct);

    private static string MetricVerdict(ComparableSnapshot from, ComparableSnapshot to, double minDeltaPct)
    {
        var fromMap = MetricLookup(from);
        var toMap = MetricLookup(to);

        if ((fromMap.Count > 0 || toMap.Count > 0) && !fromMap.Keys.Intersect(toMap.Keys, StringComparer.Ordinal).Any())
        {
            return NoOverlap;
        }

        var role = HighestRole(from.Metrics.Concat(to.Metrics));

        var improved = false;
        var regressed = false;
        foreach (var name in fromMap.Keys.Intersect(toMap.Keys, StringComparer.Ordinal))
        {
            var def = fromMap[name].Definition;
            if (def.Role != role)
            {
                continue;
            }

            var f = fromMap[name].Value;
            var t = toMap[name].Value;
            switch (Direction(def.BetterDirection, f, t, PercentDelta(f, t), minDeltaPct))
            {
                case Improved: improved = true; break;
                case Regressed: regressed = true; break;
                default: break;
            }
        }

        return Collapse(improved, regressed);
    }

    private static string KeySetVerdict(
        ComparableSnapshot from, ComparableSnapshot to, BetterDirection dir, double minDeltaPct)
    {
        var notesSink = new List<string>();
        var fromMap = KeyLookup(from, notesSink);
        var toMap = KeyLookup(to, notesSink);

        if ((fromMap.Count > 0 || toMap.Count > 0) && !fromMap.Keys.Intersect(toMap.Keys, StringComparer.Ordinal).Any())
        {
            return NoOverlap;
        }

        var improved = false;
        var regressed = false;
        foreach (var id in fromMap.Keys.Intersect(toMap.Keys, StringComparer.Ordinal))
        {
            var f = fromMap[id].Value;
            var t = toMap[id].Value;
            switch (Direction(dir, f, t, PercentDelta(f, t), minDeltaPct))
            {
                case Improved: improved = true; break;
                case Regressed: regressed = true; break;
                default: break;
            }
        }

        var added = toMap.Keys.Except(fromMap.Keys, StringComparer.Ordinal).Any();
        var removed = fromMap.Keys.Except(toMap.Keys, StringComparer.Ordinal).Any();
        if (dir == BetterDirection.Lower)
        {
            regressed |= added;
            improved |= removed;
        }
        else if (dir == BetterDirection.Higher)
        {
            improved |= added;
            regressed |= removed;
        }

        return Collapse(improved, regressed);
    }

    private static string Collapse(bool improved, bool regressed)
        => improved && regressed ? Mixed
            : regressed ? Regression
            : improved ? Improvement
            : NoChange;

    private static MetricRole HighestRole(IEnumerable<MetricValue> metrics)
    {
        var roles = metrics.Select(m => m.Definition.Role).ToArray();
        if (roles.Contains(MetricRole.Primary))
        {
            return MetricRole.Primary;
        }

        return roles.Contains(MetricRole.Secondary) ? MetricRole.Secondary : MetricRole.Context;
    }

    private static string DispersionVerdict(List<MetricSeries> series, IReadOnlyList<KeyMatrixRow> keyMatrix)
    {
        if (series.Count == 0 && keyMatrix.Count == 0)
        {
            return NoOverlap;
        }

        double maxCv;
        if (series.Count > 0)
        {
            var role = HighestRoleOf(series);
            maxCv = series
                .Where(s => s.Definition.Role == role && s.Dispersion is not null)
                .Select(s => s.Dispersion!.CoefficientOfVariation)
                .DefaultIfEmpty(0)
                .Max();
        }
        else
        {
            // Key-set kinds carry no scalar metrics; measure spread across each row's per-capture values.
            maxCv = keyMatrix
                .Select(r => Dispersion(r.Values.ToArray())?.CoefficientOfVariation ?? 0)
                .DefaultIfEmpty(0)
                .Max();
        }

        return maxCv > DispersionCvThreshold ? Dispersed : Uniform;
    }

    private static MetricRole HighestRoleOf(List<MetricSeries> series)
    {
        if (series.Any(s => s.Definition.Role == MetricRole.Primary))
        {
            return MetricRole.Primary;
        }

        return series.Any(s => s.Definition.Role == MetricRole.Secondary) ? MetricRole.Secondary : MetricRole.Context;
    }

    // ---- Numeric helpers --------------------------------------------------------------------

    private static string Direction(BetterDirection better, double from, double to, double deltaPct, double minDeltaPct)
    {
        if (better == BetterDirection.Neutral)
        {
            return NotApplicable;
        }

        if (Math.Abs(deltaPct) < minDeltaPct)
        {
            return Flat;
        }

        var delta = to - from;
        if (Math.Abs(delta) <= double.Epsilon)
        {
            return Flat;
        }

        var lowerIsBetter = better == BetterDirection.Lower;
        var wentDown = delta < 0;
        return wentDown == lowerIsBetter ? Improved : Regressed;
    }

    private static double PercentDelta(double baseline, double current)
    {
        if (baseline == 0)
        {
            return current == 0 ? 0 : 100;
        }

        return Math.Round(((current - baseline) / Math.Abs(baseline)) * 100, 2);
    }

    private static MetricTrend ClassifyTrend(double?[] values)
    {
        var v = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        if (v.Length < 2)
        {
            return MetricTrend.Insufficient;
        }

        var deltas = new double[v.Length - 1];
        for (var i = 1; i < v.Length; i++)
        {
            deltas[i - 1] = v[i] - v[i - 1];
        }

        var maxAbsValue = v.Select(Math.Abs).Max();
        var tol = Math.Max(1e-9, 1e-6 * maxAbsValue);
        var maxAbsDelta = deltas.Select(Math.Abs).Max();

        if (maxAbsDelta <= tol)
        {
            return MetricTrend.Flat;
        }

        // Sign changes over non-trivial deltas.
        var signChanges = 0;
        var lastSign = 0;
        foreach (var d in deltas)
        {
            if (Math.Abs(d) <= tol)
            {
                continue;
            }

            var sign = Math.Sign(d);
            if (lastSign != 0 && sign != lastSign)
            {
                signChanges++;
            }

            lastSign = sign;
        }

        if (signChanges >= 2)
        {
            return MetricTrend.Oscillating;
        }

        var lastDelta = Math.Abs(deltas[^1]);
        if (lastDelta <= Math.Max(tol, 0.1 * maxAbsDelta))
        {
            return MetricTrend.Converged;
        }

        var allNonNeg = deltas.All(d => d >= -tol);
        var allNonPos = deltas.All(d => d <= tol);
        if (allNonNeg)
        {
            return MetricTrend.MonotonicUp;
        }

        return allNonPos ? MetricTrend.MonotonicDown : MetricTrend.Converged;
    }

    private static DispersionStats? Dispersion(double?[] values)
    {
        var present = new List<(int Index, double Value)>();
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is double d)
            {
                present.Add((i, d));
            }
        }

        if (present.Count == 0)
        {
            return null;
        }

        var nums = present.Select(p => p.Value).ToArray();
        var min = nums.Min();
        var max = nums.Max();
        var mean = nums.Average();
        var median = Median(nums);
        var variance = nums.Select(x => (x - mean) * (x - mean)).Average();
        var stdDev = Math.Sqrt(variance);
        var cv = mean == 0 ? (stdDev == 0 ? 0 : double.PositiveInfinity) : stdDev / Math.Abs(mean);

        var outlierIndex = -1;
        var tol = Math.Max(1e-9, 1e-6 * Math.Max(Math.Abs(max), Math.Abs(min)));
        if (stdDev > tol)
        {
            var furthest = present.OrderByDescending(p => Math.Abs(p.Value - median)).First();
            if (Math.Abs(furthest.Value - median) > 2 * stdDev)
            {
                outlierIndex = furthest.Index;
            }
        }

        return new DispersionStats(
            Math.Round(min, 4), Math.Round(max, 4), Math.Round(median, 4), Math.Round(mean, 4),
            Math.Round(stdDev, 4), double.IsInfinity(cv) ? cv : Math.Round(cv, 4), outlierIndex);
    }

    private static double Median(double[] values)
    {
        var ordered = values.OrderBy(x => x).ToArray();
        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[mid - 1] + ordered[mid]) / 2
            : ordered[mid];
    }
}
