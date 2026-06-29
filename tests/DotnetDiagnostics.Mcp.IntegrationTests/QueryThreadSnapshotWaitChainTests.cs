using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class QueryThreadSnapshotWaitChainTests
{
    [Fact]
    public void QueryThreadSnapshot_WaitChainsView_BuildsRankedChainEndingInThreadPoolStarvation()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, "thread-snapshot", BuildSyncToStarvationSnapshot(), TimeSpan.FromMinutes(5));

        var result = DiagnosticTools.QueryThreadSnapshot(store, handle.Id, view: "wait-chains");

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        var data = result.Data!;
        data.View.Should().Be("wait-chains");
        data.WaitChains.Should().NotBeNull();

        var view = data.WaitChains!;
        view.ThreadPoolStarved.Should().BeTrue();
        view.Chains.Should().ContainSingle();
        var chain = view.Chains[0];
        chain.IsCycle.Should().BeFalse();
        chain.RootThreadId.Should().Be(1);
        chain.Length.Should().Be(2);
        chain.TerminalKind.Should().Be("threadpool-starvation");
        chain.Links[0].EdgeKind.Should().Be("monitor-lock");
        chain.Links[0].OwnerThreadId.Should().Be(2);
        chain.Links[0].LockObjectAddress.Should().Be("0x1000");
        chain.Links[1].EdgeKind.Should().Be("threadpool-starvation");
        result.Summary.Should().Contain("wait-chain");
    }

    [Fact]
    public void QueryThreadSnapshot_WaitChainsView_FlagsDeadlockCycle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, "thread-snapshot", BuildTwoThreadCycleSnapshot(), TimeSpan.FromMinutes(5));

        var result = DiagnosticTools.QueryThreadSnapshot(store, handle.Id, view: "wait-chains");

        result.IsError.Should().BeFalse();
        var view = result.Data!.WaitChains!;
        view.CycleCount.Should().Be(1);
        view.Chains.Should().ContainSingle().Which.IsCycle.Should().BeTrue();
    }

    private static ThreadSnapshotArtifact BuildSyncToStarvationSnapshot()
        => new(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 4242,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(25),
            RuntimeName: "CoreCLR",
            RuntimeVersion: "10.0.0",
            Threads:
            [
                BuildThread(1, 101, "System.Threading.Monitor.Enter(System.Object)", "System.Threading.Monitor"),
                BuildThread(2, 102, "System.Threading.Tasks.Task`1[[System.Int32]].get_Result()", "System.Threading.Tasks.Task"),
            ],
            Locks:
            [
                new MonitorLockState(
                    ObjectAddress: 0x1000,
                    ObjectTypeFullName: "System.Object",
                    OwnerManagedThreadId: 2,
                    OwnerOSThreadId: 102,
                    OwnerThreadAddress: 0x200,
                    RecursionCount: 1,
                    WaitingThreadCount: 1,
                    IsContended: true,
                    Source: "SyncBlock")
                {
                    WaitingManagedThreadIds = [1],
                },
            ])
        {
            ThreadPool = new ThreadPoolSnapshot(
                Initialized: true,
                UsingPortableThreadPool: true,
                UsingWindowsThreadPool: false,
                Workers: new ThreadPoolWorkerState(Current: 8, Active: 8, Idle: 0, Retired: 0, Min: 4, Max: 8),
                Iocp: new ThreadPoolIocpState(Current: 1, Idle: 1, Min: 1, Max: 1000),
                Queues: new ThreadPoolQueueState(40, Array.Empty<ThreadPoolNamedQueueLength>(), Array.Empty<ThreadPoolLocalQueueLength>()),
                PendingWorkItems: 40),
        };

    private static ThreadSnapshotArtifact BuildTwoThreadCycleSnapshot()
        => new(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 4242,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(25),
            RuntimeName: "CoreCLR",
            RuntimeVersion: "10.0.0",
            Threads:
            [
                BuildThread(1, 101, "System.Threading.Monitor.Enter(System.Object)", "System.Threading.Monitor"),
                BuildThread(2, 102, "System.Threading.Monitor.Enter(System.Object)", "System.Threading.Monitor"),
            ],
            Locks:
            [
                new MonitorLockState(0x1000, "System.Object", 1, 101, 0x100, 1, 1, true, "SyncBlock") { WaitingManagedThreadIds = [2] },
                new MonitorLockState(0x2000, "System.Object", 2, 102, 0x200, 1, 1, true, "SyncBlock") { WaitingManagedThreadIds = [1] },
            ]);

    private static ManagedThread BuildThread(int managedThreadId, uint osThreadId, string topFrame, string typeFullName)
        => new(
            ManagedThreadId: managedThreadId,
            OSThreadId: osThreadId,
            Address: osThreadId,
            State: "Wait",
            IsAlive: true,
            IsBackground: true,
            IsFinalizer: false,
            IsGc: false,
            IsThreadpoolWorker: true,
            LockCount: 1,
            CurrentExceptionType: null,
            TopFrameMethod: topFrame,
            Frames:
            [
                new ManagedStackFrame(
                    Kind: "ManagedMethod",
                    DisplayName: topFrame,
                    TypeFullName: typeFullName,
                    ModuleName: "System.Private.CoreLib",
                    InstructionPointer: 0,
                    StackPointer: 0),
            ])
        {
            IsLikelyBlocked = true,
        };
}
