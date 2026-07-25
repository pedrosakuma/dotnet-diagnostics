using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class DiagnosticProjectionShapeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void ThreadSnapshot_SummaryAndDetail_AreBoundedAndPrioritizeRunningEvidence()
    {
        var snapshot = ThreadSnapshot();

        var summaryThreads = ThreadSnapshotProjection.SelectThreads(
            snapshot,
            ThreadSnapshotProjection.SummaryThreadLimit,
            ThreadSnapshotProjection.SummaryThreadLimit,
            ThreadSnapshotProjection.SummaryFrameLimit);
        var detailThreads = ThreadSnapshotProjection.SelectThreads(
            snapshot,
            ThreadSnapshotProjection.DetailThreadLimit,
            ThreadSnapshotProjection.DetailThreadLimit,
            ThreadSnapshotProjection.DetailFrameLimit);
        var detailLocks = ThreadSnapshotProjection.SelectLocks(
            snapshot,
            ThreadSnapshotProjection.DetailLockLimit,
            ThreadSnapshotProjection.DetailLockLimit);

        summaryThreads.Should().HaveCount(ThreadSnapshotProjection.SummaryThreadLimit);
        summaryThreads.Should().OnlyContain(thread => thread.Frames.Count <= ThreadSnapshotProjection.SummaryFrameLimit);
        detailThreads.Should().HaveCount(ThreadSnapshotProjection.DetailThreadLimit);
        detailThreads.Should().OnlyContain(thread => thread.Frames.Count <= ThreadSnapshotProjection.DetailFrameLimit);
        var orderedThreadIds = summaryThreads.Select(thread => thread.ManagedThreadId).ToList();
        orderedThreadIds.IndexOf(90)
            .Should().BeLessThan(orderedThreadIds.IndexOf(5),
                "the running application frame must precede generic parked workers");
        snapshot.Threads.Should().OnlyContain(thread => thread.Frames.Count == 64, "projection must not mutate full handle evidence");

        var summary = BuildThreadResult(snapshot, summaryThreads, Array.Empty<MonitorLockState>(), ThreadSnapshotProjection.SummaryFrameLimit);
        var detail = BuildThreadResult(snapshot, detailThreads, detailLocks, ThreadSnapshotProjection.DetailFrameLimit);
        JsonSerializer.SerializeToUtf8Bytes(summary, JsonOptions).Length.Should().BeLessThan(32_000);
        JsonSerializer.SerializeToUtf8Bytes(detail, JsonOptions).Length.Should().BeLessThan(64_000);
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
        artifact.Root.Children.Should().HaveCount(160, "projection must not mutate the full call tree");
        JsonSerializer.SerializeToUtf8Bytes(outcome, JsonOptions).Length.Should().BeLessThan(64_000);
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
        IReadOnlyList<ManagedThread> threads,
        IReadOnlyList<MonitorLockState> locks,
        int frameLimit)
        => new("thread-shape", "threads-summary", "live", snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
        {
            Threads = threads,
            Locks = locks,
            TotalThreads = snapshot.Threads.Count,
            OmittedThreads = snapshot.Threads.Count - threads.Count,
            FramesPerThreadLimit = frameLimit,
            TotalLocks = snapshot.Locks.Count,
            OmittedLocks = snapshot.Locks.Count - locks.Count,
        };

    private static ThreadSnapshotArtifact ThreadSnapshot()
    {
        var threads = Enumerable.Range(1, 100)
            .Select(id => CreateThread(id, blocked: true, "System.Threading.LowLevelLifoSemaphore.WaitForSignal"))
            .ToArray();
        threads[89] = CreateThread(90, blocked: false, "MyCompany.Orders.PriceCalculator.RunningHotPath");

        return new ThreadSnapshotArtifact(
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
                    30 - index,
                    true,
                    "deterministic")
                {
                    WaitingManagedThreadIds = [2, 3, 4],
                })
                .ToArray());
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
            InferredWaitReason = blocked ? "ThreadPool park" : null,
        };
    }

    private static CpuSampleTraceArtifact BroadCpuTrace()
    {
        var children = Enumerable.Range(0, 160)
            .Select(index =>
            {
                var running = index == 159;
                return new CallTreeNode(
                    new SampledFrame(
                        "MyCompany.Application.Component.dll",
                        running
                            ? "MyCompany.Orders.PriceCalculator.RunningHotPath"
                            : $"System.Threading.GenericWaitNoise{index:D3}.WaitForSignal"),
                    running ? 10 : 1_000 - index,
                    running ? 10 : 1_000 - index,
                    Array.Empty<CallTreeNode>())
                {
                    SelfSamples = running
                        ? new SelfSampleBreakdown(10, 0)
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
            .Select(pathIndex => new RetentionPath(
                $"MyCompany.Leaks.Payload{pathIndex:D2}",
                (ulong)(0x1000 + pathIndex),
                Enumerable.Range(0, 30)
                    .Select(frameIndex => new RetentionFrame(
                        $"MyCompany.Retention.StableHolder{frameIndex:D2}",
                        (ulong)(0x10_000 + pathIndex * 100 + frameIndex)))
                    .ToArray(),
                Truncated: false))
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
}
