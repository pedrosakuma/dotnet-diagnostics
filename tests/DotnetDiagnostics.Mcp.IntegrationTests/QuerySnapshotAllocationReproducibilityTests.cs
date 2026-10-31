using System.Text.Json;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class QuerySnapshotAllocationReproducibilityTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task Query_ReusesRetainedAllocationProfilesWithoutASeparateTool()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(
            123,
            "allocation-sample",
            Artifact(Start, 100, 2, ("A", 75L, 1L), ("B", 25L, 1L)),
            TimeSpan.FromMinutes(10));
        var current = store.Register(
            123,
            "allocation-sample",
            Artifact(Start.AddSeconds(10), 120, 3, ("A", 80L, 2L), ("B", 40L, 1L)),
            TimeSpan.FromMinutes(10));

        var result = await Query(store, current.Id, [first.Id]);

        result.Error.Should().BeNull();
        result.Summary.Should().Contain("independent type and stack");
        var data = result.Data.Should().BeOfType<AllocationReproducibilityResult>().Subject;
        data.Schema.Should().Be(AllocationReproducibilityResult.SchemaV1);
        data.Method.Should().Be(AllocationReproducibilityResult.MethodV1);
        data.TopK.Should().Be(AllocationReproducibilityBounds.DefaultTopK);
        data.Availability.Should().Be(AllocationReproducibilityAvailability.Available);
        data.AllocatedTypeStability.MedianMembershipJaccard.Should().Be(1);
        data.AllocationStackStability.MedianSharedKeySpearman.Should().Be(1);
        JsonSerializer.SerializeToElement(data).TryGetProperty("Verdict", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Query_PreservesUnavailableComparisonHandleAsExplicitEvidenceState()
    {
        var store = new MemoryDiagnosticHandleStore();
        var current = store.Register(
            123,
            "allocation-sample",
            Artifact(Start, 100, 2, ("A", 100L, 2L)),
            TimeSpan.FromMinutes(10));

        var result = await Query(store, current.Id, ["missing"]);

        result.Error.Should().BeNull();
        var data = result.Data.Should().BeOfType<AllocationReproducibilityResult>().Subject;
        data.Availability.Should().Be(AllocationReproducibilityAvailability.InsufficientCaptureCount);
        data.EstimatedBytes.Median.Should().BeNull();
        data.Captures.Should().ContainSingle(capture =>
            capture.Handle == "missing"
            && capture.AttributionState == AllocationAttributionState.UnavailableHandle
            && capture.UnavailableReason == "handle-unknown");
    }

    [Fact]
    public async Task Query_ReportsInsufficientSingleCaptureAndRejectsNonAllocationHandles()
    {
        var store = new MemoryDiagnosticHandleStore();
        var allocation = store.Register(
            123,
            "allocation-sample",
            Artifact(Start, 100, 2, ("A", 100L, 2L)),
            TimeSpan.FromMinutes(10));
        var cpu = store.Register(123, "cpu-sample", Trace(10), TimeSpan.FromMinutes(10));

        var missing = await Query(store, allocation.Id, null);
        var wrongKind = await Query(store, cpu.Id, [allocation.Id]);
        var wrongComparisonKind = await Query(store, allocation.Id, [cpu.Id]);

        missing.Error.Should().BeNull();
        missing.Data.Should().BeOfType<AllocationReproducibilityResult>().Subject.Availability
            .Should().Be(AllocationReproducibilityAvailability.InsufficientCaptureCount);
        wrongKind.Error.Should().NotBeNull();
        wrongKind.Error!.Kind.Should().Be("InvalidArgument");
        wrongKind.Error.Message.Should().Contain("requires an allocation-sample handle");
        wrongComparisonKind.Error.Should().NotBeNull();
        wrongComparisonKind.Error!.Kind.Should().Be("InvalidArgument");
        wrongComparisonKind.Error.Message.Should().Contain("cpu-sample");
    }

    private static async Task<DotnetDiagnostics.Core.DiagnosticResult<object>> Query(
        MemoryDiagnosticHandleStore store,
        string handle,
        string[]? comparisonHandles)
        => await QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(null),
            new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            new DotnetDiagnostics.Core.Symbols.ClrMdNativeAddressResolver(),
            new DotnetDiagnostics.Core.Threads.ClrMdFrameVariableResolver(),
            handle: handle,
            view: QuerySnapshotTool.AllocationReproducibilityView,
            comparisonHandles: comparisonHandles,
            cancellationToken: CancellationToken.None);

    private static AllocationSampleArtifact Artifact(
        DateTimeOffset startedAt,
        long bytes,
        long ticks,
        params (string Key, long Bytes, long Ticks)[] rows)
    {
        var types = rows.Select(row => new AllocatedType(
            row.Key,
            row.Bytes,
            row.Ticks,
            HeapKind.Small)).ToArray();
        var sites = rows.Select(row => new AllocationSite(
            new SampledFrame("App.dll", row.Key),
            row.Bytes,
            row.Ticks,
            HeapKind.Small)).ToArray();
        var summary = new AllocationSample(
            123,
            startedAt,
            TimeSpan.FromSeconds(3),
            ticks,
            bytes,
            types,
            types)
        {
            TopBySite = sites,
        };
        return new AllocationSampleArtifact(summary, Trace(ticks))
        {
            EstimatedBytesTrace = Trace(bytes),
            Reproducibility = new(
                AllocationReproducibilityCaptureMetadata.SchemaV1,
                "CoreClr",
                "10.0.0",
                "windows",
                "x64",
                AllocationReproducibilityCaptureMetadata.EventPipeBackend,
                "gc-allocation-tick-estimated-bytes",
                "estimated-bytes",
                "capture-total",
                "none",
                3,
                16,
                Start.AddMinutes(-1)),
        };
    }

    private static CpuSampleTraceArtifact Trace(long total)
        => new(
            123,
            Start,
            TimeSpan.FromSeconds(3),
            total,
            new CallTreeNode(
                new SampledFrame(string.Empty, "<root>"),
                total,
                0,
                []));

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
