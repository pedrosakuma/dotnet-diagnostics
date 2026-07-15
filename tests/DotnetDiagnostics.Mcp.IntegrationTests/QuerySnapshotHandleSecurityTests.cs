using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class QuerySnapshotHandleSecurityTests
{
    [Fact]
    public async Task CapacityTombstone_RequiresExactKindScopeBeforeReturningDetails()
    {
        var store = new MemoryDiagnosticHandleStore(maxEntries: 1);
        var evicted = store.Register(424242, "heap-snapshot", new object(), TimeSpan.FromMinutes(10));
        store.Register(1, CollectionHandleKinds.Counters, new object(), TimeSpan.FromMinutes(10));

        var result = await Query(
            store,
            TestPrincipalAccessors.WithScopes("eventpipe"),
            evicted.Id,
            view: "top-types");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
        result.Summary.Should().NotContain("capacity");
        result.Summary.Should().NotContain("424242");
        result.Hints.Should().BeEmpty();
    }

    [Fact]
    public async Task CapacityTombstone_ForBaselineRequiresBaselineKindScope()
    {
        var store = new MemoryDiagnosticHandleStore(maxEntries: 2);
        var baseline = store.Register(424242, "heap-snapshot", new object(), TimeSpan.FromMinutes(10));
        store.Register(1, CollectionHandleKinds.Counters, new object(), TimeSpan.FromMinutes(10));
        var current = store.Register(2, CollectionHandleKinds.Counters, new object(), TimeSpan.FromMinutes(10));

        var result = await Query(
            store,
            TestPrincipalAccessors.WithScopes("read-counters"),
            current.Id,
            view: "diff",
            baselineHandle: baseline.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
        result.Summary.Should().NotContain("capacity");
        result.Summary.Should().NotContain("424242");
        result.Hints.Should().BeEmpty();
    }

    [Fact]
    public async Task ActiveBaseline_RequiresBaselineKindScopeBeforeKindMismatchDetails()
    {
        var store = new MemoryDiagnosticHandleStore(maxEntries: 2);
        var baseline = store.Register(424242, "heap-snapshot", new object(), TimeSpan.FromMinutes(10));
        var current = store.Register(2, CollectionHandleKinds.Counters, new object(), TimeSpan.FromMinutes(10));

        var result = await Query(
            store,
            TestPrincipalAccessors.WithScopes("read-counters"),
            current.Id,
            view: "diff",
            baselineHandle: baseline.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
        result.Summary.Should().NotContain("heap-snapshot");
        result.Summary.Should().NotContain("424242");
    }

    [Fact]
    public async Task ConcurrentCapacityEvictionAfterLookup_UsesResolvedArtifact()
    {
        var artifact = CpuArtifact();
        var handle = new DiagnosticHandle(
            "RACEHANDLE00000000000",
            DateTimeOffset.UtcNow.AddMinutes(10),
            123,
            "cpu-sample");
        var store = new EvictAfterLookupStore(new HandleLookup(handle, artifact));

        var result = await Query(
            store,
            TestPrincipalAccessors.WithScopes("investigation-export"),
            handle.Id,
            view: "call-tree");

        result.Error.Should().BeNull();
        result.Data.Should().BeOfType<CallTreeView>();
        store.GenericLookupCount.Should().Be(0, "dispatch must use the artifact resolved by the top-level lookup");
    }

    private static Task<DiagnosticResult<object>> Query(
        IDiagnosticHandleStore store,
        DotnetDiagnostics.Mcp.Security.IPrincipalAccessor principalAccessor,
        string handle,
        string? view,
        string? baselineHandle = null)
        => QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(null),
            new SensitiveValueGate(null),
            principalAccessor,
            new DotnetDiagnostics.Core.Symbols.ClrMdNativeAddressResolver(),
            new DotnetDiagnostics.Core.Threads.ClrMdFrameVariableResolver(),
            handle: handle,
            view: view,
            baselineHandle: baselineHandle,
            cancellationToken: CancellationToken.None);

    private static CpuSampleTraceArtifact CpuArtifact()
    {
        var leaf = new CallTreeNode(
            new SampledFrame("App.dll", "App.Work()"),
            InclusiveSamples: 5,
            ExclusiveSamples: 5,
            Children: Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(
            new SampledFrame("App.dll", "App.Main()"),
            InclusiveSamples: 5,
            ExclusiveSamples: 0,
            Children: [leaf]);
        return new CpuSampleTraceArtifact(
            ProcessId: 123,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(1),
            TotalSamples: 5,
            Root: root);
    }

    private sealed class EvictAfterLookupStore(HandleLookup firstLookup) : IDiagnosticHandleStore
    {
        private int _lookupCount;
        private int _genericLookupCount;

        public int GenericLookupCount => Volatile.Read(ref _genericLookupCount);

        public DiagnosticHandle Register(
            int processId,
            string kind,
            object artifact,
            TimeSpan ttl,
            bool evictWhenProcessExits = true,
            HandleOrigin? origin = null)
            => throw new NotSupportedException();

        public T? TryGet<T>(string handle) where T : class
        {
            Interlocked.Increment(ref _genericLookupCount);
            return null;
        }

        public HandleLookup? TryGetWithKind(string handle)
        {
            Interlocked.Increment(ref _genericLookupCount);
            return null;
        }

        public DiagnosticHandleLookupResult LookupWithKind(string handle)
        {
            if (Interlocked.Increment(ref _lookupCount) == 1)
            {
                return DiagnosticHandleLookupResult.Found(firstLookup);
            }

            var tombstone = new DiagnosticHandleTombstone(
                handle,
                DiagnosticHandleLookupStatus.CapacityEvicted,
                DateTimeOffset.UtcNow,
                firstLookup.Handle.ProcessId,
                firstLookup.Kind);
            return new DiagnosticHandleLookupResult(
                DiagnosticHandleLookupStatus.CapacityEvicted,
                null,
                tombstone);
        }

        public bool Invalidate(string handle) => false;

        public int InvalidateForProcess(int processId) => 0;
    }

    private sealed class StubDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(
            string dumpFilePath,
            DumpInspectionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapSnapshotArtifact> InspectLiveAsync(
            int processId,
            DumpInspectionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectInspection> InspectObjectAsync(
            HeapSnapshotArtifact snapshot,
            ulong address,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapGcRootInspection> InspectGcRootAsync(
            HeapSnapshotArtifact snapshot,
            ulong address,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(
            HeapSnapshotArtifact snapshot,
            ulong address,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
