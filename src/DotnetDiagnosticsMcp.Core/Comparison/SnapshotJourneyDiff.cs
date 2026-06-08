namespace DotnetDiagnosticsMcp.Core.Comparison;

/// <summary>
/// How an N-way comparison is interpreted. Ordered captures (one process over time) yield a
/// <see cref="Trend"/>; unordered captures (replicas/pods at one instant) yield
/// <see cref="Dispersion"/> (outlier detection), where "trend" is meaningless.
/// </summary>
public enum JourneyMode
{
    Trend,
    Dispersion,
}

/// <summary>Shape of an ordered metric series across captures.</summary>
public enum MetricTrend
{
    /// <summary>Fewer than two observed values — cannot classify.</summary>
    Insufficient,

    /// <summary>No meaningful movement across the series.</summary>
    Flat,

    /// <summary>Each step is non-negative with at least one increase.</summary>
    MonotonicUp,

    /// <summary>Each step is non-positive with at least one decrease.</summary>
    MonotonicDown,

    /// <summary>Moved early then settled — the final step is near zero after real movement.</summary>
    Converged,

    /// <summary>Direction flips repeatedly without settling.</summary>
    Oscillating,
}

/// <summary>Min/max/spread of a metric across an unordered (fleet) capture set.
/// <c>OutlierIndex</c> is the index of the capture furthest from the median, or -1 when none
/// stands out.</summary>
public sealed record DispersionStats(
    double Min,
    double Max,
    double Median,
    double Mean,
    double StdDev,
    double CoefficientOfVariation,
    int OutlierIndex);

/// <summary>
/// One metric tracked across every capture. <see cref="Values"/> is positional (index = capture
/// index); a null entry means the metric was absent from that capture. <see cref="Trend"/> is set
/// in <see cref="JourneyMode.Trend"/>; <see cref="Dispersion"/> in <see cref="JourneyMode.Dispersion"/>.
/// </summary>
public sealed record MetricSeries(
    MetricDefinition Definition,
    IReadOnlyList<double?> Values,
    double? DeltaAbs,
    double? DeltaPct,
    string Direction,
    MetricTrend Trend,
    DispersionStats? Dispersion);

/// <summary>One key-set row tracked across captures (primary metric per capture).</summary>
public sealed record KeyMatrixRow(
    ComparableKey Key,
    string DisplayName,
    IReadOnlyList<double?> Values,
    double? DeltaAbs,
    double? DeltaPct,
    string Direction);

/// <summary>A single first→last / first→each / adjacent comparison verdict.</summary>
public sealed record PairwiseComparison(
    string Relation,
    int FromIndex,
    int ToIndex,
    string Verdict);

/// <summary>The pairwise breakdown of a journey: a headline plus the supporting views.</summary>
public sealed record PairwiseJourney(
    PairwiseComparison Headline,
    IReadOnlyList<PairwiseComparison> BaselineEach,
    IReadOnlyList<PairwiseComparison> Adjacent);

/// <summary>
/// Result of comparing 2..N <see cref="ComparableSnapshot"/>s of the same kind. Carries the metric
/// series (scalar kinds), the key matrix (key-set kinds), the pairwise breakdown and an overall
/// verdict. For <see cref="JourneyMode.Trend"/> the verdict is one of
/// <c>improvement|regression|mixed|no_change|no_overlap|incomparable</c>; for
/// <see cref="JourneyMode.Dispersion"/> it is <c>uniform|dispersed|no_overlap|incomparable</c>.
/// </summary>
public sealed record SnapshotJourneyDiff(
    string Kind,
    JourneyMode Mode,
    IReadOnlyList<string> Labels,
    string Verdict,
    IReadOnlyList<MetricSeries> MetricSeries,
    IReadOnlyList<KeyMatrixRow> KeyMatrix,
    PairwiseJourney? Pairwise,
    IReadOnlyList<string> Notes);
