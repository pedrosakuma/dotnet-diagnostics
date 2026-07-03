using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Mcp.Resources;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class FindingsResourceTests
{
    private static CpuSampleTraceArtifact HotRegexArtifact()
    {
        var leaf = new CallTreeNode(
            new SampledFrame("System.Private.CoreLib.dll", "System.Text.RegularExpressions.RegexRunner.Scan()"),
            InclusiveSamples: 80,
            ExclusiveSamples: 80,
            Children: Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(
            new SampledFrame("MyApp.dll", "MyApp.Handler()"),
            InclusiveSamples: 100,
            ExclusiveSamples: 20,
            Children: new[] { leaf });

        return new CpuSampleTraceArtifact(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            Root: root);
    }

    [Fact]
    public void ReadCpuSampleFindings_ReturnsRegexFinding_ForRegisteredHandle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, "cpu-sample", HotRegexArtifact(), TimeSpan.FromMinutes(10));

        var json = FindingsResources.ReadCpuSampleFindings(store, handle.Id);

        json.Should().Contain("regex-backtracking");
        json.Should().Contain(handle.Id);
    }

    [Fact]
    public void ReadCpuSampleFindings_ReturnsError_ForNonCpuHandle()
    {
        // A CpuSampleTraceArtifact registered under a non-cpu-sample kind (allocation/native-alloc)
        // must NOT be interpreted as CPU samples.
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, "allocation-sample", HotRegexArtifact(), TimeSpan.FromMinutes(10));

        var json = FindingsResources.ReadCpuSampleFindings(store, handle.Id);

        json.Should().Contain("unknown");
        json.Should().NotContain("regex-backtracking");
    }

    [Fact]
    public void ReadCpuSampleFindings_ReturnsError_ForUnknownHandle()
    {
        var store = new MemoryDiagnosticHandleStore();

        var json = FindingsResources.ReadCpuSampleFindings(store, "does-not-exist");

        json.Should().Contain("unknown");
    }
}
