namespace DotnetDiagnostics.Core.Activities;

/// <summary>One Activity observed through DiagnosticSource EventPipe bridging.</summary>
public sealed record CapturedActivity(
    string SourceName,
    string OperationName,
    string Id,
    string? ParentId,
    string? TraceId,
    string? SpanId,
    string? ParentSpanId,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt,
    TimeSpan? Duration,
    IReadOnlyDictionary<string, string> Tags)
{
    /// <summary>Opt-in authority evidence, independent of native tags. Null means unknown/not requested.</summary>
    public HttpActivityDestination? Destination { get; init; }
}

/// <summary>Only scheme, host and port from the classic HttpClient Start bridge; never a URL.</summary>
public sealed record HttpActivityDestination(
    string Availability, string? Scheme = null, string? Host = null, int? Port = null, string? Provenance = null);

/// <summary>Whole-window identity accounting. Null on old or non-opted-in captures is unknown.</summary>
public sealed record HttpDestinationCorrelation(
    string Status, int IdentityCap, int AuthorityCap, long Starts, long Stops,
    long NonMatchingEvents, long InvalidIdentities, long DuplicateStarts, long DuplicateStops,
    long ConflictingStarts, long InvalidAuthorities, long IdentityCapEvents, long AuthorityCapEvents,
    int UnmatchedStarts, int UnmatchedStops, int Available);

/// <summary>Aggregated activity counts and duration stats for a single ActivitySource.</summary>
public sealed record ActivitySourceSummary(
    string SourceName,
    int Count,
    int CompletedCount,
    double AverageDurationMs,
    double MaxDurationMs);

/// <summary>Aggregated activity counts and duration stats for a source/operation pair.</summary>
public sealed record ActivityOperationSummary(
    string SourceName,
    string OperationName,
    int Count,
    int CompletedCount,
    double AverageDurationMs,
    double MaxDurationMs);

/// <summary>
/// ActivitySource capture window collected through <c>Microsoft-Diagnostics-DiagnosticSource</c>.
/// Counts describe stop events after source filtering; summaries describe only retained spans.
/// Missing retention provenance denotes legacy/unknown evidence, not verified zero loss.
/// </summary>
public sealed record ActivityCapture(
    int ProcessId,
    IReadOnlyList<string>? SourceFilters,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int TotalActivities,
    int CompletedActivities,
    IReadOnlyList<CapturedActivity> Activities,
    IReadOnlyList<ActivitySourceSummary> BySource,
    IReadOnlyList<ActivityOperationSummary> ByOperation,
    ActivityRetention? Retention = null,
    DateTimeOffset? ProcessStartedAt = null)
{
    /// <summary>Null on legacy captures, never proof of a clean or complete stream.</summary>
    public ActivityObservation? Observation { get; init; }
    public HttpDestinationCorrelation? HttpDestinationCorrelation { get; init; }
}

/// <summary>Transport completion and loss are independent of source filtering and retention limits.</summary>
public sealed record ActivityObservation(TimeSpan RequestedDuration, string Completion, long EventsLost);

/// <summary>
/// Insertion-time accounting after source filtering. With no applied trace filter, every observed
/// event matches. Nullable fields distinguish missing legacy metadata from measured zero.
/// </summary>
public sealed record ActivityRetention(
    string? AppliedTraceId,
    int? EffectiveCap,
    int? ObservedActivities,
    int? MatchingActivities,
    int? RetainedMatchingActivities,
    int? DroppedMatchingActivities,
    int? NonMatchingActivities)
{
    public bool? RetentionLimited => DroppedMatchingActivities is { } dropped ? dropped > 0 : null;
}
