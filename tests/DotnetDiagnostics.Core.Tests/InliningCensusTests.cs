using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class InliningCensusTests
{
    private static PublishedCodeVersion V(ulong methodId, ulong start, string tier)
        => new(methodId, 1, (uint)methodId, start, 0x100, 0, tier);

    private static InliningDecision Ok(string inliner, string inlinee) => new(inliner, inlinee, true, null);

    private static InliningDecision No(string inliner, string inlinee, string reason) => new(inliner, inlinee, false, reason);

    [Fact]
    public void Decisions_AreAttributedToTheVersionPublishedByTheMatchingLoad()
    {
        var c = new InliningCensus();
        c.OnJittingStarted(1, 7, "P.Work");
        c.OnInlining(1, Ok("P.Work", "P.Blend"));
        c.OnInlining(1, Ok("P.Blend", "P.Scale"));
        c.OnMethodLoaded(1, V(7, 0x1000, "OptimizedTier1"));

        var p = c.Build();

        p.TotalDecisions.Should().Be(2);
        p.AttributedDecisions.Should().Be(2);
        p.UnattributedDecisions.Should().Be(0);
        p.VersionsWithDecisions.Should().Be(1);
        p.Decisions.Should().OnlyContain(r => r.OptimizationTier == "OptimizedTier1" && r.CompiledMethod == "P.Work" && r.VersionId == "1:00000007@1000");
        p.Decisions.Select(r => (r.Inliner, r.Inlinee)).Should().BeEquivalentTo([("P.Work", "P.Blend"), ("P.Blend", "P.Scale")]);
    }

    [Fact]
    public void InterleavedThreads_AreJoinedPerThread()
    {
        var c = new InliningCensus();
        c.OnJittingStarted(1, 7, "A");
        c.OnJittingStarted(2, 8, "B");
        c.OnInlining(2, Ok("B", "Y"));
        c.OnInlining(1, Ok("A", "X"));
        c.OnMethodLoaded(1, V(7, 0x1000, "T1"));
        c.OnInlining(2, Ok("B", "Z"));
        c.OnMethodLoaded(2, V(8, 0x2000, "T1"));

        var p = c.Build();

        p.UnattributedDecisions.Should().Be(0);
        p.Decisions.Where(r => r.CompiledMethod == "A").Select(r => r.Inlinee).Should().Equal("X");
        p.Decisions.Where(r => r.CompiledMethod == "B").Select(r => r.Inlinee).Should().BeEquivalentTo(["Y", "Z"]);
    }

    [Fact]
    public void RefusalReasons_AreKeptAndCounted()
    {
        var c = new InliningCensus();
        c.OnJittingStarted(1, 7, "A");
        c.OnInlining(1, No("A", "Big", "too many IL bytes"));
        c.OnInlining(1, Ok("A", "Small"));
        c.OnMethodLoaded(1, V(7, 0x1000, "T1"));

        var p = c.Build();

        p.RefusedDecisions.Should().Be(1);
        p.SucceededDecisions.Should().Be(1);
        p.Decisions.Should().ContainSingle(r => !r.Succeeded).Which.FailReason.Should().Be("too many IL bytes");
        p.Decisions.Should().ContainSingle(r => r.Succeeded).Which.FailReason.Should().BeNull();
    }

    [Fact]
    public void SamePairIsRetakenPerVersion_AndAggregatedWithinOne()
    {
        var c = new InliningCensus();
        foreach (var (start, tier) in new[] { (0x1000UL, "QuickJitted"), (0x2000UL, "OptimizedTier1") })
        {
            c.OnJittingStarted(1, 7, "Work");
            c.OnInlining(1, Ok("Work", "Scale"));
            c.OnInlining(1, Ok("Work", "Scale"));
            c.OnMethodLoaded(1, V(7, start, tier));
        }

        var p = c.Build();

        p.VersionsWithDecisions.Should().Be(2);
        p.Decisions.Should().HaveCount(2).And.OnlyContain(r => r.Count == 2);
        p.Decisions.Select(r => r.VersionId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Decisions_WithoutAPublishedVersion_AreUnattributedNotGuessed()
    {
        var c = new InliningCensus();
        c.OnInlining(1, Ok("A", "X"));

        c.OnJittingStarted(2, 7, "B");
        c.OnInlining(2, Ok("B", "Y"));
        c.OnJittingStarted(2, 8, "C");
        c.OnMethodLoaded(2, V(7, 0x1000, "T1"));

        c.OnJittingStarted(3, 9, "D");
        c.OnInlining(3, Ok("D", "Z"));

        var p = c.Build();

        p.TotalDecisions.Should().Be(3);
        p.AttributedDecisions.Should().Be(0);
        p.UnattributedDecisions.Should().Be(3);
        p.Decisions.Should().BeEmpty();
        p.Notes.Should().Contain(n => n.Contains("3 inlining decisions could not be tied"));
    }

    [Fact]
    public void NoEvents_ProducesExplicitJitTracingNote()
    {
        new InliningCensus().Build().Notes.Should().Contain(n => n.Contains("0x1000"));
    }

    [Fact]
    public void MaxPendingThreads_IsEnforcedAtInsertionWithNote()
    {
        var c = new InliningCensus();
        for (uint t = 0; t < InliningCensus.MaxPendingThreads + 3; t++)
        {
            c.OnJittingStarted(t, t, "M");
        }

        c.OnInlining(InliningCensus.MaxPendingThreads + 1, Ok("M", "X"));

        var p = c.Build();

        p.Notes.Should().Contain(n => n.Contains("MaxPendingThreads") && n.Contains("3 compilations"));
        p.UnattributedDecisions.Should().Be(1);
    }

    [Fact]
    public void MaxDecisionsPerCompilation_IsEnforcedAtInsertionWithNote()
    {
        var c = new InliningCensus();
        c.OnJittingStarted(1, 7, "Huge");
        for (var i = 0; i < InliningCensus.MaxDecisionsPerCompilation + 4; i++)
        {
            c.OnInlining(1, Ok("Huge", "Callee" + i));
        }

        c.OnMethodLoaded(1, V(7, 0x1000, "T1"));
        var p = c.Build();

        p.Decisions.Should().HaveCount(InliningCensus.MaxDecisionsPerCompilation);
        p.Notes.Should().Contain(n => n.Contains("MaxDecisionsPerCompilation") && n.Contains("4 inlining decisions were dropped"));
        p.UnattributedDecisions.Should().Be(4);
    }

    [Fact]
    public void MaxPendingDecisions_BoundsBufferingAcrossThreadsWithNote()
    {
        var c = new InliningCensus();
        var threads = InliningCensus.MaxPendingDecisions / InliningCensus.MaxDecisionsPerCompilation;
        for (uint t = 1; t <= threads + 1; t++)
        {
            c.OnJittingStarted(t, t, "M" + t);
            var count = t <= threads ? InliningCensus.MaxDecisionsPerCompilation : 10;
            for (var i = 0; i < count; i++)
            {
                c.OnInlining(t, Ok("M" + t, "Callee" + i));
            }
        }

        var p = c.Build();

        p.Notes.Should().Contain(n => n.Contains("MaxPendingDecisions") && n.Contains("10 decisions were not buffered"));
        p.UnattributedDecisions.Should().Be(p.TotalDecisions);
    }

    [Fact]
    public void MaxInliningRecords_IsEnforcedAtInsertionWithNote()
    {
        var c = new InliningCensus();
        var total = InliningCensus.MaxInliningRecords + 5;
        ulong method = 1;
        var inCompilation = 0;
        c.OnJittingStarted(1, method, "M");
        for (var i = 0; i < total; i++)
        {
            if (inCompilation == InliningCensus.MaxDecisionsPerCompilation)
            {
                c.OnMethodLoaded(1, V(method, 0x1000 * method, "T1"));
                method++;
                c.OnJittingStarted(1, method, "M");
                inCompilation = 0;
            }

            c.OnInlining(1, Ok("M", "Callee" + i));
            inCompilation++;
        }

        c.OnMethodLoaded(1, V(method, 0x1000 * method, "T1"));
        var p = c.Build();

        p.Decisions.Should().HaveCount(InliningCensus.MaxInliningRecords);
        p.Notes.Should().Contain(n => n.Contains("MaxInliningRecords") && n.Contains("5 inlining decisions were dropped"));
        p.UnattributedDecisions.Should().Be(5);
    }

    [Fact]
    public void LongNames_AreTruncatedWithNote()
    {
        var c = new InliningCensus();
        c.OnJittingStarted(1, 7, "M");
        c.OnInlining(1, Ok("M", new string('x', InliningCensus.MaxNameLength + 10)));
        c.OnMethodLoaded(1, V(7, 0x1000, "T1"));

        var p = c.Build();

        p.Decisions.Single().Inlinee.Should().HaveLength(InliningCensus.MaxNameLength);
        p.Notes.Should().Contain(n => n.Contains("MaxNameLength") && n.Contains("1 name"));
    }

    [Fact]
    public void Build_IsDeterministicAndRepeatable()
    {
        static InliningProfile Make(bool reverse)
        {
            var c = new InliningCensus();
            var order = new[] { 1u, 2u, 3u };
            foreach (var t in reverse ? order.Reverse() : order)
            {
                c.OnJittingStarted(t, t, "M" + t);
                c.OnInlining(t, Ok("M" + t, "Callee"));
                c.OnMethodLoaded(t, V(t, 0x1000 * t, "T1"));
            }

            return c.Build();
        }

        var a = Make(false);
        a.Decisions.Select(r => r.VersionId).Should().Equal(Make(true).Decisions.Select(r => r.VersionId));
    }

    [Fact]
    public void Dispatcher_ExplainsInlinedMethodsAndRejectsCapturesWithoutInlining()
    {
        var c = new InliningCensus();
        c.OnJittingStarted(1, 7, "P.Work");
        c.OnInlining(1, Ok("P.Work", "P.Scale"));
        c.OnInlining(1, No("P.Work", "P.Huge", "too big"));
        c.OnMethodLoaded(1, V(7, 0x1000, "OptimizedTier1"));
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 0, 0, []);

        var without = new CpuSampleTraceArtifact(1, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), 0, root);
        CpuSampleQueryDispatcher.RenderInlining(without, "h", null, 10).Error!.Kind.Should().Be("NotSupported");

        var with = without with
        {
            Inlining = c.Build(),
            CodeVersions = new CodeVersionProfile(2, 2, 0, 2,
            [
                new CodeVersionSampleRow("1:00000007@1000", "m", "P.Work(int)", "OptimizedTier1", "0x1000", 0x100, 0, 2, 1, 2),
                new CodeVersionSampleRow("1:00000009@3000", "m", "P.Scale(int)", "OptimizedTier1", "0x3000", 0x100, 0, 0, 1, 0),
            ], []),
        };

        var view = CpuSampleQueryDispatcher.RenderInlining(with, "h", "Scale", 10);
        view.Error.Should().BeNull();
        view.Data!.Decisions.Should().ContainSingle().Which.Inlinee.Should().Be("P.Scale");
        view.Data.TotalDecisions.Should().Be(2);

        var versions = CpuSampleQueryDispatcher.RenderCodeVersions(with, "h", null, 10).Data!.Versions;
        versions.Single(v => v.Method.StartsWith("P.Scale", StringComparison.Ordinal)).InlinedInto
            .Should().ContainSingle().Which.Should().Contain("P.Work").And.Contain("OptimizedTier1");
        versions.Single(v => v.Method.StartsWith("P.Work", StringComparison.Ordinal)).InlinedInto.Should().BeNull();
    }
}
