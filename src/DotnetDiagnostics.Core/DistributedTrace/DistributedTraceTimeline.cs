namespace DotnetDiagnostics.Core.DistributedTrace;

/// <summary>
/// One span of a single distributed trace as observed on one replica (Pod). This is the
/// per-Pod projection of a <see cref="DotnetDiagnostics.Core.Activities.CapturedActivity"/>
/// once it has been matched to the requested W3C trace-id and stitched into the cross-Pod
/// timeline. <see cref="SelfDurationMs"/> is the span's own time minus the time attributed
/// to its direct children — this is what makes "which hop is slow?" answerable, because a
/// parent span that merely *waits* on a slow child should not itself be flagged.
/// </summary>
public sealed record DistributedTraceSpan(
    string PodName,
    string SourceName,
    string OperationName,
    string? SpanId,
    string? ParentSpanId,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt,
    double? DurationMs,
    double? SelfDurationMs,
    int Depth,
    bool ParentResolved,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// A single W3C trace stitched across every attached replica that observed it. Spans are
/// ordered causally (parent before child; roots first; siblings by wall-clock start) rather
/// than by raw wall-clock so that clock skew between nodes does not scramble the timeline.
/// </summary>
/// <remarks>
/// This is a <em>best-effort, in-flight</em> view: it correlates spans captured during a
/// bounded window across the currently-attached Pods. It is not a historical-trace replay —
/// spans that completed before the capture window (or on Pods that are not attached) are
/// invisible and are reported via <see cref="Warnings"/>.
/// </remarks>
public sealed record DistributedTraceTimeline(
    string TraceId,
    int PodCount,
    int SpanCount,
    IReadOnlyList<DistributedTraceSpan> Spans,
    DistributedTraceSpan? SlowestHop,
    double? WallClockDurationMs,
    IReadOnlyList<DistributedTracePodCoverage> Coverage,
    IReadOnlyList<string> Warnings);

/// <summary>How many spans of the requested trace a single attached Pod contributed.</summary>
public sealed record DistributedTracePodCoverage(
    string PodName,
    int MatchedSpans,
    int TotalCapturedActivities);
