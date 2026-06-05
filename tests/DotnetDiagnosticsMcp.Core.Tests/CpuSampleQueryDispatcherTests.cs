using DotnetDiagnosticsMcp.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

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
    public void SessionViews_IsCallTreeOnly()
    {
        CpuSampleQueryDispatcher.SessionViews.Should().Equal(CpuSampleQueryDispatcher.CallTreeView);
    }

    private static CpuSampleTraceArtifact Trace()
    {
        var leafA = new CallTreeNode(new SampledFrame("App.dll", "LeafA"), 40, 40, Array.Empty<CallTreeNode>());
        var leafB = new CallTreeNode(new SampledFrame("App.dll", "LeafB"), 60, 60, Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(new SampledFrame("App.dll", "Root"), 100, 0, new[] { leafA, leafB });
        return new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 100, root);
    }
}
