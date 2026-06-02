using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.Symbols;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// Covers the issue #275 <c>resolve-address</c> view on the unified <c>query_snapshot</c> tool:
/// addresses are parsed, dispatched to <see cref="INativeAddressResolver"/>, and rendered with
/// hex-string numerics so the LLM never sees a bare pointer.
/// </summary>
public sealed class QuerySnapshotResolveAddressToolTests
{
    [Fact]
    public async Task ResolveAddress_ClassifiesEachAddress_WithHexNumerics()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(2718, DiagnosticTools.ThreadSnapshotKind, ThreadArtifact(), TimeSpan.FromMinutes(10));

        var resolver = new StubResolver(new[]
        {
            new NativeAddressLocation(0x7f18cc41edc0, NativeAddressKind.UnmappedOrNotCaptured, null, null, null, null, false, null,
                "<unmapped-or-not-captured 0x7f18cc41edc0>"),
            new NativeAddressLocation(0x7f18cca1edc0, NativeAddressKind.Module, "libcrypto.so.3", "/usr/lib/libcrypto.so.3", 0x1edc0, "c0dbcda5", true, null,
                "libcrypto.so.3+0x1edc0"),
        });

        var result = await Invoke(store, resolver, handle.Id, "0x7f18cc41edc0,0x7f18cca1edc0");

        result.Error.Should().BeNull();
        var query = result.Data.Should().BeOfType<ThreadSnapshotQueryResult>().Subject;
        query.View.Should().Be("resolve-address");
        query.ResolvedAddresses.Should().HaveCount(2);

        var unmapped = query.ResolvedAddresses![0];
        unmapped.Address.Should().Be("0x7f18cc41edc0");
        unmapped.Kind.Should().Be("unmapped-or-not-captured");
        unmapped.Readable.Should().BeFalse();
        unmapped.Rva.Should().BeNull();

        var mapped = query.ResolvedAddresses![1];
        mapped.Kind.Should().Be("module");
        mapped.Module.Should().Be("libcrypto.so.3");
        mapped.Rva.Should().Be("0x1edc0");
        mapped.BuildId.Should().Be("c0dbcda5");
    }

    [Fact]
    public async Task ResolveAddress_MissingAddress_ReturnsInvalidArgument()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(2718, DiagnosticTools.ThreadSnapshotKind, ThreadArtifact(), TimeSpan.FromMinutes(10));

        var result = await Invoke(store, new StubResolver(Array.Empty<NativeAddressLocation>()), handle.Id, address: null);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public async Task ResolveAddress_BadAddress_ReturnsInvalidArgument()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(2718, DiagnosticTools.ThreadSnapshotKind, ThreadArtifact(), TimeSpan.FromMinutes(10));

        var result = await Invoke(store, new StubResolver(Array.Empty<NativeAddressLocation>()), handle.Id, "not-an-address");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
    }

    private static Task<DotnetDiagnosticsMcp.Core.DiagnosticResult<object>> Invoke(
        MemoryDiagnosticHandleStore store, INativeAddressResolver resolver, string handle, string? address)
        => QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(null),
            new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            resolver,
            handle: handle,
            view: "resolve-address",
            address: address,
            cancellationToken: CancellationToken.None);

    private static ThreadSnapshotArtifact ThreadArtifact() => new(
        ThreadSnapshotOrigin.Dump,
        2718,
        DateTimeOffset.UtcNow,
        TimeSpan.FromMilliseconds(50),
        "Core",
        "10.0.0",
        Array.Empty<ManagedThread>(),
        Array.Empty<MonitorLockState>())
    {
        DumpFilePath = "/tmp/crash.dmp",
    };

    private sealed class StubResolver : INativeAddressResolver
    {
        private readonly IReadOnlyList<NativeAddressLocation> _locations;
        public StubResolver(IReadOnlyList<NativeAddressLocation> locations) => _locations = locations;

        public Task<IReadOnlyList<NativeAddressLocation>> ResolveAsync(
            ThreadSnapshotArtifact artifact, IReadOnlyList<ulong> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(_locations);
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
