using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Symbols;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Covers the issue #1120 <c>thread-statics</c> view on the unified <c>query_snapshot</c> tool: a
/// <c>typeFullName</c> is required, the call dispatches to <see cref="IThreadStaticFieldResolver"/>,
/// and the sensitive-value gate is honoured. Mirrors <c>QuerySnapshotFrameVarsToolTests</c>' shape.
/// </summary>
public sealed class QuerySnapshotThreadStaticsToolTests
{
    [Fact]
    public async Task ThreadStatics_ReturnsPerThreadFieldValues()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(2718, DiagnosticTools.ThreadSnapshotKind, ThreadArtifact(), TimeSpan.FromMinutes(10));

        var stub = new StubThreadStaticResolver(new ThreadStaticFieldsResult("Fixture.Thing", new[]
        {
            new ThreadStaticFieldsForThread(12, 10012u, new[]
            {
                new ThreadStaticFieldValue("Value", "System.String", IsInitialized: true) { ValuePreview = "secret" },
            }),
        }));

        var result = await Invoke(store, stub, handle.Id, typeFullName: "Fixture.Thing", includeSensitiveValues: true);

        result.Error.Should().BeNull();
        var query = result.Data.Should().BeOfType<ThreadSnapshotQueryResult>().Subject;
        query.View.Should().Be("thread-statics");
        query.ThreadStatics!.TypeFullName.Should().Be("Fixture.Thing");
        query.ThreadStatics.Threads.Should().ContainSingle()
            .Which.Fields.Should().ContainSingle()
            .Which.ValuePreview.Should().Be("secret");
    }

    [Fact]
    public async Task ThreadStatics_MissingTypeFullName_ReturnsInvalidArgument()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(2718, DiagnosticTools.ThreadSnapshotKind, ThreadArtifact(), TimeSpan.FromMinutes(10));

        var result = await Invoke(store, new StubThreadStaticResolver(Empty()), handle.Id, typeFullName: null);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
    }

    private static ThreadStaticFieldsResult Empty() => new("Fixture.Thing", Array.Empty<ThreadStaticFieldsForThread>());

    [Fact]
    public async Task ThreadStatics_WithoutServerGateOrScope_SuppressesSensitiveValues()
    {
        const int processId = 627452;
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            processId,
            DiagnosticTools.ThreadSnapshotKind,
            ThreadArtifact(ThreadSnapshotOrigin.Live, processId),
            TimeSpan.FromMinutes(10));

        var stub = new StubThreadStaticResolver(Empty());
        var result = await Invoke(store, stub, handle.Id, typeFullName: "Fixture.Thing", includeSensitiveValues: true);

        result.Error.Should().BeNull();
        // Caller opted in but neither the server gate nor a sensitive-heap-read scope is present.
        stub.LastSensitive.Should().BeFalse();
    }

    [Fact]
    public async Task ThreadStatics_ServerGateEnabled_EmitsSensitiveValues()
    {
        const int processId = 627453;
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            processId,
            DiagnosticTools.ThreadSnapshotKind,
            ThreadArtifact(ThreadSnapshotOrigin.Live, processId),
            TimeSpan.FromMinutes(10));

        var stub = new StubThreadStaticResolver(Empty());
        var gate = new SensitiveValueGate(new SecurityOptions { AllowSensitiveHeapValues = true });
        await QuerySnapshotTool.QuerySnapshot(
            store, new StubDumpInspector(), new SensitiveDataRedactor(null), gate, TestPrincipalAccessors.Root,
            new ClrMdNativeAddressResolver(), new StubFrameResolver(),
            handle: handle.Id, view: "thread-statics", typeFullName: "Fixture.Thing", includeSensitiveValues: true,
            threadStaticFieldResolver: stub,
            cancellationToken: CancellationToken.None);

        stub.LastSensitive.Should().BeTrue();
    }

    [Fact]
    public async Task ThreadStatics_LiveOriginExitedProcess_ReturnsStructuredProcessExitedError()
    {
        const int processId = 962702;
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            processId,
            DiagnosticTools.ThreadSnapshotKind,
            ThreadArtifact(ThreadSnapshotOrigin.Live, processId),
            TimeSpan.FromMinutes(10),
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live);
        var resolver = new ThrowingThreadStaticResolver();
        new DeadProcessHandleEvictor(store, isProcessAlive: _ => false).EvictDeadProcesses().Should().Be(0);

        var result = await Invoke(store, resolver, handle.Id, typeFullName: "Fixture.Thing");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("ProcessExited");
        result.Summary.Should().Contain("requires the original live process");
        resolver.CallCount.Should().Be(1);
    }

    private static Task<DotnetDiagnostics.Core.DiagnosticResult<object>> Invoke(
        MemoryDiagnosticHandleStore store,
        IThreadStaticFieldResolver resolver,
        string handle,
        string? typeFullName,
        bool includeSensitiveValues = false,
        SensitiveValueGate? sensitiveGate = null,
        CancellationToken cancellationToken = default)
        => QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(null),
            sensitiveGate ?? new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            new ClrMdNativeAddressResolver(),
            new StubFrameResolver(),
            handle: handle,
            view: "thread-statics",
            typeFullName: typeFullName,
            includeSensitiveValues: includeSensitiveValues,
            threadStaticFieldResolver: resolver,
            cancellationToken: cancellationToken);

    private static ThreadSnapshotArtifact ThreadArtifact(
        ThreadSnapshotOrigin origin = ThreadSnapshotOrigin.Dump,
        int processId = 2718)
    {
        var artifact = new ThreadSnapshotArtifact(
            origin,
            processId,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(50),
            "Core",
            "10.0.0",
            new[]
            {
                new ManagedThread(12, 10012u, 12u, "Running", true, false, false, false, false, 0u, null, null, Array.Empty<ManagedStackFrame>()),
            },
            Array.Empty<MonitorLockState>());
        return origin == ThreadSnapshotOrigin.Dump
            ? artifact with { DumpFilePath = "/var/crash.dmp" }
            : artifact;
    }

    private sealed class StubThreadStaticResolver : IThreadStaticFieldResolver
    {
        private readonly ThreadStaticFieldsResult _result;
        private int _callCount;
        public StubThreadStaticResolver(ThreadStaticFieldsResult result) => _result = result;
        public int CallCount => Volatile.Read(ref _callCount);
        public bool LastSensitive { get; private set; }

        public Task<ThreadStaticFieldsResult> ResolveAsync(ThreadSnapshotArtifact artifact, string typeFullName, bool includeSensitiveValues, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            LastSensitive = includeSensitiveValues;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingThreadStaticResolver : IThreadStaticFieldResolver
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ThreadStaticFieldsResult> ResolveAsync(ThreadSnapshotArtifact artifact, string typeFullName, bool includeSensitiveValues, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            throw new InvalidOperationException("live process is gone");
        }
    }

    private sealed class StubFrameResolver : IFrameVariableResolver
    {
        public Task<FrameVariablesResult> ResolveAsync(ThreadSnapshotArtifact artifact, int managedThreadId, bool includeSensitiveValues, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("thread-statics tests do not exercise frame-vars.");
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
