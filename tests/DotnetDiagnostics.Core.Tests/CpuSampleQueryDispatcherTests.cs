using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit coverage for the host-neutral <see cref="CpuSampleQueryDispatcher"/> shared by the MCP server's
/// <c>get_call_tree</c> / <c>query_snapshot(view="call-tree")</c> path and the CLI <c>session</c> REPL
/// (#300). Asserts the merged call-tree renders, prunes by depth/nodes, re-roots on a method filter, and
/// that argument validation matches the server preamble.
/// </summary>
public class CpuSampleQueryDispatcherTests
{
    private const string Handle = "cpu-abc";

    [Fact]
    public void RenderCallTree_ReturnsView_FromTrace()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, rootMethodFilter: null, maxDepth: 8, maxNodes: 200);

        outcome.Error.Should().BeNull();
        outcome.Data.Should().NotBeNull();
        outcome.Data!.ProcessId.Should().Be(123);
        outcome.Data.TotalSamples.Should().Be(100);
        outcome.Data.Root.Frame.Method.Should().Be("Root");
        outcome.Data.NodeCount.Should().Be(3);
        outcome.Data.Truncated.Should().BeFalse();
    }

    [Fact]
    public void RenderCallTree_MaxDepthBelowOne_ReturnsInvalidArgument()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, null, maxDepth: 0, maxNodes: 200);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void RenderCallTree_MaxNodesBelowOne_ReturnsInvalidArgument()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, null, maxDepth: 8, maxNodes: 0);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void RenderCallTree_DepthOne_TruncatesChildren()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, null, maxDepth: 1, maxNodes: 200);

        outcome.Error.Should().BeNull();
        outcome.Data!.Root.Children.Should().BeEmpty();
        outcome.Data.Truncated.Should().BeTrue();
    }

    [Fact]
    public void RenderCallTree_RootMethodFilter_ReRootsAtMatch()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, rootMethodFilter: "leafa", maxDepth: 8, maxNodes: 200);

        outcome.Error.Should().BeNull();
        outcome.Data!.Root.Frame.Method.Should().Be("LeafA");
    }

    [Fact]
    public void RenderCallTree_RootMethodFilter_NoMatch_ReturnsNotFound()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, rootMethodFilter: "does-not-exist", maxDepth: 8, maxNodes: 200);

        outcome.Error!.Kind.Should().Be("NotFound");
    }

    [Fact]
    public void ResolveTrace_UnwrapsBareTrace_AndAllocationWrapper()
    {
        var trace = Trace();
        CpuSampleQueryDispatcher.ResolveTrace(trace).Should().BeSameAs(trace);

        var alloc = new AllocationSampleArtifact(
            new AllocationSample(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), 0, 0, Array.Empty<AllocatedType>(), Array.Empty<AllocatedType>()),
            trace);
        CpuSampleQueryDispatcher.ResolveTrace(alloc).Should().BeSameAs(trace);

        CpuSampleQueryDispatcher.ResolveTrace(new object()).Should().BeNull();
        CpuSampleQueryDispatcher.ResolveTrace(null).Should().BeNull();
    }

    [Fact]
    public void SessionViews_ExposesCallTreeAndAnalyticsViews()
    {
        CpuSampleQueryDispatcher.SessionViews.Should().Contain(new[]
        {
            CpuSampleQueryDispatcher.CallTreeView,
            CpuSampleQueryDispatcher.TopMethodsView,
            CpuSampleQueryDispatcher.ByModuleView,
            CpuSampleQueryDispatcher.ByNamespaceView,
            CpuSampleQueryDispatcher.HotPathView,
            CpuSampleQueryDispatcher.CallerCalleeView,
        });
    }

    [Fact]
    public void RenderTopMethods_Recursion_CountsInclusiveOncePerStack()
    {
        var outcome = CpuSampleQueryDispatcher.RenderTopMethods(Recursive(), Handle, sortBy: "inclusive", topN: 10);

        outcome.Error.Should().BeNull();
        var a = outcome.Data!.Methods.Single(m => m.Method == "A");
        a.ExclusiveSamples.Should().Be(50);   // 10 (outer) + 40 (inner)
        a.InclusiveSamples.Should().Be(100);  // recursion counted once
        outcome.Data.Methods.Single(m => m.Method == "Leaf").InclusiveSamples.Should().Be(50);
    }

    [Fact]
    public void RenderTopMethods_DistinctPaths_SumInclusive()
    {
        var outcome = CpuSampleQueryDispatcher.RenderTopMethods(TwoPaths(), Handle, sortBy: "inclusive", topN: 10);

        var x = outcome.Data!.Methods.Single(m => m.Method == "X");
        x.InclusiveSamples.Should().Be(100);  // 40 + 60 across two distinct stacks
        x.ExclusiveSamples.Should().Be(100);
    }

    [Fact]
    public void RenderTopMethods_SortByExclusive_Default_RanksByExclusive()
    {
        var outcome = CpuSampleQueryDispatcher.RenderTopMethods(TwoPaths(), Handle, sortBy: null, topN: 1);

        outcome.Data!.SortedBy.Should().Be("exclusive");
        outcome.Data.Methods.Should().HaveCount(1);
        outcome.Data.Methods[0].Method.Should().Be("X"); // 100 exclusive
    }

    [Fact]
    public void RenderTopMethods_InvalidSort_ReturnsInvalidArgument()
        => CpuSampleQueryDispatcher.RenderTopMethods(TwoPaths(), Handle, sortBy: "bytes", topN: 10)
            .Error!.Kind.Should().Be("InvalidArgument");

    [Fact]
    public void RenderTopMethods_TopNBelowOne_ReturnsInvalidArgument()
        => CpuSampleQueryDispatcher.RenderTopMethods(TwoPaths(), Handle, sortBy: null, topN: 0)
            .Error!.Kind.Should().Be("InvalidArgument");

    [Fact]
    public void RenderByModule_AggregatesPerAssembly()
    {
        var outcome = CpuSampleQueryDispatcher.RenderByModule(TwoPaths(), Handle, topN: 10);

        var other = outcome.Data!.Groups.Single(g => g.Group == "Other.dll");
        other.ExclusiveSamples.Should().Be(100);
        other.InclusiveSamples.Should().Be(100);
        outcome.Data.Groups.Single(g => g.Group == "App.dll").ExclusiveSamples.Should().Be(0);
    }

    [Fact]
    public void RenderByNamespace_BucketsByNamespace()
    {
        var outcome = CpuSampleQueryDispatcher.RenderByNamespace(Recursive(), Handle, topN: 10);

        outcome.Error.Should().BeNull();
        outcome.Data!.GroupBy.Should().Be("namespace");
        outcome.Data.Groups.Sum(g => g.ExclusiveSamples).Should().Be(100);
    }

    [Fact]
    public void RenderHotPath_FollowsDominantChain()
    {
        var outcome = CpuSampleQueryDispatcher.RenderHotPath(Recursive(), Handle, thresholdPercent: 50);

        outcome.Error.Should().BeNull();
        outcome.Data!.Frames.Select(f => f.Method).Should().Equal("A", "A", "Leaf");
        outcome.Data.Frames[0].FractionOfParentPercent.Should().Be(100);
    }

    [Fact]
    public void RenderHotPath_HigherThreshold_StopsEarlier()
    {
        var outcome = CpuSampleQueryDispatcher.RenderHotPath(Recursive(), Handle, thresholdPercent: 60);

        outcome.Data!.Frames.Select(f => f.Method).Should().Equal("A", "A"); // Leaf is 55% of parent
    }

    [Fact]
    public void RenderHotPath_ThresholdOutOfRange_ReturnsInvalidArgument()
        => CpuSampleQueryDispatcher.RenderHotPath(Recursive(), Handle, thresholdPercent: 0)
            .Error!.Kind.Should().Be("InvalidArgument");

    [Fact]
    public void RenderCallerCallee_SingleMatch_ReturnsCallersAndCallees()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallerCallee(Recursive(), Handle, methodFilter: "Leaf", topN: 10);

        outcome.Error.Should().BeNull();
        outcome.Data!.Method.Should().Be("Leaf");
        outcome.Data.InclusiveSamples.Should().Be(50);
        outcome.Data.Callers.Should().ContainSingle(c => c.Method == "A");
    }

    [Fact]
    public void RenderCallerCallee_NoMatch_ReturnsNotFound()
        => CpuSampleQueryDispatcher.RenderCallerCallee(Recursive(), Handle, methodFilter: "zzz", topN: 10)
            .Error!.Kind.Should().Be("NotFound");

    [Fact]
    public void RenderCallerCallee_AmbiguousSubstring_ReturnsInvalidArgument()
    {
        // "Handler" matches two distinct methods → caller-callee needs exactly one focus.
        var outcome = CpuSampleQueryDispatcher.RenderCallerCallee(Ambiguous(), Handle, methodFilter: "Handler", topN: 10);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void RenderCallerCallee_TopLevelMethod_CreditsSyntheticRootAsCaller()
    {
        // FooHandler sits directly under <root>; its only caller is the synthetic root entry point.
        var outcome = CpuSampleQueryDispatcher.RenderCallerCallee(Ambiguous(), Handle, methodFilter: "FooHandler", topN: 10);

        outcome.Error.Should().BeNull();
        outcome.Data!.Callers.Should().ContainSingle(c => c.Method == "<root>");
    }

    [Fact]
    public void RenderCallerCallee_MissingFilter_ReturnsInvalidArgument()
        => CpuSampleQueryDispatcher.RenderCallerCallee(Recursive(), Handle, methodFilter: null, topN: 10)
            .Error!.Kind.Should().Be("InvalidArgument");

    // <root>(100) → A(excl10,incl100) → A(excl40,incl90) → Leaf(excl50,incl50)
    private static CpuSampleTraceArtifact Recursive()
    {
        var leaf = new CallTreeNode(new SampledFrame("App.dll", "Leaf"), 50, 50, Array.Empty<CallTreeNode>());
        var innerA = new CallTreeNode(new SampledFrame("App.dll", "A"), 90, 40, new[] { leaf });
        var outerA = new CallTreeNode(new SampledFrame("App.dll", "A"), 100, 10, new[] { innerA });
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 100, 0, new[] { outerA });
        return new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 100, root);
    }

    // <root>(100) → B(40)→X@Other(40) ; C(60)→X@Other(60). X reached via two distinct paths.
    private static CpuSampleTraceArtifact TwoPaths()
    {
        var xUnderB = new CallTreeNode(new SampledFrame("Other.dll", "X"), 40, 40, Array.Empty<CallTreeNode>());
        var xUnderC = new CallTreeNode(new SampledFrame("Other.dll", "X"), 60, 60, Array.Empty<CallTreeNode>());
        var b = new CallTreeNode(new SampledFrame("App.dll", "B"), 40, 0, new[] { xUnderB });
        var c = new CallTreeNode(new SampledFrame("App.dll", "C"), 60, 0, new[] { xUnderC });
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 100, 0, new[] { b, c });
        return new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 100, root);
    }

    // <root>(100) → FooHandler(60) + BarHandler(40). Both contain "Handler".
    private static CpuSampleTraceArtifact Ambiguous()
    {
        var foo = new CallTreeNode(new SampledFrame("App.dll", "FooHandler"), 60, 60, Array.Empty<CallTreeNode>());
        var bar = new CallTreeNode(new SampledFrame("App.dll", "BarHandler"), 40, 40, Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 100, 0, new[] { foo, bar });
        return new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 100, root);
    }

    private static CpuSampleTraceArtifact Trace()
    {
        var leafA = new CallTreeNode(new SampledFrame("App.dll", "LeafA"), 40, 40, Array.Empty<CallTreeNode>());
        var leafB = new CallTreeNode(new SampledFrame("App.dll", "LeafB"), 60, 60, Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(new SampledFrame("App.dll", "Root"), 100, 0, new[] { leafA, leafB });
        return new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 100, root);
    }
}
