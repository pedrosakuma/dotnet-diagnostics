using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.CpuEfficiency;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Threads;
using static DotnetDiagnostics.Core.Tests.SamplerCaptureObservationTests;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SnapshotCaptureProjectionTests
{
    private static readonly DateTimeOffset CapturedAt = DateTimeOffset.Parse("2026-09-24T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void ThreadRows_AreRetainedNotOccurrences_WithIndexedFramesAndHiddenLockRoles()
    {
        var thread = new ManagedThread(3, 55, 100, "waiting", true, false, false, false, true, 1, null, "类型.Wait",
            [new ManagedStackFrame("managed", "类型.Wait", "类型", "模块", 123, 456)])
        {
            IsLockWaiter = true,
            IsContendedLockOwner = true,
            IsDeadlockCandidate = true,
        };
        var snapshot = new ThreadSnapshotArtifact(ThreadSnapshotOrigin.Dump, 42, CapturedAt, TimeSpan.Zero, "CoreClr", "10",
            [thread], [new MonitorLockState(1000, "锁", 3, 55, 100, 1, 2, true, "clrmd")])
        { DumpFilePath = "/nonexistent/not-opened.dmp" };
        var sink = new ObservationTestSink();
        SnapshotObservationProjection.Emit("thread-snapshot", snapshot, sink);
        Assert.Equal(4, sink.Rows.Count);
        AssertDerived(sink);
        var row = Assert.Single(sink.Rows, r => r.Category == "snapshot.thread.thread");
        Assert.Equal(55, row.ThreadId);
        Assert.True(Field(row, "IsLockWaiter").Boolean);
        Assert.True(Field(row, "IsContendedLockOwner").Boolean);
        Assert.True(Field(row, "IsDeadlockCandidate").Boolean);
        var frame = Assert.Single(sink.Rows, r => r.Category == "snapshot.thread.frame");
        Assert.Equal(0, Field(frame, "frameIndex").Integer);
        Assert.Equal("类型.Wait", frame.Name);
        Assert.Equal(55, frame.ThreadId);
        Assert.Single(thread.Frames);
    }

    [Fact]
    public void HeapRows_OnlyExposeRetainedTypesRootsSegmentsAndSelectedPaths_NotFullGraphOrNativeViews()
    {
        var snapshot = Heap(HeapSnapshotOrigin.Dump) with
        {
            DumpFilePath = "/nonexistent/not-opened.dmp",
            RootsByKind = [new RootKindStat("Stack", 2, 1, 80, 0, 0)],
            Segments = [new SegmentStat(0, "Large", "LOH", 10, 1000, 990, 990, 990, 80, 910, 1, 1)],
            RetentionPaths = [new RetentionPath("类型", 300, [new RetentionFrame("Root", 100)], false)],
        };
        var sink = new ObservationTestSink();
        SnapshotObservationProjection.Emit("heap-snapshot", snapshot, sink);
        AssertDerived(sink);
        Assert.Contains(sink.Rows, r => r.Category == "snapshot.heap.type-by-bytes" && r.Name == "类型");
        Assert.Contains(sink.Rows, r => r.Category == "snapshot.heap.root-kind");
        Assert.Contains(sink.Rows, r => r.Category == "snapshot.heap.segment");
        Assert.Contains(sink.Rows, r => r.Category == "snapshot.heap.retention-path");
        var metadata = sink.Rows[0];
        Assert.False(Field(metadata, "fullObjectGraph").Boolean);
        var views = Field(metadata, "availableViews").Text!;
        Assert.Contains("retention-paths", views);
        Assert.Contains("fragmentation", views);
        Assert.DoesNotContain("duplicate-strings", views);
        Assert.DoesNotContain("gcroot", views);
        Assert.DoesNotContain("objsize", views);
        Assert.DoesNotContain(sink.Rows, r => r.Category == "snapshot.heap.object");
    }

    [Fact]
    public void GcDumpViews_RemainTypeOnly_AndRejectionDoesNotMutateSnapshot()
    {
        var snapshot = Heap(HeapSnapshotOrigin.GcDump);
        var sink = new ObservationTestSink { Accept = false };
        SnapshotObservationProjection.Emit("heap-snapshot", snapshot, sink);
        Assert.Equal("top-types", Field(sink.Rows[0], "availableViews").Text);
        Assert.Single(snapshot.TopTypesByBytes);
        AssertDerived(sink);
    }

    [Fact]
    public void OptionalHeapRows_UseCodecOwnedTypesAndKeepPerRowDimensions()
    {
        var snapshot = Heap(HeapSnapshotOrigin.Live) with
        {
            FinalizableObjectsByType = [new FinalizableTypeStat("Finalizer", "m", 1, 20)],
            StaticFields = [new StaticFieldStat("T", "m", "Root", 1, 123, "Target", 20, 1)],
            DelegateTargets = [new DelegateTargetStat("Target", "T", "Callback", null, "m", 2)],
            AsyncOperations = [new AsyncOperationStat("StateMachine", 1, null, 20)],
            GcHandles = new GcHandlesView(1,
                [new GcHandleBucket("Strong", 1, 20, [new GcHandleTypeStat("Target", 1, 20, null)])], []),
            Timers = new TaskTimerLeakView(1, 1, 1,
                [new TimerCallbackStat("Timer", null, "T", "Callback", null, 1)],
                [new TaskTypeStat("Task", "m", 1, 20)],
                [new TaskTypeStat("TaskCompletionSource", "m", 1, 20)], []),
            AssemblyLoadContexts = new AssemblyLoadContextLeakView(1, 1, 0,
                [new AssemblyLoadContextStat(123, "ALC", "context", true, false, 1,
                    [new AssemblyLoadContextAssemblyStat("A", "m", "/not-opened", 1, 20)])], []),
        };
        var sink = new ObservationTestSink();
        SnapshotObservationProjection.Emit("heap-snapshot", snapshot, sink);
        AssertDerived(sink);
        Assert.All(sink.Rows.Skip(1), row => Assert.False(Field(row, "structuredOmitted").Boolean));
        Assert.Equal("Strong", Field(Assert.Single(sink.Rows, r => r.Category == "snapshot.heap.gc-handle-type"), "handleKind").Text);
        Assert.Equal("123", Field(Assert.Single(sink.Rows, r => r.Category == "snapshot.heap.assembly"), "contextAddress").Text);
        Assert.Contains(sink.Rows, r => r.Category == "snapshot.heap.static-field" && r.Name == "Root");
        Assert.Contains(sink.Rows, r => r.Category == "snapshot.heap.timer-callback" && r.Name == "Callback");
    }

    [Fact]
    public void Efficiency_RemainsWholeWindowAggregate_WithNullUnavailableMetrics()
    {
        var sink = new ObservationTestSink();
        SnapshotObservationProjection.Emit("cpu-efficiency-sample",
            new CpuEfficiencySample(42, CapturedAt, TimeSpan.FromSeconds(3), "perf-stat", Instructions: 200, Cycles: 100), sink);
        AssertDerived(sink);
        var row = Assert.Single(sink.Rows, r => r.Category == "snapshot.cpu-efficiency.window");
        Assert.Equal(200, Field(row, "Instructions").Integer);
        Assert.Equal(CaptureObservationValueKind.Null, Field(row, "CacheMisses").Kind);
        Assert.Equal("", Field(sink.Rows[0], "availableViews").Text);
    }

    [Fact]
    public void RequestsNow_RetainsRequestDimensionsWithoutInventingEventUtc()
    {
        var sink = new ObservationTestSink();
        SnapshotObservationProjection.Emit("requests-now",
            new RequestsNowSnapshot(42, CapturedAt, TimeSpan.FromSeconds(2),
                [new InFlightHttpRequest("trace-α", "/用户", "GET", 123.5, 55, ["类型.Method"])]), sink);
        AssertDerived(sink);
        var row = sink.Rows[1];
        Assert.Equal("/用户", row.Name);
        Assert.Equal(123.5, Field(row, "StartedAtMs").Number);
        Assert.Equal("snapshot-window-not-occurrence", Field(row, "timestampMeaning").Text);
        Assert.Equal(CapturedAt, row.Timestamp);
    }

    [Fact]
    public void UnknownKindOrMismatchedArtifact_IsRejectedBeforeEmission()
    {
        var sink = new ObservationTestSink();
        Assert.Throws<NotSupportedException>(() => SnapshotObservationProjection.Emit("not-a-kind", new object(), sink));
        Assert.ThrowsAny<Exception>(() => SnapshotObservationProjection.Emit("heap-snapshot", new object(), sink));
        Assert.Empty(sink.Rows);
    }

    [Fact]
    public void OversizedStructuredRow_IsExplicitlyMarked_NotUnbounded()
    {
        var sink = new ObservationTestSink();
        SnapshotObservationProjection.Emit("requests-now", new RequestsNowSnapshot(42, CapturedAt, TimeSpan.Zero,
            [new InFlightHttpRequest("trace", "/", "GET", 0, 1, [new string('x', 500000)])]), sink);
        Assert.True(Field(sink.Rows[1], "structuredOmitted").Boolean);
    }

    private static HeapSnapshotArtifact Heap(HeapSnapshotOrigin origin) => new(origin, 42, CapturedAt, TimeSpan.Zero,
        new DumpRuntimeInfo("CoreClr", "10", "x64", false, 1),
        new DumpHeapSummary(80, 80, 0, 0, 0, 0, 100),
        [new TypeStat("类型", "模块", 1, 80, 100)], []);

    private static void AssertDerived(ObservationTestSink sink)
        => Assert.All(sink.Rows, row =>
        {
            Assert.StartsWith("snapshot.", row.Category);
            Assert.False(Field(row, "sourceOccurrence").Boolean);
            Assert.True(Field(row, "derivedRetainedRow").Boolean);
        });
}
