namespace DotnetDiagnostics.Core.Triage;

/// <summary>
/// Compact triage result that separates directly observed counter signals from inferred,
/// evidence-backed hypotheses.
/// </summary>
/// <param name="Verdict">Deprecated compatibility projection for pre-v2 consumers. Migrate to <see cref="Assessment"/> and <see cref="Hypotheses"/> before v1.0.</param>
/// <param name="Severity">Observed impact level: critical, degraded, or healthy. It does not name a cause.</param>
/// <param name="Evidence">Raw key counter values retained for compatibility and independent interpretation.</param>
/// <param name="SecondaryVerdicts">Deprecated compatibility projection for additional pre-v2 classifications.</param>
/// <param name="TopIndicators">Ranked list of most notable indicators (always populated, even when healthy). Enables proactive optimization.</param>
public sealed record TriageResult(
    string Verdict,
    TriageSeverity Severity,
    TriageEvidence Evidence,
    IReadOnlyList<string>? SecondaryVerdicts = null,
    IReadOnlyList<TriageIndicator>? TopIndicators = null)
{
    /// <summary>
    /// Triage contract version. Defaults to 1 for legacy construction/deserialization;
    /// current classifier output is version 2.
    /// </summary>
    public int ModelVersion { get; init; } = 1;

    /// <summary>Neutral overall assessment: healthy, inconclusive, degraded, critical, or unknown.</summary>
    public string Assessment { get; init; } = "unknown";

    /// <summary>Direct threshold crossings from the captured window. These are observations, not diagnoses.</summary>
    public IReadOnlyList<TriageObservedSignal>? ObservedSignals { get; init; }

    /// <summary>
    /// Evidence-backed interpretations ordered by confidence, then by the strongest supporting
    /// observed-signal level. Each requires drill-down before assigning a cause.
    /// </summary>
    public IReadOnlyList<TriageHypothesis>? Hypotheses { get; init; }

    /// <summary>
    /// Selects the most important observed signal for an inconclusive fallback hint. Signals are
    /// ranked by observed level, then by the matching top-indicator score; source order breaks ties.
    /// </summary>
    public TriageObservedSignal? GetHighestPriorityObservedSignal()
    {
        if (ObservedSignals is not { Count: > 0 })
        {
            return null;
        }

        return ObservedSignals
            .OrderByDescending(static signal => SignalLevelRank(signal.Level))
            .ThenByDescending(GetTopIndicatorScore)
            .First();
    }

    private int GetTopIndicatorScore(TriageObservedSignal signal)
    {
        var score = 0;
        foreach (var indicator in TopIndicators ?? [])
        {
            if (signal.Evidence.Any(evidence =>
                    string.Equals(evidence.Name, indicator.Name, StringComparison.Ordinal)))
            {
                score = Math.Max(score, indicator.Score);
            }
        }

        return score;
    }

    private static int SignalLevelRank(string level) => level switch
    {
        "critical" => 3,
        "high" => 2,
        "elevated" => 1,
        _ => 0,
    };
}

/// <summary>A directly observed signal whose threshold was crossed during the capture window.</summary>
/// <param name="Name">Stable diagnosis-agnostic signal name, such as <c>cpu.utilization</c>.</param>
/// <param name="Level">Observed level: elevated, high, or critical.</param>
/// <param name="Summary">Plain-language description of what was observed.</param>
/// <param name="Evidence">Explicit values, comparisons, and thresholds supporting the observation.</param>
public sealed record TriageObservedSignal(
    string Name,
    string Level,
    string Summary,
    IReadOnlyList<TriageEvidenceItem> Evidence);

/// <summary>An inferred interpretation of one or more observed signals.</summary>
/// <param name="Name">Stable, diagnosis-agnostic hypothesis name.</param>
/// <param name="Confidence">Evidence strength: moderate or high.</param>
/// <param name="Summary">Bounded interpretation that does not claim a root cause.</param>
/// <param name="SupportingEvidence">Indicators that caused the hypothesis to be emitted.</param>
/// <param name="ContradictingEvidence">Available indicators that weaken or bound the interpretation.</param>
/// <param name="NextStep">Neutral drill-down needed to confirm or reject the hypothesis.</param>
public sealed record TriageHypothesis(
    string Name,
    string Confidence,
    string Summary,
    IReadOnlyList<TriageEvidenceItem> SupportingEvidence,
    IReadOnlyList<TriageEvidenceItem> ContradictingEvidence,
    string NextStep);

/// <summary>Transparent threshold evidence used by an observed signal or hypothesis.</summary>
/// <param name="Name">Metric name.</param>
/// <param name="Value">Observed value.</param>
/// <param name="Unit">Metric unit.</param>
/// <param name="Comparison">Comparison applied to the threshold, such as <c>&gt;=</c> or <c>&lt;</c>.</param>
/// <param name="Threshold">Threshold used by the deterministic rule.</param>
/// <param name="Rationale">Why this comparison supports or contradicts the interpretation.</param>
public sealed record TriageEvidenceItem(
    string Name,
    double Value,
    string? Unit,
    string Comparison,
    double Threshold,
    string Rationale);

/// <summary>
/// A ranked indicator showing how notable a metric is relative to healthy baselines.
/// Always populated in triage results to enable proactive optimization, not just reactive firefighting.
/// </summary>
/// <param name="Name">Metric name (e.g., "time-in-gc", "cpu-usage", "threadpool-queue-length").</param>
/// <param name="Value">Current value of the metric.</param>
/// <param name="Unit">Unit of measurement (e.g., "%", "MB/s", "items").</param>
/// <param name="Score">Relative score (0-100) indicating how far from baseline. Higher = more notable.</param>
/// <param name="Level">Qualitative level: normal, elevated, high, critical.</param>
public sealed record TriageIndicator(
    string Name,
    double Value,
    string? Unit,
    int Score,
    string Level);

/// <summary>Severity levels for triage classification.</summary>
public enum TriageSeverity
{
    /// <summary>All metrics within normal bounds.</summary>
    Healthy,

    /// <summary>Some metrics elevated but not critical.</summary>
    Degraded,

    /// <summary>Metrics indicate significant performance impact.</summary>
    Critical
}

/// <summary>
/// Key counter evidence that drove the triage classification. Provides just enough context
/// for a consumer to interpret the observed window without a full counter dump.
/// </summary>
/// <param name="CpuUsage">CPU usage percentage (0-100).</param>
/// <param name="TimeInGc">Percentage of time spent in GC.</param>
/// <param name="ThreadPoolQueueLength">Number of work items queued for ThreadPool.</param>
/// <param name="MonitorLockContentionCount">Lock contentions per interval.</param>
/// <param name="AllocRate">Allocation rate in bytes/second.</param>
/// <param name="Gen2GcCount">Gen2 GC count in last interval.</param>
/// <param name="GcHeapSize">Current GC heap size in bytes.</param>
/// <param name="ExceptionCount">Exceptions per interval.</param>
/// <param name="RequestDurationP95">HTTP request duration p95 in seconds (null if not ASP.NET Core).</param>
public sealed record TriageEvidence(
    double? CpuUsage,
    double? TimeInGc,
    double? ThreadPoolQueueLength,
    double? MonitorLockContentionCount,
    double? AllocRate,
    double? Gen2GcCount,
    double? GcHeapSize,
    double? ExceptionCount,
    double? RequestDurationP95);
