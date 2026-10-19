using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.EventSources;

/// <summary>Owns a continuous EventPipe session for explicitly selected EventSource providers.</summary>
public sealed class EventSourceSession : EventPipeDiagnosticSessionBase
{
    private const int ProjectionFrameBudget = 14 * 1024;
    private const int MaxMetadataLength = 256;
    private readonly HashSet<string> _providerNames;
    private readonly ILogger<EventSourceSession> _logger;

    internal EventSourceSession(
        int processId,
        EventSourceSessionOptions options,
        Func<CancellationToken, Task<EventPipeSession>> startEventPipeSession,
        Action<Exception> onShutdownError,
        ILogger<EventSourceSession>? logger = null)
        : base(processId, options.ObservationCapacity, startEventPipeSession, onShutdownError,
            EventSourceSessionOptions.MaxPendingRetainedBytes)
    {
        _providerNames = options.Providers.Select(provider => provider.ProviderName).ToHashSet(StringComparer.Ordinal);
        _logger = logger ?? NullLogger<EventSourceSession>.Instance;
    }

    private protected override void ConfigureSource(EventPipeEventSource source, DiagnosticSessionEventBuffer observations)
    {
        // Dynamic runtime exception callbacks can expose no payload fields on Linux.
        // Use TraceEvent's CLR decoder for the verified v1 identity, once only.
        source.Clr.ExceptionStart += traceEvent =>
        {
            if (ShouldPublishEvent(traceEvent.ProviderGuid, (int)traceEvent.ID, traceEvent.Version, clrDecoder: true))
            {
                Publish(traceEvent);
            }
        };
        source.Dynamic.All += traceEvent =>
        {
            if (!ShouldPublishEvent(traceEvent.ProviderGuid, (int)traceEvent.ID, traceEvent.Version, clrDecoder: false))
            {
                return;
            }

            Publish(traceEvent);
        };

        void Publish(TraceEvent traceEvent)
        {
            if (!_providerNames.Contains(traceEvent.ProviderName)) return;
            observations.TryPublish(sequence => ProjectEvent(traceEvent).WithSequence(sequence));
        }
    }

    internal static bool UsesClrExceptionDecoder(Guid providerGuid, int eventId, int version) =>
        providerGuid == ClrTraceEventParser.ProviderGuid && eventId == 80 && version == 1;

    internal static bool ShouldPublishEvent(Guid providerGuid, int eventId, int version, bool clrDecoder) =>
        clrDecoder == UsesClrExceptionDecoder(providerGuid, eventId, version);

    private EventSourceLiveObservation ProjectEvent(TraceEvent traceEvent)
    {
        var provider = TruncateMetadata(traceEvent.ProviderName, out var providerTruncated);
        var eventName = TruncateMetadata(traceEvent.EventName, out var nameTruncated);
        var level = TruncateMetadata(traceEvent.Level.ToString(), out var levelTruncated);
        var metadataTruncated = providerTruncated || nameTruncated || levelTruncated;
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        var omittedFields = 0;
        var omittedCharacters = 0;
        var unsupportedValues = 0;
        var payloadCharacters = 0;

        var names = traceEvent.PayloadNames ?? Array.Empty<string>();
        for (var index = 0; index < names.Length; index++)
        {
            if (index >= EventSourceSessionOptions.MaxPayloadFields)
            {
                omittedFields += names.Length - index;
                break;
            }

            var originalName = names[index] ?? string.Empty;
            var fieldName = Truncate(originalName, EventSourceSessionOptions.MaxPayloadFieldNameLength);
            omittedCharacters += originalName.Length - fieldName.Length;
            if (!TryFormatPayloadValue(traceEvent.PayloadByName(originalName), out var value))
            {
                value = "(unsupported value type)";
                unsupportedValues++;
            }

            var boundedValue = Truncate(value, EventSourceSessionOptions.MaxPayloadValueLength);
            omittedCharacters += value.Length - boundedValue.Length;
            var remainingCharacters = EventSourceSessionOptions.MaxPayloadCharacters - payloadCharacters;
            if (remainingCharacters <= fieldName.Length)
            {
                omittedFields += names.Length - index;
                omittedCharacters += fieldName.Length + boundedValue.Length;
                break;
            }

            if (fieldName.Length + boundedValue.Length > remainingCharacters)
            {
                var allowedValueLength = remainingCharacters - fieldName.Length;
                omittedCharacters += boundedValue.Length - allowedValueLength;
                boundedValue = boundedValue[..allowedValueLength];
            }

            var metadataSize = GetJsonSize(provider) + GetJsonSize(eventName) + GetJsonSize(level) + 512;
            var fieldSize = GetJsonSize(fieldName) + GetJsonSize(boundedValue) + 8;
            var currentPayloadSize = payload.Sum(pair => GetJsonSize(pair.Key) + GetJsonSize(pair.Value) + 8);
            if (metadataSize + currentPayloadSize + fieldSize > ProjectionFrameBudget)
            {
                omittedFields += names.Length - index;
                omittedCharacters += fieldName.Length + boundedValue.Length;
                break;
            }

            payload[fieldName] = boundedValue;
            payloadCharacters += fieldName.Length + boundedValue.Length;
        }

        Guid? activityId = traceEvent.ActivityID is { } activity && activity != Guid.Empty ? activity : null;
        Guid? relatedActivityId = traceEvent.RelatedActivityID is { } related && related != Guid.Empty ? related : null;
        int? threadId = traceEvent.ThreadID > 0 ? traceEvent.ThreadID : null;
        var observation = new EventSourceLiveObservation(
            0,
            new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero),
            DateTimeOffset.UtcNow,
            provider,
            eventName,
            (int)traceEvent.ID,
            traceEvent.Version,
            level,
            threadId,
            activityId,
            relatedActivityId,
            payload,
            metadataTruncated,
            omittedFields,
            omittedCharacters,
            unsupportedValues,
            payloadFieldsUnavailable: names.Length == 0 && traceEvent.EventDataLength > 0);

        if (JsonSerializer.SerializeToUtf8Bytes(observation).Length > ProjectionFrameBudget)
        {
            _logger.LogWarning("A bounded live EventSource observation exceeded the projection frame budget and was omitted.");
            return observation with
            {
                OmittedPayloadFields = omittedFields + payload.Count,
                OmittedPayloadCharacters = omittedCharacters + payload.Sum(pair => pair.Key.Length + pair.Value.Length),
                Payload = new Dictionary<string, string>(StringComparer.Ordinal),
            };
        }

        return observation;
    }

    private static string TruncateMetadata(string value, out bool truncated)
    {
        var bounded = Truncate(value, MaxMetadataLength);
        truncated = bounded.Length != value.Length;
        return bounded;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static int GetJsonSize(string value) => JsonSerializer.SerializeToUtf8Bytes(value).Length;

    private static bool TryFormatPayloadValue(object? value, out string formatted)
    {
        switch (value)
        {
            case null:
                formatted = "null";
                return true;
            case string text:
                formatted = text;
                return true;
            case char character:
                formatted = character.ToString();
                return true;
            case bool boolean:
                formatted = boolean ? "true" : "false";
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal:
                formatted = ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
                return true;
            case Enum enumeration:
                formatted = Convert.ToString(enumeration, CultureInfo.InvariantCulture) ?? string.Empty;
                return true;
            case Guid guid:
                formatted = guid.ToString("D");
                return true;
            case DateTime dateTime:
                formatted = dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                return true;
            case DateTimeOffset dateTimeOffset:
                formatted = dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                return true;
            default:
                formatted = string.Empty;
                return false;
        }
    }
}
