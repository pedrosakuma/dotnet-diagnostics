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
