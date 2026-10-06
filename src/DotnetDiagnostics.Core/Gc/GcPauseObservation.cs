using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.Gc;

/// <summary>
/// A sequenced GC collection observation from a live session: one paired GCStart/GCStop
/// collection, mirroring <see cref="GcEvent"/>'s shape. <c>Timestamp</c> marks when the
/// collection began; <c>Collection.PauseDuration</c> (aliased as
/// <see cref="GcEvent.CollectionElapsedDuration"/>) is the elapsed time to GCStop, i.e. the
/// collection's wall-clock duration — NOT the runtime (stop-the-world) suspension duration,
/// which can be much shorter for background/concurrent GCs. Consumers that need the actual
/// application pause window should correlate against <c>GCSuspendEE</c>/<c>GCRestartEE</c>
/// events separately; this observation only reports collection timing.
/// </summary>
public sealed record GcPauseObservation : DiagnosticSessionEvent
{
    /// <summary>Creates a GC pause observation.</summary>
    public GcPauseObservation(long sequence, DateTimeOffset timestamp, GcEvent collection)
        : base(sequence, timestamp)
    {
        Collection = collection;
    }

    /// <summary>The paired GCStart/GCStop collection this observation reports.</summary>
    public GcEvent Collection { get; }

    /// <inheritdoc />
    public override DiagnosticSessionEvent WithSequence(long sequence) =>
        new GcPauseObservation(sequence, Timestamp, Collection);
}
