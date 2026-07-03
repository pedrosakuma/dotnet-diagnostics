using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Findings;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests for the culture-aware string-op detector (#516): the <c>culture-lookup</c> case study
/// (an accidental <c>InvariantCultureIgnoreCase</c> comparer) shows up only as an ICU collation
/// frame dominating CPU self-time. Fires against that signature, silent otherwise.
/// </summary>
public sealed class CultureAwareStringFindingTests
{
    private const string IcuLeaf =
        "System.Globalization.CompareInfo.IcuGetHashCodeOfString(System.ReadOnlySpan`1<wchar>, System.Globalization.CompareOptions)";

    [Fact]
    public void EmitsFinding_WhenIcuFrameLeadsSelfTime()
    {
        // Real /culture-lookup shape: the ICU leaf burns self-time while its inclusive ancestors are
        // innocuous plumbing, so it only surfaces via the global self-time leader (TopSelfTime).
        var sample = new CpuSample(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("MyApp.dll", "MyApp.Handler()"), 100, 0),
            })
        {
            TopSelfTime = new Hotspot(new SampledFrame("System.Private.CoreLib.dll", IcuLeaf), 89, 89),
        };

        var findings = CpuSampleFindings.Detect(sample, "handle-abc");

        findings.Should().ContainSingle();
        var finding = findings[0];
        finding.Pattern.Should().Be("culture-aware-string-op");
        finding.Severity.Should().Be(FindingSeverity.High);
        finding.SuggestedFix.Should().Contain("Ordinal");
        finding.NextAction!.NextTool.Should().Be("query_snapshot");
        finding.NextAction.SuggestedArguments.Should().ContainKey("view").WhoseValue.Should().Be("call-tree");
        finding.Evidence[0].Handle.Should().Be("handle-abc");
        finding.Evidence[0].Value.Should().Be(89.0);
    }

    [Fact]
    public void EmitsFinding_FromHotspotList_WhenNoSelfTimeLeader()
    {
        var sample = new CpuSample(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("System.Private.CoreLib.dll", IcuLeaf), 60, 60),
            });

        CpuSampleFindings.Detect(sample, "h").Should().ContainSingle()
            .Which.Pattern.Should().Be("culture-aware-string-op");
    }

    [Fact]
    public void EmitsFinding_ForNlsCasingFrame()
    {
        var sample = new CpuSample(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("System.Private.CoreLib.dll", "System.Globalization.TextInfo.NlsChangeCase()"), 50, 50),
            });

        CpuSampleFindings.Detect(sample, "h").Should().ContainSingle();
    }

    [Fact]
    public void EmitsNoFinding_WhenNoCultureFrame()
    {
        var sample = new CpuSample(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("MyApp.dll", "MyApp.Services.OrderService.Process()"), 90, 90),
            });

        CpuSampleFindings.Detect(sample, "h").Should().BeEmpty();
    }

    [Fact]
    public void EmitsNoFinding_WhenCultureFrameIsCold()
    {
        // A low-share incidental culture-aware call (common in any app) must NOT fire.
        var sample = new CpuSample(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 1000,
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("MyApp.dll", "MyApp.Services.OrderService.Process()"), 900, 900),
                new Hotspot(new SampledFrame("System.Private.CoreLib.dll", IcuLeaf), 30, 30),
            });

        CpuSampleFindings.Detect(sample, "h").Should().BeEmpty();
    }

    [Fact]
    public void Detect_FromArtifact_EmitsCultureFinding_WhenIcuLeafIsHot()
    {
        // Deep hot ICU leaf under innocuous plumbing — the artifact path re-ranks the full tree, so
        // the self-time leader (the ICU leaf) is caught even though its inclusive ancestor dominates.
        var leaf = new CallTreeNode(
            new SampledFrame("System.Private.CoreLib.dll", IcuLeaf),
            InclusiveSamples: 89,
            ExclusiveSamples: 89,
            Children: Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(
            new SampledFrame("MyApp.dll", "MyApp.Handler()"),
            InclusiveSamples: 100,
            ExclusiveSamples: 11,
            Children: new[] { leaf });
        var artifact = new CpuSampleTraceArtifact(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            Root: root);

        var findings = CpuSampleFindings.Detect(artifact, "handle-xyz");

        findings.Should().ContainSingle()
            .Which.Pattern.Should().Be("culture-aware-string-op");
        findings[0].Evidence[0].Handle.Should().Be("handle-xyz");
    }
}
