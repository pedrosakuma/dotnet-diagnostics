using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class DiagnosticProjectionShapeTests
{
    [Fact]
    public void ThreadProjection_LegacyPageConstructorsAndDeconstructionRemainAvailable()
    {
        var thread = LargePagingSnapshot(threadCount: 1, lockCount: 1).Threads[0];
        var lockState = LargePagingSnapshot(threadCount: 1, lockCount: 1).Locks[0];
        var page = new BoundedProjectionPage<ManagedThread>([thread], 1, 0, null);
        var waiterPage = new ProjectedLockWaiterPage(lockState, 0, null);

        var (items, total, offset, nextOffset) = page;
        var (selectedLock, waiterOffset, nextWaiterOffset) = waiterPage;

        items.Should().ContainSingle().Which.Should().BeSameAs(thread);
        total.Should().Be(1);
        offset.Should().Be(0);
        nextOffset.Should().BeNull();
        selectedLock.Should().BeSameAs(lockState);
        waiterOffset.Should().Be(0);
        nextWaiterOffset.Should().BeNull();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void ThreadSnapshot_SummaryAndDetail_AreBoundedAndPrioritizeRunningEvidence()
    {
        var snapshot = ThreadSnapshot();

        var summaryThreads = ThreadSnapshotProjection.ProjectThreads(
            snapshot,
            ThreadSnapshotProjection.SummaryThreadLimit,
            ThreadSnapshotProjection.SummaryThreadLimit,
            ThreadSnapshotProjection.SummaryFrameLimit);
        var detailThreads = ThreadSnapshotProjection.ProjectThreads(
            snapshot,
            ThreadSnapshotProjection.DetailThreadLimit,
            ThreadSnapshotProjection.DetailThreadLimit,
            ThreadSnapshotProjection.DetailFrameLimit);
        var detailLocks = ThreadSnapshotProjection.ProjectLocks(
            snapshot,
            ThreadSnapshotProjection.DetailLockLimit,
            ThreadSnapshotProjection.DetailLockLimit);

        summaryThreads.Items.Should().HaveCount(ThreadSnapshotProjection.SummaryThreadLimit);
        summaryThreads.Items.Should().OnlyContain(thread => thread.Frames.Count <= ThreadSnapshotProjection.SummaryFrameLimit);
        detailThreads.Items.Should().HaveCount(ThreadSnapshotProjection.DetailThreadLimit);
        detailThreads.Items.Should().OnlyContain(thread => thread.Frames.Count <= ThreadSnapshotProjection.DetailFrameLimit);
        detailLocks.Items.Should().OnlyContain(lockState =>
            lockState.WaitingManagedThreadIds.Count <= ThreadSnapshotProjection.LockWaiterIdLimit &&
            lockState.TotalWaitingManagedThreadIds == 10_000 &&
            lockState.OmittedWaitingManagedThreadIds == 10_000 - ThreadSnapshotProjection.LockWaiterIdLimit);
        var orderedThreadIds = summaryThreads.Items.Select(thread => thread.ManagedThreadId).ToList();
        orderedThreadIds.IndexOf(90)
            .Should().BeLessThan(orderedThreadIds.IndexOf(5),
                "the running application frame must precede generic parked workers");
        snapshot.Threads.Should().OnlyContain(thread => thread.Frames.Count == 64, "projection must not mutate full handle evidence");

        snapshot.Locks.Should().OnlyContain(lockState => lockState.WaitingManagedThreadIds.Count == 10_000,
            "projection must not mutate full lock waiter evidence");

        var summary = BuildThreadResult(snapshot, summaryThreads, null, ThreadSnapshotProjection.SummaryFrameLimit);
        var detail = BuildThreadResult(snapshot, detailThreads, detailLocks, ThreadSnapshotProjection.DetailFrameLimit);
        JsonSerializer.SerializeToUtf8Bytes(summary, JsonOptions).Length.Should().BeLessThan(32_000);
        JsonSerializer.SerializeToUtf8Bytes(detail, JsonOptions).Length.Should().BeLessThan(64_000);
    }

    [Fact]
    public void ThreadSnapshot_RegistrationAndFirstPageAllocateIndependentlyOfCaptureVolume()
    {
        var small = LargePagingSnapshot(threadCount: 1_000, lockCount: 500);
        var large = LargePagingSnapshot(threadCount: 20_000, lockCount: 10_000);
        _ = MeasureRegistrationAndFirstPageAllocations(small);
        var smallAllocated = MeasureRegistrationAndFirstPageAllocations(small);
        var largeAllocated = MeasureRegistrationAndFirstPageAllocations(large);

        largeAllocated.Should().BeLessThanOrEqualTo(
            smallAllocated + 256_000,
            "registration and projection must retain and allocate only bounded page-sized state, not a capture-sized ranking index");

        var threadPage = ThreadSnapshotProjection.ProjectThreads(
            large,
            requestedCount: 50,
            hardThreadLimit: ThreadSnapshotProjection.QueryThreadLimit,
            frameLimit: ThreadSnapshotProjection.QueryFrameLimit,
            offset: 160);
        var lockPage = ThreadSnapshotProjection.ProjectLocks(
            large,
            requestedCount: 50,
            hardLimit: ThreadSnapshotProjection.DetailLockLimit,
            offset: 240);
        threadPage.Items.Should().HaveCount(ThreadSnapshotProjection.QueryThreadLimit);
        threadPage.TotalItems.Should().Be(20_000);
        threadPage.NextOffset.Should().Be(168);
        lockPage.Items.Should().HaveCount(ThreadSnapshotProjection.DetailLockLimit);
        lockPage.TotalItems.Should().Be(10_000);
        lockPage.NextOffset.Should().Be(252);
    }

    [Fact]
    public void ThreadSnapshot_PathologicalOffsetsRejectBeforeSelectionWork()
    {
        var source = LargePagingSnapshot(threadCount: 20_000, lockCount: 10_000);
        var threads = new CountingReadOnlyList<ManagedThread>(source.Threads);
        var blockedThreads = new CountingReadOnlyList<ManagedThread>(source.Threads);
        var locks = new CountingReadOnlyList<MonitorLockState>(source.Locks);
        var waiters = new CountingReadOnlyList<int>(source.Locks[0].WaitingManagedThreadIds);
        var firstLock = source.Locks[0] with { WaitingManagedThreadIds = waiters };
        var lockItems = source.Locks.ToArray();
        lockItems[0] = firstLock;
        locks = new CountingReadOnlyList<MonitorLockState>(lockItems);
        var snapshot = source with { Threads = threads, Locks = locks };
        var blockedSnapshot = source with { Threads = blockedThreads, Locks = locks };

        var before = GC.GetAllocatedBytesForCurrentThread();
        var rejected = 0;
        try
        {
            ThreadSnapshotProjection.ProjectThreads(
                snapshot, 50, ThreadSnapshotProjection.QueryThreadLimit, ThreadSnapshotProjection.QueryFrameLimit,
                offset: int.MaxValue);
        }
        catch (ThreadSnapshotDeepOffsetException)
        {
            rejected++;
        }
        try
        {
            ThreadSnapshotProjection.ProjectLocks(
                snapshot, 50, ThreadSnapshotProjection.DetailLockLimit, offset: int.MaxValue);
        }
        catch (ThreadSnapshotDeepOffsetException)
        {
            rejected++;
        }
        try
        {
            ThreadSnapshotProjection.ProjectThreads(
                blockedSnapshot, 50, ThreadSnapshotProjection.QueryThreadLimit, ThreadSnapshotProjection.QueryFrameLimit,
                blockedOnly: true, offset: int.MaxValue);
        }
        catch (ThreadSnapshotDeepOffsetException)
        {
            rejected++;
        }
        try
        {
            ThreadSnapshotProjection.ProjectLock(firstLock, int.MaxValue);
        }
        catch (ThreadSnapshotDeepOffsetException)
        {
            rejected++;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        rejected.Should().Be(4);
        threads.IndexReadCount.Should().Be(0);
        threads.EnumerationMoveCount.Should().Be(0);
        blockedThreads.IndexReadCount.Should().Be(0);
        blockedThreads.EnumerationMoveCount.Should().Be(0);
        locks.IndexReadCount.Should().Be(0);
        locks.EnumerationMoveCount.Should().Be(0);
        waiters.IndexReadCount.Should().Be(0);
        allocated.Should().BeLessThan(64_000);
    }

    [Fact]
    public void ThreadSnapshot_NearTerminalCursorsHaveDepthIndependentSelectionWork()
    {
        const int itemCount = 20_000;
        const string handle = "thread-large";
        var frame = new ManagedStackFrame("ManagedMethod", "App.Work", "App.Worker", "App.dll", 1, 2);
        var threadItems = Enumerable.Range(1, itemCount)
            .Select(id => new ManagedThread(id, (uint)id, (ulong)id, "Running", true, false, false, false, true, 0, null, frame.DisplayName, [frame]))
            .ToArray();
        var lockItems = Enumerable.Range(0, itemCount)
            .Select(index => new MonitorLockState(
                (ulong)(0x100_000 + index), "App.Lock", 1, 1, 1, 0, 1, true, "test"))
            .ToArray();
        var threads = new CountingReadOnlyList<ManagedThread>(threadItems);
        var locks = new CountingReadOnlyList<MonitorLockState>(lockItems);
        var snapshot = new ThreadSnapshotArtifact(
            ThreadSnapshotOrigin.Live, 42, DateTimeOffset.UnixEpoch, TimeSpan.Zero,
            "CoreClr", "10.0", threads, locks);

        var threadCursor = ThreadSnapshotCursorCodec.EncodeThread(
            handle,
            new ThreadSnapshotCursorCodec.ThreadCursor(
                BlockedOnly: false,
                UsedFallback: false,
                Position: itemCount - ThreadSnapshotProjection.QueryThreadLimit,
                Rank: 3,
                LockCount: 0,
                FrameCount: 1,
                ManagedThreadId: itemCount - ThreadSnapshotProjection.QueryThreadLimit,
                OriginalIndex: itemCount - ThreadSnapshotProjection.QueryThreadLimit - 1));
        var lockPosition = itemCount - ThreadSnapshotProjection.DetailLockLimit;
        var lockCursor = ThreadSnapshotCursorCodec.EncodeLock(
            handle,
            new ThreadSnapshotCursorCodec.LockCursor(
                lockPosition,
                IsContended: true,
                WaitingThreadCount: 1,
                RecursionCount: 0,
                ObjectAddress: lockItems[lockPosition - 1].ObjectAddress,
                OriginalIndex: lockPosition - 1));

        var threadPage = ThreadSnapshotQueryDispatcher.DispatchCursor(
            snapshot, handle, "threads-summary", null, 50, 20, 1, cursor: threadCursor);
        var lockPage = ThreadSnapshotQueryDispatcher.DispatchCursor(
            snapshot, handle, "lock-graph", null, 50, 20, 1, cursor: lockCursor);

        threadPage.Error.Should().BeNull();
        threadPage.Data!.Threads!.Select(thread => thread.ManagedThreadId)
            .Should().Equal(Enumerable.Range(itemCount - 7, 8));
        threadPage.Data.NextThreadCursor.Should().BeNull();
        lockPage.Error.Should().BeNull();
        lockPage.Data!.Locks.Should().HaveCount(ThreadSnapshotProjection.DetailLockLimit);
        lockPage.Data.NextLockCursor.Should().BeNull();
        threads.IndexReadCount.Should().BeLessThanOrEqualTo(itemCount * 10);
        locks.IndexReadCount.Should().BeLessThanOrEqualTo(itemCount * 14);
    }

    [Fact]
    public void ClrMdLockRoleStampingReusesCapturedThreadsWithoutCaptureSizedAllocations()
    {
        var snapshot = LargePagingSnapshot(threadCount: 20_000, lockCount: 10_000);
        var threads = snapshot.Threads
            .Select(thread => thread with
            {
                IsContendedLockOwner = false,
                IsLockWaiter = false,
                IsDeadlockCandidate = false,
            })
            .ToDictionary(thread => thread.ManagedThreadId);
        var originalReferences = threads.Values.ToArray();
        var warmThreads = LargePagingSnapshot(threadCount: 20, lockCount: 10)
            .Threads.ToDictionary(thread => thread.ManagedThreadId);
        var warmLocks = LargePagingSnapshot(threadCount: 20, lockCount: 10).Locks;
        ClrMdThreadSnapshotInspector.StampLockRoles(warmThreads, warmLocks);

        var before = GC.GetAllocatedBytesForCurrentThread();
        ClrMdThreadSnapshotInspector.StampLockRoles(threads, snapshot.Locks);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        threads.Values.Zip(originalReferences)
            .Should().OnlyContain(pair => ReferenceEquals(pair.First, pair.Second));
        threads[1].IsContendedLockOwner.Should().BeTrue();
        threads.Values.Should().Contain(thread => thread.IsLockWaiter);
        allocated.Should().BeLessThan(4_096,
            "the production role helper must reuse the required thread dictionary rather than clone threads or build owner/waiter sets");
    }

    [Fact]
    public void CpuCallTree_BroadRequest_IsBoundedAndRanksRunningBranchFirst()
    {
        var artifact = BroadCpuTrace();

        var outcome = CpuSampleQueryDispatcher.RenderCallTree(
            artifact,
            "cpu-shape",
            rootMethodFilter: null,
            maxDepth: 20,
            maxNodes: 500);

        outcome.Error.Should().BeNull();
        outcome.Data!.NodeCount.Should().BeLessThanOrEqualTo(CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes);
        outcome.Data.DepthLimit.Should().Be(CpuSampleQueryDispatcher.MaxProjectedCallTreeDepth);
        outcome.Data.Truncated.Should().BeTrue();
        outcome.Data.Root.Children[0].Frame.Method.Should().Contain("RunningHotPath");
        outcome.Data.Root.Children[0].Identity.Should().NotBeNull();
        outcome.Hints.Should().ContainSingle();
        outcome.Hints[0].SuggestedArguments!["view"].Should().Be(CpuSampleQueryDispatcher.TopMethodsView);
        artifact.Root.Children.Should().HaveCount(400, "projection must not mutate the full call tree");
        JsonSerializer.SerializeToUtf8Bytes(outcome, JsonOptions).Length.Should().BeLessThan(64_000);
    }

    [Fact]
    public void CpuCallTree_ReservesSelectedSiblingSlotsBeforeWalkingWideFirstBranch()
    {
        var artifact = FairSiblingCpuTrace();

        var outcome = CpuSampleQueryDispatcher.RenderCallTree(
            artifact,
            "cpu-fair-siblings",
            rootMethodFilter: null,
            maxDepth: CpuSampleQueryDispatcher.MaxProjectedCallTreeDepth,
            maxNodes: 8);

        outcome.Error.Should().BeNull();
        outcome.Data!.NodeCount.Should().Be(8);
        outcome.Data.Root.Children.Select(child => child.Frame.Method).Should().Contain(
            "MyCompany.DecisiveLate397",
            "MyCompany.DecisiveLate398",
            "MyCompany.DecisiveLate399");
        outcome.Data.Root.Children.Should().OnlyContain(child => child.Children.Count == 0,
            "all remaining slots are reserved for globally selected direct children before descendants");
        outcome.Data.Truncated.Should().BeTrue();
    }

    [Fact]
    public void RetentionPaths_AreBoundedAndKeepTypedAddressIdentity()
    {
        var snapshot = HeapSnapshot();

        var outcome = HeapSnapshotQueryDispatcher.Dispatch(
            snapshot,
            "heap-shape",
            "retention-paths",
            topN: 50,
            rankBy: "bytes",
            typeFullName: null);

        var result = outcome.Result!;
        result.Error.Should().BeNull();
        result.Data!.RetentionPaths.Should().HaveCount(HeapSnapshotQueryDispatcher.MaxProjectedRetentionPaths);
        result.Data.RetentionPaths.Should().OnlyContain(path =>
            path.Chain.Count <= HeapSnapshotQueryDispatcher.MaxProjectedRetentionFrames);
        result.Data.RetentionPaths
            .SelectMany(path => path.Chain)
            .Should().OnlyContain(frame =>
                (frame.TypeFullName != "<retainer>" && frame.ObjectAddress != 0) || frame.RootKind != null);
        result.Data.TotalRetentionPaths.Should().Be(30);
        result.Data.OmittedRetentionPaths.Should().Be(20);
        result.Data.RetentionPaths.Should().OnlyContain(path =>
            path.Chain[0].ObjectAddress == path.TargetObjectAddress &&
            path.Chain[path.Chain.Count - 1].RootKind == "StaticVar" &&
            path.Chain[path.Chain.Count - 1].ObjectAddress == 0);
        snapshot.RetentionPaths![0].Chain.Should().HaveCount(30, "projection must not mutate full handle evidence");
        JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length.Should().BeLessThan(48_000);
    }

    [Fact]
    public void RetentionChain_ResolvesIntermediateTypesWithoutExtraObjectDrilldowns()
    {
        var map = new Dictionary<ulong, (ulong From, string? RootKind)>
        {
            [0x1000] = (0x2000, null),
            [0x2000] = (0, "StaticVar"),
        };
        var types = new Dictionary<ulong, string> { [0x2000] = "MyApp.StableHolder" };

        var chain = ClrMdRetentionAnalyzer.BuildRetentionChain(
            "MyApp.LeakedItem",
            0x1000,
            map,
            depthLimit: 8,
            address => types.GetValueOrDefault(address),
            out var truncated);

        truncated.Should().BeFalse();
        chain.Select(frame => (frame.TypeFullName, frame.ObjectAddress, frame.RootKind)).Should().Equal(
            ("MyApp.LeakedItem", 0x1000UL, null),
            ("MyApp.StableHolder", 0x2000UL, null),
            ("<root>", 0UL, "StaticVar"));
    }

    private static ThreadSnapshotQueryResult BuildThreadResult(
        ThreadSnapshotArtifact snapshot,
        BoundedProjectionPage<ManagedThread> threads,
        BoundedProjectionPage<MonitorLockState>? locks,
        int frameLimit)
        => new("thread-shape", "threads-summary", "live", snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
        {
            Threads = threads.Items,
            Locks = locks?.Items ?? Array.Empty<MonitorLockState>(),
            TotalThreads = snapshot.Threads.Count,
            CandidateThreads = threads.TotalItems,
            OmittedThreads = threads.TotalItems - threads.Items.Count,
            FramesPerThreadLimit = frameLimit,
            TotalLocks = snapshot.Locks.Count,
            OmittedLocks = snapshot.Locks.Count - (locks?.Items.Count ?? 0),
            ThreadOffset = threads.Offset,
            NextThreadOffset = threads.NextOffset,
            LockOffset = locks?.Offset,
            NextLockOffset = locks?.NextOffset,
        };

    private static ThreadSnapshotArtifact ThreadSnapshot()
    {
        var threads = Enumerable.Range(1, 100)
            .Select(id => CreateThread(id, blocked: true, "System.Threading.LowLevelLifoSemaphore.WaitForSignal"))
            .ToArray();
        threads[89] = CreateThread(90, blocked: false, "MyCompany.Orders.PriceCalculator.RunningHotPath");

        var snapshot = new ThreadSnapshotArtifact(
            ThreadSnapshotOrigin.Live,
            4242,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMilliseconds(25),
            "CoreCLR",
            "10.0.0",
            threads,
            Enumerable.Range(0, 30)
                .Select(index => new MonitorLockState(
                    (ulong)(0x30_000 + index),
                    "MyCompany.Diagnostics.DeterministicScenario.With.Long.Lock.Type.Name",
                    1,
                    10_001,
                    0x40_000,
                    index % 3,
                    10_000,
                    true,
                    "deterministic")
                {
                    WaitingManagedThreadIds = Enumerable.Range(1, 10_000).ToArray(),
                })
                .ToArray());
        return snapshot;
    }

    private static ManagedThread CreateThread(int id, bool blocked, string topFrame)
    {
        var frames = Enumerable.Range(0, 64)
            .Select(index => new ManagedStackFrame(
                "ManagedMethod",
                $"{topFrame}.Frame{index:D2}(System.String,System.Collections.Generic.Dictionary`2)",
                "MyCompany.Diagnostics.DeterministicScenario.With.Long.Type.Name",
                "MyCompany.Application.Component.dll",
                (ulong)(0x1000 + index),
                (ulong)(0x2000 + index),
                Identity(index)))
            .ToArray();
        return new ManagedThread(
            id,
            (uint)(10_000 + id),
            (ulong)id,
            blocked ? "Wait" : "Running",
            true,
            false,
            false,
            false,
            true,
            0,
            null,
            topFrame,
            frames)
        {
            IsLikelyBlocked = blocked,
            IsContendedLockOwner = id == 1,
            IsLockWaiter = true,
            IsDeadlockCandidate = id == 1,
            InferredWaitReason = blocked ? "ThreadPool park" : null,
        };
    }

    private static CpuSampleTraceArtifact BroadCpuTrace()
    {
        var children = Enumerable.Range(0, 400)
            .Select(index =>
            {
                var running = index == 399;
                var runningLeaf = new CallTreeNode(
                    new SampledFrame(
                        "MyCompany.Application.Component.dll",
                        "MyCompany.Orders.PriceCalculator.RunningHotPath.Leaf"),
                    10,
                    10,
                    Array.Empty<CallTreeNode>())
                {
                    SelfSamples = new SelfSampleBreakdown(10, 0),
                };
                return new CallTreeNode(
                    new SampledFrame(
                        "MyCompany.Application.Component.dll",
                        running
                            ? "MyCompany.Orders.PriceCalculator.RunningHotPath.Entry"
                            : $"System.Threading.GenericWaitNoise{index:D3}.WaitForSignal"),
                    running ? 10 : 1_000 - index,
                    running ? 0 : 1_000 - index,
                    running ? [runningLeaf] : Array.Empty<CallTreeNode>())
                {
                    SelfSamples = running
                        ? null
                        : new SelfSampleBreakdown(0, 1_000 - index),
                };
            })
            .ToArray();
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 100_000, 0, children);
        var identities = children.ToDictionary(
            child => new SymbolRef(child.Frame.Module, child.Frame.Method),
            child => Identity((int)child.ExclusiveSamples));
        return new CpuSampleTraceArtifact(4242, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(8), 100_000, root)
        {
            SelfSamples = new SelfSampleBreakdown(10, 99_990),
            MethodIdentities = identities,
        };
    }

    private static long MeasureRegistrationAndFirstPageAllocations(ThreadSnapshotArtifact snapshot)
    {
        var handles = new MemoryDiagnosticHandleStore();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var handle = handles.Register(snapshot.ProcessId, "thread-snapshot", snapshot, TimeSpan.FromMinutes(10));
        var threadPage = ThreadSnapshotProjection.ProjectThreads(
            snapshot,
            requestedCount: 50,
            hardThreadLimit: ThreadSnapshotProjection.QueryThreadLimit,
            frameLimit: ThreadSnapshotProjection.QueryFrameLimit);
        var lockPage = ThreadSnapshotProjection.ProjectLocks(
            snapshot,
            requestedCount: 50,
            hardLimit: ThreadSnapshotProjection.DetailLockLimit);
        GC.KeepAlive((handle, threadPage, lockPage));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static ThreadSnapshotArtifact LargePagingSnapshot(int threadCount, int lockCount)
    {
        var frames = Enumerable.Range(0, 16)
            .Select(index => new ManagedStackFrame(
                "ManagedMethod",
                $"App.Worker.Frame{index}",
                "App.Worker",
                "App.dll",
                (ulong)(0x1000 + index),
                (ulong)(0x2000 + index)))
            .ToArray();
        var threads = Enumerable.Range(1, threadCount)
            .Select(id => new ManagedThread(
                id,
                (uint)(10_000 + id),
                (ulong)id,
                id % 3 == 0 ? "Wait" : "Running",
                true,
                false,
                false,
                false,
                true,
                (uint)(id % 4),
                null,
                frames[0].DisplayName,
                frames)
            {
                IsLikelyBlocked = id % 3 == 0,
                IsContendedLockOwner = id <= lockCount,
                IsLockWaiter = lockCount > 0,
                IsDeadlockCandidate = id <= lockCount,
                InferredWaitReason = id % 3 == 0 ? "Monitor.Enter" : null,
            })
            .ToArray();
        var locks = Enumerable.Range(0, lockCount)
            .Select(index => new MonitorLockState(
                (ulong)(0x100_000 + index),
                $"App.Lock{index % 16}",
                index % threadCount + 1,
                (uint)(10_001 + index % threadCount),
                (ulong)(0x200_000 + index),
                index % 3,
                32,
                true,
                "test")
            {
                WaitingManagedThreadIds = Enumerable.Range(0, 32)
                    .Select(waiter => (index * 31 + waiter) % threadCount + 1)
                    .ToArray(),
            })
            .ToArray();
        return new ThreadSnapshotArtifact(
            ThreadSnapshotOrigin.Live,
            4242,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMilliseconds(50),
            "CoreClr",
            "10.0.0",
            threads,
            locks);
    }

    private static CpuSampleTraceArtifact FairSiblingCpuTrace()
    {
        static CallTreeNode RunningLeaf(string method, long runningSamples)
            => new(new SampledFrame("App.dll", method), runningSamples, runningSamples, Array.Empty<CallTreeNode>())
            {
                SelfSamples = new SelfSampleBreakdown(runningSamples, 0),
            };

        var children = Enumerable.Range(0, 400)
            .Select(index =>
            {
                if (index == 397)
                {
                    var wideChildren = Enumerable.Range(0, 100)
                        .Select(childIndex => childIndex == 99
                            ? RunningLeaf("MyCompany.DecisiveLate397.RunningLeaf", 30)
                            : new CallTreeNode(
                                new SampledFrame("App.dll", $"MyCompany.DecisiveLate397.DeepNoise{childIndex:D3}"),
                                1,
                                1,
                                Array.Empty<CallTreeNode>())
                            {
                                SelfSamples = new SelfSampleBreakdown(0, 1),
                            })
                        .ToArray();
                    return new CallTreeNode(
                        new SampledFrame("App.dll", "MyCompany.DecisiveLate397"),
                        129,
                        0,
                        wideChildren);
                }

                if (index is 398 or 399)
                {
                    var running = index == 398 ? 20 : 10;
                    return new CallTreeNode(
                        new SampledFrame("App.dll", $"MyCompany.DecisiveLate{index}"),
                        running,
                        0,
                        [RunningLeaf($"MyCompany.DecisiveLate{index}.RunningLeaf", running)]);
                }

                return new CallTreeNode(
                    new SampledFrame("System.Threading.dll", $"System.Threading.WaitNoise{index:D3}"),
                    10_000 - index,
                    10_000 - index,
                    Array.Empty<CallTreeNode>())
                {
                    SelfSamples = new SelfSampleBreakdown(0, 10_000 - index),
                };
            })
            .ToArray();
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 4_000_000, 0, children);
        return new CpuSampleTraceArtifact(4242, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(8), 4_000_000, root);
    }

    private static MethodIdentity Identity(int token)
        => new(
            "MyCompany.Diagnostics.DeterministicScenario.With.Long.Type.Name.Execute",
            0,
            "MyCompany.Application.Component.dll",
            "/opt/app/MyCompany.Application.Component.dll",
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            0x06000001 + Math.Abs(token % 1000),
            "MyCompany.Diagnostics.DeterministicScenario.With.Long.Type.Name");

    private static HeapSnapshotArtifact HeapSnapshot()
    {
        var paths = Enumerable.Range(0, 30)
            .Select(pathIndex =>
            {
                var targetAddress = (ulong)(0x1000 + pathIndex);
                var chain = new List<RetentionFrame>
                {
                    new($"MyCompany.Leaks.Payload{pathIndex:D2}", targetAddress),
                };
                chain.AddRange(Enumerable.Range(1, 28)
                    .Select(frameIndex => new RetentionFrame(
                        $"MyCompany.Retention.StableHolder{frameIndex:D2}",
                        (ulong)(0x10_000 + pathIndex * 100 + frameIndex))));
                chain.Add(new RetentionFrame("<root>", 0) { RootKind = "StaticVar" });
                return new RetentionPath(
                    $"MyCompany.Leaks.Payload{pathIndex:D2}",
                    targetAddress,
                    chain,
                    Truncated: false);
            })
            .ToArray();

        return new HeapSnapshotArtifact(
            HeapSnapshotOrigin.Live,
            4242,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMilliseconds(50),
            new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", false, 1),
            new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
            [new TypeStat("MyCompany.Leaks.Payload00", "App", 10, 1024, 100)],
            [new TypeStat("MyCompany.Leaks.Payload00", "App", 10, 1024, 100)])
        {
            RetentionPaths = paths,
        };
    }

    private sealed class CountingReadOnlyList<T>(IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int Count => items.Count;

        public int IndexReadCount { get; private set; }

        public int EnumerationMoveCount { get; private set; }

        public T this[int index]
        {
            get
            {
                IndexReadCount++;
                return items[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var item in items)
            {
                EnumerationMoveCount++;
                yield return item;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
