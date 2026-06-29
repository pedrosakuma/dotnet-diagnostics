using DotnetDiagnostics.Core.Threads;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ThreadSnapshotQueryDispatcherTests
{
    private const string Handle = "thread-handle-1";

    [Fact]
    public void Dispatch_TopBlocked_ReturnsRankedThreads()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "top-blocked", threadId: null, topN: 50, framesToHash: 20, minCount: 1);

        outcome.Error.Should().BeNull();
        outcome.Data!.View.Should().Be("top-blocked");
        outcome.Data.Threads.Should().NotBeNull();
        outcome.Data.Threads!.Should().HaveCount(2);
    }

    [Fact]
    public void Dispatch_ThreadsSummary_ReturnsThreads()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "threads-summary", threadId: null, topN: 50, framesToHash: 20, minCount: 1);

        outcome.Error.Should().BeNull();
        outcome.Data!.View.Should().Be("threads-summary");
        outcome.Data.Threads!.Should().HaveCount(2);
    }

    [Fact]
    public void Dispatch_NormalizesViewCasingAndWhitespace()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "  Threads-Summary  ", threadId: null, topN: 50, framesToHash: 20, minCount: 1);

        outcome.Error.Should().BeNull();
        outcome.Data!.View.Should().Be("threads-summary");
    }

    [Fact]
    public void Dispatch_Stack_RequiresThreadId()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "stack", threadId: null, topN: 50, framesToHash: 20, minCount: 1);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void Dispatch_Stack_UnknownThread_ReturnsThreadNotFound()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "stack", threadId: 999, topN: 50, framesToHash: 20, minCount: 1);

        outcome.Error!.Kind.Should().Be("ThreadNotFound");
    }

    [Fact]
    public void Dispatch_Stack_ReturnsFramesForManagedThread()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "stack", threadId: 1, topN: 50, framesToHash: 20, minCount: 1);

        outcome.Error.Should().BeNull();
        outcome.Data!.View.Should().Be("stack");
        outcome.Data.ThreadId.Should().Be(1);
        outcome.Data.Thread.Should().NotBeNull();
        outcome.Data.Thread!.Frames.Should().NotBeEmpty();
    }

    [Fact]
    public void Dispatch_UnknownView_InvalidArgumentListsValidViews()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "bogus", threadId: null, topN: 50, framesToHash: 20, minCount: 1);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
        outcome.Error.Message.Should().Contain("threads-summary").And.Contain("threadpool");
    }

    [Fact]
    public void Dispatch_UniqueStacks_FramesToHashBelowOne_InvalidArgument()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "unique-stacks", threadId: null, topN: 50, framesToHash: 0, minCount: 1);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void Dispatch_UniqueStacks_MinCountBelowOne_InvalidArgument()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "unique-stacks", threadId: null, topN: 50, framesToHash: 20, minCount: 0);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void Dispatch_ThreadPool_NotCaptured_ReportsViewNotCaptured()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "threadpool", threadId: null, topN: 50, framesToHash: 20, minCount: 1);

        outcome.Error!.Kind.Should().Be("ViewNotCaptured");
    }

    [Fact]
    public void Dispatch_LockGraph_ReturnsEmptyWhenNoLocks()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "lock-graph", threadId: null, topN: 50, framesToHash: 20, minCount: 1);

        outcome.Error.Should().BeNull();
        outcome.Data!.View.Should().Be("lock-graph");
        outcome.Data.Locks.Should().NotBeNull();
    }

    [Fact]
    public void Dispatch_TopNBelowOne_InvalidArgument()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "top-blocked", threadId: null, topN: 0, framesToHash: 20, minCount: 1);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void Dispatch_WaitChains_ReturnsView()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "wait-chains", threadId: null, topN: 50, framesToHash: 20, minCount: 1);

        outcome.Error.Should().BeNull();
        outcome.Data!.View.Should().Be("wait-chains");
        outcome.Data.WaitChains.Should().NotBeNull();
        outcome.Data.WaitChains!.Chains.Should().BeEmpty();
    }

    [Fact]
    public void SessionViews_ListsNineViews()
    {
        ThreadSnapshotQueryDispatcher.SessionViews.Should().Equal(
            "threads-summary", "stack", "lock-graph", "deadlocks", "top-blocked", "unique-stacks", "async-stalls", "wait-chains", "threadpool");
    }

    private static ThreadSnapshotArtifact Snapshot()
    {
        var threads = new[]
        {
            CreateThread(1, "GroupA"),
            CreateThread(2, "GroupB"),
        };
        return new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 4242,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(25),
            RuntimeName: "CoreClr",
            RuntimeVersion: "10.0.0",
            Threads: threads,
            Locks: Array.Empty<MonitorLockState>())
        {
            Source = "clrmd-thread-walk",
        };
    }

    private static ManagedThread CreateThread(int managedThreadId, string group)
    {
        var frames = new[]
        {
            new ManagedStackFrame("ManagedMethod", $"{group}.Leaf", $"{group}.Type", "App.dll", 0x1000, 0x2000),
            new ManagedStackFrame("ManagedMethod", $"{group}.Root", $"{group}.Type", "App.dll", 0x1010, 0x2010),
        };
        return new ManagedThread(
            ManagedThreadId: managedThreadId,
            OSThreadId: (uint)(10_000 + managedThreadId),
            Address: (ulong)managedThreadId,
            State: "Wait",
            IsAlive: true,
            IsBackground: false,
            IsFinalizer: false,
            IsGc: false,
            IsThreadpoolWorker: true,
            LockCount: 0,
            CurrentExceptionType: null,
            TopFrameMethod: frames[0].DisplayName,
            Frames: frames)
        {
            IsLikelyBlocked = true,
            InferredWaitReason = "Monitor.Wait",
        };
    }
}
