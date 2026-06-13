namespace DotnetDiagnostics.Core.GatedCapture;

/// <summary>
/// Context handed to the capture callback the moment a <see cref="TriggerPredicate"/> trips.
/// </summary>
/// <param name="ProcessId">Resolved target PID.</param>
/// <param name="ObservedValue">The metric value that satisfied the predicate.</param>
/// <param name="TrippedAt">When the breach was observed.</param>
/// <param name="CaptureIndex">0-based index of this capture within the bounded watch.</param>
public sealed record GatedCaptureTrigger(
    int ProcessId,
    double ObservedValue,
    DateTimeOffset TrippedAt,
    int CaptureIndex);

/// <summary>
/// Result of a single fired capture, returned by the capture callback. Either a drilldown
/// <see cref="Handle"/> (cpu-sample / heap / thread-snapshot) or an on-disk <see cref="ArtifactPath"/>
/// (dump) is populated; <see cref="Error"/> is set when the capture failed.
/// </summary>
public sealed record GatedCaptureOutcome(
    string Summary,
    string? Handle = null,
    DateTimeOffset? HandleExpiresAt = null,
    string? ArtifactPath = null,
    string? Error = null);

/// <summary>One fired capture within a bounded threshold-gated watch.</summary>
public sealed record GatedCaptureRecord(
    int Index,
    double ObservedValue,
    DateTimeOffset TrippedAt,
    string CaptureKind,
    string Summary,
    string? Handle = null,
    DateTimeOffset? HandleExpiresAt = null,
    string? ArtifactPath = null,
    string? Error = null);

/// <summary>
/// Outcome of a bounded threshold-gated capture (issue #419). The watch polled
/// <see cref="Metric"/> for at most <see cref="Window"/>, fired up to <see cref="MaxCaptures"/>
/// captures when <see cref="Predicate"/> tripped, and returned. Nothing persists after the call.
/// </summary>
public sealed record GatedCaptureResult(
    int ProcessId,
    string Metric,
    string Counter,
    string Predicate,
    string CaptureKind,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    TimeSpan Window,
    int MaxCaptures,
    int SamplesObserved,
    double? FirstObservedValue,
    double? LastObservedValue,
    double? PeakObservedValue,
    bool Tripped,
    bool WindowExpired,
    bool ProcessExited,
    IReadOnlyList<GatedCaptureRecord> Captures,
    IReadOnlyList<string> Notes);
