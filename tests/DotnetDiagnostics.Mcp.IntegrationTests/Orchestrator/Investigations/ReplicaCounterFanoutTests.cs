using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator;

/// <summary>
/// Fan-out tests for <see cref="ReplicaCounterFanout"/> (Wave B2, issue #448). Use an in-memory
/// store plus a stub <see cref="IInvestigationProxyClient"/> returning canned
/// <c>collect_events(kind="counters")</c> envelopes, so the simultaneous fan-out + dispersion is
/// exercised without a real Kubernetes port-forward (no KindIntegration gating).
/// </summary>
public sealed class ReplicaCounterFanoutTests
{
    [Fact]
    public async Task CompareAsync_IdentifiesOutlierAcrossThreePods()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-a", "pod-a"));
        store.Add(ActiveHandle("inv-b", "pod-b"));
        store.Add(ActiveHandle("inv-c", "pod-c"));

        var proxy = new StubProxyClient
        {
            ["pod-a"] = CountersResult(cpu: 30, heap: 100, queue: 0, pid: 1, "pod-a"),
            ["pod-b"] = CountersResult(cpu: 31, heap: 105, queue: 0, pid: 2, "pod-b"),
            ["pod-c"] = CountersResult(cpu: 95, heap: 900, queue: 40, pid: 3, "pod-c"),
        };

        var fanout = await ReplicaCounterFanout.CompareAsync(
            store, proxy, callerSessionId: null, durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(3);
        fanout.PodErrors.Should().BeEmpty();
        fanout.Skew.Should().NotBeNull();
        fanout.Skew!.PodCount.Should().Be(3);
        fanout.Skew.OutlierPod.Should().Be("pod-c");
    }

    [Fact]
    public async Task CompareAsync_IsolatesPerPodFailures()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-good", "good"));
        store.Add(ActiveHandle("inv-bad", "bad"));

        var proxy = new StubProxyClient { ["good"] = CountersResult(50, 200, 2, 1, "good") };
        proxy.Throw["bad"] = new InvalidOperationException("port-forward died");

        var fanout = await ReplicaCounterFanout.CompareAsync(
            store, proxy, callerSessionId: null, durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(2);
        fanout.Skew.Should().NotBeNull();
        fanout.Skew!.Replicas.Should().ContainSingle();
        fanout.PodErrors.Should().ContainSingle().Which.Should().Contain("bad").And.Contain("port-forward died");
    }

    [Fact]
    public async Task CompareAsync_AllPodsFail_ReturnsNullSkewWithErrors()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-a", "pod-a"));
        store.Add(ActiveHandle("inv-b", "pod-b"));

        var proxy = new StubProxyClient();
        proxy.Throw["pod-a"] = new InvalidOperationException("died A");
        proxy.Throw["pod-b"] = new InvalidOperationException("died B");

        var fanout = await ReplicaCounterFanout.CompareAsync(
            store, proxy, callerSessionId: null, durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(2);
        fanout.Skew.Should().BeNull();
        fanout.PodErrors.Should().HaveCount(2);
    }

    [Fact]
    public async Task CompareAsync_SkipsNonActiveAndUnownedScoping()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-mine", "mine", ownerSessionId: "session-A"));
        store.Add(ActiveHandle("inv-theirs", "theirs", ownerSessionId: "session-B"));
        store.Add(ActiveHandle("inv-attaching", "attaching", ownerSessionId: "session-A") with
        {
            State = InvestigationState.Attaching,
        });

        var proxy = new StubProxyClient { ["mine"] = CountersResult(40, 40, 0, 1, "mine") };

        var fanout = await ReplicaCounterFanout.CompareAsync(
            store, proxy, callerSessionId: "session-A", durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(1);
        proxy.Calls.Should().ContainSingle().Which.Should().Be("mine");
    }

    private static InvestigationHandle ActiveHandle(string handleId, string podName, string? ownerSessionId = null) => new(
        HandleId: handleId,
        Namespace: "ns",
        PodName: podName,
        TargetContainerName: "api",
        EphemeralContainerName: "diag",
        PodLocalBearerToken: "pod-bearer",
        State: InvestigationState.Active,
        AttachedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
        OwnerSessionId: ownerSessionId);

    private static CallToolResult CountersResult(double cpu, double heap, double queue, int pid, string podName)
    {
        var snapshot = new CounterSnapshot(
            ProcessId: pid,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            Counters: new[]
            {
                new CounterValue("System.Runtime", "cpu-usage", "CPU", cpu, CounterKind.Mean, "%"),
                new CounterValue("System.Runtime", "gc-heap-size", "Heap", heap, CounterKind.Mean, "MB"),
                new CounterValue("System.Runtime", "threadpool-queue-length", "Q", queue, CounterKind.Mean),
            },
            Meters: Array.Empty<MeterInstrumentValue>(),
            Notes: Array.Empty<string>());
        var envelope = new CollectEventsEnvelope("counters", Counters: snapshot);
        var result = DiagnosticResult.Ok(envelope, $"collected on {podName}");
        var json = JsonSerializer.Serialize(result, SerializeOptions);
        return new CallToolResult { StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
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

        public CallToolResult this[string podName] { set => _byPod[podName] = value; }

        public Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add(handle.PodName);
            }

            if (Throw.TryGetValue(handle.PodName, out var ex))
            {
                return Task.FromException<CallToolResult>(ex);
            }

            return Task.FromResult(_byPod[handle.PodName]);
        }

        public Task DisposeForHandleAsync(string handleId) => Task.CompletedTask;
    }
}
