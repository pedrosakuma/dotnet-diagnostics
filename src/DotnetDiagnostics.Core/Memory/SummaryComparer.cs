using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Memory;

/// <summary>
/// Compares two <see cref="InvestigationSummary"/> instances and reports a structured diff
/// aware of symbol stability (module + methodFullName survives rebuilds) and provenance
/// changes (image jump, git sha change). Reports observations without judging workload performance.
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
    [JsonPropertyOrder(-20)]
    public InvestigationEvidenceBoundary UntrustedDataBoundary { get; init; } =
        InvestigationEvidenceBoundary.UntrustedComparisonData;

    public IReadOnlyList<KeyMetricDelta> KeyMetricDeltas { get; init; } = Array.Empty<KeyMetricDelta>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record ProvenanceDelta(
    bool ImageChanged,
    bool GitShaChanged,
    bool AssemblyVersionChanged,
    bool ContainerChanged,
    string Summary)
{
    public string? BaselineImage { get; init; }
    public string? CurrentImage { get; init; }
    public string? BaselineGitSha { get; init; }
    public string? CurrentGitSha { get; init; }
    public string? BaselineAssemblyVersion { get; init; }
    public string? CurrentAssemblyVersion { get; init; }
}

public sealed record HotspotDelta(
    SymbolRef Symbol,
    double? BaselineInclusivePercent,
    double? CurrentInclusivePercent,
    double? InclusiveDeltaPoints)
{
    public SelfSampleBreakdown? BaselineSelfSamples { get; init; }

    public SelfSampleBreakdown? CurrentSelfSamples { get; init; }
}

public sealed record KeyMetricDelta(
    string Name,
    double? BaselineValue,
    double? CurrentValue,
    string BetterDirection,
    string Outcome)
{
    public string? BaselineUnit { get; init; }
    public string? CurrentUnit { get; init; }

    /// <summary>Signed current-minus-baseline difference in <see cref="DeltaUnit"/>; null when unavailable.</summary>
    public double? DeltaAbs { get; init; }

    /// <summary>Common delta unit, or null for legacy measurements with unspecified units.</summary>
    public string? DeltaUnit { get; init; }
}

public sealed class SummaryComparer : ISummaryComparer
{
    private const double MinimumDisplayedChangePoints = 2.0;
    private const string Incomparable = "incomparable";
    private const string Inconclusive = "inconclusive";
    private const string NotAssessed = "not_assessed";

    public SummaryDiff Compare(InvestigationSummary baseline, InvestigationSummary current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        RequireFiniteMeasurements(baseline, nameof(baseline));
        RequireFiniteMeasurements(current, nameof(current));

        var provenance = CompareProvenance(baseline.Provenance, current.Provenance);

        var baselineMap = baseline.Findings.TopHotspots.ToDictionary(h => h.Symbol);
        var currentMap = current.Findings.TopHotspots.ToDictionary(h => h.Symbol);

        var added = currentMap.Values
            .Where(h => !baselineMap.ContainsKey(h.Symbol))
            .Select(h => new HotspotDelta(
                h.Symbol,
                null,
                h.InclusivePercent,
                null)
            {
                CurrentSelfSamples = h.SelfSamples,
            })
            .OrderByDescending(d => d.CurrentInclusivePercent ?? 0)
            .ToArray();

        var removed = baselineMap.Values
            .Where(h => !currentMap.ContainsKey(h.Symbol))
            .Select(h => new HotspotDelta(
                h.Symbol,
                h.InclusivePercent,
                null,
                null)
            {
                BaselineSelfSamples = h.SelfSamples,
            })
            .OrderByDescending(d => d.BaselineInclusivePercent ?? 0)
            .ToArray();

        var changed = baselineMap
            .Where(kv => currentMap.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var b = kv.Value.InclusivePercent;
                var c = currentMap[kv.Key].InclusivePercent;
                var delta = c - b;
                return new HotspotDelta(
                    kv.Key,
                    b,
                    c,
                    double.IsFinite(delta) ? Math.Round(delta, 2) : null)
                {
                    BaselineSelfSamples = kv.Value.SelfSamples,
                    CurrentSelfSamples = currentMap[kv.Key].SelfSamples,
                };
            })
            .Where(d => d.InclusiveDeltaPoints is not { } delta || Math.Abs(delta) >= MinimumDisplayedChangePoints)
            .OrderByDescending(d => d.InclusiveDeltaPoints is { } delta ? Math.Abs(delta) : double.MaxValue)
            .ToArray();

