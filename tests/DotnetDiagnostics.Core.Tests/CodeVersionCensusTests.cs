using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CodeVersionCensusTests
{
    private static PublishedCodeVersion V(ulong id, ulong start, uint size, string tier, ulong rejit = 0)
        => new(id, 1, (uint)id, start, size, rejit, tier);

    [Fact]
    public void SamplesOfOneMethod_SplitAcrossLiveVersions()
    {
        var c = new CodeVersionCensus();
        c.AddPublishedVersion(V(7, 0x1000, 0x100, "OptimizedTier1"));
        c.AddPublishedVersion(V(7, 0x2000, 0x80, "OptimizedTier1OSR"));
        for (var i = 0; i < 3; i++) c.AddLeafSample(0x1010, "m.dll", "P.Work");
        c.AddLeafSample(0x1020, "m.dll", "P.Work");
        c.AddLeafSample(0x2000, "m.dll", "P.Work");
        c.AddLeafSample(0x9000, "m.dll", "Native");

        var p = c.Build();

        p.TotalSamples.Should().Be(6);
        p.ResolvedSamples.Should().Be(5);
        p.UnresolvedSamples.Should().Be(1);
        p.Versions.Should().HaveCount(2);
        p.Versions[0].OptimizationTier.Should().Be("OptimizedTier1");
        p.Versions[0].Samples.Should().Be(4);
        p.Versions[1].OptimizationTier.Should().Be("OptimizedTier1OSR");
        p.Versions.Should().OnlyContain(r => r.MethodVersionCount == 2 && r.MethodSamples == 5);
        p.Versions[0].VersionId.Should().NotBe(p.Versions[1].VersionId);
    }

    [Fact]
    public void RundownStartAndStop_AreNotDoubleCounted()
    {
        var c = new CodeVersionCensus();
        c.AddPublishedVersion(V(7, 0x1000, 0x100, "QuickJitted"));
        c.AddPublishedVersion(V(7, 0x1000, 0x100, "QuickJitted"));
        c.AddLeafSample(0x1000, "m", "P.A");

        var p = c.Build();

        p.PublishedVersions.Should().Be(1);
        p.Versions.Should().ContainSingle().Which.Samples.Should().Be(1);
    }

    [Fact]
    public void RangeBoundaries_AreHalfOpen()
    {
        var c = new CodeVersionCensus();
        c.AddPublishedVersion(V(1, 0x1000, 0x10, "A"));
        c.AddPublishedVersion(V(2, 0x1010, 0x10, "B"));
        c.AddLeafSample(0x100F, "m", "One");
        c.AddLeafSample(0x1010, "m", "Two");
        c.AddLeafSample(0x1020, "m", "Gap");

        var p = c.Build();

        p.Versions.Select(v => v.OptimizationTier).Should().BeEquivalentTo(["A", "B"]);
        p.UnresolvedSamples.Should().Be(1);
    }

    [Fact]
    public void OverlappingRanges_AreLeftUnresolvedWithNote()
    {
        var c = new CodeVersionCensus();
        c.AddPublishedVersion(V(1, 0x1000, 0x100, "A"));
        c.AddPublishedVersion(V(2, 0x1000, 0x100, "B"));
        c.AddLeafSample(0x1010, "m", "X");

        var p = c.Build();

        p.Versions.Should().BeEmpty();
        p.UnresolvedSamples.Should().Be(1);
        p.Notes.Should().Contain(n => n.Contains("overlapping"));
    }

    [Fact]
    public void Build_IsDeterministicRegardlessOfInsertionOrder()
    {
        static CodeVersionProfile Make(bool reverse)
        {
            var c = new CodeVersionCensus();
            var vs = new[] { V(1, 0x1000, 0x10, "A"), V(2, 0x2000, 0x10, "B"), V(3, 0x3000, 0x10, "C") };
            foreach (var v in reverse ? vs.Reverse() : vs) c.AddPublishedVersion(v);
            foreach (var ip in new ulong[] { 0x1000, 0x2000, 0x3000 }) c.AddLeafSample(ip, "m", "M" + ip);
            return c.Build();
        }

        Make(false).Versions.Select(v => v.VersionId)
            .Should().Equal(Make(true).Versions.Select(v => v.VersionId));
    }

    [Fact]
    public void Caps_AreEnforcedAtInsertionWithNotes()
    {
        var c = new CodeVersionCensus();
        for (ulong i = 0; i < CodeVersionCensus.MaxDistinctLeafAddresses + 5; i++)
        {
            c.AddLeafSample(0x10000 + i, "m", "X");
        }

        var p = c.Build();

        p.Notes.Should().Contain(n => n.Contains("MaxDistinctLeafAddresses") && n.Contains("5 samples"));
        p.TotalSamples.Should().Be(CodeVersionCensus.MaxDistinctLeafAddresses + 5);
    }

    [Fact]
    public void NoPublishedVersions_ProducesExplicitNote()
    {
        var c = new CodeVersionCensus();
        c.AddLeafSample(0x1000, "m", "X");
        c.Build().Notes.Should().Contain(n => n.Contains("No method load or rundown"));
    }
}
