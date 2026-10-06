namespace DotnetDiagnostics.Core.EventSources;

/// <summary>
/// Captures a broad, metadata-only catalog of EventPipe events emitted by selected providers.
/// </summary>
public interface IEventCatalogCollector
{
    Task<EventCatalogSnapshot> CaptureAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? providers = null,
        int maxEvents = 200,
        CancellationToken cancellationToken = default);
}

/// <summary>Captures an event catalog while publishing metadata-only observations incrementally.</summary>
public interface IStreamingEventCatalogCollector
{
    Task<EventCatalogSnapshot> CaptureStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<CatalogEventOccurrence> onObservation,
        IReadOnlyList<string>? providers = null,
        int maxEvents = 200,
        CancellationToken cancellationToken = default);
}
