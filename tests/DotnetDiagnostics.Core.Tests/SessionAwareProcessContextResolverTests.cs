using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Covers the session-aware <see cref="IProcessContextResolver.ResolveAsync(string?, int?, CancellationToken)"/>
/// overload introduced in Phase 2 of the central-orchestrator design (issue #20).
/// </summary>
/// <remarks>
/// The non-session overload is exhaustively covered by <see cref="ProcessContextResolverTests"/>;
/// these tests only assert the session-binding precedence rules and that the legacy path is
/// preserved verbatim when no binding store is registered or the session has no binding.
/// </remarks>
public sealed class SessionAwareProcessContextResolverTests
{
    private static readonly DiagnosticCapabilities DefaultCaps = new(
        ProcessId: 0,
        Runtime: RuntimeFlavor.CoreClr,
        RuntimeVersion: "10.0.0",
        CanReadEventCounters: true,
        CanSampleCpu: true,
        CanCollectGcDump: true,
        CanCollectExceptions: true,
        CanCollectHttpActivity: true,
        CanCollectCustomEventSource: true,
        CanCollectProcessDump: true,
        Notes: "");

    [Fact]
    public async Task ResolveAsync_NoStore_DelegatesToLegacyPath()
    {
        var discovery = new StubDiscovery(new DotnetProcess(99, "/myapp", "linux", "x64", "10.0.0", "myapp"));
        var detector = new StubDetector(_ => DefaultCaps);
        var resolver = new ProcessContextResolver(discovery, detector, bindings: null);

        var result = await resolver.ResolveAsync(sessionId: "any-session", requestedProcessId: null, default);

        result.Error.Should().BeNull();
        result.Context!.ProcessId.Should().Be(99);
        result.Context.AutoResolved.Should().BeTrue();
        result.Context.BindingSource.Should().Be("local-auto");
    }

    [Fact]
    public async Task ResolveAsync_SessionWithBinding_PrefersBindingOverLocalAutoResolution()
    {
        // Two local processes would normally cause AmbiguousDotnetProcess; binding overrides.
        var discovery = new StubDiscovery(
            new DotnetProcess(1, "/a", "linux", "x64", "10.0.0", "a"),
            new DotnetProcess(2, "/b", "linux", "x64", "10.0.0", "b"),
            new DotnetProcess(42, "/bound", "linux", "x64", "10.0.0", "bound"));
        var detector = new StubDetector(_ => DefaultCaps);
        var store = new MemorySessionTargetBindingStore();
        store.SetBinding("s1", new SessionTargetBinding(42, "orchestrator-attach"));
        var resolver = new ProcessContextResolver(discovery, detector, store);

        var result = await resolver.ResolveAsync(sessionId: "s1", requestedProcessId: null, default);

        result.Error.Should().BeNull();
        result.Context!.ProcessId.Should().Be(42);
        result.Context.AutoResolved.Should().BeFalse();
        result.Context.BindingSource.Should().Be("session-binding:orchestrator-attach");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitPid_BeatsSessionBinding()
    {
        var discovery = new StubDiscovery(
            new DotnetProcess(11, "/explicit", "linux", "x64", "10.0.0", "explicit"),
            new DotnetProcess(22, "/bound", "linux", "x64", "10.0.0", "bound"));
        var detector = new StubDetector(_ => DefaultCaps);
        var store = new MemorySessionTargetBindingStore();
        store.SetBinding("s1", new SessionTargetBinding(22, "orchestrator-attach"));
        var resolver = new ProcessContextResolver(discovery, detector, store);

        var result = await resolver.ResolveAsync(sessionId: "s1", requestedProcessId: 11, default);

        result.Error.Should().BeNull();
        result.Context!.ProcessId.Should().Be(11);
        result.Context.AutoResolved.Should().BeFalse();
        result.Context.BindingSource.Should().Be("explicit");
    }

    [Fact]
    public async Task ResolveAsync_SessionWithoutBinding_FallsThroughToLocalAuto()
    {
        var discovery = new StubDiscovery(new DotnetProcess(7, "/only", "linux", "x64", "10.0.0", "only"));
        var detector = new StubDetector(_ => DefaultCaps);
        var store = new MemorySessionTargetBindingStore();
        store.SetBinding("other-session", new SessionTargetBinding(99, "orchestrator-attach"));
        var resolver = new ProcessContextResolver(discovery, detector, store);

        var result = await resolver.ResolveAsync(sessionId: "s1", requestedProcessId: null, default);

        result.Error.Should().BeNull();
        result.Context!.ProcessId.Should().Be(7);
        result.Context.AutoResolved.Should().BeTrue();
        result.Context.BindingSource.Should().Be("local-auto");
    }

    [Fact]
    public async Task ResolveAsync_NullSessionId_BehavesLikeLegacyOverload()
    {
        var discovery = new StubDiscovery(new DotnetProcess(7, "/only", "linux", "x64", "10.0.0", "only"));
        var detector = new StubDetector(_ => DefaultCaps);
        var store = new MemorySessionTargetBindingStore();
        store.SetBinding("s1", new SessionTargetBinding(99, "orchestrator-attach"));
        var resolver = new ProcessContextResolver(discovery, detector, store);

        var withNullSession = await resolver.ResolveAsync(sessionId: null, requestedProcessId: null, default);
        var legacy = await resolver.ResolveAsync(requestedProcessId: null, default);

        withNullSession.Error.Should().BeNull();
        withNullSession.Context!.ProcessId.Should().Be(7);
        legacy.Context!.ProcessId.Should().Be(7);
    }

    [Fact]
    public async Task DefaultInterfaceMethod_DelegatesToLegacyWhenImplementorOnlyImplementsLegacy()
    {
        // Test stubs that only implement the legacy overload (e.g. a downstream consumer)
        // get the default-interface-method delegating behaviour for free.
        IProcessContextResolver impl = new LegacyOnlyResolver(99);

        var result = await impl.ResolveAsync(sessionId: "any", requestedProcessId: null, default);

        result.Context!.ProcessId.Should().Be(99);
    }

    private sealed class LegacyOnlyResolver : IProcessContextResolver
    {
        private readonly int _pid;
        public LegacyOnlyResolver(int pid) => _pid = pid;
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(_pid, RuntimeFlavor.CoreClr, true, true, AutoResolved: false),
                Error: null));
    }

    private sealed class StubDiscovery : IProcessDiscovery
    {
        private readonly IReadOnlyList<DotnetProcess> _processes;
        public StubDiscovery(params DotnetProcess[] processes) => _processes = processes;
        public IReadOnlyList<DotnetProcess> ListProcesses() => _processes;
        public DotnetProcess? TryGetProcess(int processId) => _processes.FirstOrDefault(p => p.ProcessId == processId);
    }

    private sealed class StubDetector : ICapabilityDetector
    {
        private readonly Func<int, DiagnosticCapabilities> _factory;
        public StubDetector(Func<int, DiagnosticCapabilities> factory) => _factory = factory;
        public Task<DiagnosticCapabilities> DetectAsync(int processId, CancellationToken cancellationToken = default)
            => Task.FromResult(_factory(processId));
    }
}
