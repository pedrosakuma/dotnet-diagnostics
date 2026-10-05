namespace DotnetDiagnostics.Core.Counters;

/// <summary>Base type for typed events published by a diagnostic session.</summary>
public abstract record DiagnosticSessionEvent
{
    /// <summary>Creates a sequenced event.</summary>
    protected DiagnosticSessionEvent(long sequence, DateTimeOffset timestamp)
    {
        Sequence = sequence;
        Timestamp = timestamp;
    }

    /// <summary>Monotonically increasing sequence within the session.</summary>
    public long Sequence { get; }

    /// <summary>Timestamp reported by the source event.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Creates an equivalent event with the composed-session sequence.</summary>
    public abstract DiagnosticSessionEvent WithSequence(long sequence);
}

/// <summary>A sequenced EventCounter observation from a live session.</summary>
public sealed record CounterObservation : DiagnosticSessionEvent
{
    /// <summary>Creates a counter observation.</summary>
    public CounterObservation(long sequence, DateTimeOffset timestamp, CounterValue counter)
        : base(sequence, timestamp)
    {
        Counter = counter;
    }

    /// <summary>The observed counter value.</summary>
    public CounterValue Counter { get; }

    /// <inheritdoc />
    public override DiagnosticSessionEvent WithSequence(long sequence) =>
        new CounterObservation(sequence, Timestamp, Counter);
}

/// <summary>The typed terminal result produced by a finite capture added to a composed session.</summary>
public sealed record DiagnosticSessionCaptureResult<TCapture> : DiagnosticSessionEvent
{
    /// <summary>Creates a terminal capture-result event.</summary>
    public DiagnosticSessionCaptureResult(long sequence, DateTimeOffset timestamp, TCapture result)
        : base(sequence, timestamp)
    {
        Result = result;
    }

    /// <summary>The capture result, retaining its original static type.</summary>
    public TCapture Result { get; }

    /// <inheritdoc />
    public override DiagnosticSessionEvent WithSequence(long sequence) =>
        new DiagnosticSessionCaptureResult<TCapture>(sequence, Timestamp, Result);
}

/// <summary>A typed observation emitted while a finite capture is running.</summary>
public sealed record DiagnosticSessionObservation<TObservation> : DiagnosticSessionEvent
{
    /// <summary>Creates a typed capture observation.</summary>
    public DiagnosticSessionObservation(long sequence, DateTimeOffset timestamp, TObservation observation)
        : base(sequence, timestamp)
    {
        Observation = observation;
    }

    /// <summary>The source observation.</summary>
    public TObservation Observation { get; }

    /// <inheritdoc />
    public override DiagnosticSessionEvent WithSequence(long sequence) =>
        new DiagnosticSessionObservation<TObservation>(sequence, Timestamp, Observation);
}

/// <summary>Terminal state of a diagnostic session.</summary>
public enum DiagnosticSessionStatus
{
    /// <summary>The caller stopped or cancelled the session.</summary>
    Stopped,

    /// <summary>All configured sources completed successfully.</summary>
    Completed,

    /// <summary>The EventPipe stream ended without a caller-requested stop.</summary>
    TargetExited,

    /// <summary>The EventPipe stream or its shutdown failed.</summary>
    Failed,
}

/// <summary>Final quality and termination details for a live diagnostic session.</summary>
public sealed record DiagnosticSessionCompletion(
    DiagnosticSessionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long? EventPipeEventsLost,
    long DroppedObservations,
    Exception? Error);
