using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class QuerySnapshotGrowthToolTests
{
    [Fact]
    public async Task Growth_TwoLiveHeapHandles_ReturnsRankedGrowthEnvelope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, "heap-snapshot", HeapSnapshot(("Leaky.Cache", 1_000_000, 10), ("Noise", 100, 1)), TimeSpan.FromMinutes(10));
        var current = store.Register(123, "heap-snapshot", HeapSnapshot(("Leaky.Cache", 5_000_000, 50), ("Noise", 400, 4)), TimeSpan.FromMinutes(10));

        var result = await Growth(store, current.Id, baseline.Id);

        result.Error.Should().BeNull();
        var growth = result.Data.Should().BeOfType<HeapGrowthResult>().Subject;
        growth.Verdict.Should().Be("leak_suspected");
        growth.RankBy.Should().Be("bytes");
        growth.Growers.Should().HaveCount(2);
        growth.Growers[0].TypeFullName.Should().Be("Leaky.Cache");
        growth.Growers[0].BytesDelta.Should().Be(4_000_000);
    }

    [Fact]
    public async Task Growth_AttachesRetentionPathsToTopGrower()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, "heap-snapshot", HeapSnapshot(("Leaky.Cache", 1_000, 10)), TimeSpan.FromMinutes(10));
        var path = new RetentionPath(
            "Leaky.Cache",
            0xDEAD,
            new[] { new RetentionFrame("Root.Holder", 0xBEEF) { RootKind = "StaticVar" } },
            Truncated: false);
        var current = store.Register(123, "heap-snapshot", HeapSnapshot(new[] { path }, ("Leaky.Cache", 5_000, 50)), TimeSpan.FromMinutes(10));

        var result = await Growth(store, current.Id, baseline.Id);

        result.Error.Should().BeNull();
        var growth = result.Data.Should().BeOfType<HeapGrowthResult>().Subject;
        growth.Growers.Should().ContainSingle().Which.RetentionPaths.Should().ContainSingle();
    }

    [Fact]
    public async Task Growth_MissingBaselineHandle_ReturnsInvalidArgument()
    {
        var store = new MemoryDiagnosticHandleStore();
        var current = store.Register(123, "heap-snapshot", HeapSnapshot(("X", 100, 1)), TimeSpan.FromMinutes(10));

        var result = await Growth(store, current.Id, baselineHandle: null);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("baselineHandle");
    }

    [Fact]
    public async Task Growth_UnknownBaselineHandle_ReturnsHandleNotFound()
    {
        var store = new MemoryDiagnosticHandleStore();
        var current = store.Register(123, "heap-snapshot", HeapSnapshot(("X", 100, 1)), TimeSpan.FromMinutes(10));

        var result = await Growth(store, current.Id, baselineHandle: "does-not-exist");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("HandleNotFound");
    }

    [Fact]
    public async Task QuerySnapshot_CapacityEvictedHandle_ReturnsRecoveryOrientedError()
    {
        var store = new MemoryDiagnosticHandleStore(maxEntries: 1);
        var evicted = store.Register(123, "heap-snapshot", HeapSnapshot(("Old", 100, 1)), TimeSpan.FromMinutes(10));
        store.Register(123, "heap-snapshot", HeapSnapshot(("New", 200, 2)), TimeSpan.FromMinutes(10));

        var result = await Growth(store, evicted.Id, baselineHandle: null);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("HandleCapacityEvicted");
        result.Error.Message.Should().Contain("Diagnostics:HandleStore:MaxEntries");
        result.Hints.Should().ContainSingle().Which.NextTool.Should().Be("inspect_heap");
    }

    [Fact]
    public async Task Growth_MixedKinds_ReturnsInvalidArgument()
    {
        var store = new MemoryDiagnosticHandleStore();
        var heap = store.Register(123, "heap-snapshot", HeapSnapshot(("X", 100, 1)), TimeSpan.FromMinutes(10));
        var cpu = store.Register(123, "cpu-sample", HeapSnapshot(("X", 100, 1)), TimeSpan.FromMinutes(10));

        // current=heap, baseline=cpu → kind mismatch.
        var result = await Growth(store, heap.Id, cpu.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public async Task Growth_RankByInstances_OrdersByInstanceDelta()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, "heap-snapshot", HeapSnapshot(("ManyInstances", 1_000, 10), ("BigBytes", 10_000, 11)), TimeSpan.FromMinutes(10));
        var current = store.Register(123, "heap-snapshot", HeapSnapshot(("ManyInstances", 2_000, 1_010), ("BigBytes", 90_000, 20)), TimeSpan.FromMinutes(10));

        var result = await Growth(store, current.Id, baseline.Id, rankBy: "instances");

        result.Error.Should().BeNull();
        var growth = result.Data.Should().BeOfType<HeapGrowthResult>().Subject;
        growth.RankBy.Should().Be("instances");
        growth.Growers[0].TypeFullName.Should().Be("ManyInstances");
    }

    [Fact]
    public async Task Growth_InvalidRankBy_ReturnsInvalidArgument()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, "heap-snapshot", HeapSnapshot(("X", 100, 1)), TimeSpan.FromMinutes(10));
        var current = store.Register(123, "heap-snapshot", HeapSnapshot(("X", 500, 5)), TimeSpan.FromMinutes(10));

        var result = await Growth(store, current.Id, baseline.Id, rankBy: "percent");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("rankBy");
    }

    private static async Task<DotnetDiagnostics.Core.DiagnosticResult<object>> Growth(
        MemoryDiagnosticHandleStore store,
        string currentHandle,
        string? baselineHandle,
        string rankBy = "bytes",
        int? topN = null)
        => await QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(null),
            new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            new DotnetDiagnostics.Core.Symbols.ClrMdNativeAddressResolver(),
            new DotnetDiagnostics.Core.Threads.ClrMdFrameVariableResolver(),
            handle: currentHandle,
            view: "growth",
            topN: topN,
            rankBy: rankBy,
            baselineHandle: baselineHandle,
            cancellationToken: CancellationToken.None);

    [Fact]
    public async Task Growth_NonLiveSnapshot_ReturnsInvalidArgument()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, "heap-snapshot", HeapSnapshot(HeapSnapshotOrigin.Dump, ("Leaky.Cache", 1_000, 10)), TimeSpan.FromMinutes(10));
        var current = store.Register(123, "heap-snapshot", HeapSnapshot(HeapSnapshotOrigin.Dump, ("Leaky.Cache", 5_000, 50)), TimeSpan.FromMinutes(10));

        var result = await Growth(store, current.Id, baseline.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("LIVE");
    }

    private static HeapSnapshotArtifact HeapSnapshot(params (string typeName, long bytes, long instances)[] rows)
        => HeapSnapshot(retentionPaths: null, HeapSnapshotOrigin.Live, rows);

    private static HeapSnapshotArtifact HeapSnapshot(HeapSnapshotOrigin origin, params (string typeName, long bytes, long instances)[] rows)
        => HeapSnapshot(retentionPaths: null, origin, rows);

    private static HeapSnapshotArtifact HeapSnapshot(IReadOnlyList<RetentionPath>? retentionPaths, params (string typeName, long bytes, long instances)[] rows)
        => HeapSnapshot(retentionPaths, HeapSnapshotOrigin.Live, rows);

    private static HeapSnapshotArtifact HeapSnapshot(IReadOnlyList<RetentionPath>? retentionPaths, HeapSnapshotOrigin origin, params (string typeName, long bytes, long instances)[] rows)
    {
        var stats = rows.Select(row =>
            new TypeStat(
                row.typeName,
                ModuleName: null,
                InstanceCount: row.instances,
                TotalBytes: row.bytes,
                TotalBytesPercent: 0,
                Identity: new TypeIdentity(row.typeName))).ToArray();

        return new HeapSnapshotArtifact(
            Origin: origin,
            ProcessId: 123,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(10),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
            TopTypesByBytes: stats,
            TopTypesByInstances: stats)
        {
            RetentionPaths = retentionPaths,
        };
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
