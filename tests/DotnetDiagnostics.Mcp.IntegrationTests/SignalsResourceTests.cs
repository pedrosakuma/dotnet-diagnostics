using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Mcp.Resources;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class SignalsResourceTests
{
    private static CpuSampleTraceArtifact HotArtifact()
    {
        var leaf = new CallTreeNode(
            new SampledFrame("System.Private.CoreLib.dll", "System.Globalization.CompareInfo.IcuGetHashCodeOfString()"),
            InclusiveSamples: 89,
            ExclusiveSamples: 89,
            Children: Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(
            new SampledFrame("MyApp.dll", "MyApp.Handler()"),
            InclusiveSamples: 100,
            ExclusiveSamples: 11,
            Children: new[] { leaf });

        return new CpuSampleTraceArtifact(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            Root: root);
    }

    [Fact]
    public void ReadCpuSampleSignals_ReturnsGroupings_ForRegisteredHandle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, "cpu-sample", HotArtifact(), TimeSpan.FromMinutes(10));

        var json = SignalsResources.ReadCpuSampleSignals(store, handle.Id);

        json.Should().Contain("cpu.self-time.concentration");
        json.Should().Contain("cpu.self-time.by-namespace");
        json.Should().Contain("System.Globalization");
        json.Should().Contain(handle.Id);
    }

    [Fact]
    public void ReadCpuSampleSignals_ReturnsError_ForNonCpuHandle()
    {
        // A CpuSampleTraceArtifact registered under a non-cpu-sample kind (allocation/native-alloc)
        // must NOT be interpreted as CPU samples.
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, "allocation-sample", HotArtifact(), TimeSpan.FromMinutes(10));

        var json = SignalsResources.ReadCpuSampleSignals(store, handle.Id);

        json.Should().Contain("unknown");
        json.Should().NotContain("cpu.self-time");
    }

    [Fact]
    public void ReadCpuSampleSignals_ReturnsError_ForUnknownHandle()
    {
        var store = new MemoryDiagnosticHandleStore();

        var json = SignalsResources.ReadCpuSampleSignals(store, "does-not-exist");

        json.Should().Contain("unknown");
    }
}
