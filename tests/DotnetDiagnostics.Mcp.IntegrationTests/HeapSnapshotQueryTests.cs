using System.Threading;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class HeapSnapshotQueryTests
{
    [Fact]
    public async Task QueryHeapSnapshot_GcHandlesView_ReturnsAggregatedHandleTable()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: 1234,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(150),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 128, 128, 512, 0, 0, 1024),
            TopTypesByBytes: Array.Empty<TypeStat>(),
            TopTypesByInstances: Array.Empty<TypeStat>())
        {
            GcHandles = new GcHandlesView(
                TotalHandles: 7,
                ByKind:
                [
                    new GcHandleBucket("Pinned", 3, 3072, [new GcHandleTypeStat("System.Byte[]", 3, 3072, null)]),
                    new GcHandleBucket("Normal", 2, 256, [new GcHandleTypeStat("MyApp.Root", 2, 256, null)]),
                    new GcHandleBucket("Weak", 1, 64, [new GcHandleTypeStat("MyApp.CacheEntry", 1, 64, null)]),
                    new GcHandleBucket("WeakTrackResurrection", 0, 0, []),
                    new GcHandleBucket("Dependent", 1, 128, [new GcHandleTypeStat("MyApp.Node", 1, 128, null)]),
                    new GcHandleBucket("AsyncPinned", 0, 0, []),
                ],
                Notes:
                [
                    "Encountered 1 RefCounted (48 bytes) handle(s). These ClrMD-internal kinds are counted in TotalHandles but are omitted from byKind because they do not map to public GCHandleType values.",
                ]),
        };

        var handle = store.Register(snapshot.ProcessId, "heap-snapshot", snapshot, TimeSpan.FromMinutes(10));

        var result = await DiagnosticTools.QueryHeapSnapshot(store, new StubDumpInspector(), new SensitiveDataRedactor(null), new SensitiveValueGate(null), TestPrincipalAccessors.Root, handle.Id, view: "gchandles");

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        result.Data!.View.Should().Be("gchandles");
        result.Data.GcHandles.Should().NotBeNull();
        result.Data.GcHandles!.TotalHandles.Should().Be(7);
        result.Data.GcHandles.ByKind.Should().ContainSingle(bucket => bucket.Kind == "Pinned" && bucket.Count == 3);
        result.Data.GcHandles.Notes.Should().ContainSingle();
        result.Summary.Should().Contain("GCHandle aggregation");
        result.Summary.Should().Contain("RefCounted");
    }

    [Fact]
    public async Task QueryHeapSnapshot_AsyncView_ReturnsPendingOperations()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: 1234,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(150),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 128, 128, 512, 0, 0, 1024),
            TopTypesByBytes: Array.Empty<TypeStat>(),
            TopTypesByInstances: Array.Empty<TypeStat>())
        {
            AsyncOperations =
            [
                new AsyncOperationStat("MyApp.AsyncFixture+<LeafAsync>d__3", 0, "System.Runtime.CompilerServices.TaskAwaiter", 192)
                {
                    StateMachineAddress = 0x1000,
                    TaskAddress = 0x2000,
                    TaskId = 77,
                    TaskTypeFullName = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[MyApp.AsyncFixture+<LeafAsync>d__3]]",
                    ContinuationObjectTypeFullName = "System.Threading.Tasks.Task+ContinuationResultTaskFromTask`1",
                    ObservedOrder = 4,
                    Stack =
                    [
                        new AsyncChainFrame("MyApp.AsyncFixture+<LeafAsync>d__3", 0, "System.Runtime.CompilerServices.TaskAwaiter", 0x1000)
                        {
                            TaskAddress = 0x2000,
                            TaskId = 77,
                            ContinuationObjectTypeFullName = "System.Threading.Tasks.Task+ContinuationResultTaskFromTask`1",
                        },
                        new AsyncChainFrame("MyApp.AsyncFixture+<OuterAsync>d__1", 0, "System.Runtime.CompilerServices.TaskAwaiter", 0x3000)
                        {
                            TaskAddress = 0x4000,
                            TaskId = 78,
                        },
                    ],
                },
            ],
        };

        var handle = store.Register(snapshot.ProcessId, "heap-snapshot", snapshot, TimeSpan.FromMinutes(10));

        var result = await DiagnosticTools.QueryHeapSnapshot(store, new StubDumpInspector(), new SensitiveDataRedactor(null), new SensitiveValueGate(null), TestPrincipalAccessors.Root, handle.Id, view: "async", topN: 10);

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        result.Data!.View.Should().Be("async");
        result.Data.AsyncOperations.Should().ContainSingle();
        result.Data.SortedBy.Should().Be("heap-order");
        result.Summary.Should().Contain("First pending state machine in heap-walk order");
        result.Data.AsyncOperations![0].Stack.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryHeapSnapshot_TimersView_ReturnsTaskTimerLeakCandidates()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: 1234,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(150),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 128, 128, 512, 0, 0, 1024),
            TopTypesByBytes: Array.Empty<TypeStat>(),
            TopTypesByInstances: Array.Empty<TypeStat>())
        {
            Timers = new TaskTimerLeakView(
                TotalTimers: 5,
                TotalTasks: 7,
                TotalTaskCompletionSources: 2,
                TimersByCallback:
                [
                    new TimerCallbackStat(
                        TimerTypeFullName: "System.Threading.TimerQueueTimer",
                        CallbackTargetTypeFullName: "MyApp.LeakyTimer",
                        DeclaringTypeFullName: "MyApp.LeakyTimer",
                        MethodName: "OnTick",
                        MethodSignature: "void OnTick(object)",
                        Count: 5),
                ],
                TasksByType:
                [
                    new TaskTypeStat("System.Threading.Tasks.Task", "System.Private.CoreLib.dll", 7, 448),
                ],
                TaskCompletionSourcesByType:
                [
                    new TaskTypeStat("System.Threading.Tasks.TaskCompletionSource`1", "System.Private.CoreLib.dll", 2, 128),
                ],
                Notes: []),
        };

        var handle = store.Register(snapshot.ProcessId, "heap-snapshot", snapshot, TimeSpan.FromMinutes(10));

        var result = await DiagnosticTools.QueryHeapSnapshot(store, new StubDumpInspector(), new SensitiveDataRedactor(null), new SensitiveValueGate(null), TestPrincipalAccessors.Root, handle.Id, view: "timers", topN: 10);

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        result.Data!.View.Should().Be("timers");
        result.Data.Timers.Should().NotBeNull();
        result.Data.Timers!.TotalTimers.Should().Be(5);
        result.Data.Timers.TimersByCallback.Should().ContainSingle(row => row.MethodName == "OnTick" && row.Count == 5);
        result.Data.Timers.TasksByType.Should().ContainSingle(row => row.Count == 7);
        result.Summary.Should().Contain("task/timer leak candidates");
    }

    private sealed class StubDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
