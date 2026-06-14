namespace DotnetDiagnostics.Core.EventSources;

public sealed record EventCatalogEntry(string Provider, string EventName, string Level, long Count);

/// <summary>
/// One metadata-only occurrence retained from the event catalog stream. Payload field values are
/// intentionally omitted: arbitrary EventSource payloads can contain PII, so callers needing payloads
/// must use the targeted event_source collector with its allowlist/redaction gates.
/// </summary>
public sealed record CatalogEventOccurrence(DateTimeOffset Timestamp, string Provider, string EventName, string Level);

public sealed record EventCatalogSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<string> Providers,
    long TotalEvents,
    int DistinctEventTypes,
    IReadOnlyList<EventCatalogEntry> Catalog,
    int SampleCap,
    IReadOnlyList<CatalogEventOccurrence> Sample);

public sealed record EventCatalogView(
    int ProcessId,
    long TotalEvents,
    int DistinctEventTypes,
    int Returned,
    IReadOnlyList<EventCatalogEntry> Entries);

public sealed record EventCatalogProviderGroup(string Provider, int DistinctEventTypes, long TotalCount);

public sealed record EventCatalogByProviderView(
    int ProcessId,
    long TotalEvents,
    int Providers,
    int Returned,
    IReadOnlyList<EventCatalogProviderGroup> Entries);

public sealed record EventCatalogEventsView(
    int ProcessId,
    long TotalEvents,
    int SampleCap,
    int SampledEvents,
    int Returned,
    IReadOnlyList<CatalogEventOccurrence> Events);
