namespace DotnetDiagnostics.Core.Comparison;

/// <summary>Where a metric sits in the comparison verdict: the primary axis, a supporting
/// signal, or pure context that should be displayed but never drive a verdict.</summary>
public enum MetricRole
{
    Primary,
    Secondary,
    Context,
}

/// <summary>Which direction of change is an improvement. Lets the differ label a delta as a
/// regression vs an improvement without per-kind special-casing.</summary>
public enum BetterDirection
{
    /// <summary>Higher is worse (e.g. pause %, allocation rate, heap-count churn).</summary>
    Lower,

    /// <summary>Higher is better (e.g. throughput, requests/sec).</summary>
    Higher,

    /// <summary>Neither direction is inherently good or bad (e.g. a raw gauge / budget size).</summary>
    Neutral,
}

/// <summary>How a metric value aggregates over the collection window. Drives normalization and
/// how series are summarized.</summary>
public enum MetricAggregation
{
    /// <summary>An instantaneous gauge / last-observed value.</summary>
    Point,

    /// <summary>A per-second rate.</summary>
    Rate,

    /// <summary>A cumulative total over the window.</summary>
    Total,

    /// <summary>A percentage (0..100).</summary>
    Percent,

    /// <summary>A duration expressed in a fixed unit.</summary>
    Duration,
}

/// <summary>What basis a value was normalized by, so captures of unequal length/size compare
/// fairly. <see cref="None"/> means the raw value is already comparable.</summary>
public enum MetricNormalization
{
    None,
    DurationSeconds,
    SampleCount,
}

/// <summary>
/// Semantic descriptor for a single comparable metric. Carries the units and the
/// "which way is better" knowledge a generic differ needs to turn a delta into a verdict.
/// <c>Name</c> is a stable identifier unique within a snapshot's metric set; <c>Unit</c> is a
/// display unit (e.g. <c>%</c>, <c>MB</c>, <c>count</c>, <c>ms</c>), null when unitless.
/// </summary>
public sealed record MetricDefinition(
    string Name,
    MetricRole Role,
    BetterDirection BetterDirection,
    MetricAggregation Aggregation,
    MetricNormalization NormalizedBy = MetricNormalization.None,
    string? Unit = null);

/// <summary>A descriptor paired with its observed value.</summary>
public sealed record MetricValue(MetricDefinition Definition, double Value);

/// <summary>
/// Stable, comparable identity for a key-set row (CPU method, heap type, contention site).
/// Matching policy: <see cref="ExactId"/> (e.g. MVID+token) first, then <see cref="StableId"/>
/// (module+name, survives rebuilds), then name-only with a collision note. The structured fields
/// are display/grouping aids.
/// </summary>
public sealed record ComparableKey(
    string Kind,
    string StableId,
    string? ExactId = null,
    string? Module = null,
    string? TypeName = null,
    string? MethodName = null,
    string? GenericSignature = null);

/// <summary>One key-set row: a stable key plus its per-row metrics.</summary>
public sealed record ComparableRow(
    ComparableKey Key,
    string DisplayName,
    IReadOnlyList<MetricValue> Metrics);

/// <summary>
/// Normalized, bounded, kind-tagged projection of a collector artifact, designed to be
/// persisted to JSON and compared (pairwise or N-ary) regardless of where the captures came
/// from — different points in time (trend) or different pods/replicas (dispersion). This is the
/// portable substrate the comparison engine consumes; the server stays stateless.
/// <c>Kind</c> is the collector kind projected from (e.g. <c>gc-datas</c>); <c>Label</c>
/// distinguishes the capture in a journey (e.g. <c>baseline</c>, <c>pod-3</c>); <c>Metrics</c>
/// holds scalar headline metrics (datas / counters) and <c>Rows</c> holds key-set rows
/// (cpu / heap), empty for metric kinds.
/// </summary>
public sealed record ComparableSnapshot(
    string Schema,
    string Kind,
    string Label,
    DateTimeOffset CapturedAt,
    int ProcessId,
    IReadOnlyList<MetricValue> Metrics,
    IReadOnlyList<ComparableRow> Rows,
    Memory.InvestigationProvenance? Provenance = null)
{
    public const string SchemaV1 = "dotnet-diagnostics-mcp/comparable-snapshot/v1";
}
