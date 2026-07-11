using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator;

/// <summary>
/// Fan-out tests for <see cref="DistributedTraceCorrelator"/> (Phase 13 / G3, issue #437). These
/// use an in-memory store plus a stub <see cref="IInvestigationProxyClient"/> that returns canned
/// <c>collect_events(kind="activities")</c> envelopes, so the orchestrator fan-out + stitching is
/// exercised end-to-end without a real Kubernetes port-forward (no KindIntegration gating).
/// </summary>
public sealed class DistributedTraceCorrelatorTests
{
    private const string TraceId = "0af7651916cd43dd8448eb211c80319c";

    [Fact]
    public async Task CorrelateAsync_StitchesSpansAcrossTwoAttachedPods()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-frontend", "frontend"));
        store.Add(ActiveHandle("inv-backend", "backend"));

        var start = DateTimeOffset.UtcNow;
        var proxy = new StubProxyClient
        {
            ["frontend"] = ActivitiesResult(CaptureWith(
                Span("frontend-source", "GET /checkout", spanId: "1111111111111111", parentSpanId: null,
                     start, TimeSpan.FromMilliseconds(120))),
            "frontend"),
            ["backend"] = ActivitiesResult(CaptureWith(
                Span("backend-source", "DB query", spanId: "2222222222222222", parentSpanId: "1111111111111111",
                     start.AddMilliseconds(10), TimeSpan.FromMilliseconds(90))),
            "backend"),
        };

        var fanout = await DistributedTraceCorrelator.CorrelateAsync(
            store, proxy, callerBearerName: null, investigationHandleIds: null, TraceId, durationSeconds: 5, maxActivities: 100,
            sources: null, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(2);
        fanout.PodErrors.Should().BeEmpty();
        fanout.Timeline.Should().NotBeNull();

        var timeline = fanout.Timeline!;
        timeline.TraceId.Should().Be(TraceId);
        timeline.SpanCount.Should().Be(2);
        timeline.Spans.Select(s => s.PodName).Should().BeEquivalentTo(new[] { "frontend", "backend" });

        // The backend child span resolves its parent on the frontend Pod — cross-Pod stitching worked.
        var backend = timeline.Spans.Single(s => s.PodName == "backend");
        backend.ParentResolved.Should().BeTrue();
        backend.ParentSpanId.Should().Be("1111111111111111");

        // Frontend self-time = 120ms total − 90ms attributed to its backend child = 30ms, so the
        // slowest *hop* (self time) is the backend DB query, not the wrapping frontend span.
        timeline.SlowestHop.Should().NotBeNull();
        timeline.SlowestHop!.PodName.Should().Be("backend");
    }

    [Fact]
    public async Task CorrelateAsync_IsolatesPerPodFailures()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-good", "good"));
        store.Add(ActiveHandle("inv-bad", "bad"));

        var start = DateTimeOffset.UtcNow;
        var proxy = new StubProxyClient
        {
            ["good"] = ActivitiesResult(CaptureWith(
                Span("svc", "op", spanId: "aaaaaaaaaaaaaaaa", parentSpanId: null, start, TimeSpan.FromMilliseconds(40))),
            "good"),
        };
        proxy.Throw["bad"] = new InvalidOperationException("port-forward died");

        var fanout = await DistributedTraceCorrelator.CorrelateAsync(
            store, proxy, callerBearerName: null, investigationHandleIds: null, TraceId, durationSeconds: 5, maxActivities: 100,
            sources: null, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(2);
        fanout.Timeline.Should().NotBeNull();
        fanout.Timeline!.SpanCount.Should().Be(1);
        fanout.PodErrors.Should().ContainSingle()
            .Which.Should().Contain("bad").And.Contain("port-forward died");
    }

    [Fact]
    public async Task CorrelateAsync_AllPodsFail_ReturnsNullTimelineWithErrors()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-a", "pod-a"));
        store.Add(ActiveHandle("inv-b", "pod-b"));

