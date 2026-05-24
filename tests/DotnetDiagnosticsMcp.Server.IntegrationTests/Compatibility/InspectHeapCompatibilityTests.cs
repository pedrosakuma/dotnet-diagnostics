using System.Collections.ObjectModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;

/// <summary>
/// RFC 0002 / #206 — dual-entrypoint compatibility test for <see cref="InspectHeapTool"/>.
/// Asserts that <c>inspect_heap(source=live|dump, …)</c> returns response envelopes
/// structurally identical to the legacy <c>inspect_live_heap</c> / <c>inspect_dump</c>
/// tools, using <see cref="CompatibilityEnvelopeAssert"/>. Volatile fields (issued handle
/// ids, expiration timestamps) are masked. The fixed stub <see cref="DumpInspector"/>
/// returns the same <see cref="HeapSnapshotArtifact"/> for every invocation so the
/// envelopes only diverge on those volatile fields.
/// </summary>
public sealed class InspectHeapCompatibilityTests
{
    private static readonly CompatibilityEnvelopeAssert.CompatibilityIgnore VolatileMask =
        CompatibilityEnvelopeAssert.CompatibilityIgnore.Paths(
            "handle",
            "handleExpiresAt",
            "data/handle");

    [Fact]
    public async Task InspectHeap_Live_ProducesIdenticalEnvelopeToLegacy()
    {
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => InvokeLegacyLiveAsync(),
            successor: () => InvokeSuccessorLiveAsync(),
            ignore: VolatileMask);
    }

    [Fact]
    public async Task InspectHeap_Dump_ProducesIdenticalEnvelopeToLegacy()
    {
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => InvokeLegacyDumpAsync(),
            successor: () => InvokeSuccessorDumpAsync(),
            ignore: VolatileMask);
    }

    [Fact]
    public async Task InspectHeap_Live_StampsLiveOriginAndPidEviction()
    {
        var (handles, inspector) = BuildContext();
        var result = await InspectHeapTool.InspectHeap(
            inspector,
            handles,
            EchoResolver(StubPid),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            source: "live",
            processId: StubPid);

        result.Error.Should().BeNull();
        var lookup = handles.TryGetWithKind(DeterministicHandleStore.PublicConstantHandleId);
        lookup.Should().NotBeNull();
        lookup!.Value.Handle.Origin.Should().Be(HandleOrigin.Live);
    }

    [Fact]
    public async Task InspectHeap_Dump_StampsDumpOriginAndDoesNotEvictOnPidExit()
    {
        var (handles, inspector) = BuildContext();
        var result = await InspectHeapTool.InspectHeap(
            inspector,
            handles,
            EchoResolver(StubPid),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            source: "dump",
            dumpFilePath: StubDumpPath);

        result.Error.Should().BeNull();
        var lookup = handles.TryGetWithKind(DeterministicHandleStore.PublicConstantHandleId);
        lookup.Should().NotBeNull();
        lookup!.Value.Handle.Origin.Should().Be(HandleOrigin.Dump);

        var invalidated = handles.InvalidateForProcess(StubPid);
        invalidated.Should().Be(0, "dump-origin handles are not evicted by PID-exit sweeps");
        handles.TryGetWithKind(DeterministicHandleStore.PublicConstantHandleId).Should().NotBeNull();
    }

    [Fact]
    public async Task InspectHeap_UnknownSource_ReturnsInvalidArgument()
    {
        var (handles, inspector) = BuildContext();
        var result = await InspectHeapTool.InspectHeap(
            inspector,
            handles,
            EchoResolver(StubPid),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            source: "trace");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("source");
    }

    [Fact]
    public async Task InspectHeap_LiveWithDumpPath_ReturnsInvalidArgument()
    {
        var (handles, inspector) = BuildContext();
        var result = await InspectHeapTool.InspectHeap(
            inspector,
            handles,
            EchoResolver(StubPid),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            source: "live",
            processId: StubPid,
            dumpFilePath: StubDumpPath);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("dumpFilePath");
    }

    [Fact]
    public async Task InspectHeap_DumpWithExplicitPid_ReturnsInvalidArgument()
    {
        var (handles, inspector) = BuildContext();
        var result = await InspectHeapTool.InspectHeap(
            inspector,
            handles,
            EchoResolver(StubPid),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            source: "dump",
            processId: StubPid,
            dumpFilePath: StubDumpPath);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("processId");
    }

    [Fact]
    public async Task InspectHeap_DumpWithoutPath_ReturnsInvalidArgument()
    {
        var (handles, inspector) = BuildContext();
        var result = await InspectHeapTool.InspectHeap(
            inspector,
            handles,
            EchoResolver(StubPid),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            source: "dump");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("dumpFilePath");
    }

    private const int StubPid = 4242;
    private const string StubDumpPath = "/tmp/inspect-heap-compat.dmp";

    private static async Task<DiagnosticResult<LiveHeapInspection>> InvokeLegacyLiveAsync()
    {
        var (handles, inspector) = BuildContext();
        return await DiagnosticTools.InspectLiveHeap(
            inspector,
            handles,
            EchoResolver(StubPid),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            processId: StubPid);
    }

    private static async Task<DiagnosticResult<object>> InvokeSuccessorLiveAsync()
    {
        var (handles, inspector) = BuildContext();
        return await InspectHeapTool.InspectHeap(
            inspector,
            handles,
            EchoResolver(StubPid),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            source: "live",
            processId: StubPid);
    }

    private static async Task<DiagnosticResult<DumpInspection>> InvokeLegacyDumpAsync()
    {
        var (handles, inspector) = BuildContext();
        return await DiagnosticTools.InspectDump(
            inspector,
            handles,
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            dumpFilePath: StubDumpPath);
    }

    private static async Task<DiagnosticResult<object>> InvokeSuccessorDumpAsync()
    {
        var (handles, inspector) = BuildContext();
        return await InspectHeapTool.InspectHeap(
            inspector,
            handles,
            EchoResolver(StubPid),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            source: "dump",
            dumpFilePath: StubDumpPath);
    }

    private static (DeterministicHandleStore Handles, StubDumpInspector Inspector) BuildContext()
        => (new DeterministicHandleStore(), new StubDumpInspector());

    private static IProcessContextResolver EchoResolver(int pid) => new EchoResolverImpl(pid);

    /// <summary>
    /// Wrapper around <see cref="MemoryDiagnosticHandleStore"/> that re-issues a deterministic
    /// fake handle id (<see cref="ConstantHandleId"/>) on every <see cref="Register"/> call.
    /// The compatibility-envelope assertion needs the summary string + suggested-argument
    /// payload to be byte-equal between legacy and successor invocations; since both ids
    /// are baked into those text fields by the existing tool code, masking via JSON paths
    /// is insufficient. A deterministic id makes the envelopes literally equal.
    /// </summary>
    private sealed class DeterministicHandleStore : IDiagnosticHandleStore
    {
        private const string ConstantHandleId = "test-handle-0000000000000000";
        internal const string PublicConstantHandleId = ConstantHandleId;
        private readonly MemoryDiagnosticHandleStore _inner = new();

        public DiagnosticHandle Register(int processId, string kind, object artifact, TimeSpan ttl, bool evictWhenProcessExits = true, HandleOrigin? origin = null)
        {
            // Delegate registration to the real store, then overwrite both id and expiration
            // so the public envelope rendered by the tool layer is deterministic. The inner
            // store keeps its own (random) keying, so we re-register under the constant id.
            var real = _inner.Register(processId, kind, artifact, ttl, evictWhenProcessExits, origin);
            _inner.Invalidate(real.Id);

            var stableExpiration = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var stableHandle = new DiagnosticHandle(ConstantHandleId, stableExpiration, processId, kind) { Origin = real.Origin };
            // Re-register under the constant id by going through the real store's contract:
            // it accepts any id. Easiest is to expose the deterministic handle as a fresh slot.
            _slots[ConstantHandleId] = new Slot(stableHandle, artifact, evictWhenProcessExits);
            return stableHandle;
        }

        public T? TryGet<T>(string handle) where T : class
            => _slots.TryGetValue(handle, out var slot) ? slot.Artifact as T : null;

        public HandleLookup? TryGetWithKind(string handle)
            => _slots.TryGetValue(handle, out var slot) ? new HandleLookup(slot.Handle, slot.Artifact) : null;

        public bool Invalidate(string handle) => _slots.Remove(handle);

        public int InvalidateForProcess(int processId)
        {
            var removed = 0;
            foreach (var key in _slots.Keys.ToArray())
            {
                if (_slots[key].EvictOnPidExit && _slots[key].Handle.ProcessId == processId)
                {
                    _slots.Remove(key);
                    removed++;
                }
            }
            return removed;
        }

        private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

        private sealed record Slot(DiagnosticHandle Handle, object Artifact, bool EvictOnPidExit);
    }

    private sealed class EchoResolverImpl : IProcessContextResolver
    {
        private readonly int _pid;
        public EchoResolverImpl(int pid) { _pid = pid; }

        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken = default)
        {
            var pid = requestedProcessId is int p && p > 0 ? p : _pid;
            var ctx = new ProcessContext(pid, RuntimeFlavor.CoreClr, RuntimeVersion: null, CanSampleCpu: true, CanCollectGcDump: true, AutoResolved: false);
            return Task.FromResult(new ProcessContextResolution(Context: ctx, Error: null, Candidates: null));
        }
    }

    /// <summary>
    /// Deterministic IDumpInspector — both live and dump invocations return the same
    /// heap snapshot, so envelope differences come exclusively from per-call volatile
    /// fields (handle id, expiration). Required for <see cref="CompatibilityEnvelopeAssert"/>.
    /// </summary>
    private sealed class StubDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BuildArtifact(HeapSnapshotOrigin.Dump, dumpFilePath));
        }

        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BuildArtifact(HeapSnapshotOrigin.Live, dumpFilePath: null));
        }

        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private static HeapSnapshotArtifact BuildArtifact(HeapSnapshotOrigin origin, string? dumpFilePath)
        {
            var runtime = new DumpRuntimeInfo(Name: ".NETCoreApp", Version: "10.0.0", Architecture: "x64", IsServerGC: false, HeapCount: 1);
            var heap = new DumpHeapSummary(
                TotalBytes: 1024,
                Gen0Bytes: 128,
                Gen1Bytes: 256,
                Gen2Bytes: 512,
                LargeObjectHeapBytes: 64,
                PinnedObjectHeapBytes: 32,
                CommittedBytes: 2048);
            var stat = new TypeStat(
                TypeFullName: "System.String",
                ModuleName: "System.Private.CoreLib",
                InstanceCount: 10,
                TotalBytes: 1024,
                TotalBytesPercent: 100.0);
            IReadOnlyList<TypeStat> topByBytes = new ReadOnlyCollection<TypeStat>(new[] { stat });
            IReadOnlyList<TypeStat> topByCount = new ReadOnlyCollection<TypeStat>(new[] { stat });

            // CapturedAt + WalkDuration are deterministic so the envelope diff stays clean.
            var captured = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var walk = TimeSpan.FromMilliseconds(42);

            return new HeapSnapshotArtifact(
                Origin: origin,
                ProcessId: StubPid,
                CapturedAt: captured,
                WalkDuration: walk,
                Runtime: runtime,
                Heap: heap,
                TopTypesByBytes: topByBytes,
                TopTypesByInstances: topByCount)
            {
                DumpFilePath = dumpFilePath,
                DumpFileSizeBytes = origin == HeapSnapshotOrigin.Dump ? 8192 : null,
            };
        }
    }
}
