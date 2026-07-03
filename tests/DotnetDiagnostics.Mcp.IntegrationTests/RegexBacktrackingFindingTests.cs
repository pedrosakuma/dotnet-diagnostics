using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Findings;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Reference-adopter tests for the findings layer (#515): the regex-backtracking detector migrated
/// from <c>DiagnosticTools.TryBuildRegexBacktrackingHint</c> (#388) onto the <see cref="Finding"/>
/// schema via <see cref="RegexBacktrackingFindingProvider"/> / <see cref="CpuSampleFindings"/>.
/// Detection thresholds are unchanged.
/// </summary>
public sealed class RegexBacktrackingFindingTests
{
    [Fact]
    public void EmitsFinding_WhenRegexEngineIsHot()
    {
        var sample = SampleWithTopFrame(
            "System.Text.RegularExpressions.RegexRunner.Scan(System.Text.RegularExpressions.Regex, System.ReadOnlySpan`1[[System.Char]])");

        var findings = CpuSampleFindings.Detect(sample, "handle-123");

        findings.Should().ContainSingle();
        var finding = findings[0];
        finding.Pattern.Should().Be("regex-backtracking");
        finding.Severity.Should().Be(FindingSeverity.High);
        finding.SuggestedFix.Should().Contain("GeneratedRegex");
        finding.NextAction.Should().NotBeNull();
        finding.NextAction!.NextTool.Should().Be("query_snapshot");
        finding.NextAction.SuggestedArguments.Should().ContainKey("handle").WhoseValue.Should().Be("handle-123");
        finding.NextAction.SuggestedArguments.Should().ContainKey("view").WhoseValue.Should().Be("call-tree");
        finding.Evidence.Should().ContainSingle();
        finding.Evidence[0].Handle.Should().Be("handle-123");
    }

    [Fact]
    public void EmitsFinding_ForInterpreter()
    {
        var sample = SampleWithTopFrame(
            "System.Text.RegularExpressions.RegexInterpreter.Go()");

        CpuSampleFindings.Detect(sample, "h").Should().ContainSingle();
    }

    [Fact]
    public void EmitsNoFinding_WhenNoRegexFrame()
    {
        var sample = SampleWithTopFrame("MyApp.Services.OrderService.Process()");

        CpuSampleFindings.Detect(sample, "h").Should().BeEmpty();
    }

    [Fact]
    public void EmitsNoFinding_WhenRegexFrameIsColdInTopN()
    {
        // A low-share incidental regex frame (e.g. 5 of 1000 inclusive samples) must NOT fire the
        // catastrophic-backtracking finding — only a genuinely hot regex profile should.
        var sample = new CpuSample(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 1000,
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("MyApp.dll", "MyApp.Services.OrderService.Process()"), 900, 900),
                new Hotspot(new SampledFrame("System.Private.CoreLib.dll", "System.Text.RegularExpressions.RegexRunner.Scan()"), 5, 5),
            });

        CpuSampleFindings.Detect(sample, "h").Should().BeEmpty();
    }

    private static CpuSample SampleWithTopFrame(string method)
        => new(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("System.Private.CoreLib.dll", method), 80, 80),
            });
}
