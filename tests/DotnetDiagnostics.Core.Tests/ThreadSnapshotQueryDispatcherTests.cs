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
    public void Dispatch_ThreadPages_ExposeEveryThreadAndExactStackRemainsAvailable()
    {
        var snapshot = PagingSnapshot();

        var first = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "threads-summary", null, 50, 20, 1, offset: 0);
        var second = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "threads-summary", null, 50, 20, 1, offset: 8);
        var third = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "threads-summary", null, 50, 20, 1, offset: 16);

        first.Data!.NextThreadOffset.Should().Be(8);
        second.Data!.NextThreadOffset.Should().Be(16);
        third.Data!.NextThreadOffset.Should().BeNull();
        first.Data.Threads!
            .Concat(second.Data.Threads!)
            .Concat(third.Data.Threads!)
            .Select(thread => thread.ManagedThreadId)
            .Should().BeEquivalentTo(Enumerable.Range(1, 20));
        first.Data.Threads.Should().OnlyContain(thread => thread.Frames.Count <= ThreadSnapshotProjection.QueryFrameLimit);

        var exact = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "stack", threadId: 20, topN: 50, framesToHash: 20, minCount: 1);
        exact.Data!.Thread!.ManagedThreadId.Should().Be(20);
        exact.Data.Thread.Frames.Should().HaveCount(12);
    }

    [Fact]
    public void Dispatch_TopBlocked_SeparatesSnapshotTotalFromPagedCandidateTotal()
    {
        var source = PagingSnapshot();
        var snapshot = source with
        {
            Threads = source.Threads
                .Select((thread, index) => thread with
                {
                    IsLikelyBlocked = index < 10,
                    IsContendedLockOwner = false,
                    IsLockWaiter = false,
                    IsDeadlockCandidate = false,
                    InferredWaitReason = index < 10 ? "Monitor.Enter" : null,
                })
                .ToArray(),
            Locks = Array.Empty<MonitorLockState>(),
        };
        var first = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "top-blocked", null, 50, 20, 1, offset: 0);
        var second = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "top-blocked", null, 50, 20, 1, offset: 8);

        first.Data!.TotalThreads.Should().Be(20);
        first.Data.CandidateThreads.Should().Be(10);
        first.Data.OmittedThreads.Should().Be(2);
        first.Data.NextThreadOffset.Should().Be(8);
        second.Data!.TotalThreads.Should().Be(20);
        second.Data.CandidateThreads.Should().Be(10);
        second.Data.OmittedThreads.Should().Be(8);
        second.Data.NextThreadOffset.Should().BeNull();
        first.Data.Threads!
            .Concat(second.Data.Threads!)
            .Should().HaveCount(10)
            .And.OnlyContain(thread => thread.IsLikelyBlocked);
    }

    [Fact]
    public void Dispatch_TopBlocked_LockWaiterUsesBlockedWordingWithoutLikelyBlockedFlag()
    {
        var source = Snapshot();
        var snapshot = source with
        {
            Threads = source.Threads
                .Select(thread => thread with
                {
                    IsLikelyBlocked = false,
                    IsLockWaiter = thread.ManagedThreadId == 1,
                    InferredWaitReason = null,
                })
                .ToArray(),
            Locks =
            [
                new MonitorLockState(0x30_000, "App.Lock", 2, 10_002, 0x40_000, 0, 1, true, "test")
                {
                    WaitingManagedThreadIds = [1],
                },
            ],
        };
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "top-blocked", null, 50, 20, 1);

        outcome.Error.Should().BeNull();
        outcome.Data!.CandidateThreads.Should().Be(1);
        outcome.Data.Threads.Should().ContainSingle()
            .Which.ManagedThreadId.Should().Be(1);
        outcome.Summary.Should().Contain("blocked/waiting");
        outcome.Summary.Should().NotContain("decisive/running");
    }

    [Fact]
    public void Dispatch_LockPages_ExposeEveryLockWithBoundedWaiterIds()
    {
        var snapshot = PagingSnapshot();

        var first = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "lock-graph", null, 50, 20, 1, offset: 0);
        var second = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "lock-graph", null, 50, 20, 1, offset: 12);
        var third = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "lock-graph", null, 50, 20, 1, offset: 24);

        first.Data!.NextLockOffset.Should().Be(12);
        second.Data!.NextLockOffset.Should().Be(24);
        third.Data!.NextLockOffset.Should().BeNull();
        var locks = first.Data.Locks!.Concat(second.Data.Locks!).Concat(third.Data.Locks!).ToArray();
        locks.Select(lockState => lockState.ObjectAddress).Should().OnlyHaveUniqueItems().And.HaveCount(30);
        locks.Should().OnlyContain(lockState =>
            lockState.WaitingManagedThreadIds.Count == ThreadSnapshotProjection.LockWaiterIdLimit &&
            lockState.TotalWaitingManagedThreadIds == 1_000 &&
            lockState.OmittedWaitingManagedThreadIds == 1_000 - ThreadSnapshotProjection.LockWaiterIdLimit);
        snapshot.Locks.Should().OnlyContain(lockState => lockState.WaitingManagedThreadIds.Count == 1_000);
    }

    [Fact]
    public void Dispatch_ExactLockPagesExposeEveryRetainedWaiterId()
    {
        var snapshot = PagingSnapshot();
        const ulong lockAddress = 0x30_000;
        var recoveredWaiterIds = new List<int>();
        var offset = 0;
        string? cursor = null;

        while (true)
        {
            var page = ThreadSnapshotQueryDispatcher.DispatchCursor(
                snapshot,
                Handle,
                "lock-graph",
                threadId: null,
                topN: 50,
                framesToHash: 20,
                minCount: 1,
                offset: offset,
                lockAddress: $"0x{lockAddress:x}",
                cursor: cursor);

            page.Error.Should().BeNull();
            var selected = page.Data!.Locks.Should().ContainSingle().Subject;
            selected.ObjectAddress.Should().Be(lockAddress);
            selected.WaitingManagedThreadIds.Should().HaveCountLessThanOrEqualTo(ThreadSnapshotProjection.LockWaiterIdLimit);
            recoveredWaiterIds.AddRange(selected.WaitingManagedThreadIds);
            if (page.Data.NextWaiterCursor is not { } nextCursor)
            {
                break;
            }
            page.Data.NextWaiterOffset.Should().BeGreaterThan(offset);
            offset = 0;
            cursor = nextCursor;
        }

        recoveredWaiterIds.Should().Equal(Enumerable.Range(1, 1_000));
        snapshot.Locks.Single(lockState => lockState.ObjectAddress == lockAddress)
            .WaitingManagedThreadIds.Should().HaveCount(1_000);
    }

    [Fact]
    public void Dispatch_NegativeOffset_ReturnsInvalidArgument()
    {
        var outcome = ThreadSnapshotQueryDispatcher.Dispatch(
            Snapshot(), Handle, "threads-summary", null, 50, 20, 1, offset: -1);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void Dispatch_PathologicalOffsets_AreRejectedWithoutConfusingEmptyCaptures()
    {
        var snapshot = PagingSnapshot();

        var threads = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "threads-summary", null, 50, 20, 1, offset: int.MaxValue);
        var blocked = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "top-blocked", null, 50, 20, 1, offset: int.MaxValue);
        var locks = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "lock-graph", null, 50, 20, 1, offset: int.MaxValue);
        var waiters = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot,
            Handle,
            "lock-graph",
            threadId: null,
            topN: 50,
            framesToHash: 20,
            minCount: 1,
            offset: int.MaxValue,
            lockAddress: "0x30000");

        foreach (var outcome in new[] { threads, blocked, locks, waiters })
        {
            outcome.Data.Should().BeNull();
            outcome.Error!.Kind.Should().Be("InvalidArgument");
            outcome.Error.Message.Should().Contain("use the returned next cursor");
            outcome.Hints.Should().ContainSingle()
                .Which.NextTool.Should().Be("query_snapshot");
        }

        var empty = snapshot with
        {
            Threads = Array.Empty<ManagedThread>(),
            Locks = Array.Empty<MonitorLockState>(),
        };
        ThreadSnapshotQueryDispatcher.Dispatch(empty, Handle, "threads-summary", null, 50, 20, 1)
            .Summary.Should().Contain("contains no captured threads").And.NotContain("exhausted");
        ThreadSnapshotQueryDispatcher.Dispatch(empty, Handle, "lock-graph", null, 50, 20, 1)
            .Summary.Should().Contain("contains no held or contended SyncBlocks").And.NotContain("exhausted");
    }

    [Fact]
    public void Dispatch_CursorRejectsMalformedCrossHandleAndCrossViewValues()
    {
        var snapshot = PagingSnapshot();
        var first = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, Handle, "threads-summary", null, 50, 20, 1);
        var cursor = first.Data!.NextThreadCursor;
        cursor.Should().NotBeNull();
        var padded = cursor!.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
        var versionBytes = Convert.FromBase64String(padded);
        versionBytes[0] = 99;
        var unsupportedVersion = Convert.ToBase64String(versionBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var malformedLength = Convert.ToBase64String([1, 1, 128, 128, 128, 128, 16])
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var malformed = ThreadSnapshotQueryDispatcher.DispatchCursor(
            snapshot, Handle, "threads-summary", null, 50, 20, 1, cursor: "not-base64!");
        var malformedString = ThreadSnapshotQueryDispatcher.DispatchCursor(
            snapshot, Handle, "threads-summary", null, 50, 20, 1, cursor: malformedLength);
        var versioned = ThreadSnapshotQueryDispatcher.DispatchCursor(
            snapshot, Handle, "threads-summary", null, 50, 20, 1, cursor: unsupportedVersion);
        var crossHandle = ThreadSnapshotQueryDispatcher.DispatchCursor(
            snapshot, "other-handle", "threads-summary", null, 50, 20, 1, cursor: cursor!);
        var crossView = ThreadSnapshotQueryDispatcher.DispatchCursor(
            snapshot, Handle, "top-blocked", null, 50, 20, 1, cursor: cursor!);
        var mixedPaging = ThreadSnapshotQueryDispatcher.DispatchCursor(
            snapshot, Handle, "threads-summary", null, 50, 20, 1, offset: 1, cursor: cursor!);

        malformed.Error!.Message.Should().Contain("valid base64url");
        malformedString.Error!.Message.Should().Contain("truncated");
        versioned.Error!.Message.Should().Contain("version is unsupported");
        crossHandle.Error!.Message.Should().Contain("different snapshot handle");
        crossView.Error!.Message.Should().Contain("different thread view");
        mixedPaging.Error!.Message.Should().Contain("non-zero offset cannot be combined");
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
        var snapshot = new ThreadSnapshotArtifact(
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
        return snapshot;
    }

    private static ThreadSnapshotArtifact PagingSnapshot()
    {
        var threads = Enumerable.Range(1, 20)
            .Select(id =>
            {
                var frames = Enumerable.Range(0, 12)
                    .Select(index => new ManagedStackFrame(
                        "ManagedMethod",
                        $"Group{id}.Frame{index}",
                        $"Group{id}.Type",
                        "App.dll",
                        (ulong)(0x1000 + index),
                        (ulong)(0x2000 + index)))
                    .ToArray();
                return new ManagedThread(
                    id,
                    (uint)(10_000 + id),
                    (ulong)id,
                    "Running",
                    true,
                    false,
                    false,
                    false,
                    true,
                    0,
                    null,
                    frames[0].DisplayName,
                    frames)
                {
                    IsContendedLockOwner = id == 1,
                    IsLockWaiter = true,
                    IsDeadlockCandidate = id == 1,
                };
            })
            .ToArray();
        var locks = Enumerable.Range(0, 30)
            .Select(index => new MonitorLockState(
                (ulong)(0x30_000 + index),
                $"Lock.Type{index}",
                1,
                10_001,
                0x40_000,
                0,
                1_000 - index,
                true,
                "test")
            {
                WaitingManagedThreadIds = Enumerable.Range(1, 1_000).ToArray(),
            })
            .ToArray();
        var snapshot = new ThreadSnapshotArtifact(
            ThreadSnapshotOrigin.Live,
            4242,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMilliseconds(25),
            "CoreClr",
            "10.0.0",
            threads,
            locks)
        {
            Source = "clrmd-thread-walk",
        };
        return snapshot;
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
