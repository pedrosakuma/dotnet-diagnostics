using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class CollectCpuSampleSecurityTests
{
    [Fact]
    public async Task SymbolPath_RemoteHost_NotAllowlisted_IsRejected()
    {
        var sampler = new ThrowingCpuSampler();
        var store = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.CollectCpuSample(
            sampler, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            processId: 4242,
            durationSeconds: 1,
            resolveSourceLines: true,
            symbolPath: @"srv*c:\sym*https://msdl.microsoft.com/download/symbols");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("SymbolServerNotAllowed");
        sampler.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task SymbolPath_RemoteHost_OnAllowlist_PassesThrough()
    {
        var sampler = new StubCpuSampler();
        var store = new MemoryDiagnosticHandleStore();
        var options = new SecurityOptions { SymbolServerAllowlist = { "msdl.microsoft.com" } };

        var result = await DiagnosticTools.CollectCpuSample(
            sampler, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(options),
            TestPrincipalAccessors.Root,
            processId: 4242,
            durationSeconds: 1,
            resolveSourceLines: true,
            symbolPath: @"srv*c:\sym*https://msdl.microsoft.com/download/symbols");

        result.Error.Should().BeNull();
        sampler.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task SymbolPath_LocalPath_PassesThrough()
    {
        var sampler = new StubCpuSampler();
        var store = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.CollectCpuSample(
            sampler, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            processId: 4242,
            durationSeconds: 1,
            resolveSourceLines: true,
            symbolPath: "/srv/symbols");

        result.Error.Should().BeNull();
        sampler.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task ExplicitCpuBackend_PassesThroughWithoutFallback()
    {
        var sampler = new StubCpuSampler();
        var store = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.CollectCpuSample(
            sampler, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            processId: 4242,
            durationSeconds: 1,
            cpuBackend: CpuSamplingMode.Os);

        result.Error.Should().BeNull();
        sampler.LastMode.Should().Be(CpuSamplingMode.Os);
    }

    [Theory]
    [InlineData(true, false, "exportTrace")]
    [InlineData(false, true, "resolveMethodInstantiations")]
    public async Task OsBackend_RejectsEventPipeOnlyOptions(
        bool exportTrace,
        bool resolveMethodInstantiations,
        string expectedParameter)
    {
        var sampler = new StubCpuSampler();
        var result = await DiagnosticTools.CollectCpuSample(
            sampler,
            new MemoryDiagnosticHandleStore(),
            ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            processId: 4242,
            durationSeconds: 1,
            resolveMethodInstantiations: resolveMethodInstantiations,
            cpuBackend: CpuSamplingMode.Os,
            exportTrace: exportTrace);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain(expectedParameter);
        sampler.Invocations.Should().Be(0);
    }

    private sealed class StubCpuSampler : ICpuSampler
    {
        public int Invocations { get; private set; }
        public CpuSamplingMode? LastMode { get; private set; }

        public Task<CpuSampleResult> SampleAsync(int processId, TimeSpan duration, int topN = 25, SourceResolutionOptions? sourceResolution = null, MethodInstantiationResolutionOptions? methodInstantiationResolution = null, NativeAotSymbolResolutionOptions? nativeAotSymbols = null, bool exportTrace = false, CancellationToken cancellationToken = default)
        {
            Invocations++;
            var summary = new CpuSample(processId, DateTimeOffset.UtcNow, duration, 0, Array.Empty<Hotspot>());
            var root = new CallTreeNode(new SampledFrame("stub", "Root"), 0, 0, Array.Empty<CallTreeNode>());
            var artifact = new CpuSampleTraceArtifact(processId, DateTimeOffset.UtcNow, duration, 0, root);
            return Task.FromResult(new CpuSampleResult(summary, artifact));
        }

        public Task<CpuSampleResult> SampleAsync(
            int processId,
            TimeSpan duration,
            int topN,
            SourceResolutionOptions? sourceResolution,
            MethodInstantiationResolutionOptions? methodInstantiationResolution,
            NativeAotSymbolResolutionOptions? nativeAotSymbols,
            bool exportTrace,
            CpuSamplingMode mode,
            CancellationToken cancellationToken = default)
        {
            LastMode = mode;
            return SampleAsync(
                processId,
                duration,
                topN,
                sourceResolution,
                methodInstantiationResolution,
                nativeAotSymbols,
                exportTrace,
                cancellationToken);
        }
    }

    private sealed class ThrowingCpuSampler : ICpuSampler
    {
        public int Invocations { get; private set; }

        public Task<CpuSampleResult> SampleAsync(int processId, TimeSpan duration, int topN = 25, SourceResolutionOptions? sourceResolution = null, MethodInstantiationResolutionOptions? methodInstantiationResolution = null, NativeAotSymbolResolutionOptions? nativeAotSymbols = null, bool exportTrace = false, CancellationToken cancellationToken = default)
        {
            Invocations++;
            throw new InvalidOperationException("should not be reached when symbol path is rejected");
        }
    }
}
