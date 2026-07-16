using DotnetDiagnostics.Core;

namespace DotnetDiagnostics.Core.EventSources;

public static class EventCatalogQueryDispatcher
{
    public const string CatalogView = "catalog";
    public const string ByProviderView = "byProvider";
    public const string EventsView = "events";
    public const int DefaultTopN = 50;

    private static readonly string[] Views = { CatalogView, ByProviderView, EventsView };

    public static IReadOnlyList<string> SessionViews => Views;

    public static bool IsKnownView(string? view)
        => view is not null && Array.Exists(Views, v => string.Equals(v, view, StringComparison.Ordinal));

    public static DiagnosticResult<object> Render(
        EventCatalogSnapshot snapshot,
        string handle,
        string? view,
        int topN,
        string? providerFilter = null,
        string? eventNameFilter = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (topN < 1) return InvalidArg<object>(nameof(topN), "must be >= 1");

        var effectiveView = string.IsNullOrWhiteSpace(view) ? CatalogView : view.Trim();
        return effectiveView.ToLowerInvariant() switch
        {
            "catalog" => Box(RenderCatalog(snapshot, handle, topN, providerFilter, eventNameFilter)),
            "byprovider" => Box(RenderByProvider(snapshot, handle, topN, providerFilter, eventNameFilter)),
            "events" => Box(RenderEvents(snapshot, handle, topN, providerFilter, eventNameFilter)),
            _ => DiagnosticResult.Fail<object>(
                $"Unknown event-catalog view '{effectiveView}' for handle '{handle}'.",
                new DiagnosticError("InvalidArgument", $"Valid views: {string.Join(", ", Views)}.", effectiveView),
                new NextActionHint("query_snapshot", "Retry with a supported event-catalog view.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CatalogView })),
        };
    }

    public static DiagnosticResult<EventCatalogView> RenderCatalog(
        EventCatalogSnapshot snapshot,
        string handle,
        int topN,
        string? providerFilter = null,
        string? eventNameFilter = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (topN < 1) return InvalidArg<EventCatalogView>(nameof(topN), "must be >= 1");

        var filtered = FilterEntries(snapshot.Catalog, providerFilter, eventNameFilter).Take(topN).ToList();
        var view = new EventCatalogView(snapshot.ProcessId, snapshot.TotalEvents, snapshot.DistinctEventTypes, filtered.Count, filtered);
        var summary = filtered.Count == 0
            ? "No catalog entries matched the supplied filters."
            : $"Showing {filtered.Count} event type(s) from the catalog. Hottest: {filtered[0].Provider}/{filtered[0].EventName} ({filtered[0].Count}).";

        return DiagnosticResult.Ok(view, summary,
            new NextActionHint("query_snapshot", "Roll up the catalog by provider.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = ByProviderView }));
    }

    public static DiagnosticResult<EventCatalogByProviderView> RenderByProvider(
        EventCatalogSnapshot snapshot,
        string handle,
        int topN,
        string? providerFilter = null,
        string? eventNameFilter = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (topN < 1) return InvalidArg<EventCatalogByProviderView>(nameof(topN), "must be >= 1");

        var allGroups = FilterEntries(snapshot.Catalog, providerFilter, eventNameFilter)
            .GroupBy(e => e.Provider, StringComparer.Ordinal)
            .Select(g => new EventCatalogProviderGroup(g.Key, g.Count(), g.Sum(e => e.Count)))
            .OrderByDescending(g => g.TotalCount)
            .ThenBy(g => g.Provider, StringComparer.Ordinal)
            .ToList();
        var groups = allGroups.Take(topN).ToList();

        var view = new EventCatalogByProviderView(snapshot.ProcessId, snapshot.TotalEvents, allGroups.Count, groups.Count, groups);
        var summary = groups.Count == 0
            ? "No providers matched the supplied filters."
            : $"Showing {groups.Count} provider(s) from the catalog. Busiest: {groups[0].Provider} ({groups[0].TotalCount}).";

        var hintArguments = new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CatalogView };
        if (groups.Count > 0)
        {
            hintArguments["providerFilter"] = groups[0].Provider;
        }

        return DiagnosticResult.Ok(view, summary,
            new NextActionHint("query_snapshot",
                groups.Count == 0 ? "Return to the unfiltered event catalog." : "Inspect event names for the busiest provider.",
                hintArguments));
    }

    public static DiagnosticResult<EventCatalogEventsView> RenderEvents(
        EventCatalogSnapshot snapshot,
        string handle,
        int topN,
        string? providerFilter = null,
        string? eventNameFilter = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (topN < 1) return InvalidArg<EventCatalogEventsView>(nameof(topN), "must be >= 1");

        var events = snapshot.Sample
            .Where(e => Matches(e.Provider, providerFilter) && Matches(e.EventName, eventNameFilter))
            .OrderBy(e => e.Timestamp)
            .Take(topN)
            .ToList();
        var view = new EventCatalogEventsView(snapshot.ProcessId, snapshot.TotalEvents, snapshot.SampleCap, snapshot.Sample.Count, events.Count, events);
        var summary = $"Showing {events.Count} metadata-only occurrence(s) from the bounded sample; payload values are not captured.";

        return DiagnosticResult.Ok(view, summary,
            new NextActionHint("query_snapshot", "Return to the ranked event catalog.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CatalogView }));
    }

    private static DiagnosticResult<object> Box<T>(DiagnosticResult<T> source) where T : class
        => new(source.Summary, source.Hints, source.Error)
        {
            Data = source.Data,
            Handle = source.Handle,
            HandleExpiresAt = source.HandleExpiresAt,
            ResolvedProcess = source.ResolvedProcess,
        };

    private static IEnumerable<EventCatalogEntry> FilterEntries(
        IEnumerable<EventCatalogEntry> entries,
        string? providerFilter,
        string? eventNameFilter)
        => entries.Where(e => Matches(e.Provider, providerFilter) && Matches(e.EventName, eventNameFilter));

    private static bool Matches(string value, string? filter)
        => string.IsNullOrWhiteSpace(filter) || value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("query_snapshot", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
