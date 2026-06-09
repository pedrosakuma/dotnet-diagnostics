namespace DotnetDiagnostics.Core.Exceptions;

/// <summary>A single managed exception observed via EventPipe.</summary>
public sealed record ManagedExceptionEvent(
    DateTimeOffset Timestamp,
    string ExceptionType,
    string ExceptionMessage,
    string ExceptionHResult,
    int ThreadId);

/// <summary>Aggregated count of a given exception type in the sample window.</summary>
public sealed record ExceptionCount(string ExceptionType, int Count);

/// <summary>Result of an exception collection window.</summary>
public sealed record ExceptionSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int TotalExceptions,
    IReadOnlyList<ExceptionCount> ByType,
    IReadOnlyList<ManagedExceptionEvent> Recent)
{
    /// <summary>
    /// Cap applied to <see cref="Recent"/> during collection (the <c>maxRecent</c> argument to
    /// <c>collect_exceptions</c>; default <c>100</c>). When <see cref="TotalExceptions"/>
    /// exceeds this value, <see cref="Recent"/> is the truncated head of the stream (the first
    /// N observed during the window) and the tail is dropped — counts in <see cref="ByType"/>
    /// remain exact. Surfaced so the LLM can distinguish "fewer than the cap occurred" from
    /// "more occurred but only the head is shown". See #36.
    /// </summary>
    public int RecentCap { get; init; }
}
