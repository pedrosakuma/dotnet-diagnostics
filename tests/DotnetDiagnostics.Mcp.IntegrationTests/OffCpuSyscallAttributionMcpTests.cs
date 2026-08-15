using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Symbols;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Issue #829 — end-to-end MCP-layer coverage proving the syscall/wait-reason attribution
/// enrichment on <see cref="OffCpuStackHotspot.SyscallBreakdown"/> survives both surfaces the tool
/// contract exposes: the inline <c>collect_sample(kind="off_cpu")</c> summary (top-N stacks
/// returned directly) and the handle-backed <c>query_snapshot</c> drilldown (<c>topStacks</c> /
/// <c>stack</c> views). A stub <see cref="IOffCpuSampler"/> stands in for a live perf/ETW capture —
/// the platform-specific correlation logic itself (perf raw_syscalls parsing, ETW FileIO/TcpIp
/// correlation) is unit-tested directly against <c>PerfSchedOffCpuSampler</c> /
/// <c>EtwOffCpuSampler</c> in <c>DotnetDiagnostics.Core.Tests</c>; this test only asserts the MCP
/// tool surface plumbs the new field through unchanged, which is the explicit
/// "downstream query_snapshot views stay platform-agnostic" contract called out on
/// <c>RoutingOffCpuSampler</c>.
/// </summary>
/// <remarks>
/// Fake pids in this class use a dedicated 848900-series range, never reused by any other test
/// class/file (see the rationale documented on <see cref="SymbolPathSecurityTests"/>).
/// </remarks>
public sealed class OffCpuSyscallAttributionMcpTests
{
    private const int Pid = 848901;

    [Fact]
    public async Task CollectOffCpuSample_InlineSummary_SurfacesSyscallBreakdown()
    {
        var sampler = new StubOffCpuSampler();
        var store = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.CollectOffCpuSample(
            sampler,
            store,
            ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            processId: Pid,
            durationSeconds: 1);

        result.Error.Should().BeNull();
        var hotspot = result.Data!.TopBlockingStacks.Should().ContainSingle().Subject;
        hotspot.SyscallBreakdown.Should().NotBeNull();
        hotspot.SyscallBreakdown!.Should().HaveCount(2);
        hotspot.SyscallBreakdown![0].Name.Should().Be("futex");
        hotspot.SyscallBreakdown![0].Micros.Should().Be(800);
        hotspot.SyscallBreakdown![1].Name.Should().Be("read");
        hotspot.SyscallBreakdown![1].Micros.Should().Be(200);
    }

    [Fact]
    public async Task QuerySnapshot_TopStacks_SurfacesSyscallBreakdown()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(Pid, DiagnosticTools.OffCpuHandleKind, Artifact(), TimeSpan.FromMinutes(10));

        var result = await Invoke(store, handle.Id, view: "topStacks");

        result.Error.Should().BeNull();
        var query = result.Data.Should().BeOfType<OffCpuQueryView>().Subject;
        var hotspot = query.Stacks.Should().ContainSingle().Subject;
        hotspot.SyscallBreakdown.Should().NotBeNull();
        hotspot.SyscallBreakdown!.Select(s => s.Name).Should().Equal("futex", "read");
    }

    [Fact]
    public async Task QuerySnapshot_Stack_SurfacesSyscallBreakdown()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(Pid, DiagnosticTools.OffCpuHandleKind, Artifact(), TimeSpan.FromMinutes(10));

        var result = await Invoke(store, handle.Id, view: "stack", stackRank: 1);

        result.Error.Should().BeNull();
        var query = result.Data.Should().BeOfType<OffCpuQueryView>().Subject;
        query.Stack.Should().NotBeNull();
        query.Stack!.SyscallBreakdown.Should().NotBeNull();
        query.Stack!.SyscallBreakdown!.Should().HaveCount(2);
    }

    private static OffCpuSnapshotArtifact Artifact()
    {
        var hotspot = new OffCpuStackHotspot(
            "LeafA",
            1000,
            5,
            "UserRequest",
            new[] { new OffCpuFrame("App.dll", "LeafA"), new OffCpuFrame("App.dll", "RootA") },
            SyscallBreakdown: new[]
            {
                new OffCpuSyscallAttribution("futex", 3, 800),
                new OffCpuSyscallAttribution("read", 2, 200),
            });
        return new OffCpuSnapshotArtifact(
            ProcessId: Pid,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalOffCpuMicros: 1000,
            SchedSwitches: 5,
            Stacks: new[] { hotspot },
            Threads: Array.Empty<OffCpuThreadView>(),
            SymbolSource: "stub");
    }

    private static Task<DotnetDiagnostics.Core.DiagnosticResult<object>> Invoke(
        MemoryDiagnosticHandleStore store,
        string handle,
        string view,
        int? stackRank = null,
        CancellationToken cancellationToken = default)
        => QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(null),
            new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            new ClrMdNativeAddressResolver(),
            new ThrowingFrameVariableResolver(),
            handle: handle,
            view: view,
            stackRank: stackRank,
            cancellationToken: cancellationToken);

    private sealed class StubOffCpuSampler : IOffCpuSampler
    {
        public bool IsAvailable() => true;

        public Task<OffCpuSampleResult> SampleAsync(
            int processId,
            TimeSpan duration,
            int topN = 25,
            string? symbolPath = null,
            CancellationToken cancellationToken = default)
        {
            var artifact = Artifact() with { ProcessId = processId, Duration = duration };
            var summary = new OffCpuSnapshot(
                processId,
                DateTimeOffset.UtcNow,
                duration,
                artifact.TotalOffCpuMicros,
                DistinctThreads: 1,
                TopBlockingStacks: artifact.Stacks,
                SchedSwitches: artifact.SchedSwitches,
                SymbolSource: artifact.SymbolSource);
            return Task.FromResult(new OffCpuSampleResult(summary, artifact));
        }
    }

    private sealed class ThrowingFrameVariableResolver : IFrameVariableResolver
    {
        public Task<FrameVariablesResult> ResolveAsync(ThreadSnapshotArtifact artifact, int managedThreadId, bool includeSensitiveValues, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used by off-cpu views");
    }

    private sealed class StubDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
