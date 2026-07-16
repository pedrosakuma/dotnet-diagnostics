using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class QuerySnapshotHandleSecurityTests
{
    public static TheoryData<string, string, string?> ReplayableRecoveryHints()
        => new()
        {
            { "cpu-sample", "collect_sample", "cpu" },
            { "allocation-sample", "collect_sample", "allocation" },
            { DiagnosticTools.NativeAllocHandleKind, "collect_sample", "native-alloc" },
            { DiagnosticTools.OffCpuHandleKind, "collect_sample", "off_cpu" },
            { CollectionHandleKinds.Counters, "collect_events", "counters" },
            { CollectionHandleKinds.ExceptionSnapshot, "collect_events", "exceptions" },
            { CollectionHandleKinds.CrashGuardSnapshot, "collect_events", "crash-guard" },
            { CollectionHandleKinds.GcEvents, "collect_events", "gc" },
            { CollectionHandleKinds.GcDatas, "collect_events", "datas" },
            { CollectionHandleKinds.EventCatalog, "collect_events", "catalog" },
            { CollectionHandleKinds.Activities, "collect_events", "activities" },
            { CollectionHandleKinds.LogSnapshot, "collect_events", "logs" },
            { CollectionHandleKinds.JitSnapshot, "collect_events", "jit" },
            { CollectionHandleKinds.ThreadPoolSnapshot, "collect_events", "threadpool" },
            { CollectionHandleKinds.ContentionSnapshot, "collect_events", "contention" },
            { CollectionHandleKinds.DbSnapshot, "collect_events", "db" },
            { CollectionHandleKinds.KestrelSnapshot, "collect_events", "kestrel" },
            { CollectionHandleKinds.NetworkingSnapshot, "collect_events", "networking" },
            { CollectionHandleKinds.StartupSnapshot, "collect_events", "startup" },
            { CollectionHandleKinds.InFlightRequests, "collect_events", "requests" },
        };

    public static TheoryData<string, string, string?> NonReplayableRecoveryHints()
        => new()
        {
            { DiagnosticTools.HeapSnapshotKind, "inspect_heap", null },
            { DiagnosticTools.ThreadSnapshotKind, "collect_thread_snapshot", null },
            { CollectionHandleKinds.EventSource, "collect_events", "event_source" },
            { MethodParameterCaptureUseCases.HandleKind, "collect_sample", "method-params" },
        };

    [Theory]
    [MemberData(nameof(ReplayableRecoveryHints))]
    public async Task CapacityTombstone_ReconstructsCanonicalCollectorArguments(
        string handleKind,
        string expectedTool,
        string? expectedCollectorKind)
    {
        var hint = await CapacityRecoveryHint(handleKind);

        hint.NextTool.Should().Be(expectedTool);
        hint.SuggestedArguments.Should().NotBeNull();
        hint.SuggestedArguments!["processId"].Should().Be(424242);
        if (expectedCollectorKind is null)
        {
            hint.SuggestedArguments.Should().NotContainKey("kind");
        }
        else
        {
            hint.SuggestedArguments!["kind"].Should().Be(expectedCollectorKind);
        }
        hint.ShouldMatchCanonicalSchema();
    }

    [Theory]
    [MemberData(nameof(NonReplayableRecoveryHints))]
    public async Task CapacityTombstone_OmitsArgumentsWhenOriginalInputsCannotBeReconstructed(
        string handleKind,
        string expectedTool,
        string? expectedCollectorKind)
    {
        var hint = await CapacityRecoveryHint(handleKind);

        hint.NextTool.Should().Be(expectedTool);
        hint.SuggestedArguments.Should().BeNull();
        if (expectedCollectorKind is not null)
        {
            hint.Reason.Should().Contain($"kind=\"{expectedCollectorKind}\"");
        }
        hint.ShouldMatchCanonicalSchema();
    }

    [Fact]
    public void RecoveryMapping_CoversEveryQueryableHandleKind()
    {
        var expected = ReplayableRecoveryHints()
            .Select(row => (string)row[0])
            .Concat(NonReplayableRecoveryHints().Select(row => (string)row[0]));

        expected.Should().BeEquivalentTo(QuerySnapshotTool.RegisteredKinds);
    }

    [Fact]
    public async Task CountersDiff_RequiresReadCountersInsteadOfEventPipe()
    {
        var store = new MemoryDiagnosticHandleStore();
        var current = store.Register(1, CollectionHandleKinds.Counters, new object(), TimeSpan.FromMinutes(10));

        var result = await Query(
            store,
            TestPrincipalAccessors.WithScopes("eventpipe"),
            current.Id,
            view: "diff");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
        result.Error.Message.Should().Contain("read-counters");
    }

    [Fact]
    public async Task CounterTombstone_RequiresReadCountersBeforeReturningDetails()
    {
        var store = new MemoryDiagnosticHandleStore(maxEntries: 1);
        var evicted = store.Register(424242, CollectionHandleKinds.Counters, new object(), TimeSpan.FromMinutes(10));
        store.Register(1, CollectionHandleKinds.EventSource, new object(), TimeSpan.FromMinutes(10));

        var result = await Query(
            store,
            TestPrincipalAccessors.WithScopes("eventpipe"),
            evicted.Id,
            view: "summary");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
        result.Summary.Should().NotContain("capacity");
        result.Summary.Should().NotContain("424242");
    }

    [Fact]
    public async Task MethodParameterTombstone_RequiresSensitiveScopeForSummary()
    {
        var store = new MemoryDiagnosticHandleStore(maxEntries: 1);
        var evicted = store.Register(424242, MethodParameterCaptureUseCases.HandleKind, new object(), TimeSpan.FromMinutes(10));
        store.Register(1, CollectionHandleKinds.EventSource, new object(), TimeSpan.FromMinutes(10));

        var result = await Query(
            store,
            TestPrincipalAccessors.WithScopes("eventpipe"),
            evicted.Id,
            view: "summary");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
        result.Error.Message.Should().Contain("sensitive-parameter-read");
        result.Summary.Should().NotContain("capacity");
        result.Summary.Should().NotContain("424242");
    }

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

    private static async Task<NextActionHint> CapacityRecoveryHint(string handleKind)
    {
        var store = new MemoryDiagnosticHandleStore(maxEntries: 1);
        var evicted = store.Register(424242, handleKind, new object(), TimeSpan.FromMinutes(10));
        store.Register(1, CollectionHandleKinds.Counters, new object(), TimeSpan.FromMinutes(10));

        var principal = handleKind == MethodParameterCaptureUseCases.HandleKind
            ? TestPrincipalAccessors.WithScopes("eventpipe", "sensitive-parameter-read")
            : TestPrincipalAccessors.Root;
        var result = await Query(
            store,
            principal,
            evicted.Id,
            view: "summary");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("HandleCapacityEvicted");
        return result.Hints.Should().ContainSingle().Subject;
    }

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
