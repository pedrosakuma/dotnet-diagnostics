using System.Collections.Immutable;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit coverage for the host-neutral <see cref="HeapSnapshotQueryDispatcher"/> shared by the MCP
/// server's <c>query_heap_snapshot</c> tool and the CLI <c>session</c> REPL (#300). Asserts the
/// projection views render from a walked snapshot, that the four capability-bound views are reported
/// as <c>ServerOnlyView</c>, and that argument / view validation matches the server preamble.
/// </summary>
public class HeapSnapshotQueryDispatcherTests
{
    private const string Handle = "heap-abc";

    [Theory]
    [InlineData("top-types")]
    [InlineData("retention-paths")]
    [InlineData("roots-by-kind")]
    [InlineData("finalizer-queue")]
    [InlineData("fragmentation")]
    [InlineData("static-fields")]
    [InlineData("delegate-targets")]
    [InlineData("gchandles")]
    [InlineData("async")]
    [InlineData("timers")]
    public void ProjectionViews_RenderResult(string view)
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, view, topN: 10, rankBy: "bytes", typeFullName: null);

        outcome.ServerOnlyView.Should().BeFalse();
        outcome.UnknownView.Should().BeFalse();
        outcome.Result.Should().NotBeNull();
        outcome.Result!.Error.Should().BeNull();
        outcome.Result.Data!.View.Should().Be(view);
    }

    [Fact]
    public void TopTypes_RanksByInstances_WhenRequested()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "top-types", topN: 10, rankBy: "instances", typeFullName: null);

        outcome.Result!.Data!.RankBy.Should().Be("instances");
        outcome.Result.Data.TopTypes.Should().NotBeEmpty();
    }

    [Fact]
    public void TopTypes_NullRankBy_DefaultsToBytes()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "top-types", topN: 10, rankBy: null, typeFullName: null);

        outcome.Result!.Error.Should().BeNull();
        outcome.Result.Data!.RankBy.Should().Be("bytes");
    }

    [Fact]
    public void TopTypes_BadRankBy_ReturnsInvalidArgument()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "top-types", topN: 10, rankBy: "nonsense", typeFullName: null);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void View_IsNormalized_TrimAndCase()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "  TOP-TYPES  ", topN: 10, rankBy: "bytes", typeFullName: null);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.Data!.View.Should().Be("top-types");
    }

    [Theory]
    [InlineData("object")]
    [InlineData("gcroot")]
    [InlineData("objsize")]
    [InlineData("duplicate-strings")]
    public void ServerOnlyViews_AreReportedNotRendered(string view)
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, view, topN: 10, rankBy: "bytes", typeFullName: null);

        outcome.ServerOnlyView.Should().BeTrue();
        outcome.UnknownView.Should().BeFalse();
        outcome.Result.Should().BeNull();
    }

    [Fact]
    public void UnknownView_IsReported()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "nope", topN: 10, rankBy: "bytes", typeFullName: null);

        outcome.UnknownView.Should().BeTrue();
        outcome.ServerOnlyView.Should().BeFalse();
        outcome.Result.Should().BeNull();
    }

    [Fact]
    public void TopNBelowOne_ReturnsInvalidArgument()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "top-types", topN: 0, rankBy: "bytes", typeFullName: null);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void ServerOnlyView_TakesPrecedenceOverTopNGuard()
    {
        // Server preamble validates topN before computing the view, so the dispatcher must NOT
        // pre-empt a server-only routing decision with its own topN guard.
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "object", topN: 0, rankBy: "bytes", typeFullName: null);

        outcome.ServerOnlyView.Should().BeTrue();
        outcome.Result.Should().BeNull();
    }

    [Fact]
    public void ProjectionViews_ExposesTenViews_WithoutServerOnly()
    {
        HeapSnapshotQueryDispatcher.ProjectionViews.Should().HaveCount(10);
        HeapSnapshotQueryDispatcher.ProjectionViews.Should().NotContain("object");
        HeapSnapshotQueryDispatcher.ProjectionViews.Should().NotContain("duplicate-strings");
    }

    private static HeapSnapshotArtifact Snapshot() => new(
        Origin: HeapSnapshotOrigin.Live,
        ProcessId: 123,
        CapturedAt: DateTimeOffset.UtcNow,
        WalkDuration: TimeSpan.FromMilliseconds(50),
        Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", IsServerGC: false, HeapCount: 1),
        Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
        TopTypesByBytes: new[] { new TypeStat("System.String", "System.Private.CoreLib", 100, 4096, 40.0) },
        TopTypesByInstances: new[] { new TypeStat("System.Byte[]", "System.Private.CoreLib", 200, 2048, 20.0) })
    {
        RetentionPaths = new[] { new RetentionPath("System.String", 0x1000, new[] { new RetentionFrame("System.String", 0x1000) }, Truncated: false) },
        RootsByKind = new[] { new RootKindStat("StaticVar", 5, 5, 4096, 0, 0) },
        FinalizableObjectsByType = new[] { new FinalizableTypeStat("System.IO.FileStream", null, 3, 384) },
        Segments = new[] { new SegmentStat(0, "Gen2", "Gen2", 0x1000, 0x2000, 4096, 4096, 0, 2048, 2048, 10, 1) { FreePercent = 50.0 } },
        StaticFields = new[] { new StaticFieldStat("MyType", null, "Cache", 0x0A000001, 0x2000, "System.Collections.Generic.Dictionary`2", 4096, 1) },
        DelegateTargets = new[] { new DelegateTargetStat("Subscriber", "Publisher", "OnChanged", null, null, 7) },
        GcHandles = new GcHandlesView(2, ImmutableArray.Create(new GcHandleBucket("Strong", 2, 4096, ImmutableArray<GcHandleTypeStat>.Empty)), ImmutableArray<string>.Empty),
        AsyncOperations = new[] { new AsyncOperationStat("MyAsyncStateMachine", 0, "TaskAwaiter", 128) },
        Timers = new TaskTimerLeakView(1, 2, 1, [new TimerCallbackStat("System.Threading.TimerQueueTimer", null, "MyTimer", "Tick", null, 1)], [new TaskTypeStat("System.Threading.Tasks.Task", "System.Private.CoreLib", 2, 128)], [new TaskTypeStat("System.Threading.Tasks.TaskCompletionSource", "System.Private.CoreLib", 1, 64)], []),
    };
}
