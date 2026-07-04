using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests the diagnosis-agnostic thread signal groupings (#526): coarse wait-state roll-up
/// (<c>threads.by-wait-state</c>) and finer wait-target roll-up (<c>threads.by-wait-target</c>).
/// These describe <i>how many threads share a wait state / converge on a lock</i>, never <i>why</i>
/// (no lock-contention / sync-over-async naming).
/// </summary>
public sealed class ThreadWaitSignalsTests
{
    private static ManagedThread Thread(int id, bool blocked, string? waitReason) => new(
        ManagedThreadId: id,
        OSThreadId: (uint)(10_000 + id),
        Address: (ulong)id,
        State: blocked ? "Wait" : "Running",
        IsAlive: true,
        IsBackground: false,
        IsFinalizer: false,
        IsGc: false,
        IsThreadpoolWorker: true,
        LockCount: 0,
        CurrentExceptionType: null,
        TopFrameMethod: null,
        Frames: Array.Empty<ManagedStackFrame>())
    {
        IsLikelyBlocked = blocked,
        InferredWaitReason = waitReason,
    };

    private static MonitorLockState Lock(ulong address, string? typeName, int waitingCount) => new(
        ObjectAddress: address,
        ObjectTypeFullName: typeName,
        OwnerManagedThreadId: 1,
        OwnerOSThreadId: 10_001,
        OwnerThreadAddress: 1,
        RecursionCount: 1,
        WaitingThreadCount: waitingCount,
        IsContended: waitingCount > 0,
        Source: "SyncBlock");

    // ---- by-wait-state ------------------------------------------------------------------------

    [Fact]
    public void ByWaitState_Emits_WhenManyThreadsShareOneReason()
    {
        var threads = new[]
        {
            Thread(1, blocked: true, "Monitor.Enter (contended)"),
            Thread(2, blocked: true, "Monitor.Enter (contended)"),
            Thread(3, blocked: true, "Monitor.Enter (contended)"),
            Thread(4, blocked: false, null),
            Thread(5, blocked: false, null),
        };

        var signals = ThreadWaitSignals.Detect(new ThreadWaitSignalContext(threads.Length, threads, Array.Empty<MonitorLockState>(), "handle-threads"));

        var byState = signals.Should().ContainSingle(s => s.Signal == "threads.by-wait-state").Subject;
        byState.Salience.Should().BeApproximately(0.6, 0.001);
        byState.Summary.Should().Contain("Monitor.Enter (contended)");
        byState.Buckets[0].Key.Should().Be("Monitor.Enter (contended)");
        byState.Buckets[0].Magnitude.Should().Be(3);
        byState.Buckets[0].Handle.Should().Be("handle-threads");
        byState.NextAction!.SuggestedArguments!["view"].Should().Be("top-blocked");
    }

    [Fact]
    public void ByWaitState_EmitsNothing_WhenHealthyPool()
    {
        var threads = Enumerable.Range(1, 10).Select(i => Thread(i, blocked: false, null)).ToArray();

        ThreadWaitSignals.Detect(new ThreadWaitSignalContext(threads.Length, threads, Array.Empty<MonitorLockState>(), "h"))
            .Should().NotContain(s => s.Signal == "threads.by-wait-state");
    }

    [Fact]
    public void ByWaitState_EmitsNothing_WhenTooFewThreadsBlocked()
    {
        var threads = new[]
        {
            Thread(1, blocked: true, "Thread.Sleep"),
            Thread(2, blocked: false, null),
            Thread(3, blocked: false, null),
        };

        ThreadWaitSignals.Detect(new ThreadWaitSignalContext(threads.Length, threads, Array.Empty<MonitorLockState>(), "h"))
            .Should().NotContain(s => s.Signal == "threads.by-wait-state");
    }

    [Fact]
    public void ByWaitState_EmitsNothing_WhenSpreadAcrossManyReasons()
    {
        var threads = new[]
        {
            Thread(1, blocked: true, "Monitor.Enter (contended)"),
            Thread(2, blocked: true, "Thread.Sleep"),
            Thread(3, blocked: true, "Socket I/O"),
            Thread(4, blocked: true, "Thread.Join"),
            Thread(5, blocked: false, null),
        };

        ThreadWaitSignals.Detect(new ThreadWaitSignalContext(threads.Length, threads, Array.Empty<MonitorLockState>(), "h"))
            .Should().NotContain(s => s.Signal == "threads.by-wait-state");
    }

    // ---- by-wait-target ------------------------------------------------------------------------

    [Fact]
    public void ByWaitTarget_Emits_WhenManyThreadsConvergeOnOneLock()
    {
        var locks = new[]
        {
            Lock(0x1000, "MyApp.Cache", waitingCount: 6),
            Lock(0x2000, "MyApp.Other", waitingCount: 1),
        };
        var threads = Array.Empty<ManagedThread>();

        var signals = ThreadWaitSignals.Detect(new ThreadWaitSignalContext(10, threads, locks, "handle-threads"));

        var byTarget = signals.Should().ContainSingle(s => s.Signal == "threads.by-wait-target").Subject;
        byTarget.Salience.Should().BeApproximately(6.0 / 7.0, 0.001);
        byTarget.Buckets[0].Key.Should().Be("MyApp.Cache @ 0x1000");
        byTarget.Buckets[0].Magnitude.Should().Be(6);
        byTarget.Buckets[0].Handle.Should().Be("handle-threads");
        byTarget.NextAction!.SuggestedArguments!["view"].Should().Be("lock-graph");
    }

    [Fact]
    public void ByWaitTarget_EmitsNothing_WhenNoContendedLocks()
    {
        var locks = new[] { Lock(0x1000, "MyApp.Cache", waitingCount: 0) };

        ThreadWaitSignals.Detect(new ThreadWaitSignalContext(10, Array.Empty<ManagedThread>(), locks, "h"))
            .Should().NotContain(s => s.Signal == "threads.by-wait-target");
    }

    [Fact]
    public void ByWaitTarget_EmitsNothing_WhenSpreadAcrossManyLocks()
    {
        var locks = new[]
        {
            Lock(0x1000, "MyApp.A", waitingCount: 2),
            Lock(0x2000, "MyApp.B", waitingCount: 2),
            Lock(0x3000, "MyApp.C", waitingCount: 2),
        };

        ThreadWaitSignals.Detect(new ThreadWaitSignalContext(10, Array.Empty<ManagedThread>(), locks, "h"))
            .Should().NotContain(s => s.Signal == "threads.by-wait-target");
    }

    [Fact]
    public void ByWaitTarget_UsesUnknownTypePlaceholder_WhenTypeNameMissing()
    {
        var locks = new[] { Lock(0x1000, null, waitingCount: 5) };

        var signals = ThreadWaitSignals.Detect(new ThreadWaitSignalContext(10, Array.Empty<ManagedThread>(), locks, "h"));

        var byTarget = signals.Should().ContainSingle(s => s.Signal == "threads.by-wait-target").Subject;
        byTarget.Buckets[0].Key.Should().Be("<unknown type> @ 0x1000");
    }
}
