using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class WaitChainAnalyzerTests
{
    [Fact]
    public void Analyze_OpenChain_AToBToCEndingInThreadPoolStarvation()
    {
        // A (1) waits on a lock held by B (2); B waits on a lock held by C (3); C is blocked
        // sync-over-async while the ThreadPool is starved -> chain sinks in threadpool-starvation.
        var snapshot = SnapshotWith(
            threadPool: StarvedThreadPool(),
            threads:
            [
                MonitorWaiter(1),
                MonitorWaiter(2),
                SyncOverAsyncThread(3),
            ],
            locks:
            [
                Lock(address: 0x1000, owner: 2, waiter: 1),
                Lock(address: 0x2000, owner: 3, waiter: 2),
            ]);

        var view = WaitChainAnalyzer.Analyze(snapshot, "h", maxChains: 10);

        view.View.Should().Be("wait-chains");
        view.ThreadPoolStarved.Should().BeTrue();
        view.Chains.Should().ContainSingle();

        var chain = view.Chains[0];
        chain.IsCycle.Should().BeFalse();
        chain.RootThreadId.Should().Be(1);
        chain.Length.Should().Be(3);
        chain.TerminalKind.Should().Be("threadpool-starvation");
        chain.Links.Select(l => l.WaitingThreadId).Should().Equal(1, 2, 3);
        chain.Links[0].EdgeKind.Should().Be("monitor-lock");
        chain.Links[0].OwnerThreadId.Should().Be(2);
        chain.Links[1].EdgeKind.Should().Be("monitor-lock");
        chain.Links[1].OwnerThreadId.Should().Be(3);
        chain.Links[2].EdgeKind.Should().Be("threadpool-starvation");
        chain.Links[2].TargetKind.Should().Be("threadpool-starvation");
        view.OpenChainCount.Should().Be(1);
        view.CycleCount.Should().Be(0);
    }

    [Fact]
    public void Analyze_TrueCycle_IsFlaggedAsDeadlock()
    {
        // A (1) waits on a lock held by B (2); B (2) waits on a lock held by A (1) -> deadlock.
        var snapshot = SnapshotWith(
            threadPool: null,
            threads: [MonitorWaiter(1), MonitorWaiter(2)],
            locks:
            [
                Lock(address: 0x1000, owner: 1, waiter: 2),
                Lock(address: 0x2000, owner: 2, waiter: 1),
            ]);

        var view = WaitChainAnalyzer.Analyze(snapshot, "h", maxChains: 10);

        view.CycleCount.Should().Be(1);
        view.Chains.Should().ContainSingle();
        var chain = view.Chains[0];
        chain.IsCycle.Should().BeTrue();
        chain.TerminalKind.Should().Be("cycle");
        chain.Links.Select(l => l.WaitingThreadId).Should().Equal(1, 2);
        chain.Links.Should().OnlyContain(l => l.EdgeKind == "monitor-lock");
    }

    [Fact]
    public void Analyze_MixedSyncAndAsyncChain_EmitsBothEdgeKinds()
    {
        // A (1) waits on a lock held by B (2); B is parked awaiting a SemaphoreSlim (async edge).
        var snapshot = SnapshotWith(
            threadPool: null,
            threads:
            [
                MonitorWaiter(1),
                SemaphoreWaiter(2),
            ],
            locks: [Lock(address: 0x1000, owner: 2, waiter: 1)]);

        var view = WaitChainAnalyzer.Analyze(snapshot, "h", maxChains: 10);

        view.Chains.Should().ContainSingle();
        var chain = view.Chains[0];
        chain.Length.Should().Be(2);
        chain.IsCycle.Should().BeFalse();
        chain.TerminalKind.Should().Be("async-construct");
        chain.Links[0].EdgeKind.Should().Be("monitor-lock");
        chain.Links[1].EdgeKind.Should().Be("async-continuation");
        chain.Links[1].TargetLabel.Should().Be("SemaphoreSlim");
        // Indeterminate async-ownership note is emitted, never a guessed owner.
        chain.Links[1].OwnerThreadId.Should().BeNull();
        chain.Notes.Should().Contain(n => n.Contains("not determinable", StringComparison.OrdinalIgnoreCase));
        view.Notes.Should().Contain(n => n.Contains("not determinable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_RanksLongestAndMostBlockedFirst()
    {
        // Long chain: 10 -> 11 -> 12 (sync-over-async). Short chain: 20 -> 21.
        var snapshot = SnapshotWith(
            threadPool: null,
            threads:
            [
                MonitorWaiter(10), MonitorWaiter(11), SyncOverAsyncThread(12),
                MonitorWaiter(20), SemaphoreWaiter(21),
            ],
            locks:
            [
                Lock(address: 0x1000, owner: 11, waiter: 10),
                Lock(address: 0x2000, owner: 12, waiter: 11),
                Lock(address: 0x3000, owner: 21, waiter: 20),
            ]);

        var view = WaitChainAnalyzer.Analyze(snapshot, "h", maxChains: 10);

        view.Chains.Should().HaveCount(2);
        view.Chains[0].Rank.Should().Be(1);
        view.Chains[0].RootThreadId.Should().Be(10);
        view.Chains[0].Length.Should().Be(3);
        view.Chains[1].Rank.Should().Be(2);
        view.Chains[1].RootThreadId.Should().Be(20);
        view.Chains[1].Length.Should().Be(2);
    }

    [Fact]
    public void Analyze_IndeterminateAsyncOwnership_EmitsHonestNote_NoStarvationGuess()
    {
        // Sync-over-async thread but the ThreadPool is healthy (idle workers available): we must NOT
        // claim starvation, and must emit the indeterminate async-ownership note rather than guess.
        var snapshot = SnapshotWith(
            threadPool: HealthyThreadPool(),
            threads: [SyncOverAsyncThread(7)],
            locks: []);

        var view = WaitChainAnalyzer.Analyze(snapshot, "h", maxChains: 10);

        view.ThreadPoolStarved.Should().BeFalse();
        view.Chains.Should().ContainSingle();
        var chain = view.Chains[0];
        chain.TerminalKind.Should().Be("async-construct");
        chain.Links[0].EdgeKind.Should().Be("async-continuation");
        chain.Links[0].TargetLabel.Should().Be("Task");
        chain.Links[0].OwnerThreadId.Should().BeNull();
        chain.Notes.Should().Contain(n => n.Contains("not determinable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_NoBlockedThreads_ReturnsEmpty()
    {
        var snapshot = SnapshotWith(
            threadPool: null,
            threads: [RunningThread(1)],
            locks: []);

        var view = WaitChainAnalyzer.Analyze(snapshot, "h", maxChains: 10);

        view.Chains.Should().BeEmpty();
        view.EdgeCount.Should().Be(0);
        view.CycleCount.Should().Be(0);
        view.OpenChainCount.Should().Be(0);
    }

    private static ThreadSnapshotArtifact SnapshotWith(
        ThreadPoolSnapshot? threadPool,
        ManagedThread[] threads,
        MonitorLockState[] locks)
        => new(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 4242,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(10),
            RuntimeName: "CoreCLR",
            RuntimeVersion: "10.0.0",
            Threads: threads,
            Locks: locks)
        {
            ThreadPool = threadPool,
        };

    private static MonitorLockState Lock(ulong address, int owner, int waiter)
        => new(
            ObjectAddress: address,
            ObjectTypeFullName: "System.Object",
            OwnerManagedThreadId: owner,
            OwnerOSThreadId: (uint)(100 + owner),
            OwnerThreadAddress: address,
            RecursionCount: 1,
            WaitingThreadCount: 1,
            IsContended: true,
            Source: "SyncBlock")
        {
            WaitingManagedThreadIds = [waiter],
        };

    private static ManagedThread MonitorWaiter(int id)
        => Thread(id, blocked: true, "System.Threading.Monitor.Enter(System.Object)", "System.Threading.Monitor");

    private static ManagedThread SyncOverAsyncThread(int id)
        => Thread(id, blocked: true, "System.Threading.Tasks.Task`1[[System.Int32]].get_Result()", "System.Threading.Tasks.Task");

    private static ManagedThread SemaphoreWaiter(int id)
        => Thread(id, blocked: true, "System.Threading.SemaphoreSlim.WaitAsync(System.Threading.CancellationToken)", "System.Threading.SemaphoreSlim");

    private static ManagedThread RunningThread(int id)
        => Thread(id, blocked: false, "MyApp.Worker.DoWork()", "MyApp.Worker");

    private static ManagedThread Thread(int id, bool blocked, string topFrame, string typeFullName)
        => new(
            ManagedThreadId: id,
            OSThreadId: (uint)(1000 + id),
            Address: (ulong)id,
            State: blocked ? "Wait" : "Running",
            IsAlive: true,
            IsBackground: true,
            IsFinalizer: false,
            IsGc: false,
            IsThreadpoolWorker: true,
            LockCount: 0,
            CurrentExceptionType: null,
            TopFrameMethod: topFrame,
            Frames:
            [
                new ManagedStackFrame(
                    Kind: "ManagedMethod",
                    DisplayName: topFrame,
                    TypeFullName: typeFullName,
                    ModuleName: "Sample.dll",
                    InstructionPointer: 0,
                    StackPointer: 0),
            ])
        {
            IsLikelyBlocked = blocked,
        };

    private static ThreadPoolSnapshot StarvedThreadPool()
        => new(
            Initialized: true,
            UsingPortableThreadPool: true,
            UsingWindowsThreadPool: false,
            Workers: new ThreadPoolWorkerState(Current: 8, Active: 8, Idle: 0, Retired: 0, Min: 4, Max: 8),
            Iocp: new ThreadPoolIocpState(Current: 1, Idle: 1, Min: 1, Max: 1000),
            Queues: new ThreadPoolQueueState(
                GlobalQueueLength: 25,
                GlobalQueues: Array.Empty<ThreadPoolNamedQueueLength>(),
                LocalQueues: Array.Empty<ThreadPoolLocalQueueLength>()),
            PendingWorkItems: 25);

    private static ThreadPoolSnapshot HealthyThreadPool()
        => new(
            Initialized: true,
            UsingPortableThreadPool: true,
            UsingWindowsThreadPool: false,
            Workers: new ThreadPoolWorkerState(Current: 4, Active: 1, Idle: 3, Retired: 0, Min: 4, Max: 32),
            Iocp: new ThreadPoolIocpState(Current: 1, Idle: 1, Min: 1, Max: 1000),
            Queues: new ThreadPoolQueueState(
                GlobalQueueLength: 0,
                GlobalQueues: Array.Empty<ThreadPoolNamedQueueLength>(),
                LocalQueues: Array.Empty<ThreadPoolLocalQueueLength>()),
            PendingWorkItems: 0);
}
