namespace DotnetDiagnostics.Core.Counters;

/// <summary>A sequenced EventCounter observation from a live session.</summary>
public sealed record CounterObservation(
    long Sequence,
    DateTimeOffset Timestamp,
    CounterValue Counter);

/// <summary>Terminal state of a live counter session.</summary>
public enum CounterSessionStatus
{
    /// <summary>The caller stopped or cancelled the session.</summary>
    Stopped,

    /// <summary>The EventPipe stream ended without a caller-requested stop.</summary>
    TargetExited,

    /// <summary>The EventPipe stream or its shutdown failed.</summary>
    Failed,
}

/// <summary>Final quality and termination details for a live counter session.</summary>
public sealed record CounterSessionCompletion(
    CounterSessionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long? EventPipeEventsLost,
    long DroppedObservations,
    Exception? Error);
