using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Counters;

/// <summary>
/// Owns a live EventPipe counter session and dispatches typed session events to attached handlers.
/// </summary>
public sealed class CounterSession : EventPipeDiagnosticSessionBase
{
    internal CounterSession(
        int processId,
        CounterSessionOptions options,
        Func<CancellationToken, Task<EventPipeSession>> startEventPipeSession,
        Action<Exception> onShutdownError)
        : base(processId, options.ObservationCapacity, startEventPipeSession, onShutdownError)
    {
    }

    private protected override void ConfigureSource(EventPipeEventSource source, DiagnosticSessionEventBuffer observations)
    {
        source.Dynamic.All += traceEvent =>
        {
            if (!string.Equals(traceEvent.EventName, "EventCounters", StringComparison.Ordinal))
            {
                return;
            }

            var value = EventPipeCounterCollector.ExtractCounterPayload(traceEvent, out _);
            if (value is not null)
            {
                var timestamp = new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime());
                observations.TryPublish(sequence => new CounterObservation(
                    sequence,
                    timestamp,
                    value with { Provider = traceEvent.ProviderName }));
            }
        };
    }
}