        var proxy = new StubProxyClient();
        proxy.Throw["pod-a"] = new InvalidOperationException("forward died A");
        proxy.Throw["pod-b"] = new InvalidOperationException("forward died B");

        var fanout = await DistributedTraceCorrelator.CorrelateAsync(
            store, proxy, callerBearerName: null, investigationHandleIds: null, TraceId, durationSeconds: 5, maxActivities: 100,
            sources: null, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(2);
        fanout.Timeline.Should().BeNull();
        fanout.PodErrors.Should().HaveCount(2);
        fanout.PodErrors.Should().Contain(e => e.Contains("forward died A"))
            .And.Contain(e => e.Contains("forward died B"));
    }

    [Fact]
    public async Task CorrelateAsync_SkipsNonActiveAndUnownedScoping()    {
        var store = new MemoryInvestigationStore();
        // Active + owned by the calling session.
        store.Add(ActiveHandle("inv-mine", "mine", ownerBearerName: "session-A"));
        // Active but owned by a different session — must be skipped.
        store.Add(ActiveHandle("inv-theirs", "theirs", ownerBearerName: "session-B"));
        // Owned by my session but not Active — must be skipped.
        store.Add(ActiveHandle("inv-attaching", "attaching", ownerBearerName: "session-A") with
        {
            State = InvestigationState.Attaching,
        });

        var start = DateTimeOffset.UtcNow;
        var proxy = new StubProxyClient
        {
            ["mine"] = ActivitiesResult(CaptureWith(
                Span("svc", "op", spanId: "aaaaaaaaaaaaaaaa", parentSpanId: null, start, TimeSpan.FromMilliseconds(40))),
            "mine"),
        };

        var fanout = await DistributedTraceCorrelator.CorrelateAsync(
            store, proxy, callerBearerName: "session-A", investigationHandleIds: null, TraceId, durationSeconds: 5, maxActivities: 100,
            sources: null, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(1);
        proxy.Calls.Should().ContainSingle().Which.Should().Be("mine");
    }

    [Fact]
    public async Task CorrelateAsync_UsesExplicitHandleIdsInsteadOfCallerWideDiscovery()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-a", "pod-a", ownerBearerName: "bearer-A"));
        store.Add(ActiveHandle("inv-b", "pod-b", ownerBearerName: "bearer-A"));

        var start = DateTimeOffset.UtcNow;
        var proxy = new StubProxyClient
        {
            ["pod-b"] = ActivitiesResult(CaptureWith(
                Span("svc", "op", spanId: "aaaaaaaaaaaaaaaa", parentSpanId: null, start, TimeSpan.FromMilliseconds(40))),
            "pod-b"),
        };

        var fanout = await DistributedTraceCorrelator.CorrelateAsync(
            store, proxy, callerBearerName: "bearer-A", investigationHandleIds: new[] { "inv-b" }, TraceId, durationSeconds: 5, maxActivities: 100,
            sources: null, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(1);
        proxy.Calls.Should().ContainSingle().Which.Should().Be("pod-b");
    }

    [Fact]
    public async Task CorrelateAsync_ExplicitEmptyHandleList_DoesNotFallBackToCallerWideDiscovery()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-a", "pod-a", ownerBearerName: "bearer-A"));

        var fanout = await DistributedTraceCorrelator.CorrelateAsync(
            store, new StubProxyClient(), callerBearerName: "bearer-A", investigationHandleIds: Array.Empty<string>(), TraceId,
            durationSeconds: 5, maxActivities: 100, sources: null, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(0);
        fanout.PodErrors.Should().BeEmpty();
        fanout.Timeline.Should().BeNull();
    }

    [Fact]
    public async Task CorrelateAsync_FansOutToPodsConcurrently()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-a", "pod-a"));
        store.Add(ActiveHandle("inv-b", "pod-b"));

        using var release = new SemaphoreSlim(0, 2);
        var entered = 0;
        var maxConcurrent = 0;
        var start = DateTimeOffset.UtcNow;
        var proxy = new StubProxyClient
        {
            Gate = async cancellationToken =>
            {
                var concurrent = Interlocked.Increment(ref entered);
                while (true)
                {
                    var snapshot = Volatile.Read(ref maxConcurrent);
                    if (concurrent <= snapshot ||
                        Interlocked.CompareExchange(ref maxConcurrent, concurrent, snapshot) == snapshot)
                    {
                        break;
                    }
                }

                await release.WaitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Decrement(ref entered);
            },
            ["pod-a"] = ActivitiesResult(CaptureWith(
                Span("svc", "op-a", spanId: "aaaaaaaaaaaaaaaa", parentSpanId: null, start, TimeSpan.FromMilliseconds(10))),
            "pod-a"),
            ["pod-b"] = ActivitiesResult(CaptureWith(
                Span("svc", "op-b", spanId: "bbbbbbbbbbbbbbbb", parentSpanId: null, start, TimeSpan.FromMilliseconds(10))),
            "pod-b"),
        };

        var fanoutTask = DistributedTraceCorrelator.CorrelateAsync(
            store, proxy, callerBearerName: null, investigationHandleIds: null, TraceId, durationSeconds: 5, maxActivities: 100,
            sources: null, CancellationToken.None);

        await Task.Delay(50);
        maxConcurrent.Should().BeGreaterThan(1);
        release.Release(2);

        var fanout = await fanoutTask;
        fanout.PodErrors.Should().BeEmpty();
    }