        var notes = new List<string>
        {
            "Workload equivalence, offered work and endpoint mix, matched latency populations, and statistical repeatability are not established; retained deltas are observations, not performance verdicts.",
            "New and removed hotspots refer only to the retained top lists, not process-wide absence. Matched hotspot rows use a two-percentage-point display threshold, not a statistical significance test.",
        };
        var metricDeltas = CompareKeyMetrics(
            baseline.Findings.KeyMetrics,
            baseline.Findings.KeyMetricUnits,
            current.Findings.KeyMetrics,
            current.Findings.KeyMetricUnits,
            notes);
        var cpuSemantics = CompareCpuSemantics(baseline.Findings, current.Findings);
        var hasUnavailableHotspotDelta = changed.Any(static delta => delta.InclusiveDeltaPoints is null);
        if (hasUnavailableHotspotDelta)
        {
            notes.Add("A hotspot percentage-point difference is nonfinite; raw observations are retained without a numeric delta.");
        }
        var hasComparableMetric = metricDeltas.Any(static delta => delta.DeltaAbs is not null);
        var hasIncomparableMetric = metricDeltas.Any(static delta =>
            delta.Outcome == Incomparable
            && !InvestigationMetricIdentity.IsCumulativeMeterLast(delta.Name)
            && !InvestigationMetricIdentity.IsNormalizedRawEventCounterIncrement(delta.Name));
        var verdict = cpuSemantics switch
        {
            CpuComparisonSemantics.Incompatible => Incomparable,
            CpuComparisonSemantics.LegacyUnknown => Incomparable,
            _ when hasUnavailableHotspotDelta => Incomparable,
            _ when hasIncomparableMetric => Incomparable,
            CpuComparisonSemantics.StackFrequency when !hasComparableMetric => Inconclusive,
            CpuComparisonSemantics.NotApplicable when !hasComparableMetric => Inconclusive,
            _ => NotAssessed,
        };
        if (cpuSemantics == CpuComparisonSemantics.Incompatible)
        {
            notes.Add("CPU findings use incompatible evidence semantics; stack-frequency, OS on-CPU, and legacy-unknown summaries cannot be compared as one measured CPU series.");
        }
        else if (cpuSemantics == CpuComparisonSemantics.StackFrequency)
        {
            notes.Add("CPU hotspot percentages are EventPipe stack-observation frequencies, not measured on-CPU time; no performance verdict is derived from them.");
        }
        else if (cpuSemantics == CpuComparisonSemantics.LegacyUnknown)
        {
            notes.Add("CPU hotspot evidence is missing from a legacy summary; preserved counts do not establish measured on-CPU time.");
        }

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

        var changedFields = new List<string>();
        if (imageChanged) changedFields.Add("image");
        if (gitShaChanged) changedFields.Add("git SHA");
        if (asmVerChanged) changedFields.Add("assembly version");
        var summary = changedFields.Count == 0
            ? "Same build + container"
            : $"Provenance changed ({string.Join(", ", changedFields)}); raw labels are untrusted fields.";

