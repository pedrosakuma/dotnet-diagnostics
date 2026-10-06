using DotnetDiagnostics.Core.Counters;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Gc;

/// <summary>
/// Owns a live EventPipe GC session and dispatches a <see cref="GcPauseObservation"/> for each
/// paired GCStart/GCStop collection. Reuses the same <see cref="EventPipeDiagnosticSessionBase"/>
/// lifecycle (start/stop/sequencing/drop-accounting/status classification) as <see cref="CounterSession"/>,
/// and reuses <see cref="GcCaptureState"/>'s already-verified incremental GCStart/GCStop pairing
/// (conflict/overflow handling, capped pending state) instead of re-implementing it.
/// </summary>
public sealed class GcSession : EventPipeDiagnosticSessionBase
{
    private readonly int _observationCapacity;

    internal GcSession(
        int processId,
        GcSessionOptions options,
        Func<CancellationToken, Task<EventPipeSession>> startEventPipeSession,
        Action<Exception> onShutdownError)
        : base(processId, options.ObservationCapacity, startEventPipeSession, onShutdownError)
    {
        _observationCapacity = options.ObservationCapacity;
    }

    private protected override void ConfigureSource(EventPipeEventSource source, DiagnosticSessionEventBuffer observations)
    {
        var state = new GcCaptureState(
            _observationCapacity,
            sink: null,
            onCollection: collection => observations.TryPublish(
                sequence => new GcPauseObservation(sequence, collection.Timestamp, collection)));

        source.Clr.GCStart += traceEvent => state.CollectionBegin(
            traceEvent.ClrInstanceID,
            unchecked((uint)traceEvent.Count),
            traceEvent.Version,
            new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime()),
            traceEvent.Depth,
            traceEvent.Reason.ToString(),
            traceEvent.Type.ToString());

        source.Clr.GCStop += traceEvent => state.CollectionEnd(
            traceEvent.ClrInstanceID,
            unchecked((uint)traceEvent.Count),
            traceEvent.Version,
            new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime()));
    }
}
