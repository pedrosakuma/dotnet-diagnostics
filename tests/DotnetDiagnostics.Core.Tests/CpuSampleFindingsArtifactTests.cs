using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Findings;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Exercises the Resource path: findings derived from a stored <see cref="CpuSampleTraceArtifact"/>
/// (re-ranking the full call tree), not the compact summary. This is what the findings Resource runs.
/// </summary>
public sealed class CpuSampleFindingsArtifactTests
{
    private static CpuSampleTraceArtifact ArtifactWithLeaf(string leafMethod, long leafSamples, long total)
    {
        var leaf = new CallTreeNode(
            new SampledFrame("System.Private.CoreLib.dll", leafMethod),
            InclusiveSamples: leafSamples,
            ExclusiveSamples: leafSamples,
            Children: Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(
            new SampledFrame("MyApp.dll", "MyApp.Handler()"),
            InclusiveSamples: total,
            ExclusiveSamples: total - leafSamples,
            Children: new[] { leaf });

        return new CpuSampleTraceArtifact(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: total,
            Root: root);
    }

    [Fact]
    public void Detect_FromArtifact_EmitsRegexFinding_WhenRegexLeafIsHot()
    {
        var artifact = ArtifactWithLeaf("System.Text.RegularExpressions.RegexRunner.Scan()", leafSamples: 80, total: 100);

        var findings = CpuSampleFindings.Detect(artifact, "handle-xyz");

        findings.Should().ContainSingle();
        findings[0].Pattern.Should().Be("regex-backtracking");
        findings[0].Evidence[0].Handle.Should().Be("handle-xyz");
    }

    [Fact]
    public void Detect_FromArtifact_EmitsNoFinding_WhenRegexLeafIsCold()
    {
        var artifact = ArtifactWithLeaf("System.Text.RegularExpressions.RegexRunner.Scan()", leafSamples: 5, total: 1000);

        CpuSampleFindings.Detect(artifact, "h").Should().BeEmpty();
    }
}
