using System.Reflection;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class CpuSummaryPresentationTests
{
    [Theory]
    [InlineData(false, 4)]
    [InlineData(false, 0)]
    [InlineData(true, 4)]
    [InlineData(true, 0)]
    public void CpuSummary_KeepsEvidenceAndStructuredNavigation_WithoutRepeatedCommands(bool core, int exclusiveSamples)
    {
        var hotspot = new Hotspot(new SampledFrame("fixture", "Fixture.Work"), 10, exclusiveSamples);
        var sample = new CpuSample(4242, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(3), 10, [hotspot]);
        const string handle = "PRESENTATIONHANDLE";
        var hints = new[]
        {
            new NextActionHint("query_snapshot", "Rank exclusive stack samples.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = "top-methods", ["rankBy"] = "exclusive" }),
            new NextActionHint("query_snapshot", "Explore caller/callee paths.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = "call-tree" }),
        };
        var owner = core ? typeof(SamplerUseCases) : typeof(DiagnosticToolSampling);
        var build = owner.GetMethod("BuildCpuSampleResult", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (DiagnosticResult<CpuSample>)build.Invoke(null,
            [sample, 3, handle, DateTimeOffset.UtcNow.AddMinutes(5), SamplingDepth.Summary, null, Array.Empty<SignalGroup>(), hints])!;

        result.Data.Should().BeSameAs(sample);
        result.Hints.Should().BeEquivalentTo(hints);
        result.Handle.Should().Be(handle);
        result.Summary.Should().Contain("Captured 10 samples over 3s");
        result.Summary.Should().Contain("Fixture.Work");
        result.Summary.Should().Contain("scheduler-state semantics are not established");
        result.Summary.Should().NotContain("query_snapshot");
        result.Summary.Should().NotContain(handle);
        if (exclusiveSamples > 0)
        {
            result.Summary.Should().Contain("4 of 10 observations, 40% of stack samples");
        }
        else
        {
            result.Summary.Should().Contain("10 inclusive / 0 exclusive");
            result.Summary.Should().Contain("wait/blocking primitive");
        }
    }
}
