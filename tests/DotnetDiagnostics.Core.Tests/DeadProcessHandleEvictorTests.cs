using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Coverage for the host-neutral <see cref="DeadProcessHandleEvictor"/> (issue #300) — the single
/// dead-PID sweep shared by the MCP server's hosted eviction service and the CLI <c>session</c> REPL.
/// Liveness is driven through an injected predicate so these tests are deterministic and never spawn
/// real processes.
/// </summary>
public class DeadProcessHandleEvictorTests
{
    [Fact]
    public void EvictDeadProcesses_DropsHandlesForExitedPids_KeepsLiveOnes()
    {
        var store = new MemoryDiagnosticHandleStore();
        var keep = store.Register(100, "cpu-sample", new Payload("alive"), TimeSpan.FromMinutes(5));
        var drop = store.Register(200, "cpu-sample", new Payload("dead"), TimeSpan.FromMinutes(5));

        var evictor = new DeadProcessHandleEvictor(store, isProcessAlive: pid => pid == 100);
        var removed = evictor.EvictDeadProcesses();

        removed.Should().Be(1);
        store.TryGet<Payload>(drop.Id).Should().BeNull("the exited PID's handle must be invalidated");
        store.TryGet<Payload>(keep.Id).Should().NotBeNull("the live PID's handle must survive");
    }

    [Fact]
    public void EvictDeadProcesses_ReportsEachExitedPidViaCallback()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(200, "cpu-sample", new Payload("a"), TimeSpan.FromMinutes(5));
        store.Register(200, "cpu-sample", new Payload("b"), TimeSpan.FromMinutes(5));
        store.Register(300, "cpu-sample", new Payload("c"), TimeSpan.FromMinutes(5));

        var evicted = new List<(int Pid, int Count)>();
        var evictor = new DeadProcessHandleEvictor(store, isProcessAlive: _ => false);

        var removed = evictor.EvictDeadProcesses((pid, count) => evicted.Add((pid, count)));

        removed.Should().Be(3);
        evicted.Should().BeEquivalentTo(new[] { (200, 2), (300, 1) });
    }

    [Fact]
    public void EvictDeadProcesses_AllAlive_RemovesNothing()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(100, "cpu-sample", new Payload("a"), TimeSpan.FromMinutes(5));

        var evicted = new List<int>();
        var evictor = new DeadProcessHandleEvictor(store, isProcessAlive: _ => true);

        var removed = evictor.EvictDeadProcesses((pid, _) => evicted.Add(pid));

        removed.Should().Be(0);
        evicted.Should().BeEmpty();
    }

    [Fact]
    public void EvictDeadProcesses_DumpOriginHandles_SurviveExit()
    {
        // Dump-origin handles register with evictWhenProcessExits:false, so the PID-exit sweep must
        // never drop them even though the originating PID looks dead.
        var store = new MemoryDiagnosticHandleStore();
        var dump = store.Register(200, "heap-snapshot", new Payload("dump"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false);

        var evictor = new DeadProcessHandleEvictor(store, isProcessAlive: _ => false);
        var removed = evictor.EvictDeadProcesses();

        removed.Should().Be(0);
        store.TryGet<Payload>(dump.Id).Should().NotBeNull("dump-origin handles are not PID-evictable");
    }

    [Fact]
    public void EvictDeadProcesses_SelfContainedArtifactsSurviveExit_ButMethodParamHandlesDoNot()
    {
        // Regression guard for #662: self-contained drilldown artifacts stay queryable after the
        // producer exits, so they must be TTL-evicted only. Method-parameter capture is the
        // deliberate exception because its handle lifetime is tied to the live profiler attach.
        const int pid = 200;
        var store = new MemoryDiagnosticHandleStore();
        var survivingHandles = new[]
        {
            store.Register(pid, "cpu-sample", new Payload("cpu"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, "allocation-sample", new Payload("alloc"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, SamplerUseCases.OffCpuHandleKind, new Payload("offcpu"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, SamplerUseCases.NativeAllocHandleKind, new Payload("native"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.Counters, new Payload("counters"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.ExceptionSnapshot, new Payload("exceptions"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.GcEvents, new Payload("gc"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.EventCatalog, new Payload("catalog"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.GcDatas, new Payload("datas"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.LogSnapshot, new Payload("logs"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.JitSnapshot, new Payload("jit"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.ThreadPoolSnapshot, new Payload("threadpool"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.ContentionSnapshot, new Payload("contention"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.DbSnapshot, new Payload("db"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.KestrelSnapshot, new Payload("kestrel"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.NetworkingSnapshot, new Payload("networking"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.InFlightRequests, new Payload("requests"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.StartupSnapshot, new Payload("startup"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.Activities, new Payload("activities"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, CollectionHandleKinds.EventSource, new Payload("event-source"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
            store.Register(pid, SamplerUseCases.ThreadSnapshotKind, new Payload("thread"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false, origin: HandleOrigin.Live),
        };
        var methodParams = store.Register(
            pid,
            MethodParameterCaptureUseCases.HandleKind,
            new Payload("method-params"),
            TimeSpan.FromMinutes(5),
            evictWhenProcessExits: true,
            origin: HandleOrigin.Live);

        var evictor = new DeadProcessHandleEvictor(store, isProcessAlive: _ => false);

        evictor.EvictDeadProcesses().Should().Be(1);
        store.TryGet<Payload>(methodParams.Id).Should().BeNull("method-parameter capture stays tied to the live process");
        foreach (var handle in survivingHandles)
        {
            store.TryGet<Payload>(handle.Id).Should().NotBeNull($"handle kind '{handle.Kind}' must survive producer exit");
        }
    }

    [Fact]
    public async Task RunAsync_SweepsOnInterval_UntilCancelled()
    {
        var store = new MemoryDiagnosticHandleStore();
        var drop = store.Register(200, "cpu-sample", new Payload("dead"), TimeSpan.FromMinutes(5));

        using var cts = new CancellationTokenSource();
        var swept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evictor = new DeadProcessHandleEvictor(store, isProcessAlive: _ => false);

        var loop = evictor.RunAsync(
            TimeSpan.FromMilliseconds(20),
            onEvicted: (_, _) => swept.TrySetResult(),
            cancellationToken: cts.Token);

        await swept.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.TryGet<Payload>(drop.Id).Should().BeNull();

        cts.Cancel();
        // The loop terminates by throwing OperationCanceledException out of WaitForNextTickAsync.
        await FluentActions.Awaiting(() => loop).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_SwallowsSweepErrors_AndKeepsLooping()
    {
        // A store whose RegisteredProcessIds throws would surface via onError; here we assert the
        // loop reports the error and survives to the next tick rather than faulting the task.
        var store = new MemoryDiagnosticHandleStore();
        store.Register(200, "cpu-sample", new Payload("dead"), TimeSpan.FromMinutes(5));

        using var cts = new CancellationTokenSource();
        var calls = 0;
        var firstCallDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evictor = new DeadProcessHandleEvictor(store, isProcessAlive: _ =>
        {
            calls++;
            firstCallDone.TrySetResult();
            throw new InvalidOperationException("boom");
        });

        var errors = 0;
        var loop = evictor.RunAsync(
            TimeSpan.FromMilliseconds(20),
            onError: _ => Interlocked.Increment(ref errors),
            cancellationToken: cts.Token);

        await firstCallDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await FluentActions.Awaiting(() => loop).Should().ThrowAsync<OperationCanceledException>();

        errors.Should().BeGreaterThan(0, "the per-tick exception must be reported, not thrown out of the loop");
    }

    private sealed record Payload(string Value);
}
