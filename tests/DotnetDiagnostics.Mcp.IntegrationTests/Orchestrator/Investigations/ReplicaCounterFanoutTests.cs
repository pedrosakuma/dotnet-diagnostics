using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
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
            store, proxy, callerPrincipal: null, investigationHandleIds: null, durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

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
            store, proxy, callerPrincipal: null, investigationHandleIds: null, durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

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
            store, proxy, callerPrincipal: null, investigationHandleIds: null, durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(2);
        fanout.Skew.Should().BeNull();
        fanout.PodErrors.Should().HaveCount(2);
    }

    [Fact]
    public async Task CompareAsync_SkipsNonActiveAndUnownedScoping()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-mine", "mine", ownerBearerName: "session-A"));
        store.Add(ActiveHandle("inv-theirs", "theirs", ownerBearerName: "session-B"));
        store.Add(ActiveHandle("inv-attaching", "attaching", ownerBearerName: "session-A") with
        {
            State = InvestigationState.Attaching,
        });

        var proxy = new StubProxyClient { ["mine"] = CountersResult(40, 40, 0, 1, "mine") };

        var fanout = await ReplicaCounterFanout.CompareAsync(
            store, proxy, callerPrincipal: Principal("session-A"), investigationHandleIds: null, durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(1);
        proxy.Calls.Should().ContainSingle().Which.Should().Be("mine");
    }

    [Fact]
    public async Task CompareAsync_UsesExplicitHandleIdsInsteadOfCallerWideDiscovery()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-a", "pod-a", ownerBearerName: "bearer-A"));
        store.Add(ActiveHandle("inv-b", "pod-b", ownerBearerName: "bearer-A"));

        var proxy = new StubProxyClient
        {
            ["pod-b"] = CountersResult(50, 200, 2, 1, "pod-b"),
        };

        var fanout = await ReplicaCounterFanout.CompareAsync(
            store, proxy, callerPrincipal: Principal("bearer-A"), investigationHandleIds: new[] { "inv-b" }, durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(1);
        proxy.Calls.Should().ContainSingle().Which.Should().Be("pod-b");
    }

    [Fact]
    public async Task CompareAsync_ResolvesStoredSelectorsAndPassesPodLocalProcessIds()
    {
        var selector = new InvestigationProcessSelector(ManagedEntrypointAssemblyName: "CoreClrSample");
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-a", "pod-a", processSelector: selector));
        store.Add(ActiveHandle("inv-b", "pod-b", processSelector: selector));

        var proxy = new StubProxyClient
        {
            ["pod-a"] = CountersResult(30, 100, 0, 101, "pod-a"),
            ["pod-b"] = CountersResult(31, 105, 0, 207, "pod-b"),
        };
        proxy.ProcessLists["pod-a"] = ProcessListResult(
            Process(1, "DotnetDiagnostics.Mcp", "dotnet DotnetDiagnostics.Mcp.dll"),
            Process(101, "CoreClrSample", "dotnet CoreClrSample.dll --p6-target=a"));
        proxy.ProcessLists["pod-b"] = ProcessListResult(
            Process(2, "DotnetDiagnostics.Mcp", "dotnet DotnetDiagnostics.Mcp.dll"),
            Process(207, "CoreClrSample", "dotnet CoreClrSample.dll --p6-target=b"));

        var fanout = await ReplicaCounterFanout.CompareAsync(
            store, proxy, callerPrincipal: null, investigationHandleIds: null,
            durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

        fanout.PodErrors.Should().BeEmpty();
        fanout.Skew!.Replicas.Should().Contain(r => r.PodName == "pod-a" && r.ProcessId == 101);
        fanout.Skew.Replicas.Should().Contain(r => r.PodName == "pod-b" && r.ProcessId == 207);
        proxy.CounterProcessIds.Should().BeEquivalentTo(
            new Dictionary<string, int> { ["pod-a"] = 101, ["pod-b"] = 207 });
    }

    [Fact]
    public async Task CompareAsync_AmbiguousSelectorIsIsolatedAsPerPodError()
    {
        var selector = new InvestigationProcessSelector(ManagedEntrypointAssemblyName: "Worker");
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-good", "good", processSelector: selector));
        store.Add(ActiveHandle("inv-ambiguous", "ambiguous", processSelector: selector));

        var proxy = new StubProxyClient { ["good"] = CountersResult(50, 200, 2, 41, "good") };
        proxy.ProcessLists["good"] = ProcessListResult(Process(41, "Worker", "dotnet Worker.dll --slot=one"));
        proxy.ProcessLists["ambiguous"] = ProcessListResult(
            Process(51, "Worker", "dotnet Worker.dll --slot=one"),
            Process(52, "Worker", "dotnet Worker.dll --slot=two"));

        var fanout = await ReplicaCounterFanout.CompareAsync(
            store, proxy, callerPrincipal: null, investigationHandleIds: null,
            durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

        fanout.Skew!.Replicas.Should().ContainSingle(r => r.PodName == "good");
        fanout.PodErrors.Should().ContainSingle()
            .Which.Should().Contain("ambiguous").And.Contain("PIDs 51, 52");
        proxy.Calls.Should().ContainSingle().Which.Should().Be("good");
    }

    [Fact]
    public async Task CompareAsync_WaitsForAllResolutionsBeforeCollectingAndKeepsPartialFailures()
    {
        var selector = new InvestigationProcessSelector(ManagedEntrypointAssemblyName: "Worker");
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-fast", "fast", processSelector: selector));
        store.Add(ActiveHandle("inv-slow", "slow", processSelector: selector));
        store.Add(ActiveHandle("inv-bad", "bad", processSelector: selector));

        var proxy = new StubProxyClient();
        proxy.ProcessLists["fast"] = ProcessListResult(Process(11, "Worker", "dotnet Worker.dll --slot=fast"));
        proxy.ProcessLists["bad"] = ProcessListResult(
            Process(31, "Worker", "dotnet Worker.dll --slot=one"),
            Process(32, "Worker", "dotnet Worker.dll --slot=two"));

        var slowResult = ProcessListResult(Process(22, "Worker", "dotnet Worker.dll --slot=slow"));
        var slowGate = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.ProcessListGates["slow"] = slowGate;
        var fastCollectionGate = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCollectionGate = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.CollectionGates["fast"] = fastCollectionGate;
        proxy.CollectionGates["slow"] = slowCollectionGate;
        foreach (var podName in new[] { "fast", "slow", "bad" })
        {
            proxy.InspectStarted[podName] = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
        foreach (var podName in new[] { "fast", "slow" })
        {
            proxy.CollectionStarted[podName] = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var fanoutTask = ReplicaCounterFanout.CompareAsync(
            store,
            proxy,
            callerPrincipal: null,
            investigationHandleIds: new[] { "inv-fast", "inv-slow", "inv-bad" },
            durationSeconds: 5,
            intervalSeconds: 1,
            cts.Token);

        try
        {
            await Task.WhenAll(proxy.InspectStarted.Values.Select(tcs => tcs.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
            proxy.Calls.Should().BeEmpty(
                "the fast selector must wait at the common barrier while the slow selector is unresolved");

            slowGate.SetResult(slowResult);
            await Task.WhenAll(proxy.CollectionStarted.Values.Select(tcs => tcs.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var finalResolution = proxy.ResolutionCompletedTicks.Values.Max();
            proxy.CollectionStartedTicks.Values.Should().OnlyContain(
                started => started >= finalResolution,
                "every collection must start after the last selector resolution completes");

            fastCollectionGate.SetResult(CountersResult(30, 100, 0, 11, "fast"));
            slowCollectionGate.SetResult(CountersResult(31, 105, 0, 22, "slow"));
            var fanout = await fanoutTask;

            fanout.PodErrors.Should().ContainSingle()
                .Which.Should().Contain("bad").And.Contain("PIDs 31, 32");
            fanout.Skew!.Replicas.Select(r => r.PodName).Should().Equal("fast", "slow");
            proxy.Calls.Should().Equal("fast", "slow");
            proxy.CounterProcessIds.Should().BeEquivalentTo(
                new Dictionary<string, int> { ["fast"] = 11, ["slow"] = 22 });
        }
        finally
        {
            slowGate.TrySetResult(slowResult);
            fastCollectionGate.TrySetCanceled();
            slowCollectionGate.TrySetCanceled();
            if (!fanoutTask.IsCompleted)
            {
                cts.Cancel();
                try
                {
                    await fanoutTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected cleanup path after a failed bounded assertion.
                }
            }
        }
    }

    [Fact]
    public async Task CompareAsync_HungSelectorTimesOutWithoutConsumingHealthyCollectionBudget()
    {
        var selector = new InvestigationProcessSelector(ManagedEntrypointAssemblyName: "Worker");
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-hung", "hung", processSelector: selector));
        store.Add(ActiveHandle("inv-healthy", "healthy", processSelector: selector));

        var proxy = new StubProxyClient
        {
            ["healthy"] = CountersResult(30, 100, 0, 42, "healthy"),
        };
        proxy.ProcessLists["healthy"] = ProcessListResult(
            Process(42, "Worker", "dotnet Worker.dll --slot=healthy"));
        proxy.ProcessListGates["hung"] = new TaskCompletionSource<CallToolResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var stopwatch = Stopwatch.StartNew();
        var fanout = await ReplicaCounterFanout.CompareAsync(
            store,
            proxy,
            callerPrincipal: null,
            investigationHandleIds: new[] { "inv-hung", "inv-healthy" },
            durationSeconds: 5,
            intervalSeconds: 1,
            selectorResolutionTimeout: TimeSpan.FromMilliseconds(250),
            CancellationToken.None);

        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        fanout.PodErrors.Should().ContainSingle()
            .Which.Should().Contain("hung").And.Contain("process selection timed out");
        var reading = fanout.Skew!.Replicas.Should().ContainSingle().Which;
        reading.PodName.Should().Be("healthy");
        reading.ProcessId.Should().Be(42);
        proxy.Calls.Should().ContainSingle().Which.Should().Be("healthy");
        proxy.CounterProcessIds.Should().Contain("healthy", 42);
    }

    [Fact]
    public async Task CompareAsync_ExplicitEmptyHandleList_DoesNotFallBackToCallerWideDiscovery()
    {
        var store = new MemoryInvestigationStore();
        store.Add(ActiveHandle("inv-a", "pod-a", ownerBearerName: "bearer-A"));

        var fanout = await ReplicaCounterFanout.CompareAsync(
            store, new StubProxyClient(), callerPrincipal: Principal("bearer-A"), investigationHandleIds: Array.Empty<string>(),
            durationSeconds: 5, intervalSeconds: 1, CancellationToken.None);

        fanout.AttachedActivePods.Should().Be(0);
        fanout.PodErrors.Should().BeEmpty();
        fanout.Skew.Should().BeNull();
    }

    private static InvestigationHandle ActiveHandle(
        string handleId,
        string podName,
        string? ownerBearerName = null,
        InvestigationProcessSelector? processSelector = null) => new(
        HandleId: handleId,
        Namespace: "ns",
        PodName: podName,
        TargetContainerName: "api",
        EphemeralContainerName: "diag",
        PodLocalBearerToken: "pod-bearer",
        State: InvestigationState.Active,
        AttachedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
        OwnerBearerName: ownerBearerName,
        OwnerPrincipalKey: ownerBearerName is null
            ? null
            : PrincipalOwnershipKey.ForSynthetic(ownerBearerName),
        ProcessSelector: processSelector);

    private static BearerPrincipal Principal(string name)
        => new(name, ImmutableHashSet.Create("orchestrator-attach"));

    private static DotnetProcess Process(int processId, string entrypoint, string commandLine)
        => new(processId, commandLine, "linux", "x64", "10.0.0", entrypoint);

    private static CallToolResult ProcessListResult(params DotnetProcess[] processes)
    {
        var result = DiagnosticResult.Ok(new InspectProcessReport("list", List: processes), "listed processes");
        var json = JsonSerializer.Serialize(result, SerializeOptions);
        return new CallToolResult { StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }

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
        public Dictionary<string, CallToolResult> ProcessLists { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TaskCompletionSource<CallToolResult>> ProcessListGates { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TaskCompletionSource<bool>> InspectStarted { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TaskCompletionSource<CallToolResult>> CollectionGates { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TaskCompletionSource<bool>> CollectionStarted { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = new();
        public Dictionary<string, int> CounterProcessIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> ResolutionCompletedTicks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> CollectionStartedTicks { get; } = new(StringComparer.Ordinal);

        public CallToolResult this[string podName] { set => _byPod[podName] = value; }

        public Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
        {
            if (Throw.TryGetValue(handle.PodName, out var ex))
            {
                return Task.FromException<CallToolResult>(ex);
            }

            if (request.Name == "inspect_process")
            {
                return ResolveProcessListAsync(handle.PodName, cancellationToken);
            }

            lock (Calls)
            {
                Calls.Add(handle.PodName);
                CollectionStartedTicks[handle.PodName] = Stopwatch.GetTimestamp();
                if (CollectionStarted.TryGetValue(handle.PodName, out var started))
                {
                    started.TrySetResult(true);
                }
                if (request.Arguments is not null &&
                    request.Arguments.TryGetValue("processId", out var processId))
                {
                    CounterProcessIds[handle.PodName] = processId.GetInt32();
                }
            }

            if (CollectionGates.TryGetValue(handle.PodName, out var gate))
            {
                return gate.Task.WaitAsync(cancellationToken);
            }

            return Task.FromResult(_byPod[handle.PodName]);
        }

        private async Task<CallToolResult> ResolveProcessListAsync(
            string podName,
            CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                if (InspectStarted.TryGetValue(podName, out var started))
                {
                    started.TrySetResult(true);
                }
            }

            var result = ProcessListGates.TryGetValue(podName, out var gate)
                ? await gate.Task.WaitAsync(cancellationToken)
                : ProcessLists[podName];

            lock (Calls)
            {
                ResolutionCompletedTicks[podName] = Stopwatch.GetTimestamp();
            }

            return result;
        }

        public Task DisposeForHandleAsync(string handleId) => Task.CompletedTask;
    }
}
