using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Signals;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests the diagnosis-agnostic CPU signal groupings (#523): self-time concentration (both the inline
/// summary path and the full-tree Resource path) and the namespace roll-up (Resource path only).
/// These describe <i>where</i> the CPU concentrates, never <i>what</i> the bug is.
/// </summary>
public sealed class CpuSampleSignalsTests
{
    private const string IcuLeaf =
        "System.Globalization.CompareInfo.IcuGetHashCodeOfString(System.ReadOnlySpan`1<wchar>)";

    // ---- Inline summary path (from CpuSample.TopSelfTime) ---------------------------------------

    [Fact]
    public void Inline_EmitsConcentration_WhenSelfTimeLeaderIsHot()
    {
        var sample = new CpuSample(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            TopHotspots: new[] { new Hotspot(new SampledFrame("MyApp.dll", "MyApp.Handler()"), 100, 0) })
        {
            TopSelfTime = new Hotspot(new SampledFrame("System.Private.CoreLib.dll", IcuLeaf), 89, 89),
        };

        var signals = CpuSampleSignals.Detect(sample, "handle-abc");

        var concentration = signals.Should().ContainSingle(s => s.Signal == "cpu.self-time.concentration").Subject;
        concentration.Salience.Should().BeApproximately(0.89, 0.001);
        concentration.Summary.Should().Contain("concentrated");
        concentration.Buckets[0].Key.Should().Be(IcuLeaf);
        concentration.Buckets[0].Handle.Should().Be("handle-abc");
        // Namespace roll-up is Resource-only; it must NOT appear on the inline path.
        signals.Should().NotContain(s => s.Signal == "cpu.self-time.by-namespace");
    }

    [Fact]
    public void Inline_EmitsNothing_WhenSelfTimeIsDiffuse()
    {
        var sample = new CpuSample(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            TopHotspots: new[] { new Hotspot(new SampledFrame("MyApp.dll", "MyApp.Handler()"), 100, 0) })
        {
            TopSelfTime = new Hotspot(new SampledFrame("MyApp.dll", "MyApp.Bits()"), 10, 10),
        };

        CpuSampleSignals.Detect(sample, "h").Should().BeEmpty();
    }

    // ---- Resource path (from the stored CpuSampleTraceArtifact, full tree) ----------------------

    [Fact]
    public void Artifact_EmitsConcentrationAndNamespaceRollup_WhenHot()
    {
        var artifact = ArtifactWithLeaves(total: 100, ("MyApp.dll", "MyApp.Handler()", 11), ("System.Private.CoreLib.dll", IcuLeaf, 89));

        var signals = CpuSampleSignals.Detect(artifact, "handle-xyz");

        signals.Should().Contain(s => s.Signal == "cpu.self-time.concentration");
        var byNs = signals.Should().ContainSingle(s => s.Signal == "cpu.self-time.by-namespace").Subject;
        byNs.Buckets[0].Key.Should().Be("System.Globalization");
        byNs.Buckets[0].Handle.Should().Be("handle-xyz");
        byNs.Salience.Should().BeApproximately(0.89, 0.001);
    }

    [Fact]
    public void Artifact_EmitsNothing_WhenDiffuse()
    {
        // Eight distinct namespaces each at 12.5% self-time — nothing crosses either salience gate.
        var leaves = Enumerable.Range(0, 8)
            .Select(i => ("Lib.dll", $"Ns{i}.Type.Work()", 100L))
            .ToArray();
        var artifact = ArtifactWithLeaves(total: 800, leaves);

        CpuSampleSignals.Detect(artifact, "h").Should().BeEmpty();
    }

    private static CpuSampleTraceArtifact ArtifactWithLeaves(long total, params (string Module, string Method, long Exclusive)[] leaves)
    {
        var leafSum = leaves.Sum(l => l.Exclusive);
        var children = leaves
            .Select(l => new CallTreeNode(new SampledFrame(l.Module, l.Method), l.Exclusive, l.Exclusive, Array.Empty<CallTreeNode>()))
            .ToArray();
        var root = new CallTreeNode(
            new SampledFrame("MyApp.dll", "MyApp.Root()"),
            InclusiveSamples: total,
            ExclusiveSamples: total - leafSum,
            Children: children);

        return new CpuSampleTraceArtifact(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: total,
            Root: root);
    }
}
