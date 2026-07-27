using System.Text;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliThreadSnapshotRenderingTests
{
    [Fact]
    public void RenderThreadSnapshotEvidence_ExposesBoundedBlockingAndOwnerWaiterEvidence()
    {
        var snapshot = CreateSnapshot(groupCount: 6, lockCount: 6);
        var output = new StringBuilder();

        CliCommands.RenderThreadSnapshotEvidence(output, snapshot);

        var human = output.ToString();
        human.Should().Contain("Bounded evidence");
        human.Should().Contain("Blocked stack groups (showing 5/6)");
        human.Should().Contain("TaskAwaiter.GetResult");
        human.Should().Contain("Contended locks (showing 5/6)");
        human.Should().Contain("owner managed 100 / OS 10100");
        human.Should().Contain("8 waiter(s)");
        human.Should().Contain("ThreadPool: pending=42");
    }

    private static ThreadSnapshotArtifact CreateSnapshot(int groupCount, int lockCount)
    {
        var threads = Enumerable.Range(1, groupCount)
            .Select(id =>
            {
                var frames = new[]
                {
                    new ManagedStackFrame("ManagedMethod", $"App.Worker{id}", "App.Worker", "App.dll", (ulong)id, (ulong)(id + 100)),
                    new ManagedStackFrame("ManagedMethod", "System.Runtime.CompilerServices.TaskAwaiter.GetResult", "System.Runtime.CompilerServices.TaskAwaiter", "System.Private.CoreLib.dll", (ulong)(id + 200), (ulong)(id + 300)),
                };
                return new ManagedThread(
                    id,
                    (uint)(10_000 + id),
                    (ulong)id,
                    "Wait",
                    IsAlive: true,
                    IsBackground: false,
                    IsFinalizer: false,
                    IsGc: false,
                    IsThreadpoolWorker: true,
                    LockCount: 0,
                    CurrentExceptionType: null,
                    TopFrameMethod: frames[^1].DisplayName,
                    Frames: frames)
                {
                    IsLikelyBlocked = true,
                    InferredWaitReason = "Task.Wait",
                };
            })
            .ToArray();

        var locks = Enumerable.Range(0, lockCount)
            .Select(index => new MonitorLockState(
                ObjectAddress: (ulong)(0x1000 + index),
                ObjectTypeFullName: "App.SharedGate",
                OwnerManagedThreadId: 100 + index,
                OwnerOSThreadId: (uint)(10_100 + index),
                OwnerThreadAddress: (ulong)(0x2000 + index),
                RecursionCount: 1,
                WaitingThreadCount: 8 - index,
                IsContended: true,
                Source: "SyncBlock")
            {
                WaitingManagedThreadIds = Enumerable.Range(1, 8 - index).ToArray(),
            })
            .ToArray();

        return new ThreadSnapshotArtifact(
            ThreadSnapshotOrigin.Live,
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(25),
            "CoreCLR",
            "10.0.0",
            threads,
            locks)
        {
            ThreadPool = new ThreadPoolSnapshot(
                Initialized: true,
                UsingPortableThreadPool: true,
                UsingWindowsThreadPool: false,
                Workers: new ThreadPoolWorkerState(64, 64, 0, 0, 1, 32_767),
                Iocp: new ThreadPoolIocpState(1, 1, 1, 1_000),
                Queues: new ThreadPoolQueueState(42, [], []),
                PendingWorkItems: 42),
        };
    }
}
