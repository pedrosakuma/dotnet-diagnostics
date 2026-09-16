using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Two real MCP HTTP endpoints: the orchestrator forwards actual arguments to destination dispatch,
/// whose deterministic event source feeds the same retention state as the EventPipe callback.
/// Only IPC event delivery and Kubernetes transport are substituted; neither endpoint returns canned captures.
/// </summary>
[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class DistributedTraceRetentionProtocolTests
{
    private const string Trace = "abcdef0123456789abcdef0123456789";
    private static readonly string[] Sources = ["test-source"];
    private static readonly string[] HandleIds = ["inv-test"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    public async Task PublicRequest_FanoutDestinationAndJson_PreserveTargetAndRetention(int cap, int dropped)
    {
        await using var destinationFactory = new TraceFactory();
        await using var destination = await ConnectAsync(destinationFactory);
        var proxy = new ForwardingProxy(destination);
        await using var orchestratorFactory = new TraceFactory(proxy);
        await using var orchestrator = await ConnectAsync(orchestratorFactory);
        var result = await orchestrator.CallToolAsync("collect_events", new Dictionary<string, object?>
        {
            ["kind"] = "distributed_trace",
            ["traceId"] = $" {Trace.ToUpperInvariant()} ",
            ["maxActivities"] = 1,
            ["maxMatchedActivities"] = cap,
            ["durationSeconds"] = 1,
            ["sources"] = Sources,
            ["investigationHandleIds"] = HandleIds,
        }, cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBeTrue();
        var envelope = result.StructuredContent!.Value.Deserialize<DiagnosticResult<CollectEventsEnvelope>>(JsonOptions)!;
        envelope.Error.Should().BeNull();
        var timeline = envelope.Data!.DistributedTrace!;
        timeline.TraceId.Should().Be(Trace);
        timeline.SpanCount.Should().Be(cap);
        timeline.Spans.Select(s => s.OperationName).Should().Equal(cap == 2 ? ["parent", "child"] : ["child"]);
        var retention = timeline.Coverage.Single().Retention!;
        retention.Should().Be(new ActivityRetention(Trace, cap, 14, 2, cap, dropped, 12));
        envelope.Summary.Should().Contain($"dropped matching={dropped}");
        proxy.LastRequest!.Arguments!["traceId"].GetString().Should().Be(Trace);
        proxy.LastRequest.Arguments["maxMatchedActivities"].GetInt32().Should().Be(cap);
        destinationFactory.Collector.LastSources.Should().Equal("test-source");
        destinationFactory.Collector.LastMaxActivities.Should().Be(1);
        timeline.Warnings.Any(w => w.Contains("retention truncation", StringComparison.Ordinal)).Should().Be(dropped > 0);

        var destinationCapture = proxy.LastResponse!.StructuredContent!.Value
            .Deserialize<DiagnosticResult<CollectEventsEnvelope>>(JsonOptions)!;
        destinationCapture.Summary.Should().Contain($"dropped matching={dropped}");
        destinationCapture.Summary.Contains("Retention truncation", StringComparison.Ordinal).Should().Be(dropped > 0);
        foreach (var view in new[] { "summary", "bySource", "byOperation", "activities", "trace" })
        {
            var query = await destination.CallToolAsync("query_snapshot", new Dictionary<string, object?>
            {
                ["handle"] = destinationCapture.Handle,
                ["view"] = view,
                ["traceId"] = Trace,
            }, cancellationToken: CancellationToken.None);
            query.IsError.Should().NotBeTrue();
            var payload = query.StructuredContent!.Value.GetProperty("data").GetProperty("payload");
            payload.GetProperty("retention").Deserialize<ActivityRetention>(JsonOptions).Should().Be(retention);
            if (view is "summary" or "bySource" or "byOperation")
            {
                payload.GetProperty("truncated").GetBoolean().Should().Be(dropped > 0);
            }
        }
    }

    [Theory]
    [InlineData("", 1, "traceId")]
    [InlineData("00000000000000000000000000000000", 1, "traceId")]
    [InlineData("abcdef0123456789abcdef0123456789", 0, "maxMatchedActivities")]
    public async Task PublicRequests_RejectInvalidFilterAndBudget(string traceId, int cap, string detail)
    {
        await using var factory = new TraceFactory();
        await using var client = await ConnectAsync(factory);
        foreach (var kind in new[] { "activities", "distributed_trace" })
        {
            var result = await client.CallToolAsync("collect_events", new Dictionary<string, object?>
            {
                ["kind"] = kind, ["traceId"] = traceId, ["maxMatchedActivities"] = cap,
            }, cancellationToken: CancellationToken.None);
            var envelope = result.StructuredContent!.Value.Deserialize<DiagnosticResult<CollectEventsEnvelope>>(JsonOptions)!;
            envelope.Error!.Kind.Should().Be("InvalidArgument");
            envelope.Error.Detail.Should().Be(detail);
        }
        factory.Collector.Calls.Should().Be(0);
    }

    [Fact]
    public async Task LegacyDestinationMetadataRemainsUnknownAfterRealJsonRoundTrip()
    {
        await using var destinationFactory = new TraceFactory();
        await using var destination = await ConnectAsync(destinationFactory);
        await using var orchestratorFactory = new TraceFactory(new ForwardingProxy(destination, omitRetention: true));
        await using var orchestrator = await ConnectAsync(orchestratorFactory);
        var response = await orchestrator.CallToolAsync("collect_events", new Dictionary<string, object?>
        {
            ["kind"] = "distributed_trace", ["traceId"] = Trace,
            ["investigationHandleIds"] = HandleIds,
        }, cancellationToken: CancellationToken.None);
        var envelope = response.StructuredContent!.Value.Deserialize<DiagnosticResult<CollectEventsEnvelope>>(JsonOptions)!;
        var timeline = envelope.Data!.DistributedTrace!;
        timeline.Coverage.Single().Retention.Should().BeNull();
        timeline.Warnings.Should().Contain(w => w.Contains("unknown", StringComparison.Ordinal))
            .And.Contain(w => w.Contains("filtering was not confirmed", StringComparison.Ordinal));
        envelope.Summary.Should().Contain("matching=unknown").And.Contain("dropped matching=unknown");
    }

    [Fact]
    public async Task PublicActivities_DefaultRemainsExploratoryAndTargetDefaultIsIndependent()
    {
        await using var factory = new TraceFactory();
        await using var client = await ConnectAsync(factory);
        foreach (var trace in new string?[] { null, Trace })
        {
            var args = new Dictionary<string, object?> { ["kind"] = "activities", ["maxActivities"] = 1 };
            if (trace is not null) args["traceId"] = trace;
            var result = await client.CallToolAsync("collect_events", args, cancellationToken: CancellationToken.None);
            var capture = result.StructuredContent!.Value.Deserialize<DiagnosticResult<CollectEventsEnvelope>>(JsonOptions)!.Data!.Activities!;
            capture.Retention!.EffectiveCap.Should().Be(trace is null ? 1 : 200);
            capture.Activities.Select(a => a.OperationName).Should().Equal(trace is null ? ["noise"] : ["child", "parent"]);
        }
    }

    private static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TraceFactory.Token);
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true), cancellationToken: CancellationToken.None);
    }

    private sealed class TraceFactory(ForwardingProxy? proxy = null) : WebApplicationFactory<Program>
    {
        internal const string Token = "trace-retention-test-token";
        internal EventStreamCollector Collector { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Auth:BearerTokens:0:Name", "trace-test");
            builder.UseSetting("Auth:BearerTokens:0:Token", Token);
            builder.UseSetting("Auth:BearerTokens:0:Scopes:0", "root");
            builder.UseSetting("Orchestrator:Enabled", proxy is null ? "false" : "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IActivityCollector>();
                services.AddSingleton<IActivityCollector>(Collector);
                services.RemoveAll<IProcessContextResolver>();
                services.AddSingleton<IProcessContextResolver, Resolver>();
                if (proxy is not null)
                {
                    var store = new MemoryInvestigationStore();
                    store.Add(new InvestigationHandle("inv-test",
                        new KubernetesInvestigationTarget("test", "pod", "app", "sidecar", "pod-test-token"),
                        InvestigationState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10),
                        OwnerBearerName: "trace-test",
                        OwnerPrincipalKey: PrincipalOwnershipKey.ForOpaqueEntry("Auth:BearerTokens:0")));
                    services.RemoveAll<IInvestigationStore>();
                    services.AddSingleton<IInvestigationStore>(store);
                    services.RemoveAll<IInvestigationProxyClient>();
                    services.AddSingleton<IInvestigationProxyClient>(proxy);
                }
            });
        }
    }

    private sealed class ForwardingProxy(McpClient destination, bool omitRetention = false) : IInvestigationProxyClient
    {
        internal CallToolRequestParams? LastRequest { get; private set; }
        internal CallToolResult? LastResponse { get; private set; }
        public async Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastResponse = await destination.CallToolAsync(request.Name,
                request.Arguments!.ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
                cancellationToken: cancellationToken);
            if (omitRetention)
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(LastResponse.StructuredContent!.Value.GetRawText())!;
                json["data"]!["activities"]!.AsObject().Remove("retention");
                LastResponse.StructuredContent = JsonSerializer.SerializeToElement(json);
            }
            return LastResponse;
        }
        public Task EnsureInitializedAsync(InvestigationHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisposeForHandleAsync(string handleId) => Task.CompletedTask;
    }

    private sealed class Resolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(new ProcessContext(1234, RuntimeFlavor.CoreClr, true, true, true), null));
    }

    private sealed class EventStreamCollector : IActivityCollector
    {
        internal IReadOnlyList<string>? LastSources { get; private set; }
        internal int LastMaxActivities { get; private set; }
        internal int Calls { get; private set; }
        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources = null,
            int maxActivities = 200, CancellationToken cancellationToken = default)
            => CollectAsync(processId, duration, sources, maxActivities, null, 200, cancellationToken);

        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources,
            int maxActivities, string? traceId, int maxMatchedActivities, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastSources = sources;
            LastMaxActivities = maxActivities;
            var state = new ActivityRetentionState(maxActivities, traceId, maxMatchedActivities);
            var start = DateTimeOffset.UnixEpoch;
            var noise = new CapturedActivity("test-source", "noise", "noise", null, "ffffffffffffffffffffffffffffffff",
                "ffffffffffffffff", null, start, start.AddMilliseconds(1), TimeSpan.FromMilliseconds(1), new Dictionary<string, string>());
            for (var i = 0; i < 12; i++) state.Observe(noise);
            state.Observe(noise with { OperationName = "child", TraceId = Trace, SpanId = "2222222222222222",
                ParentSpanId = "1111111111111111", StartedAt = start.AddMilliseconds(1),
                StoppedAt = start.AddMilliseconds(9), Duration = TimeSpan.FromMilliseconds(8) });
            state.Observe(noise with { OperationName = "parent", TraceId = Trace, SpanId = "1111111111111111",
                StoppedAt = start.AddMilliseconds(10), Duration = TimeSpan.FromMilliseconds(10) });
            return Task.FromResult(new ActivityCapture(processId, sources, start, duration,
                state.ObservedActivities, state.ObservedActivities, state.Activities, [], [], state.Retention));
        }
    }
}
