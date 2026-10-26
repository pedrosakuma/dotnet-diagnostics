using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Evidence;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.Comparison;

public sealed record CpuSampleComparisonReason(string Code, string Field, string Message);

public sealed record CpuSampleComparisonCompatibility(
    string Status,
    IReadOnlyList<CpuSampleComparisonReason> Reasons);

public sealed record CpuSampleComparisonCapture(
    string Handle,
    int ProcessId,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    long TotalSamples,
    CpuSampleBackend? Backend,
    CpuSampleEvidenceKind? EvidenceKind,
    HandleOrigin Origin,
    string? ProducingTool,
    DateTimeOffset ExpiresAt,
    long UnresolvedExclusiveSamples,
    double? UnresolvedExclusivePercent,
    EvidenceQuality Quality);

public sealed record CpuSampleObjectiveDelta(
    double? BaselineValue,
    double? CurrentValue,
    string? Unit,
    double? DeltaAbs,
    double? RelativeDelta,
    string BetterDirection,
    string Outcome,
    string? UnavailableReason)
{
    public double? BaselineDenominator { get; init; }
    public double? CurrentDenominator { get; init; }
    public string? DeltaUnit { get; init; }
    public string? RelativeDeltaUnavailableReason { get; init; }
}

public sealed record CpuSampleComparisonMetric(
    string Name,
    string Population,
    string Aggregation,
    string Normalization,
    CpuSampleObjectiveDelta Delta);

public sealed record CpuSampleTopMover(
    ComparableKey Key,
    string DisplayName,
    long? BaselineExclusiveSamples,
    long? CurrentExclusiveSamples,
    long BaselineTotalSamples,
    long CurrentTotalSamples,
    CpuSampleObjectiveDelta ExclusiveShare,
    bool Unresolved);

public sealed record CpuSampleHandleComparisonResult(
    string Schema,
    CpuSampleComparisonCapture Baseline,
    CpuSampleComparisonCapture Current,
    CpuSampleComparisonCompatibility Compatibility,
    IReadOnlyList<CpuSampleComparisonMetric> Metrics,
    IReadOnlyList<CpuSampleTopMover> TopMovers,
    IReadOnlyList<string> Notes)
{
    public const string SchemaV1 = "dotnet-diagnostics/cpu-sample-handle-comparison/v1";

    [JsonPropertyOrder(-20)]
    public InvestigationEvidenceBoundary UntrustedDataBoundary { get; init; } =
        InvestigationEvidenceBoundary.UntrustedCpuHandleComparisonData;
}

/// <summary>
/// Builds a bounded, objective comparison of two live CPU-sample handles. It reuses the canonical
/// CPU comparable projection for identity aggregation, quality and sample-share semantics, but
/// deliberately emits no performance verdict.
/// </summary>
public static class CpuSampleHandleComparison
{
    public const int MaxTopMovers = 50;

    private const string Qualified = "qualified";
    private const string Incompatible = "incompatible";
    private const string Neutral = "neutral";
    private const string Unavailable = "unavailable";

