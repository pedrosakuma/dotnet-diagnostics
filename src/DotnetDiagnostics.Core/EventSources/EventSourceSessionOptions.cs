namespace DotnetDiagnostics.Core.EventSources;

/// <summary>One provider subscription for a live EventSource session.</summary>
public sealed record EventSourceProviderSubscription(string ProviderName, int EventLevel, long Keywords);

/// <summary>Options for a bounded live EventSource session.</summary>
public sealed record EventSourceSessionOptions
{
    /// <summary>Maximum number of provider subscriptions.</summary>
    public const int MaxProviderCount = 8;

    /// <summary>Maximum provider-name length.</summary>
    public const int MaxProviderNameLength = 256;

    /// <summary>Maximum observation queue capacity.</summary>
    public const int MaxObservationCapacity = 256;

    /// <summary>Maximum conservative retained-byte budget for the pending observation queue.</summary>
    public const long MaxPendingRetainedBytes = 4 * 1024 * 1024;

    /// <summary>Maximum serialized event frame size, including the protocol envelope.</summary>
    public const int MaxEventFrameBytes = 16 * 1024;

    /// <summary>Maximum retained payload fields in one event.</summary>
    public const int MaxPayloadFields = 32;

    /// <summary>Maximum retained field-name length.</summary>
    public const int MaxPayloadFieldNameLength = 128;

    /// <summary>Maximum retained individual string value length.</summary>
    public const int MaxPayloadValueLength = 1024;

    /// <summary>Maximum aggregate retained payload name/value characters.</summary>
    public const int MaxPayloadCharacters = 2048;

    /// <summary>Explicit provider subscriptions. Empty subscriptions are rejected.</summary>
    public IReadOnlyList<EventSourceProviderSubscription> Providers { get; init; } = [];

    /// <summary>Maximum number of observations queued for dispatch.</summary>
    public int ObservationCapacity { get; init; } = MaxObservationCapacity;
}
