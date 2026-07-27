using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Memory;

/// <summary>
/// Compares two <see cref="InvestigationSummary"/> instances and reports a structured diff
/// aware of symbol stability (module + methodFullName survives rebuilds) and provenance
/// changes (image jump, git sha change). Lets the LLM tell "regression" from "different deploy".
/// </summary>
public interface ISummaryComparer
{
    SummaryDiff Compare(InvestigationSummary baseline, InvestigationSummary current);
}

public sealed record SummaryDiff(
    string Verdict,
    ProvenanceDelta Provenance,
    IReadOnlyList<HotspotDelta> NewHotspots,
    IReadOnlyList<HotspotDelta> RemovedHotspots,
    IReadOnlyList<HotspotDelta> ChangedHotspots)
{
    public IReadOnlyList<KeyMetricDelta> KeyMetricDeltas { get; init; } = Array.Empty<KeyMetricDelta>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record ProvenanceDelta(
    bool ImageChanged,
    bool GitShaChanged,
    bool AssemblyVersionChanged,
    bool ContainerChanged,
    string Summary);

public sealed record HotspotDelta(
    SymbolRef Symbol,
    double? BaselineInclusivePercent,
    double? CurrentInclusivePercent,
    double? InclusiveDeltaPoints,
    SelfSampleBreakdown? BaselineSelfSamples = null,
    SelfSampleBreakdown? CurrentSelfSamples = null);

public sealed record KeyMetricDelta(
    string Name,
    double? BaselineValue,
    double? CurrentValue,
    string BetterDirection,
    string Outcome);

public sealed class SummaryComparer : ISummaryComparer
{
    private const double SignificantChangePoints = 2.0;
    private const string Improved = "improved";
    private const string Regressed = "regressed";
    private const string Unchanged = "unchanged";
    private const string Incomparable = "incomparable";

    private static readonly Dictionary<string, MetricDirection> MetricDirections =
        new Dictionary<string, MetricDirection>(StringComparer.Ordinal)
        {
            ["threadpoolqueuelength"] = MetricDirection.Lower,
            ["threadpoolpendingworkitems"] = MetricDirection.Lower,
            ["threadpoolthreadcount"] = MetricDirection.Lower,
            ["requestp95milliseconds"] = MetricDirection.Lower,
            ["requestp95seconds"] = MetricDirection.Lower,
            ["requestlatencyp95"] = MetricDirection.Lower,
            ["requestscompleted"] = MetricDirection.Higher,
            ["requestthroughput"] = MetricDirection.Higher,
            ["requestspersecond"] = MetricDirection.Higher,
            ["throughput"] = MetricDirection.Higher,
        };

    public SummaryDiff Compare(InvestigationSummary baseline, InvestigationSummary current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var provenance = CompareProvenance(baseline.Provenance, current.Provenance);

        var baselineMap = baseline.Findings.TopHotspots.ToDictionary(h => h.Symbol);
        var currentMap = current.Findings.TopHotspots.ToDictionary(h => h.Symbol);

        var added = currentMap.Values
            .Where(h => !baselineMap.ContainsKey(h.Symbol))
            .Select(h => new HotspotDelta(
                h.Symbol,
                null,
                h.InclusivePercent,
                h.InclusivePercent,
                CurrentSelfSamples: h.SelfSamples))
            .OrderByDescending(d => d.CurrentInclusivePercent ?? 0)
            .ToArray();

        var removed = baselineMap.Values
            .Where(h => !currentMap.ContainsKey(h.Symbol))
            .Select(h => new HotspotDelta(
                h.Symbol,
                h.InclusivePercent,
                null,
                -h.InclusivePercent,
                BaselineSelfSamples: h.SelfSamples))
            .OrderByDescending(d => d.BaselineInclusivePercent ?? 0)
            .ToArray();

        var changed = baselineMap
            .Where(kv => currentMap.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var b = kv.Value.InclusivePercent;
                var c = currentMap[kv.Key].InclusivePercent;
                return new HotspotDelta(
                    kv.Key,
                    b,
                    c,
                    Math.Round(c - b, 2),
                    kv.Value.SelfSamples,
                    currentMap[kv.Key].SelfSamples);
            })
            .Where(d => Math.Abs(d.InclusiveDeltaPoints!.Value) >= SignificantChangePoints)
            .OrderByDescending(d => Math.Abs(d.InclusiveDeltaPoints!.Value))
            .ToArray();

        var notes = new List<string>();
        var metricDeltas = CompareKeyMetrics(baseline.Findings.KeyMetrics, current.Findings.KeyMetrics, notes);
        var verdict = Verdict(provenance, added, removed, changed, metricDeltas);
        return new SummaryDiff(verdict, provenance, added, removed, changed)
        {
            KeyMetricDeltas = metricDeltas,
            Notes = notes,
        };
    }

    private static ProvenanceDelta CompareProvenance(InvestigationProvenance b, InvestigationProvenance c)
    {
        var imageChanged = !string.Equals(b.Container?.Image, c.Container?.Image, StringComparison.Ordinal);
        var gitShaChanged = !string.Equals(b.Build?.GitSha, c.Build?.GitSha, StringComparison.Ordinal);
        var asmVerChanged = !string.Equals(b.Build?.AssemblyVersion, c.Build?.AssemblyVersion, StringComparison.Ordinal);
        var containerChanged = imageChanged;

        var parts = new List<string>();
        if (imageChanged) parts.Add($"image {b.Container?.Image ?? "(none)"} → {c.Container?.Image ?? "(none)"}");
        if (gitShaChanged) parts.Add($"git {b.Build?.GitSha ?? "(none)"} → {c.Build?.GitSha ?? "(none)"}");
        if (asmVerChanged) parts.Add($"version {b.Build?.AssemblyVersion ?? "(none)"} → {c.Build?.AssemblyVersion ?? "(none)"}");
        var summary = parts.Count == 0 ? "Same build + container" : string.Join("; ", parts);

        return new ProvenanceDelta(imageChanged, gitShaChanged, asmVerChanged, containerChanged, summary);
    }

    private static string Verdict(
        ProvenanceDelta provenance,
        HotspotDelta[] added,
        HotspotDelta[] removed,
        HotspotDelta[] changed,
        IReadOnlyList<KeyMetricDelta> metricDeltas)
    {
        var metrics = MetricEvidence(metricDeltas);
        var removedWaiting = removed.Any(static delta => Activity(delta.BaselineSelfSamples) == SampleActivity.Waiting);
        var removedRunning = removed.Any(static delta => Activity(delta.BaselineSelfSamples) == SampleActivity.Running);
        var addedWaiting = added.Any(static delta => Activity(delta.CurrentSelfSamples) == SampleActivity.Waiting);
        var addedRunning = added.Any(static delta => Activity(delta.CurrentSelfSamples) == SampleActivity.Running);
        var turnoverHasUnknown = added.Any(static delta => Activity(delta.CurrentSelfSamples) == SampleActivity.Unknown)
            || removed.Any(static delta => Activity(delta.BaselineSelfSamples) == SampleActivity.Unknown);
        var hasHotspotChanges = added.Length > 0 || removed.Length > 0 || changed.Length > 0;

        if (!hasHotspotChanges)
        {
            if (metrics == Evidence.Improved) return "improvement";
            if (metrics == Evidence.Regressed) return "regression_metrics";
            if (metrics == Evidence.Mixed) return "mixed";
            if (metrics == Evidence.Incomparable) return Incomparable;

            return provenance.ImageChanged || provenance.GitShaChanged
                ? "no_regression_after_deploy"
                : "no_regression";
        }

        if (metrics == Evidence.Mixed)
        {
            return "mixed";
        }

        if (metrics == Evidence.Improved)
        {
            if (removedWaiting && !addedWaiting)
            {
                return "improvement";
            }

            var increasedHotspot = changed.Any(static delta => delta.InclusiveDeltaPoints > 0);
            return added.Length == 0 && !increasedHotspot ? "improvement" : "mixed";
        }

        if (metrics == Evidence.Regressed)
        {
            if (removedWaiting || removed.Length > 0)
            {
                return "mixed";
            }

            if (added.Length > 0) return "regression_new_hotspot";
            if (changed.Length > 0 && changed[0].InclusiveDeltaPoints > 0) return "regression_increased_hotspot";
            return "regression_metrics";
        }

        if (added.Length > 0 && removed.Length > 0)
        {
            if (removedWaiting && addedRunning && !addedWaiting)
            {
                return "improvement";
            }

            if (removedRunning && addedWaiting && !addedRunning)
            {
                return "regression_new_hotspot";
            }

            return turnoverHasUnknown || metrics == Evidence.Incomparable
                ? Incomparable
                : "mixed";
        }

        if (added.Length > 0) return "regression_new_hotspot";
        if (changed.Length > 0 && changed[0].InclusiveDeltaPoints > 0) return "regression_increased_hotspot";
        return "improvement";
    }

    private static KeyMetricDelta[] CompareKeyMetrics(
        IReadOnlyDictionary<string, double>? baseline,
        IReadOnlyDictionary<string, double>? current,
        List<string> notes)
    {
        if (baseline is null && current is null)
        {
            return Array.Empty<KeyMetricDelta>();
        }

        var baselineMetrics = CanonicalizeMetrics(baseline, "baseline", notes);
        var currentMetrics = CanonicalizeMetrics(current, "current", notes);
        var names = baselineMetrics.Keys
            .Concat(currentMetrics.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var deltas = new List<KeyMetricDelta>(names.Length);

        foreach (var canonicalName in names)
        {
            var hasBaseline = baselineMetrics.TryGetValue(canonicalName, out var baselineMetric);
            var hasCurrent = currentMetrics.TryGetValue(canonicalName, out var currentMetric);
            var name = hasBaseline ? baselineMetric!.Name : currentMetric!.Name;
            var directionName = NormalizeMetricName(
                InvestigationMetricIdentity.ComparableName(name));
            var hasDirection = MetricDirections.TryGetValue(directionName, out var direction);
            if (!hasBaseline || !hasCurrent)
            {
                deltas.Add(new KeyMetricDelta(
                    name,
                    hasBaseline ? baselineMetric!.Value : null,
                    hasCurrent ? currentMetric!.Value : null,
                    hasDirection ? DirectionName(direction) : "unknown",
                    Incomparable));
                notes.Add($"Key metric '{name}' is absent from one summary; it does not drive the verdict.");
                continue;
            }

            if (!hasDirection)
            {
                deltas.Add(new KeyMetricDelta(name, baselineMetric!.Value, currentMetric!.Value, "unknown", Incomparable));
                notes.Add($"Key metric '{name}' has no registered better-direction semantics; its delta is reported but does not drive the verdict.");
                continue;
            }

            var delta = currentMetric!.Value - baselineMetric!.Value;
            var outcome = Math.Abs(delta) <= double.Epsilon
                ? Unchanged
                : (delta < 0) == (direction == MetricDirection.Lower)
                    ? Improved
                    : Regressed;
            deltas.Add(new KeyMetricDelta(
                name,
                baselineMetric.Value,
                currentMetric.Value,
                DirectionName(direction),
                outcome));
        }

        return deltas.ToArray();
    }

    private static Evidence MetricEvidence(IReadOnlyList<KeyMetricDelta> deltas)
    {
        if (deltas.Count == 0)
        {
            return Evidence.None;
        }

        var improved = deltas.Any(static delta => delta.Outcome == Improved);
        var regressed = deltas.Any(static delta => delta.Outcome == Regressed);
        if (improved && regressed) return Evidence.Mixed;
        if (improved) return Evidence.Improved;
        if (regressed) return Evidence.Regressed;
        return deltas.Any(static delta => delta.Outcome == Incomparable)
            ? Evidence.Incomparable
            : Evidence.Unchanged;
    }

    private static Dictionary<string, CanonicalMetric> CanonicalizeMetrics(
        IReadOnlyDictionary<string, double>? metrics,
        string side,
        List<string> notes)
    {
        var result = new Dictionary<string, CanonicalMetric>(StringComparer.Ordinal);
        if (metrics is null)
        {
            return result;
        }

        foreach (var metric in metrics.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            var canonicalName = InvestigationMetricIdentity.IsCanonical(metric.Key)
                ? metric.Key
                : NormalizeMetricName(metric.Key);
            if (result.TryAdd(canonicalName, new CanonicalMetric(metric.Key, metric.Value)))
            {
                continue;
            }

            notes.Add(
                $"{side} key metrics '{result[canonicalName].Name}' and '{metric.Key}' normalize to '{canonicalName}'; " +
                $"keeping '{result[canonicalName].Name}' deterministically.");
        }

        return result;
    }

    private static string DirectionName(MetricDirection direction)
        => direction == MetricDirection.Lower ? "lower" : "higher";

    private static SampleActivity Activity(SelfSampleBreakdown? samples)
    {
        if (samples is null || samples.RunningSamples + samples.WaitingSamples == 0)
        {
            return SampleActivity.Unknown;
        }

        if (samples.WaitingSamples > samples.RunningSamples)
        {
            return SampleActivity.Waiting;
        }

        if (samples.RunningSamples > samples.WaitingSamples)
        {
            return SampleActivity.Running;
        }

        return SampleActivity.Mixed;
    }

    private static string NormalizeMetricName(string name)
        => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private enum MetricDirection
    {
        Lower,
        Higher,
    }

    private sealed record CanonicalMetric(string Name, double Value);

    private enum SampleActivity
    {
        Unknown,
        Running,
        Waiting,
        Mixed,
    }

    private enum Evidence
    {
        None,
        Unchanged,
        Improved,
        Regressed,
        Mixed,
        Incomparable,
    }
}