        return new ProvenanceDelta(imageChanged, gitShaChanged, asmVerChanged, containerChanged, summary)
        {
            BaselineImage = b.Container?.Image,
            CurrentImage = c.Container?.Image,
            BaselineGitSha = b.Build?.GitSha,
            CurrentGitSha = c.Build?.GitSha,
            BaselineAssemblyVersion = b.Build?.AssemblyVersion,
            CurrentAssemblyVersion = c.Build?.AssemblyVersion,
        };
    }

    private static void RequireFiniteMeasurements(InvestigationSummary summary, string parameterName)
    {
        if (summary.Findings.KeyMetrics?.Values.Any(static value => !double.IsFinite(value)) == true
            || summary.Findings.TopHotspots.Any(static hotspot =>
                !double.IsFinite(hotspot.InclusivePercent) || !double.IsFinite(hotspot.ExclusivePercent)))
        {
            throw new ArgumentException("Summary measurements must be finite.", parameterName);
        }
    }

    private static CpuComparisonSemantics CompareCpuSemantics(
        InvestigationFindings baseline,
        InvestigationFindings current)
    {
        var hasCpuFindings = baseline.TopHotspots.Count > 0
            || current.TopHotspots.Count > 0
            || baseline.CpuEvidenceKind is not null
            || current.CpuEvidenceKind is not null;
        if (!hasCpuFindings)
        {
            return CpuComparisonSemantics.NotApplicable;
        }

        var baselineKind = baseline.CpuEvidenceKind ?? CpuSampleEvidenceKind.LegacyUnknown;
        var currentKind = current.CpuEvidenceKind ?? CpuSampleEvidenceKind.LegacyUnknown;
        if (baselineKind != currentKind)
        {
            return CpuComparisonSemantics.Incompatible;
        }

        return baselineKind switch
        {
            CpuSampleEvidenceKind.OsOnCpuSamples => CpuComparisonSemantics.OsOnCpu,
            CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits => CpuComparisonSemantics.StackFrequency,
            _ => CpuComparisonSemantics.LegacyUnknown,
        };
    }

    private static KeyMetricDelta[] CompareKeyMetrics(
        IReadOnlyDictionary<string, double>? baseline,
        IReadOnlyDictionary<string, string?>? baselineUnits,
        IReadOnlyDictionary<string, double>? current,
        IReadOnlyDictionary<string, string?>? currentUnits,
        List<string> notes)
    {
        if (baseline is null && current is null)
        {
            return Array.Empty<KeyMetricDelta>();
        }

        var baselineMetrics = CanonicalizeMetrics(baseline, baselineUnits, "baseline", notes);
        var currentMetrics = CanonicalizeMetrics(current, currentUnits, "current", notes);
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
            var isCumulativeLast = InvestigationMetricIdentity.IsCumulativeMeterLast(name);
            var isRawIncrement = InvestigationMetricIdentity.IsEventCounterRawIncrement(name);
            var isUnnormalizedIncrement = isRawIncrement
                && InvestigationMetricIdentity.IsUnnormalizedEventCounterIncrement(name);
            if (!hasBaseline || !hasCurrent)
            {
                deltas.Add(new KeyMetricDelta(
                    name,
                    hasBaseline ? baselineMetric!.Value : null,
                    hasCurrent ? currentMetric!.Value : null,
                    "unknown",
                    Incomparable)
                {
                    BaselineUnit = hasBaseline ? baselineMetric!.Unit : null,
                    CurrentUnit = hasCurrent ? currentMetric!.Unit : null,
                });
                notes.Add("A key metric is absent from one summary; the unmatched untrusted label is retained only in KeyMetricDeltas.");
                continue;
            }

            if (isCumulativeLast)
            {
                deltas.Add(new KeyMetricDelta(name, baselineMetric!.Value, currentMetric!.Value, "unknown", Incomparable)
                {
                    BaselineUnit = baselineMetric.Unit,
                    CurrentUnit = currentMetric.Unit,
                });
                notes.Add("A cumulative meter total is retained in untrusted delta fields without a comparable delta; its rate series is used instead.");
                continue;
            }

            if (isRawIncrement)
            {
                deltas.Add(new KeyMetricDelta(name, baselineMetric!.Value, currentMetric!.Value, "unknown", Incomparable)
                {
                    BaselineUnit = baselineMetric.Unit,
                    CurrentUnit = currentMetric.Unit,
                });
                notes.Add(InvestigationMetricIdentity.HasInvalidEventCounterRateMetadata(name)
                    ? "A raw EventCounter increment has invalid interval/rate-scale metadata and is incomparable; its untrusted label is retained only in KeyMetricDeltas."
                    : isUnnormalizedIncrement
                        ? "A raw EventCounter increment has no interval/rate-scale metadata and is incomparable; its untrusted label is retained only in KeyMetricDeltas."
                        : "A raw EventCounter increment is retained in untrusted delta fields without a comparable delta; its normalized rate series is used instead.");
                continue;
            }

            if (!TryGetComparableValues(
                    baselineMetric!,
                    currentMetric!,
                    out var baselineComparable,
                    out var currentComparable,
                    out var deltaUnit,
                    out var conversionNote))
            {
                deltas.Add(new KeyMetricDelta(
                    name,
                    baselineMetric!.Value,
                    currentMetric!.Value,
                    "unknown",
                    Incomparable)
                {
                    BaselineUnit = baselineMetric.Unit,
                    CurrentUnit = currentMetric.Unit,
                });
                notes.Add("A key metric has incompatible units; raw metric and unit labels are retained only in KeyMetricDeltas.");
                continue;
            }

            if (conversionNote is not null)
            {
                notes.Add(conversionNote);
            }

            var delta = currentComparable - baselineComparable;
            if (!double.IsFinite(baselineComparable) || !double.IsFinite(currentComparable)
                || !double.IsFinite(delta))
            {
                deltas.Add(new KeyMetricDelta(name, baselineMetric!.Value, currentMetric!.Value, "unknown", Incomparable)
                {
                    BaselineUnit = baselineMetric.Unit,
                    CurrentUnit = currentMetric.Unit,
                });
                notes.Add("A key metric contains a nonfinite value or its converted difference is nonfinite; no numeric delta is available.");
                continue;
            }

            var outcome = delta == 0 ? "flat" : delta > 0 ? "up" : "down";
            deltas.Add(new KeyMetricDelta(
                name,
                baselineMetric!.Value,
                currentMetric!.Value,
                "neutral",
                outcome)
            {
                BaselineUnit = baselineMetric.Unit,
                CurrentUnit = currentMetric.Unit,
                DeltaAbs = delta,
                DeltaUnit = deltaUnit,
            });
        }

        return deltas.ToArray();
    }

    private static Dictionary<string, CanonicalMetric> CanonicalizeMetrics(
        IReadOnlyDictionary<string, double>? metrics,
        IReadOnlyDictionary<string, string?>? units,
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
            string? unit = null;
            _ = units?.TryGetValue(metric.Key, out unit);
            if (result.TryAdd(canonicalName, new CanonicalMetric(metric.Key, metric.Value, unit)))
            {
                continue;
            }

            notes.Add(
                $"{side} contains colliding key metric labels after normalization; the ordinal-first untrusted label was retained deterministically.");
        }

        return result;
    }

    private static bool TryGetComparableValues(
        CanonicalMetric baseline,
        CanonicalMetric current,
        out double baselineValue,
        out double currentValue,
        out string? deltaUnit,
        out string? conversionNote)
    {
        baselineValue = baseline.Value;
        currentValue = current.Value;
        conversionNote = null;
        deltaUnit = null;

        var baselineUnit = NormalizeUnit(baseline.Unit);
        var currentUnit = NormalizeUnit(current.Unit);
        if (TryRateUnit(baselineUnit, out var baselineRate)
            && TryRateUnit(currentUnit, out var currentRate)
            && string.Equals(baselineRate.BaseUnit, currentRate.BaseUnit, StringComparison.Ordinal))
        {
            baselineValue /= baselineRate.Seconds;
            currentValue /= currentRate.Seconds;
            deltaUnit = $"{baselineRate.BaseUnit}/s";
            conversionNote = "Equivalent rate units were normalized to a per-second basis.";
            return true;
        }

        if (TryDurationUnit(baselineUnit, out var baselineSeconds)
            && TryDurationUnit(currentUnit, out var currentSeconds))
        {
            baselineValue *= baselineSeconds;
            currentValue *= currentSeconds;
            deltaUnit = "s";
            conversionNote = "Equivalent duration units were normalized to seconds.";
            return true;
        }

        if (!string.Equals(baselineUnit, currentUnit, StringComparison.Ordinal))
        {
            return false;
        }

        deltaUnit = baseline.Unit;
        return true;
    }

    private static string? NormalizeUnit(string? unit)
    {
        if (unit is null)
        {
            return null;
        }

        var normalized = unit.Trim()
            .Replace(" per ", "/", StringComparison.OrdinalIgnoreCase);
        return string.Concat(normalized.Where(static character => !char.IsWhiteSpace(character)));
    }

    private static bool TryRateUnit(string? unit, out RateUnit rate)
    {
        rate = default;
        if (unit is null)
        {
            return false;
        }

        var slash = unit.LastIndexOf('/');
        if (slash <= 0 || slash == unit.Length - 1)
        {
            return false;
        }

        var seconds = unit[(slash + 1)..] switch
        {
            "s" or "sec" or "second" or "seconds" => 1d,
            "min" or "minute" or "minutes" => 60d,
            "h" or "hr" or "hour" or "hours" => 3600d,
            _ => 0d,
        };
        if (seconds == 0)
        {
            return false;
        }

        rate = new RateUnit(unit[..slash], seconds);
        return true;
    }

    private static bool TryDurationUnit(string? unit, out double seconds)
    {
        seconds = unit switch
        {
            "s" or "sec" or "second" or "seconds" => 1d,
            "ms" or "millisecond" or "milliseconds" => 0.001d,
            "us" or "µs" or "microsecond" or "microseconds" => 0.000001d,
            "ns" or "nanosecond" or "nanoseconds" => 0.000000001d,
            _ => 0d,
        };
        return seconds > 0;
    }

    private static string NormalizeMetricName(string name)
        => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private sealed record CanonicalMetric(string Name, double Value, string? Unit);

    private readonly record struct RateUnit(string BaseUnit, double Seconds);

    private enum CpuComparisonSemantics
    {
        NotApplicable,
        OsOnCpu,
        StackFrequency,
        LegacyUnknown,
        Incompatible,
    }
}