    public static DiagnosticResult<CpuSampleHandleComparisonResult> Compare(
        DiagnosticHandle baselineHandle,
        CpuSampleTraceArtifact baseline,
        DiagnosticHandle currentHandle,
        CpuSampleTraceArtifact current,
        int topN = 25)
    {
        ArgumentNullException.ThrowIfNull(baselineHandle);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(currentHandle);
        ArgumentNullException.ThrowIfNull(current);

        if (topN is < 1 or > MaxTopMovers)
        {
            return Invalid(nameof(topN), $"must be between 1 and {MaxTopMovers}");
        }

        if (!string.Equals(baselineHandle.Kind, "cpu-sample", StringComparison.Ordinal)
            || !string.Equals(currentHandle.Kind, "cpu-sample", StringComparison.Ordinal))
        {
            return Invalid(
                "baselineHandle",
                $"requires two 'cpu-sample' handles (got '{baselineHandle.Kind}' and '{currentHandle.Kind}')");
        }

        if (baselineHandle.Origin != HandleOrigin.Live || currentHandle.Origin != HandleOrigin.Live)
        {
            return Invalid(
                "baselineHandle",
                $"requires two live CPU handles (got '{baselineHandle.Origin}' and '{currentHandle.Origin}')");
        }

        if (baselineHandle.ProcessId != baseline.ProcessId
            || currentHandle.ProcessId != current.ProcessId)
        {
            return Invalid(
                "handle",
                "metadata process provenance does not match the retained CPU artifact");
        }

        if (baseline.ProcessId != current.ProcessId)
        {
            return Invalid(
                "baselineHandle",
                $"requires captures from the same process (baseline pid {baseline.ProcessId}, current pid {current.ProcessId})");
        }

        if (current.StartedAt < baseline.StartedAt)
        {
            return Invalid(
                "baselineHandle",
                $"must identify the earlier capture (baseline {baseline.StartedAt:O}, current {current.StartedAt:O})");
        }

        var baselineSnapshot = CpuSampleComparableProjection.Project(baseline, baselineHandle.Id, "cpu-sample");
        var currentSnapshot = CpuSampleComparableProjection.Project(current, currentHandle.Id, "cpu-sample");
        var baselineRows = Rows(baseline);
        var currentRows = Rows(current);
        var compatibilityReasons = CompatibilityReasons(baseline.Evidence, current.Evidence);
        var compatibility = new CpuSampleComparisonCompatibility(
            compatibilityReasons.Count == 0 ? Qualified : Incompatible,
            compatibilityReasons);
        var incompatibilityCode = compatibilityReasons.Count > 0
            ? compatibilityReasons[0].Code
            : null;

        var baselineUnresolved = UnresolvedSamples(baselineRows.Values);
        var currentUnresolved = UnresolvedSamples(currentRows.Values);
        var baselineUnresolvedPercent = Percent(baselineUnresolved, baseline.TotalSamples);
        var currentUnresolvedPercent = Percent(currentUnresolved, current.TotalSamples);

        var metrics = new[]
        {
            new CpuSampleComparisonMetric(
                "samplePopulation",
                "retained-samples",
                "total",
                "none",
                Delta(
                    baseline.TotalSamples,
                    current.TotalSamples,
                    "samples",
                    "samples",
                    incompatibilityCode)),
            new CpuSampleComparisonMetric(
                "duration",
                "capture-window",
                "duration",
                "none",
                Delta(
                    Math.Round(baseline.Duration.TotalSeconds, 4),
                    Math.Round(current.Duration.TotalSeconds, 4),
                    "s",
                    "s",
                    unavailableReason: null)),
            new CpuSampleComparisonMetric(
                "unresolvedExclusiveSamples",
                "retained-exclusive-samples",
                "total",
                "none",
                Delta(
                    baselineUnresolved,
                    currentUnresolved,
                    "samples",
                    "samples",
                    incompatibilityCode)),
            new CpuSampleComparisonMetric(
                "unresolvedExclusivePercent",
                "retained-exclusive-samples",
                "percent",
                "retained-total-samples",
                Delta(
                    baselineUnresolvedPercent,
                    currentUnresolvedPercent,
                    "%",
                    "percentage-points",
                    incompatibilityCode ?? DenominatorReason(baseline.TotalSamples, current.TotalSamples),
                    baseline.TotalSamples,
                    current.TotalSamples)),
        };

        var ids = baselineRows.Keys
            .Concat(currentRows.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var movers = ids
            .Select(id => BuildMover(
                baselineRows.GetValueOrDefault(id),
                currentRows.GetValueOrDefault(id),
                baseline.TotalSamples,
                current.TotalSamples,
                incompatibilityCode))
            .OrderBy(static mover => mover.ExclusiveShare.DeltaAbs is null)
            .ThenByDescending(static mover => Math.Abs(mover.ExclusiveShare.DeltaAbs ?? 0))
            .ThenByDescending(static mover => Math.Max(
                mover.ExclusiveShare.BaselineValue ?? 0,
                mover.ExclusiveShare.CurrentValue ?? 0))
            .ThenBy(static mover => mover.DisplayName, StringComparer.Ordinal)
            .Take(topN)
            .ToArray();

        var notes = new List<string>
        {
            "Workload equivalence, offered work, throughput, latency, causation and statistical repeatability are not established; retained deltas are observations, not performance verdicts.",
            "Method shares are fractions of each capture's retained sample population. They are not elapsed CPU time or work done.",
            "A method missing from one retained call-tree projection remains null/unavailable; absence is never coerced to zero.",
        };
        if (baseline.Duration != current.Duration)
        {
            notes.Add(
                $"Capture durations differ ({baseline.Duration.TotalSeconds:F3}s and {current.Duration.TotalSeconds:F3}s); sample shares use each capture's own retained-sample denominator.");
        }
        notes.AddRange(compatibilityReasons.Select(static reason => reason.Message));

        var result = new CpuSampleHandleComparisonResult(
            CpuSampleHandleComparisonResult.SchemaV1,
            Capture(baselineHandle, baseline, baselineSnapshot.Quality, baselineUnresolved, baselineUnresolvedPercent),
            Capture(currentHandle, current, currentSnapshot.Quality, currentUnresolved, currentUnresolvedPercent),
            compatibility,
            metrics,
            movers,
            notes);
        var summary = compatibility.Status == Qualified
            ? $"Compared CPU sample handles '{currentHandle.Id}' and baseline '{baselineHandle.Id}' with {movers.Length} bounded objective mover row(s); no performance verdict was produced."
            : $"CPU sample handles '{currentHandle.Id}' and baseline '{baselineHandle.Id}' are structurally incompatible; observations were retained and comparison deltas are unavailable.";
        return DiagnosticResult.Ok(result, summary);
    }

    private static CpuSampleComparisonCapture Capture(
        DiagnosticHandle handle,
        CpuSampleTraceArtifact artifact,
        EvidenceQuality? quality,
        long unresolvedSamples,
        double? unresolvedPercent)
        => new(
            handle.Id,
            artifact.ProcessId,
            artifact.StartedAt,
            Math.Round(artifact.Duration.TotalSeconds, 4),
            artifact.TotalSamples,
            artifact.Evidence?.Backend,
            artifact.Evidence?.Kind,
            handle.Origin,
            handle.ProducingTool,
            handle.ExpiresAt,
            unresolvedSamples,
            unresolvedPercent,
            quality ?? EvidenceQuality.LegacyUnknown);

    private static List<CpuSampleComparisonReason> CompatibilityReasons(
        CpuSampleEvidence? baseline,
        CpuSampleEvidence? current)
    {
        var reasons = new List<CpuSampleComparisonReason>();
        if (!KnownEvidence(baseline) || !KnownEvidence(current))
        {
            reasons.Add(new(
                "CpuEvidenceUnavailable",
                "evidence",
                "Both captures require explicit, recognized CPU evidence metadata; legacy or unknown evidence is not coerced."));
            return reasons;
        }

        if (baseline!.Backend != current!.Backend)
        {
            reasons.Add(new(
                "CpuBackendMismatch",
                "backend",
                $"CPU backends differ ({baseline.Backend} and {current.Backend}); no cross-backend method delta is computed."));
        }

        if (baseline.Kind != current.Kind)
        {
            reasons.Add(new(
                "CpuEvidenceKindMismatch",
                "evidenceKind",
                $"CPU evidence semantics differ ({baseline.Kind} and {current.Kind}); no cross-semantics method delta is computed."));
        }

        return reasons;
    }

    private static bool KnownEvidence(CpuSampleEvidence? evidence)
        => evidence?.Schema == CpuSampleEvidence.SchemaV1
            && evidence.Backend != CpuSampleBackend.LegacyUnknown
            && evidence.Kind != CpuSampleEvidenceKind.LegacyUnknown;

    private static Dictionary<string, RowObservation> Rows(CpuSampleTraceArtifact artifact)
    {
        var rows = new Dictionary<string, RowObservation>(StringComparer.Ordinal);
        foreach (var row in CpuSampleComparableProjection.ProjectTyped(artifact, "cpu-sample"))
        {
            var key = ComparableKeyFactory.ForMethod("cpu-sample", row.Key.Symbol, row.Key.Identity);
            rows.TryAdd(
                key.ExactId ?? key.StableId,
                new RowObservation(
                    key,
                    row.Key.Symbol.MethodFullName,
                    row.Value.ExclusiveSamples,
                    CpuSampleComparableProjection.IsUnresolved(row.Key.Symbol.MethodFullName)));
        }

        return rows;
    }

    private static CpuSampleTopMover BuildMover(
        RowObservation? baseline,
        RowObservation? current,
        long baselineTotal,
        long currentTotal,
        string? incompatibilityCode)
    {
        var unavailableReason = incompatibilityCode
            ?? (baseline is null ? "MissingBaselineObservation"
                : current is null ? "MissingCurrentObservation"
                : DenominatorReason(baselineTotal, currentTotal));
        var baselinePercent = baseline is null
            ? null
            : Percent(baseline.ExclusiveSamples, baselineTotal);
        var currentPercent = current is null
            ? null
            : Percent(current.ExclusiveSamples, currentTotal);
        var delta = Delta(
            baselinePercent,
            currentPercent,
            "%",
            "percentage-points",
            unavailableReason,
            baselineTotal,
            currentTotal);
        var selected = current ?? baseline!;
        return new CpuSampleTopMover(
            selected.Key,
            selected.DisplayName,
            baseline?.ExclusiveSamples,
            current?.ExclusiveSamples,
            baselineTotal,
            currentTotal,
            delta,
            selected.Unresolved);
    }

    private static CpuSampleObjectiveDelta Delta(
        double? baseline,
        double? current,
        string? unit,
        string? deltaUnit,
        string? unavailableReason,
        double? baselineDenominator = null,
        double? currentDenominator = null)
    {
        double? absolute = null;
        double? relative = null;
        string? relativeUnavailable = null;
        var outcome = Unavailable;
        if (unavailableReason is null && baseline is { } baselineValue && current is { } currentValue)
        {
            absolute = Math.Round(currentValue - baselineValue, 4);
            outcome = absolute == 0 ? "flat" : absolute > 0 ? "up" : "down";
            if (baselineValue == 0)
            {
                relativeUnavailable = "ZeroBaseline";
            }
            else
            {
                relative = Math.Round(absolute.Value / Math.Abs(baselineValue), 4);
            }
        }

        return new CpuSampleObjectiveDelta(
            baseline,
            current,
            unit,
            absolute,
            relative,
            Neutral,
            outcome,
            unavailableReason)
        {
            BaselineDenominator = baselineDenominator,
            CurrentDenominator = currentDenominator,
            DeltaUnit = deltaUnit,
            RelativeDeltaUnavailableReason = relativeUnavailable,
        };
    }

    private static string? DenominatorReason(long baselineTotal, long currentTotal)
        => baselineTotal <= 0 || currentTotal <= 0 ? "SampleDenominatorUnavailable" : null;

    private static double? Percent(long samples, long total)
        => total <= 0 ? null : Math.Round(100.0 * samples / total, 4);

    private static long UnresolvedSamples(IEnumerable<RowObservation> rows)
        => rows.Where(static row => row.Unresolved).Sum(static row => row.ExclusiveSamples);

    private static DiagnosticResult<CpuSampleHandleComparisonResult> Invalid(string parameterName, string requirement)
        => DiagnosticResult.Fail<CpuSampleHandleComparisonResult>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError(
                "InvalidArgument",
                $"Argument '{parameterName}' {requirement}.",
                parameterName));

    private sealed record RowObservation(
        ComparableKey Key,
        string DisplayName,
        long ExclusiveSamples,
        bool Unresolved);
}
