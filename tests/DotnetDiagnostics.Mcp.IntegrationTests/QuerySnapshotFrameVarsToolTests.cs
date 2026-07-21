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
        const int processId = 627450;
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            processId,
            DiagnosticTools.ThreadSnapshotKind,
            ThreadArtifact(ThreadSnapshotOrigin.Live, processId),
            TimeSpan.FromMinutes(10));

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
        const int processId = 627451;
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            processId,
            DiagnosticTools.ThreadSnapshotKind,
            ThreadArtifact(ThreadSnapshotOrigin.Live, processId),
            TimeSpan.FromMinutes(10));

        var stub = new StubFrameResolver(Empty());
        var gate = new SensitiveValueGate(new SecurityOptions { AllowSensitiveHeapValues = true });
        await QuerySnapshotTool.QuerySnapshot(
            store, new StubDumpInspector(), new SensitiveDataRedactor(null), gate, TestPrincipalAccessors.Root,
            new ClrMdNativeAddressResolver(), stub,
            handle: handle.Id, view: "frame-vars", threadId: 12, includeSensitiveValues: true,
            cancellationToken: CancellationToken.None);

        stub.LastSensitive.Should().BeTrue();
    }

    [Fact]
    public async Task FrameVars_LiveOriginConcurrentCall_ReturnsSchemaValidBusyHint()
    {
        const int processId = 627449;
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            processId,
            DiagnosticTools.ThreadSnapshotKind,
            ThreadArtifact(ThreadSnapshotOrigin.Live, processId),
            TimeSpan.FromMinutes(10));
        var resolver = new BlockingFrameResolver(Empty());
        var gate = new SensitiveValueGate(new SecurityOptions { AllowSensitiveHeapValues = true });
        var first = Invoke(
            store,
            resolver,
            handle.Id,
            threadId: 12,
            includeSensitiveValues: true,
            sensitiveGate: gate);

        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DotnetDiagnostics.Core.DiagnosticResult<object> busy;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            busy = await Invoke(
                store,
                resolver,
                handle.Id,
                threadId: 12,
                includeSensitiveValues: true,
                sensitiveGate: gate,
                cancellationToken: timeout.Token);
        }
        finally
        {
            resolver.Release();
        }
        await first;

        resolver.CallCount.Should().Be(1);
        resolver.LastSensitive.Should().BeTrue(
            "the live attach guard must not bypass the existing sensitive-value policy");
        busy.Error!.Kind.Should().Be("Busy");
        var hint = busy.Hints.Should().ContainSingle().Which;
        hint.SuggestedArguments.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["handle"] = handle.Id,
            ["view"] = "frame-vars",
            ["threadId"] = 12,
            ["includeSensitiveValues"] = true,
        });
        hint.SuggestedArguments.Should().NotContainKey("processId");
        hint.ShouldMatchCanonicalSchema();
    }

    [Fact]
    public async Task ArtifactOnlyThreadView_LiveOriginHandleSurvivesProcessExit()
    {
        const int processId = 962700;
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            processId,
            DiagnosticTools.ThreadSnapshotKind,
            ThreadArtifact(ThreadSnapshotOrigin.Live, processId),
            TimeSpan.FromMinutes(10),
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live);
        new DeadProcessHandleEvictor(store, isProcessAlive: _ => false).EvictDeadProcesses().Should().Be(0);

        var result = await QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(null),
            new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            new ClrMdNativeAddressResolver(),
            new StubFrameResolver(Empty()),
            handle: handle.Id,
            view: "top-blocked",
            cancellationToken: CancellationToken.None);

        result.Error.Should().BeNull();
        var query = result.Data.Should().BeOfType<ThreadSnapshotQueryResult>().Subject;
        query.View.Should().Be("top-blocked");
        query.Threads.Should().ContainSingle();
    }

    [Fact]
    public async Task FrameVars_LiveOriginExitedProcess_ReturnsStructuredProcessExitedError()
    {
        const int processId = 962701;
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            processId,
            DiagnosticTools.ThreadSnapshotKind,
            ThreadArtifact(ThreadSnapshotOrigin.Live, processId),
            TimeSpan.FromMinutes(10),
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live);
        var resolver = new ThrowingFrameResolver();
        new DeadProcessHandleEvictor(store, isProcessAlive: _ => false).EvictDeadProcesses().Should().Be(0);

        var result = await Invoke(store, resolver, handle.Id, threadId: 12);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("ProcessExited");
        result.Summary.Should().Contain("requires the original live process");
        resolver.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task FrameVars_LiveOriginProcessIdentityMismatch_ReturnsStructuredProcessExitedError()
    {
        var processId = Environment.ProcessId;
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            processId,
            DiagnosticTools.ThreadSnapshotKind,
            ThreadArtifact(ThreadSnapshotOrigin.Live, processId) with
            {
                ProcessStartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            },
            TimeSpan.FromMinutes(10),
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live);
        var result = await Invoke(store, new ThrowingFrameResolver(), handle.Id, threadId: 12);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("ProcessExited");
        result.Summary.Should().Contain("exited or been reused");
    }


    private static Task<DotnetDiagnostics.Core.DiagnosticResult<object>> Invoke(
        MemoryDiagnosticHandleStore store,
        IFrameVariableResolver resolver,
        string handle,
        int? threadId,
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
            resolver,
            handle: handle,
            view: "frame-vars",
            threadId: threadId,
            includeSensitiveValues: includeSensitiveValues,
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

    private sealed class BlockingFrameResolver(FrameVariablesResult result) : IFrameVariableResolver
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public TaskCompletionSource Entered => _entered;

        public int CallCount => Volatile.Read(ref _callCount);

        public bool LastSensitive { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task<FrameVariablesResult> ResolveAsync(
            ThreadSnapshotArtifact artifact,
            int managedThreadId,
            bool includeSensitiveValues,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            LastSensitive = includeSensitiveValues;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class StubFrameResolver : IFrameVariableResolver
    {
        private readonly FrameVariablesResult _result;
        private int _callCount;
        public StubFrameResolver(FrameVariablesResult result) => _result = result;
        public int CallCount => Volatile.Read(ref _callCount);
        public bool LastSensitive { get; private set; }

        public Task<FrameVariablesResult> ResolveAsync(ThreadSnapshotArtifact artifact, int managedThreadId, bool includeSensitiveValues, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            LastSensitive = includeSensitiveValues;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingFrameResolver : IFrameVariableResolver
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<FrameVariablesResult> ResolveAsync(ThreadSnapshotArtifact artifact, int managedThreadId, bool includeSensitiveValues, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            throw new InvalidOperationException("live process is gone");
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
