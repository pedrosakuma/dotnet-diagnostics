using System.Collections.ObjectModel;
using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.EventSources;

/// <summary>A bounded, sequenced event from a live EventPipe EventSource subscription.</summary>
public sealed record EventSourceLiveObservation : DiagnosticSessionEvent
{
    /// <summary>Creates a bounded EventPipe observation.</summary>
    public EventSourceLiveObservation(
        long sequence,
        DateTimeOffset timestamp,
        DateTimeOffset receivedAt,
        string provider,
        string eventName,
        int eventId,
        int version,
        string level,
        int? threadId,
        Guid? activityId,
        Guid? relatedActivityId,
        IReadOnlyDictionary<string, string> payload,
        bool metadataTruncated,
        int omittedPayloadFields,
        int omittedPayloadCharacters,
        int unsupportedPayloadValues,
        bool payloadFieldsUnavailable = false)
        : base(sequence, timestamp)
    {
        ReceivedAt = receivedAt;
        Provider = provider;
        EventName = eventName;
        EventId = eventId;
        Version = version;
        Level = level;
        ThreadId = threadId;
        ActivityId = activityId;
        RelatedActivityId = relatedActivityId;
        Payload = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(payload, StringComparer.Ordinal));
        MetadataTruncated = metadataTruncated;
        OmittedPayloadFields = omittedPayloadFields;
        OmittedPayloadCharacters = omittedPayloadCharacters;
        UnsupportedPayloadValues = unsupportedPayloadValues;
        PayloadFieldsUnavailable = payloadFieldsUnavailable;
    }

    /// <summary>UTC time when this process received the event callback.</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>EventSource provider name.</summary>
    public string Provider { get; init; }

    /// <summary>Event name.</summary>
    public string EventName { get; init; }

    /// <summary>Provider-defined event identity.</summary>
    public int EventId { get; init; }

    /// <summary>Provider-defined event schema version.</summary>
    public int Version { get; init; }

    /// <summary>Event level name.</summary>
    public string Level { get; init; }

    /// <summary>Thread identifier, when reported by TraceEvent.</summary>
    public int? ThreadId { get; init; }

    /// <summary>Activity identifier, when present.</summary>
    public Guid? ActivityId { get; init; }

    /// <summary>Related activity identifier, when present.</summary>
    public Guid? RelatedActivityId { get; init; }

    /// <summary>Bounded, inert-text payload values.</summary>
    public IReadOnlyDictionary<string, string> Payload { get; init; }

    /// <summary>Whether one or more metadata strings were shortened to the metadata limit.</summary>
    public bool MetadataTruncated { get; init; }

    /// <summary>Number of payload fields omitted by field-count or frame-size limits.</summary>
    public int OmittedPayloadFields { get; init; }

    /// <summary>Number of payload name/value characters omitted by character limits.</summary>
    public int OmittedPayloadCharacters { get; init; }

    /// <summary>Number of payload values represented by an unsupported-type marker.</summary>
    public int UnsupportedPayloadValues { get; init; }

    /// <summary>Whether event-data bytes were present but TraceEvent exposed no decoded payload fields.</summary>
    public bool PayloadFieldsUnavailable { get; init; }

    /// <summary>Whether any payload was omitted, shortened, or could not be formatted.</summary>
    public bool PayloadTruncated =>
        OmittedPayloadFields > 0 || OmittedPayloadCharacters > 0 || UnsupportedPayloadValues > 0;

    /// <inheritdoc />
    public override int RetainedSizeBytes
    {
        get
        {
            var chars = (long)Provider.Length + EventName.Length + Level.Length
                + Payload.Sum(pair => (long)pair.Key.Length + pair.Value.Length);
            return EstimateRetainedSizeBytes(chars, 1024 + (Payload.Count * 128L));
        }
    }

    /// <inheritdoc />
    public override DiagnosticSessionEvent WithSequence(long sequence) =>
        new EventSourceLiveObservation(
            sequence,
            Timestamp,
            ReceivedAt,
            Provider,
            EventName,
            EventId,
            Version,
            Level,
            ThreadId,
            ActivityId,
            RelatedActivityId,
            Payload,
            MetadataTruncated,
            OmittedPayloadFields,
            OmittedPayloadCharacters,
            UnsupportedPayloadValues,
            PayloadFieldsUnavailable);
}
