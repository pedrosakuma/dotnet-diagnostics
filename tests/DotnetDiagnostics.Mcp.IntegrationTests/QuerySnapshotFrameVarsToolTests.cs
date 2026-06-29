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
/// Covers the issue #449 <c>frame-vars</c> view on the unified <c>query_snapshot</c> tool: a
/// thread id is required, the call dispatches to <see cref="IFrameVariableResolver"/>, and the
/// sensitive-value gate is honoured. Mirrors the resolve-address view's shape.
/// </summary>
public sealed class QuerySnapshotFrameVarsToolTests
{
    [Fact]
    public async Task FrameVars_ReturnsPerFrameVariables_AndCurrentException()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(2718, DiagnosticTools.ThreadSnapshotKind, ThreadArtifact(), TimeSpan.FromMinutes(10));

        var stub = new StubFrameResolver(new FrameVariablesResult(12, 4242, new[]
        {
            new FrameVariables(0, "Foo()", "App.Bar", "App.dll", "0x1000", "0x2000", new[]
            {
                new FrameVariable("local", "System.String", "0x7f00", "rbp+0x18", false, false) { ValuePreview = "secret" },
            }),
        })
        {
            CurrentExceptionType = "System.InvalidOperationException",
            Warnings = new[] { "1 stack root(s) could not be attributed." },
        });

        var result = await Invoke(store, stub, handle.Id, threadId: 12, includeSensitiveValues: true);

        result.Error.Should().BeNull();
        var query = result.Data.Should().BeOfType<ThreadSnapshotQueryResult>().Subject;
        query.View.Should().Be("frame-vars");
        query.ThreadId.Should().Be(12);
        query.FrameVariables!.CurrentExceptionType.Should().Be("System.InvalidOperationException");
        query.FrameVariables.Frames.Should().HaveCount(1);
        query.FrameVariables.Frames[0].Variables.Should().ContainSingle()
            .Which.TypeFullName.Should().Be("System.String");
    }

    [Fact]
    public async Task FrameVars_ThreadNotInSnapshot_ReturnsRecaptureError()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(2718, DiagnosticTools.ThreadSnapshotKind, ThreadArtifact(), TimeSpan.FromMinutes(10));

        var result = await Invoke(store, new StubFrameResolver(Empty()), handle.Id, threadId: 999);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("ThreadNotInSnapshot");
    }

    [Fact]
    public async Task FrameVars_MissingThreadId_ReturnsInvalidArgument()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(2718, DiagnosticTools.ThreadSnapshotKind, ThreadArtifact(), TimeSpan.FromMinutes(10));

        var result = await Invoke(store, new StubFrameResolver(Empty()), handle.Id, threadId: null);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public async Task FrameVars_WithoutServerGateOrScope_SuppressesSensitiveValues()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(2718, DiagnosticTools.ThreadSnapshotKind, ThreadArtifact(), TimeSpan.FromMinutes(10));

        var stub = new StubFrameResolver(Empty());
        var result = await Invoke(store, stub, handle.Id, threadId: 12, includeSensitiveValues: true);

        result.Error.Should().BeNull();
        // Caller opted in but neither the server gate nor a sensitive-heap-read scope is present.
        stub.LastSensitive.Should().BeFalse();
    }

    private static FrameVariablesResult Empty() => new(12, 4242, Array.Empty<FrameVariables>());

    [Fact]
    public async Task FrameVars_ServerGateEnabled_EmitsSensitiveValues()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(2718, DiagnosticTools.ThreadSnapshotKind, ThreadArtifact(), TimeSpan.FromMinutes(10));

        var stub = new StubFrameResolver(Empty());
        var gate = new SensitiveValueGate(new SecurityOptions { AllowSensitiveHeapValues = true });
        await QuerySnapshotTool.QuerySnapshot(
            store, new StubDumpInspector(), new SensitiveDataRedactor(null), gate, TestPrincipalAccessors.Root,
            new ClrMdNativeAddressResolver(), stub,
            handle: handle.Id, view: "frame-vars", threadId: 12, includeSensitiveValues: true,
            cancellationToken: CancellationToken.None);

        stub.LastSensitive.Should().BeTrue();
    }


    private static Task<DotnetDiagnostics.Core.DiagnosticResult<object>> Invoke(
        MemoryDiagnosticHandleStore store, IFrameVariableResolver resolver, string handle, int? threadId, bool includeSensitiveValues = false)
        => QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(null),
            new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            new ClrMdNativeAddressResolver(),
            resolver,
            handle: handle,
            view: "frame-vars",
            threadId: threadId,
            includeSensitiveValues: includeSensitiveValues,
            cancellationToken: CancellationToken.None);

    private static ThreadSnapshotArtifact ThreadArtifact() => new(
        ThreadSnapshotOrigin.Dump,
        2718,
        DateTimeOffset.UtcNow,
        TimeSpan.FromMilliseconds(50),
        "Core",
        "10.0.0",
        new[]
        {
            new ManagedThread(12, 10012u, 12u, "Running", true, false, false, false, false, 0u, null, null, Array.Empty<ManagedStackFrame>()),
        },
        Array.Empty<MonitorLockState>())
    {
        DumpFilePath = "/var/crash.dmp",
    };

    private sealed class StubFrameResolver : IFrameVariableResolver
    {
        private readonly FrameVariablesResult _result;
        public StubFrameResolver(FrameVariablesResult result) => _result = result;
        public bool LastSensitive { get; private set; }

        public Task<FrameVariablesResult> ResolveAsync(ThreadSnapshotArtifact artifact, int managedThreadId, bool includeSensitiveValues, CancellationToken cancellationToken = default)
        {
            LastSensitive = includeSensitiveValues;
            return Task.FromResult(_result);
        }
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
