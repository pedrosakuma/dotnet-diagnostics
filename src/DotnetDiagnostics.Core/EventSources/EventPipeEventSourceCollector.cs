using System.Diagnostics.Tracing;
using DotnetDiagnostics.Core.CaptureRecording;
using System.Globalization;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.EventSources;

/// <summary>
/// Default <see cref="IEventSourceCollector"/>: opens an EventPipe session against a single
/// EventSource and snapshots every event (name + payload) it emits in the window.
/// </summary>
public sealed class EventPipeEventSourceCollector : IEventSourceCollector
{
    private readonly ILogger<EventPipeEventSourceCollector> _logger;

    public EventPipeEventSourceCollector(ILogger<EventPipeEventSourceCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeEventSourceCollector>.Instance;
    }

    public async Task<EventSourceCapture> CaptureAsync(
        int processId,
        string providerName,
        TimeSpan duration,
        long keywords = -1,
        int eventLevel = 5,
        int maxEvents = 200,
        CancellationToken cancellationToken = default)
    {
        var observationSink = CaptureRecordingContext.Current;
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (maxEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "maxEvents must be >= 1.");
        }

        if (eventLevel < 0 || eventLevel > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(eventLevel), "eventLevel must be 0..5 (matches System.Diagnostics.Tracing.EventLevel).");
        }

        var providers = new[]
        {
            new EventPipeProvider(providerName, (EventLevel)eventLevel, keywords),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        // EventPipeEventSource invokes these callbacks on the single source.Process() thread, so
        // plain collections are sufficient and avoid unnecessary synchronization on the hot path.
        var captured = new List<CapturedEvent>(Math.Min(maxEvents, 128));
        var total = 0;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.ProviderName, providerName, StringComparison.Ordinal))
                    {
                        return;
                    }

                    total++;
                    if (captured.Count >= maxEvents)
                    {
                        return;
                    }

                    var payload = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var name in traceEvent.PayloadNames ?? Array.Empty<string>())
                    {
                        try
                        {
                            var value = traceEvent.PayloadByName(name);
                            payload[name] = value switch
                            {
                                null => string.Empty,
                                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                                _ => value.ToString() ?? string.Empty,
                            };
                        }
                        catch (Exception)
                        {
                            payload[name] = "(unserializable)";
                        }
                    }

                    RetainEvent(captured, maxEvents, providerName, new CapturedEvent(
                        Timestamp: new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero),
                        Provider: traceEvent.ProviderName,
                        EventName: traceEvent.EventName,
                        Level: traceEvent.Level.ToString(),
                        Payload: payload), observationSink);
                };

                source.Process();
                observationSink?.ReportSourceLoss(providerName, source.EventsLost);
            }
            catch (Exception ex)
            {
                observationSink?.ReportSourceLoss(providerName, null);
                _logger.LogDebug(ex, "EventPipe custom source ended for pid {Pid} provider {Provider}.", processId, providerName);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await EventPipeSessionShutdown.StopAndDrainAsync(
                session,
                processingTask,
                ex => _logger.LogDebug(ex, "Stopping custom EventPipe session for pid {Pid} provider {Provider} failed.", processId, providerName))
                .ConfigureAwait(false);
        }

        observationSink?.TryAppend(ProviderObservationProjection.Create(
            "event-source.retention.aggregate", null, null, providerName,
            ("provider", providerName), ("totalEvents", total), ("retainedEvents", captured.Count),
            ("maxEvents", maxEvents), ("unretainedEvents", total - captured.Count)));
        return new EventSourceCapture(
            ProcessId: processId,
            Provider: providerName,
            StartedAt: startedAt,
            Duration: duration,
            TotalEvents: total,
            Events: captured);
    }

    internal static void RetainEvent(
        List<CapturedEvent> captured, int maxEvents, string providerName, CapturedEvent observation, ICaptureObservationSink? sink)
    {
        if (captured.Count >= maxEvents || !string.Equals(providerName, observation.Provider, StringComparison.Ordinal)) return;
        captured.Add(observation);
        if (sink is null) return;
        const int maxPayloadFields = 256;
        var fields = new List<CaptureObservationField>(Math.Min(observation.Payload.Count, maxPayloadFields) + 3)
        {
            CaptureObservationField.String("provider", observation.Provider),
            CaptureObservationField.String("level", observation.Level),
            CaptureObservationField.Int64("omittedPayloadFields", Math.Max(0, observation.Payload.Count - maxPayloadFields)),
        };
        foreach (var field in observation.Payload.Take(maxPayloadFields))
            fields.Add(CaptureObservationField.String("payload." + field.Key,
                EventSourceDurableSanitizer.SanitizeValue(field.Key, field.Value)));
        sink.TryAppend(new CaptureObservation("event-source.event", observation.Timestamp, null, observation.EventName, fields));
    }
}