    private static InvestigationHandle ActiveHandle(string handleId, string podName, string? ownerBearerName = null) => new(
        HandleId: handleId,
        Namespace: "ns",
        PodName: podName,
        TargetContainerName: "api",
        EphemeralContainerName: "diag",
        PodLocalBearerToken: "pod-bearer",
        State: InvestigationState.Active,
        AttachedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
        OwnerBearerName: ownerBearerName);

    private static CapturedActivity Span(
        string source, string operation, string spanId, string? parentSpanId,
        DateTimeOffset startedAt, TimeSpan duration) => new(
        SourceName: source,
        OperationName: operation,
        Id: spanId,
        ParentId: parentSpanId,
        TraceId: TraceId,
        SpanId: spanId,
        ParentSpanId: parentSpanId,
        StartedAt: startedAt,
        StoppedAt: startedAt + duration,
        Duration: duration,
        Tags: new Dictionary<string, string>());

    private static ActivityCapture CaptureWith(params CapturedActivity[] activities) => new(
        ProcessId: 1234,
        SourceFilters: null,
        StartedAt: DateTimeOffset.UtcNow,
        Duration: TimeSpan.FromSeconds(5),
        TotalActivities: activities.Length,
        CompletedActivities: activities.Length,
        Activities: activities,
        BySource: Array.Empty<ActivitySourceSummary>(),
        ByOperation: Array.Empty<ActivityOperationSummary>());

    private static CallToolResult ActivitiesResult(ActivityCapture capture, string podName)
    {
        var envelope = new CollectEventsEnvelope("activities", Activities: capture);
        var result = DiagnosticResult.Ok(envelope, $"collected on {podName}");
        var json = JsonSerializer.Serialize(result, SerializeOptions);
        return new CallToolResult
        {
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(json),
        };
    }

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class StubProxyClient : IInvestigationProxyClient
    {
        private readonly Dictionary<string, CallToolResult> _byPod = new(StringComparer.Ordinal);

        public Dictionary<string, Exception> Throw { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = new();
        public Func<CancellationToken, Task>? Gate { get; init; }

        public CallToolResult this[string podName]
        {
            set => _byPod[podName] = value;
        }

        public async Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
        {
            Calls.Add(handle.PodName);
            if (Gate is not null)
            {
                await Gate(cancellationToken);
            }

            if (Throw.TryGetValue(handle.PodName, out var ex))
            {
                throw ex;
            }

            return _byPod[handle.PodName];
        }

        public Task DisposeForHandleAsync(string handleId) => Task.CompletedTask;
    }
}
