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
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, rootMethodFilter: null, maxDepth: 8, maxNodes: CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes);

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
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, null, maxDepth: 0, maxNodes: CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes);

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
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, null, maxDepth: 1, maxNodes: CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes);

        outcome.Error.Should().BeNull();
        outcome.Data!.Root.Children.Should().BeEmpty();
        outcome.Data.Truncated.Should().BeTrue();
    }

    [Fact]
    public void RenderCallTree_RootMethodFilter_ReRootsAtMatch()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, rootMethodFilter: "leafa", maxDepth: 8, maxNodes: CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes);

        outcome.Error.Should().BeNull();
        outcome.Data!.Root.Frame.Method.Should().Be("LeafA");
    }

    [Fact]
    public void RenderCallTree_RootMethodFilter_NoMatch_HintUsesEffectiveCaps()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(
            Trace(),
            Handle,
            rootMethodFilter: "does-not-exist",
            maxDepth: 20,
            maxNodes: 500);

        outcome.Error!.Kind.Should().Be("NotFound");
        outcome.Hints.Should().ContainSingle();
        outcome.Hints[0].SuggestedArguments!["maxDepth"].Should().Be(CpuSampleQueryDispatcher.MaxProjectedCallTreeDepth);
        outcome.Hints[0].SuggestedArguments!["maxNodes"].Should().Be(CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes);
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
            CpuSampleQueryDispatcher.TriageView,
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
    public void RenderTopMethods_EmitsRunningVsWaitingSelfSplit()
    {
        var outcome = CpuSampleQueryDispatcher.RenderTopMethods(ClassifiedTrace(), Handle, sortBy: "exclusive", topN: 2);

        outcome.Error.Should().BeNull();
        outcome.Data!.SelfSamples.Should().Be(new SelfSampleBreakdown(40, 60));
        outcome.Data.Methods[0].Method.Should().Be("System.Threading.LowLevelLifoSemaphore.WaitForSignal");
        outcome.Data.Methods[0].SelfSamples.Should().Be(new SelfSampleBreakdown(0, 60));
        outcome.Data.Methods[0].WaitReason.Should().Be("ThreadPool worker idle wait");
        outcome.Data.Methods[1].Method.Should().Be("MyApp.Worker.BurnCpu");
        outcome.Data.Methods[1].SelfSamples.Should().Be(new SelfSampleBreakdown(40, 0));
        outcome.Data.Methods[1].WaitReason.Should().BeNull();
    }

    [Fact]
    public void RenderTopMethods_SortByRunning_PromotesBusyUserCodeOverWaitFrame()
    {
        // WaitForSignal has 60 exclusive samples (all waiting) vs BurnCpu's 40 (all running).
        // rankBy="exclusive" ranks the wait frame first; rankBy="running" must flip the order (#811).
        var outcome = CpuSampleQueryDispatcher.RenderTopMethods(ClassifiedTrace(), Handle, sortBy: "running", topN: 2);

        outcome.Error.Should().BeNull();
        outcome.Data!.SortedBy.Should().Be("running");
        outcome.Data.Methods[0].Method.Should().Be("MyApp.Worker.BurnCpu");
        outcome.Data.Methods[0].SelfSamples.Should().Be(new SelfSampleBreakdown(40, 0));
        outcome.Data.Methods[1].Method.Should().Be("System.Threading.LowLevelLifoSemaphore.WaitForSignal");
        outcome.Data.Methods[1].SelfSamples.Should().Be(new SelfSampleBreakdown(0, 60));
    }

    [Fact]
    public void RenderTopMethods_SortByRunning_NoClassification_FallsBackToExclusiveOrder()
    {
        // TwoPaths() has no classified leaves at all, so "running" must degrade to the same order
        // as "exclusive" instead of reshuffling unclassified rows.
        var outcome = CpuSampleQueryDispatcher.RenderTopMethods(TwoPaths(), Handle, sortBy: "running", topN: 1);

        outcome.Error.Should().BeNull();
        outcome.Data!.Methods[0].Method.Should().Be("X"); // 100 exclusive, same leader as sortBy="exclusive"
        outcome.Data.Methods[0].WaitReason.Should().BeNull(); // no classification available for this trace at all
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
    public void RenderTopMethods_FoldAsyncTrue_RenamesMoveNextLeafToDeclaringMethod()
    {
        // foldAsync=true (issue #811 part 3) rewrites the compiler-generated MoveNext leaf to its
        // declaring async method name and flags the row as folded.
        var outcome = CpuSampleQueryDispatcher.RenderTopMethods(AsyncMoveNextTrace(), Handle, sortBy: "exclusive", topN: 2, foldAsync: true);

        outcome.Error.Should().BeNull();
        outcome.Data!.Methods[0].Method.Should().Be("B3.Umdf.FixConflated.FixTcpClientSession.WriteLoopAsync() [async]");
        outcome.Data.Methods[0].AsyncFolded.Should().BeTrue();
        outcome.Data.Methods[1].Method.Should().Be("MyApp.Worker.BurnCpu");
        outcome.Data.Methods[1].AsyncFolded.Should().BeFalse();
    }

    [Fact]
    public void RenderTopMethods_FoldAsyncFalse_LeavesMoveNextLeafUnchanged()
    {
        // Default behavior (foldAsync omitted/false) must be byte-for-byte unchanged from before #811 part 3.
        var outcome = CpuSampleQueryDispatcher.RenderTopMethods(AsyncMoveNextTrace(), Handle, sortBy: "exclusive", topN: 1);

        outcome.Error.Should().BeNull();
        outcome.Data!.Methods[0].Method.Should().Be("B3.Umdf.FixConflated.FixTcpClientSession+<WriteLoopAsync>d__22.MoveNext()");
        outcome.Data.Methods[0].AsyncFolded.Should().BeFalse();
    }

    [Fact]
    public void RenderTopMethods_FoldAsyncTrue_NonAsyncFrameIsUnaffected()
    {
        // A trace with no MoveNext-shaped leaf must degrade gracefully: foldAsync=true is a no-op.
        var outcome = CpuSampleQueryDispatcher.RenderTopMethods(TwoPaths(), Handle, sortBy: "exclusive", topN: 1, foldAsync: true);

        outcome.Error.Should().BeNull();
        outcome.Data!.Methods[0].Method.Should().Be("X");
        outcome.Data.Methods[0].AsyncFolded.Should().BeFalse();
    }

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
    public void RenderHotPath_EmitsLeafRunningVsWaitingSelfSplit()
    {
        var outcome = CpuSampleQueryDispatcher.RenderHotPath(ClassifiedTrace(), Handle, thresholdPercent: 50);

        outcome.Error.Should().BeNull();
        outcome.Data!.SelfSamples.Should().Be(new SelfSampleBreakdown(40, 60));
        outcome.Data.Frames.Should().ContainSingle();
        outcome.Data.Frames[0].Method.Should().Be("System.Threading.LowLevelLifoSemaphore.WaitForSignal");
        outcome.Data.Frames[0].SelfSamples.Should().Be(new SelfSampleBreakdown(0, 60));
    }

    [Fact]
    public void RenderHotPath_ThresholdOutOfRange_ReturnsInvalidArgument()
        => CpuSampleQueryDispatcher.RenderHotPath(Recursive(), Handle, thresholdPercent: 0)
            .Error!.Kind.Should().Be("InvalidArgument");

    [Fact]
    public void RenderTriage_BundlesBusyMethodsWaitCategoriesAndHotPathLeaf()
    {
        var outcome = CpuSampleQueryDispatcher.RenderTriage(ClassifiedTrace(), Handle, topN: 5, hotPathThresholdPercent: 50);

        outcome.Error.Should().BeNull();
        outcome.Data!.ProcessId.Should().Be(123);
        outcome.Data.TotalSamples.Should().Be(100);
        outcome.Data.SelfSamples.Should().Be(new SelfSampleBreakdown(40, 60));

        // rankBy="running" order (#811 pt.1): BurnCpu (all running) ranks above WaitForSignal (all waiting).
        outcome.Data.TopBusyMethods[0].Method.Should().Be("MyApp.Worker.BurnCpu");

        outcome.Data.TopWaitCategories.Should().ContainSingle();
        outcome.Data.TopWaitCategories[0].WaitReason.Should().Be("ThreadPool worker idle wait");
        outcome.Data.TopWaitCategories[0].ExclusiveSamples.Should().Be(60);
        outcome.Data.TopWaitCategories[0].MethodCount.Should().Be(1);

        outcome.Data.HotPathLeaf.Should().NotBeNull();
        outcome.Data.HotPathLeaf!.Method.Should().Be("System.Threading.LowLevelLifoSemaphore.WaitForSignal");
    }

    [Theory]
    [InlineData(90, 10, "cpu-bound")]   // 10% waiting < 20% threshold
    [InlineData(50, 50, "wait-bound")]  // 50% waiting >= 50% threshold
    [InlineData(70, 30, "mixed")]       // between 20% and 50%
    public void RenderTriage_ClassifiesVerdictFromRunningWaitingSplit(long running, long waiting, string expectedVerdict)
    {
        var leaf = new CallTreeNode(new SampledFrame("App.dll", "Leaf"), running + waiting, running + waiting, Array.Empty<CallTreeNode>())
        {
            SelfSamples = new SelfSampleBreakdown(running, waiting),
        };
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), running + waiting, 0, new[] { leaf });
        var artifact = new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), running + waiting, root)
        {
            SelfSamples = new SelfSampleBreakdown(running, waiting),
        };

        var outcome = CpuSampleQueryDispatcher.RenderTriage(artifact, Handle, topN: 5, hotPathThresholdPercent: 50);

        outcome.Error.Should().BeNull();
        outcome.Data!.Verdict.Should().Be(expectedVerdict);
    }

    [Fact]
    public void RenderTriage_NoSelfSampleClassification_VerdictIsUnclassified()
    {
        // TwoPaths() has no SelfSamples split on any node, so triage cannot classify a verdict.
        var outcome = CpuSampleQueryDispatcher.RenderTriage(TwoPaths(), Handle, topN: 5, hotPathThresholdPercent: 50);

        outcome.Error.Should().BeNull();
        outcome.Data!.Verdict.Should().Be("unclassified");
        outcome.Data.TopWaitCategories.Should().BeEmpty();
    }

    [Fact]
    public void RenderTriage_SummaryMentionsVerdictBusyMethodAndWaitCategory()
    {
        var outcome = CpuSampleQueryDispatcher.RenderTriage(ClassifiedTrace(), Handle, topN: 5, hotPathThresholdPercent: 50);

        outcome.Summary.Should().Contain("wait-bound").And.Contain("BurnCpu").And.Contain("ThreadPool worker idle wait");
    }

    [Fact]
    public void RenderTriage_TopNBelowOne_ReturnsInvalidArgument()
        => CpuSampleQueryDispatcher.RenderTriage(ClassifiedTrace(), Handle, topN: 0, hotPathThresholdPercent: 50)
            .Error!.Kind.Should().Be("InvalidArgument");

    [Fact]
    public void RenderTriage_ThresholdOutOfRange_ReturnsInvalidArgument()
        => CpuSampleQueryDispatcher.RenderTriage(ClassifiedTrace(), Handle, topN: 5, hotPathThresholdPercent: 0)
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
    public void RenderCallTree_PropagatesSelfSampleSplit()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(ClassifiedTrace(), Handle, rootMethodFilter: null, maxDepth: 8, maxNodes: CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes);

        outcome.Error.Should().BeNull();
        outcome.Data!.SelfSamples.Should().Be(new SelfSampleBreakdown(40, 60));
        outcome.Data.Root.Children[0].Frame.Method.Should().Be("MyApp.Worker.BurnCpu");
        outcome.Data.Root.Children[0].SelfSamples.Should().Be(new SelfSampleBreakdown(40, 0));
        outcome.Data.Root.Children[1].Frame.Method.Should().Be("System.Threading.LowLevelLifoSemaphore.WaitForSignal");
        outcome.Data.Root.Children[1].SelfSamples.Should().Be(new SelfSampleBreakdown(0, 60));
    }

    [Fact]
    public void RenderCallTree_UntruncatedResponse_DoesNotRecommendAlreadySatisfiedCallTree()
    {
        var outcome = CpuSampleQueryDispatcher.RenderCallTree(Trace(), Handle, rootMethodFilter: null, maxDepth: 8, maxNodes: CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes);

        outcome.Data!.Truncated.Should().BeFalse();
        outcome.Hints.Should().BeEmpty();
    }

    [Fact]
    public void RenderCallTree_WithoutClassification_RanksInclusiveBeforeExclusive()
    {
        var coldLeaf = new CallTreeNode(
            new SampledFrame("App.dll", "ColdLeaf"),
            InclusiveSamples: 1,
            ExclusiveSamples: 1,
            Array.Empty<CallTreeNode>());
        var hotDeepLeaf = new CallTreeNode(
            new SampledFrame("App.dll", "HotDeepLeaf"),
            InclusiveSamples: 1000,
            ExclusiveSamples: 1000,
            Array.Empty<CallTreeNode>());
        var hotBranch = new CallTreeNode(
            new SampledFrame("App.dll", "HotBranch"),
            InclusiveSamples: 1000,
            ExclusiveSamples: 0,
            new[] { hotDeepLeaf });
        var root = new CallTreeNode(
            new SampledFrame("App.dll", "Root"),
            InclusiveSamples: 1001,
            ExclusiveSamples: 0,
            new[] { coldLeaf, hotBranch });
        var trace = new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), 1001, root);

        var outcome = CpuSampleQueryDispatcher.RenderCallTree(trace, Handle, null, maxDepth: 8, maxNodes: 2);

        outcome.Data!.Root.Children.Should().ContainSingle();
        outcome.Data.Root.Children[0].Frame.Method.Should().Be("HotBranch");
    }

    [Fact]
    public void RenderCallTree_LargeWideDeepTree_BoundsMetricTraversal()
    {
        const int branchCount = 200;
        const int branchDepth = 600;
        var branches = new CallTreeNode[branchCount];
        for (var branchIndex = 0; branchIndex < branchCount; branchIndex++)
        {
            CallTreeNode node = new(
                new SampledFrame("App.dll", $"Leaf{branchIndex}"),
                InclusiveSamples: 1,
                ExclusiveSamples: 1,
                Array.Empty<CallTreeNode>());
            for (var depth = 1; depth < branchDepth; depth++)
            {
                node = new CallTreeNode(
                    new SampledFrame("App.dll", $"Branch{branchIndex}.Depth{depth}"),
                    InclusiveSamples: 1,
                    ExclusiveSamples: 0,
                    new[] { node });
            }
            branches[branchIndex] = node;
        }

        var root = new CallTreeNode(
            new SampledFrame("App.dll", "Root"),
            InclusiveSamples: branchCount,
            ExclusiveSamples: 0,
            branches);
        var trace = new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), branchCount, root);

        var outcome = CpuSampleQueryDispatcher.RenderCallTree(
            trace,
            Handle,
            rootMethodFilter: null,
            maxDepth: 8,
            maxNodes: CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes);

        outcome.Data!.TraversalLimitReached.Should().BeTrue();
        outcome.Data.TraversalNodesVisited.Should().Be(outcome.Data.TraversalNodeLimit);
        outcome.Data.NodeCount.Should().BeLessThanOrEqualTo(CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes);
    }

    [Fact]
    public void RenderCallTree_IncompleteClassifiedTraversal_UsesInclusiveFallbackForLateBranch()
    {
        CallTreeNode deep = new(
            new SampledFrame("App.dll", "EarlyLeaf"),
            InclusiveSamples: 1,
            ExclusiveSamples: 1,
            Array.Empty<CallTreeNode>())
        {
            SelfSamples = new SelfSampleBreakdown(0, 1),
        };
        for (var depth = 0; depth < 100_005; depth++)
        {
            deep = new CallTreeNode(
                new SampledFrame("App.dll", $"Early.Depth{depth}"),
                InclusiveSamples: 1,
                ExclusiveSamples: depth == 100_004 ? 1 : 0,
                new[] { deep })
            {
                SelfSamples = new SelfSampleBreakdown(0, depth == 100_004 ? 1 : 0),
            };
        }

        var lateRunning = new CallTreeNode(
            new SampledFrame("App.dll", "LateRunningBranch"),
            InclusiveSamples: 1_000,
            ExclusiveSamples: 0,
            Array.Empty<CallTreeNode>())
        {
            SelfSamples = new SelfSampleBreakdown(1_000, 0),
        };
        var root = new CallTreeNode(
            new SampledFrame("App.dll", "Root"),
            InclusiveSamples: 1_001,
            ExclusiveSamples: 0,
            new[] { deep, lateRunning })
        {
            SelfSamples = new SelfSampleBreakdown(0, 0),
        };
        var trace = new CpuSampleTraceArtifact(
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            1_001,
            root);

        var outcome = CpuSampleQueryDispatcher.RenderCallTree(
            trace,
            Handle,
            rootMethodFilter: null,
            maxDepth: 2,
            maxNodes: 2);

        outcome.Data!.TraversalLimitReached.Should().BeTrue();
        outcome.Data.Root.Children.Should().ContainSingle()
            .Which.Frame.Method.Should().Be("LateRunningBranch");
    }

    [Fact]
    public void RenderCallTree_MixedMetricCompleteness_UsesOneTransitiveSiblingOrder()
    {
        var running = new CallTreeNode(
            new SampledFrame("App.dll", "Running"),
            InclusiveSamples: 1,
            ExclusiveSamples: 1,
            Array.Empty<CallTreeNode>())
        {
            SelfSamples = new SelfSampleBreakdown(1, 0),
        };
        var inclusive = new CallTreeNode(
            new SampledFrame("App.dll", "Inclusive"),
            InclusiveSamples: 100,
            ExclusiveSamples: 0,
            Array.Empty<CallTreeNode>())
        {
            SelfSamples = new SelfSampleBreakdown(0, 100),
        };
        CallTreeNode incomplete = new(
            new SampledFrame("App.dll", "IncompleteLeaf"),
            InclusiveSamples: 50,
            ExclusiveSamples: 0,
            Array.Empty<CallTreeNode>())
        {
            SelfSamples = new SelfSampleBreakdown(0, 0),
        };
        for (var depth = 0; depth < 100_005; depth++)
        {
            incomplete = new CallTreeNode(
                new SampledFrame("App.dll", depth == 100_004 ? "Incomplete" : $"Incomplete.Depth{depth}"),
                InclusiveSamples: 50,
                ExclusiveSamples: 0,
                new[] { incomplete })
            {
                SelfSamples = new SelfSampleBreakdown(0, 0),
            };
        }

        var root = new CallTreeNode(
            new SampledFrame("App.dll", "Root"),
            InclusiveSamples: 151,
            ExclusiveSamples: 0,
            new[] { running, inclusive, incomplete })
        {
            SelfSamples = new SelfSampleBreakdown(0, 0),
        };
        var trace = new CpuSampleTraceArtifact(
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            151,
            root);

        var outcome = CpuSampleQueryDispatcher.RenderCallTree(
            trace,
            Handle,
            rootMethodFilter: null,
            maxDepth: 2,
            maxNodes: 3);

        outcome.Data!.Root.Children.Select(child => child.Frame.Method)
            .Should().Equal("Inclusive", "Incomplete");
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

    private static CpuSampleTraceArtifact AsyncMoveNextTrace()
    {
        // Mirrors the exact shape from issue #811's motivating example: a compiler-generated async
        // state-machine's own MoveNext is the on-CPU leaf (busy work between awaits), and TraceEvent's
        // FullMethodName renders it as "Owner+<Method>d__NN.MoveNext()".
        var asyncLeaf = new CallTreeNode(
            new SampledFrame("MyApp.dll", "B3.Umdf.FixConflated.FixTcpClientSession+<WriteLoopAsync>d__22.MoveNext()"),
            60,
            60,
            Array.Empty<CallTreeNode>());
        var running = new CallTreeNode(
            new SampledFrame("MyApp.dll", "MyApp.Worker.BurnCpu"),
            40,
            40,
            Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 100, 0, new[] { asyncLeaf, running });
        return new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 100, root);
    }

    private static CpuSampleTraceArtifact ClassifiedTrace()
    {
        var waiting = new CallTreeNode(
            new SampledFrame("System.Private.CoreLib.dll", "System.Threading.LowLevelLifoSemaphore.WaitForSignal"),
            60,
            60,
            Array.Empty<CallTreeNode>())
        {
            SelfSamples = new SelfSampleBreakdown(0, 60),
        };
        var running = new CallTreeNode(
            new SampledFrame("MyApp.dll", "MyApp.Worker.BurnCpu"),
            40,
            40,
            Array.Empty<CallTreeNode>())
        {
            SelfSamples = new SelfSampleBreakdown(40, 0),
        };
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 100, 0, new[] { waiting, running });
        return new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 100, root)
        {
            SelfSamples = new SelfSampleBreakdown(40, 60),
        };
    }
}
