using DotnetDiagnosticsMcp.Core.EventSources;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class EventCatalogQueryDispatcherTests
{
    private const string Handle = "catalog-1";

    [Fact]
    public void RenderCatalog_Default_RanksByCount()
    {
        var result = EventCatalogQueryDispatcher.RenderCatalog(Snapshot(), Handle, topN: 10);

        result.Error.Should().BeNull();
        result.Data!.Entries.Select(e => e.EventName).Should().Equal("RequestStart", "RequestStop", "GcStart", "TaskScheduled");
        result.Data.TotalEvents.Should().Be(11);
        result.Data.DistinctEventTypes.Should().Be(4);
    }

    [Fact]
    public void RenderCatalog_AppliesProviderFilter()
    {
        var result = EventCatalogQueryDispatcher.RenderCatalog(Snapshot(), Handle, topN: 10, providerFilter: "aspnetcore");

        result.Data!.Entries.Should().OnlyContain(e => e.Provider.Contains("AspNetCore", StringComparison.Ordinal));
        result.Data.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void RenderCatalog_AppliesEventNameFilter()
    {
        var result = EventCatalogQueryDispatcher.RenderCatalog(Snapshot(), Handle, topN: 10, eventNameFilter: "start");

        result.Data!.Entries.Select(e => e.EventName).Should().Equal("RequestStart", "GcStart");
    }

    [Fact]
    public void RenderCatalog_AppliesTopNCap()
    {
        var result = EventCatalogQueryDispatcher.RenderCatalog(Snapshot(), Handle, topN: 2);

        result.Data!.Returned.Should().Be(2);
        result.Data.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void RenderByProvider_RollsUpCounts()
    {
        var result = EventCatalogQueryDispatcher.RenderByProvider(Snapshot(), Handle, topN: 10);

        result.Error.Should().BeNull();
        result.Data!.Entries.Should().ContainSingle(e =>
            e.Provider == "Microsoft.AspNetCore.Hosting" && e.DistinctEventTypes == 2 && e.TotalCount == 8);
        result.Data.Entries[0].Provider.Should().Be("Microsoft.AspNetCore.Hosting");
    }

    [Fact]
    public void RenderEvents_ReturnsBoundedMetadataSample_WithFilters()
    {
        var result = EventCatalogQueryDispatcher.RenderEvents(Snapshot(), Handle, topN: 2, providerFilter: "runtime", eventNameFilter: "gc");

        result.Error.Should().BeNull();
        result.Data!.Events.Should().HaveCount(1);
        result.Data.Events[0].Provider.Should().Be("Microsoft-Windows-DotNETRuntime");
        result.Data.Events[0].EventName.Should().Be("GcStart");
    }

    [Fact]
    public void EmptySnapshot_ReturnsEmptyViews()
    {
        var snapshot = new EventCatalogSnapshot(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(1),
            Array.Empty<string>(),
            0,
            0,
            Array.Empty<EventCatalogEntry>(),
            10,
            Array.Empty<CatalogEventOccurrence>());

        EventCatalogQueryDispatcher.RenderCatalog(snapshot, Handle, topN: 10).Data!.Entries.Should().BeEmpty();
        EventCatalogQueryDispatcher.RenderByProvider(snapshot, Handle, topN: 10).Data!.Entries.Should().BeEmpty();
        EventCatalogQueryDispatcher.RenderEvents(snapshot, Handle, topN: 10).Data!.Events.Should().BeEmpty();
    }

    [Fact]
    public void Render_UnknownView_ReturnsInvalidArgument()
    {
        var result = EventCatalogQueryDispatcher.Render(Snapshot(), Handle, "nonsense", topN: 10);

        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Summary.Should().Contain("Unknown event-catalog view");
    }

    [Fact]
    public void Render_TopNBelowOne_ReturnsInvalidArgument()
    {
        var result = EventCatalogQueryDispatcher.Render(Snapshot(), Handle, "catalog", topN: 0);

        result.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void SessionViews_EnumeratesSupportedViews()
    {
        EventCatalogQueryDispatcher.SessionViews.Should().Equal("catalog", "byProvider", "events");
        EventCatalogQueryDispatcher.IsKnownView("byProvider").Should().BeTrue();
        EventCatalogQueryDispatcher.IsKnownView("summary").Should().BeFalse();
    }

    private static EventCatalogSnapshot Snapshot()
    {
        var at = DateTimeOffset.UnixEpoch;
        var catalog = new[]
        {
            new EventCatalogEntry("Microsoft.AspNetCore.Hosting", "RequestStart", "Informational", 5),
            new EventCatalogEntry("Microsoft.AspNetCore.Hosting", "RequestStop", "Informational", 3),
            new EventCatalogEntry("Microsoft-Windows-DotNETRuntime", "GcStart", "Informational", 2),
            new EventCatalogEntry("System.Threading.Tasks.TplEventSource", "TaskScheduled", "Informational", 1),
        };
        var sample = new[]
        {
            new CatalogEventOccurrence(at.AddMilliseconds(1), "Microsoft.AspNetCore.Hosting", "RequestStart", "Informational"),
            new CatalogEventOccurrence(at.AddMilliseconds(2), "Microsoft-Windows-DotNETRuntime", "GcStart", "Informational"),
            new CatalogEventOccurrence(at.AddMilliseconds(3), "Microsoft.AspNetCore.Hosting", "RequestStop", "Informational"),
        };

        return new EventCatalogSnapshot(
            123,
            at,
            TimeSpan.FromSeconds(5),
            new[] { "Microsoft.AspNetCore.Hosting", "Microsoft-Windows-DotNETRuntime", "System.Threading.Tasks.TplEventSource" },
            11,
            catalog.Length,
            catalog,
            10,
            sample);
    }
}
