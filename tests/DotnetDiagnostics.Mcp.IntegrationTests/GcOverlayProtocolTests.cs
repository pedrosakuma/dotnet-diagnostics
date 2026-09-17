using System.Net.Http.Headers;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class GcOverlayProtocolTests
{
    [Fact]
    public async Task CompactBatch_PreservesUnavailableMeasurementAndFinalTransportLoss()
    {
        await using var factory = new Factory(unreliableGc: true);
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "gc-overlay-test-token");
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http), cancellationToken: CancellationToken.None);
        var response = await client.CallToolAsync("collect_batch", new Dictionary<string, object?>
        {
            ["processId"] = 1234,
            ["requests"] = new[] { new { tool = "collect_events", kind = "gc" } },
            ["depth"] = "compact",
            ["durationSeconds"] = 1,
        }, cancellationToken: CancellationToken.None);
        response.IsError.Should().NotBeTrue();
        var entry = response.StructuredContent!.Value.GetProperty("data").GetProperty("results")[0];
        entry.GetProperty("summary").GetString().Should().Contain("GC suspension unavailable (unreliable)")
            .And.Contain("transport-events-lost=3");
        entry.GetProperty("handle").GetString().Should().NotBeNullOrWhiteSpace();
        if (entry.TryGetProperty("data", out var data)) data.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task PublicMcp_GcHandleSelectsActualCoreAttribution_AndOmissionFails()
    {
        await using var factory = new Factory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "gc-overlay-test-token");
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http), cancellationToken: CancellationToken.None);
        var store = factory.Services.GetRequiredService<IDiagnosticHandleStore>();
        var at = DateTimeOffset.UnixEpoch;
        var capture = new ActivityCapture(Environment.ProcessId, null, at, TimeSpan.FromSeconds(1), 100, 100,
            [new("test", "request", "a", null, null, null, null, at, at.AddMilliseconds(100),
                TimeSpan.FromMilliseconds(100), new Dictionary<string, string>())], [], [],
            new(null, 1, 100, 100, 1, 99, 0));
        var activity = store.Register(Environment.ProcessId, CollectionHandleKinds.Activities, capture, TimeSpan.FromMinutes(10));
        var args = new Dictionary<string, object?> { ["handle"] = activity.Id, ["view"] = "gc-overlay" };
        var missing = await client.CallToolAsync("query_snapshot", args, cancellationToken: CancellationToken.None);
        missing.StructuredContent!.Value.GetProperty("error").GetProperty("kind").GetString().Should().Be("InvalidArgument");
        foreach (var milliseconds in new[] { 20, 40 })
        {
            var state = new GcCaptureState(10);
            state.SuspendBegin(1, 1, 1, at, 6, 99);
            state.Boundary(1, 1, 1, at.AddMilliseconds(10), 0);
            state.Boundary(1, 1, 1, at.AddMilliseconds(10 + milliseconds), 1);
            state.Boundary(1, 1, 1, at.AddMilliseconds(60), 2);
            var gc = new GcSummary(Environment.ProcessId, at, TimeSpan.FromSeconds(1), 0,
                TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(900), [], [],
                Suspension: state.Finish(at, at.AddSeconds(1), null));
            args["gcHandle"] = store.Register(Environment.ProcessId, CollectionHandleKinds.GcEvents, gc, TimeSpan.FromMinutes(10)).Id;
            var response = await client.CallToolAsync("query_snapshot", args, cancellationToken: CancellationToken.None);
            response.IsError.Should().NotBeTrue();
            var payload = response.StructuredContent!.Value.GetProperty("data").GetProperty("payload");
            payload.GetProperty("measurementStatus").GetString().Should().Be("no-detected-loss");
            payload.GetProperty("candidateSelection").GetString().Should().Be("incomplete");
            payload.GetProperty("impactedActivities")[0].GetProperty("gcPauseMs").GetDouble().Should().Be(milliseconds);
            payload.GetProperty("impactedActivities")[0].GetProperty("gcPauseIsLowerBound").GetBoolean().Should().BeFalse();
        }
    }

    private sealed class Factory(bool unreliableGc = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Auth:BearerTokens:0:Name", "gc-test");
            builder.UseSetting("Auth:BearerTokens:0:Token", "gc-overlay-test-token");
            builder.UseSetting("Auth:BearerTokens:0:Scopes:0", "root");
            builder.UseSetting("Orchestrator:Enabled", "false");
            if (unreliableGc)
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IGcCollector>();
                    services.AddSingleton<IGcCollector, LostEventCollector>();
                    services.RemoveAll<IProcessContextResolver>();
                    services.AddSingleton<IProcessContextResolver, Resolver>();
                });
        }
    }

    private sealed class Resolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(new ProcessContext(1234, RuntimeFlavor.CoreClr, true, true, true), null));
    }

    private sealed class LostEventCollector : IGcCollector
    {
        public Task<GcSummary> CollectAsync(int processId, TimeSpan duration, int maxEvents = 200, CancellationToken cancellationToken = default)
        {
            var at = DateTimeOffset.UnixEpoch;
            var state = new GcCaptureState(1);
            state.CollectionBegin(1, 1, 2, at, 2, "test", "BackgroundGC");
            state.SuspendBegin(1, 1, 1, at, 6, 99);
            state.Boundary(1, 1, 1, at.AddMilliseconds(10), 0);
            state.Boundary(1, 1, 1, at.AddMilliseconds(20), 1);
            state.Boundary(1, 1, 1, at.AddMilliseconds(30), 2);
            state.CollectionEnd(1, 1, 1, at.AddMilliseconds(900));
            return Task.FromResult(new GcSummary(processId, at, duration, 1, state.Collections.TotalPauseTime,
                state.Collections.MaxPauseTime, state.Collections.Generations, state.Collections.Events,
                Suspension: state.Finish(at, at + duration, null, eventsLost: 3)));
        }
    }
}
