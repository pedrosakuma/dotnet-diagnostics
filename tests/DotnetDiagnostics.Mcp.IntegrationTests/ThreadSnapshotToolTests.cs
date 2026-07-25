using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class ThreadSnapshotToolTests
{
    [Fact]
    public async Task CollectThreadSnapshot_DefaultResponseAllocationDoesNotScaleWithCaptureVolume()
    {
        var small = LargeSnapshot(threadCount: 1_000, lockCount: 500);
        var large = LargeSnapshot(threadCount: 20_000, lockCount: 10_000);

        _ = await MeasureCollectionAllocations(small);
        var (smallAllocated, _, _) = await MeasureCollectionAllocations(small);
        var (largeAllocated, result, handles) = await MeasureCollectionAllocations(large);

        largeAllocated.Should().BeLessThanOrEqualTo(
            smallAllocated + 256_000,
            "the actual collect/register/first-response path must not eagerly index every thread, lock, waiter, signal, or deadlock edge");
        result.Error.Should().BeNull();
        result.Signals.Should().BeNull("capture-sized signal analysis is deferred to explicit drilldown");
        result.Data!.Threads.Should().HaveCount(ThreadSnapshotProjection.SummaryThreadLimit);
        result.Data.Locks.Should().BeEmpty();

        var nextPage = DiagnosticTools.QueryThreadSnapshot(
            handles,
            result.Data.Handle,
            view: "threads-summary",
            offset: ThreadSnapshotProjection.QueryThreadLimit);
        nextPage.Data!.Threads.Should().HaveCount(ThreadSnapshotProjection.QueryThreadLimit);
        var exactLock = DiagnosticTools.QueryThreadSnapshot(
            handles,
            result.Data.Handle,
            view: "lock-graph",
            offset: 0,
            lockAddress: "0x100000");
        exactLock.Data!.Locks.Should().ContainSingle();
    }

    [Fact]
    public void QueryThreadSnapshot_ThreadpoolView_ReturnsCapturedThreadPool()
    {
        var handles = new MemoryDiagnosticHandleStore();
        var snapshot = new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 42,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(12),
            RuntimeName: "CoreClr",
            RuntimeVersion: "10.0.0",
            Threads: Array.Empty<ManagedThread>(),
            Locks: Array.Empty<MonitorLockState>())
        {
            Source = "clrmd-thread-walk",
            ThreadPool = new ThreadPoolSnapshot(
                Initialized: true,
                UsingPortableThreadPool: true,
                UsingWindowsThreadPool: false,
                Workers: new ThreadPoolWorkerState(Current: 7, Active: 3, Idle: 4, Retired: 0, Min: 1, Max: 32767),
                Iocp: new ThreadPoolIocpState(Current: 0, Idle: 0, Min: 1, Max: 1000),
                Queues: new ThreadPoolQueueState(
                    GlobalQueueLength: 5,
                    GlobalQueues: new[]
                    {
                        new ThreadPoolNamedQueueLength("workItems", 5) { QueueAddress = 0x1000 },
                    },
                    LocalQueues: new[]
                    {
                        new ThreadPoolLocalQueueLength(0x2000, 2) { ManagedThreadId = 11, OSThreadId = 22, QueueIndex = 0 },
                    }),
                PendingWorkItems: 7)
            {
                CpuUtilization = 42,
                HillClimbing = new ThreadPoolHillClimbingState(123, 4, 1, 12.5f, "Warmup") { AdjustmentIntervalMs = 10 },
            },
        };
        var handle = handles.Register(snapshot.ProcessId, "thread-snapshot", snapshot, TimeSpan.FromMinutes(10));

        var result = DiagnosticTools.QueryThreadSnapshot(handles, handle.Id, view: "threadpool");

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        result.Data!.View.Should().Be("threadpool");
        result.Data.ThreadPool.Should().NotBeNull();
        result.Data.ThreadPool!.PendingWorkItems.Should().Be(7);
        result.Data.ThreadPool.Queues.LocalQueues.Should().ContainSingle();
        result.Summary.Should().Contain("pending work items 7");
    }

    private static async Task<(long Allocated, DiagnosticResult<ThreadSnapshotQueryResult> Result, MemoryDiagnosticHandleStore Handles)>
        MeasureCollectionAllocations(ThreadSnapshotArtifact snapshot)
    {
        var handles = new MemoryDiagnosticHandleStore();
        var inspector = new StubThreadSnapshotInspector(snapshot);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = await DiagnosticTools.CollectThreadSnapshot(
            inspector,
            handles,
            ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            processId: snapshot.ProcessId);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (allocated, result, handles);
    }

    private static ThreadSnapshotArtifact LargeSnapshot(int threadCount, int lockCount)
    {
        var frame = new ManagedStackFrame(
            "ManagedMethod",
            "App.Worker.Run",
            "App.Worker",
            "App.dll",
            0x1000,
            0x2000);
        var threads = Enumerable.Range(1, threadCount)
            .Select(id => new ManagedThread(
                id,
                (uint)(10_000 + id),
                (ulong)id,
                "Wait",
                true,
                false,
                false,
                false,
                true,
                0,
                null,
                frame.DisplayName,
                [frame])
            {
                IsLikelyBlocked = true,
                IsContendedLockOwner = id <= lockCount,
                IsLockWaiter = id > 1,
                IsDeadlockCandidate = id is > 1 && id <= lockCount,
                InferredWaitReason = $"WaitReason{id}",
            })
            .ToArray();
        var locks = Enumerable.Range(0, lockCount)
            .Select(index => new MonitorLockState(
                (ulong)(0x100_000 + index),
                $"App.Lock{index}",
                index + 1,
                (uint)(10_001 + index),
                (ulong)(index + 1),
                0,
                1,
                true,
                "test")
            {
                WaitingManagedThreadIds = [index % threadCount + 2],
            })
            .ToArray();
        return new ThreadSnapshotArtifact(
            ThreadSnapshotOrigin.Live,
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(25),
            "CoreClr",
            "10.0.0",
            threads,
            locks);
    }

    private sealed class StubThreadSnapshotInspector(ThreadSnapshotArtifact snapshot) : IThreadSnapshotInspector
    {
        public Task<ThreadSnapshotArtifact> InspectLiveAsync(
            int processId,
            ThreadSnapshotOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<ThreadSnapshotArtifact> InspectDumpAsync(
            string dumpFilePath,
            ThreadSnapshotOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }
}
