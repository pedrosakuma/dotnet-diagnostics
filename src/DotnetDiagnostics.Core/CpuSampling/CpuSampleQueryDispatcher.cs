using DotnetDiagnostics.Core;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// Host-neutral drill-down engine for <see cref="CpuSampleTraceArtifact"/> handles — the CPU-sampling
/// analogue of <see cref="DotnetDiagnostics.Core.Dump.HeapSnapshotQueryDispatcher"/>. It renders the
/// merged caller→callee <c>call-tree</c> view (pruned by <c>maxDepth</c>/<c>maxNodes</c>, optionally
/// re-rooted at a method substring) directly from the already-collected trace, so both the MCP server's
/// <c>query_snapshot(view="call-tree")</c> path and the standalone CLI
/// <c>session</c> REPL (issue #300) share one implementation.
/// </summary>
/// <remarks>
/// The <c>diff</c> view is deliberately not handled here: it correlates a second (baseline) handle the
/// session cannot supply, so it stays server-owned. Handles of kind <c>cpu-sample</c>,
/// <c>allocation-sample</c> and <c>native-alloc-sample</c> all back a <see cref="CpuSampleTraceArtifact"/>
/// (allocation-sample wraps it in an <see cref="AllocationSampleArtifact"/>); use
/// <see cref="ResolveTrace"/> to unwrap the stored artifact regardless of which kind issued it.
/// </remarks>
public static class CpuSampleQueryDispatcher
{
    /// <summary>The merged caller→callee call tree (the original drill-down projection).</summary>
    public const string CallTreeView = "call-tree";

    /// <summary>Methods ranked by exclusive (default) or inclusive samples.</summary>
    public const string TopMethodsView = "top-methods";

    /// <summary>Samples aggregated by module (assembly).</summary>
    public const string ByModuleView = "by-module";

    /// <summary>Samples aggregated by namespace.</summary>
    public const string ByNamespaceView = "by-namespace";

    /// <summary>The dominant call chain (heaviest child until it drops below a threshold).</summary>
    public const string HotPathView = "hot-path";

    /// <summary>Callers and callees of a single focus method (PerfView-style).</summary>
    public const string CallerCalleeView = "caller-callee";

    /// <summary>
    /// One-shot performance-triage projection: top busy hotspots + top wait/noise categories +
    /// dominant hot-path leaf, one round trip instead of chaining <see cref="TopMethodsView"/> and
    /// <see cref="HotPathView"/> by hand (issue #812).
    /// </summary>
    public const string TriageView = "triage";

    /// <summary>Default number of rows returned by the ranked CPU views.</summary>
    public const int DefaultTopN = 20;

    /// <summary>Hard cap for the inline call-tree projection. The handle retains the complete tree.</summary>
    public const int MaxProjectedCallTreeNodes = 64;

    /// <summary>Hard cap for the inline call-tree depth. Narrow with rootMethodFilter for deeper evidence.</summary>
    public const int MaxProjectedCallTreeDepth = 8;

    /// <summary>
    /// <c>depth="compact"</c> row cap for <see cref="TopMethodsView"/> (issue #805) — a deliberately
    /// small, stable "first page" projection distinct from <see cref="DefaultTopN"/>/<see cref="MaxProjectedCallTreeNodes"/>-level requests.
    /// </summary>
    public const int CompactTopN = 5;

    /// <summary><c>depth="compact"</c> node cap for <see cref="CallTreeView"/> (issue #805).</summary>
    public const int CompactMaxNodes = 16;

    /// <summary><c>depth="compact"</c> tree-depth cap for <see cref="CallTreeView"/> (issue #805).</summary>
    public const int CompactMaxDepth = 3;

    /// <summary>Hard cap for source-tree metric traversal used by bounded call-tree ranking.</summary>
    internal const int MaxCallTreeTraversalNodes = 100_000;

    /// <summary>Default hot-path threshold: a child must carry at least this % of its parent to extend the chain.</summary>
    public const double DefaultHotPathThresholdPercent = 50d;

    private static readonly string[] Views =
    {
        CallTreeView, TopMethodsView, ByModuleView, ByNamespaceView, HotPathView, CallerCalleeView, TriageView,
    };

    /// <summary>The view names this dispatcher can render from a trace alone (drill-down without re-sampling).</summary>
    public static IReadOnlyList<string> SessionViews => Views;

    /// <summary><c>true</c> when <paramref name="view"/> is one of the analytics views this dispatcher renders.</summary>
    public static bool IsKnownView(string? view)
        => view is not null && Array.Exists(Views, v => string.Equals(v, view, StringComparison.Ordinal));

    /// <summary>
    /// Unwraps the <see cref="CpuSampleTraceArtifact"/> from a stored drill-down artifact: a bare trace
    /// (<c>cpu-sample</c> / <c>native-alloc-sample</c>) or the <see cref="AllocationSampleArtifact"/>
    /// wrapper (<c>allocation-sample</c>). Returns <c>null</c> when <paramref name="artifact"/> is neither.
    /// </summary>
    public static CpuSampleTraceArtifact? ResolveTrace(object? artifact) => artifact switch
    {
        CpuSampleTraceArtifact trace => trace,
        AllocationSampleArtifact alloc => alloc.TraceArtifact,
        _ => null,
    };

    /// <summary>
    /// Renders the pruned call tree from <paramref name="artifact"/>. Mirrors the server's
    /// call-tree body verbatim: stamps per-frame <c>MethodIdentity</c>, optionally
    /// re-roots at the highest-ranked frame matching <paramref name="rootMethodFilter"/>, then prunes to
    /// <paramref name="maxDepth"/> / <paramref name="maxNodes"/>.
    /// </summary>
    public static DiagnosticResult<CallTreeView> RenderCallTree(
        CpuSampleTraceArtifact artifact, string handle, string? rootMethodFilter, int maxDepth, int maxNodes)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (maxDepth < 1) return InvalidArg<CallTreeView>(nameof(maxDepth), "must be >= 1");
        if (maxNodes < 1) return InvalidArg<CallTreeView>(nameof(maxNodes), "must be >= 1");

        var effectiveDepth = Math.Min(maxDepth, MaxProjectedCallTreeDepth);
        var effectiveNodes = Math.Min(maxNodes, MaxProjectedCallTreeNodes);
        var root = artifact.Root;
        if (!string.IsNullOrWhiteSpace(rootMethodFilter))
        {
            var match = FindHighestRankedDescendant(root, rootMethodFilter);
            if (match is null)
            {
                return DiagnosticResult.Fail<CallTreeView>(
                    $"No frame matching '{rootMethodFilter}' in handle '{handle}'.",
                    new DiagnosticError("NotFound", "No frame in the merged call tree contains the supplied substring.", rootMethodFilter),
                    new NextActionHint("query_snapshot", "Re-issue without rootMethodFilter to inspect the full tree first.",
                        new Dictionary<string, object?> { ["handle"] = handle, ["maxDepth"] = effectiveDepth, ["maxNodes"] = effectiveNodes }));
            }
            root = match;
        }

        var (pruned, nodeCount, truncated, traversal) = PruneTree(root, effectiveDepth, effectiveNodes);
        var stamped = CallTreeIdentityProjector.Stamp(pruned, artifact.MethodIdentities);
        var view = new CallTreeView(artifact.ProcessId, artifact.TotalSamples, nodeCount, truncated, stamped)
        {
            SelfSamples = artifact.SelfSamples ?? traversal.TotalSelfSamples,
            NodeLimit = effectiveNodes,
            DepthLimit = effectiveDepth,
            TraversalNodesVisited = traversal.NodesVisited,
            TraversalNodeLimit = MaxCallTreeTraversalNodes,
            TraversalLimitReached = traversal.LimitReached,
        };
        var summary = truncated
            ? $"Showing a bounded {nodeCount}-node call-tree projection (limit {effectiveNodes} nodes / depth {effectiveDepth}); narrow with rootMethodFilter or use top-methods for decisive self-time. Root: {root.Frame.Method} — {root.InclusiveSamples} inclusive samples. The handle retains the full tree."
            : $"Showing the full sub-tree rooted at {root.Frame.Method} ({nodeCount} nodes, {root.InclusiveSamples} inclusive samples).";
        if (view.SelfSamples is { } self)
        {
            summary += $" Self split: {self.RunningSamples} running / {self.WaitingSamples} waiting.";
        }
        if (traversal.LimitReached)
        {
            summary += $" Decision-first ranking visited the bounded maximum of {MaxCallTreeTraversalNodes:N0} source nodes.";
        }

        return !truncated
            ? DiagnosticResult.Ok(view, summary)
            : DiagnosticResult.Ok(
                view,
                summary,
                new NextActionHint("query_snapshot", "Rank methods by exclusive self-time instead of requesting another broad tree.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = TopMethodsView, ["rankBy"] = "exclusive" }));
    }

    /// <summary>Renders the <c>top-methods</c> view: per-method exclusive/inclusive aggregation, ranked and capped.</summary>
    public static DiagnosticResult<TopMethodsView> RenderTopMethods(
        CpuSampleTraceArtifact artifact, string handle, string? sortBy, int topN, bool foldAsync = false)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (topN < 1) return InvalidArg<TopMethodsView>(nameof(topN), "must be >= 1");

        var normalizedSort = string.IsNullOrWhiteSpace(sortBy) ? "exclusive" : sortBy.Trim().ToLowerInvariant();
        if (normalizedSort is not ("exclusive" or "inclusive" or "running"))
        {
            return InvalidArg<TopMethodsView>(nameof(sortBy), "must be 'exclusive', 'inclusive', or 'running'");
        }

        var root = CallTreeIdentityProjector.Stamp(artifact.Root, artifact.MethodIdentities);
        // "running" (issue #811) ranks "busy user code" ahead of wait-dominated exclusive leaders: it
        // re-orders the exclusive-ranked list by on-CPU (running) self-time instead of raw exclusive
        // samples, so a hot wait frame (e.g. LowLevelLifoSemaphore.WaitForSignal) no longer buries the
        // actual busy hotspot underneath it.
        // "foldAsync" (issue #811 part 3) renames compiler-generated async state-machine MoveNext
        // leaves back to their declaring async method name, so `Owner+<Method>d__22.MoveNext()`
        // reads as `Owner.Method() [async]` instead of unfamiliar runtime-plumbing-looking text.
        var ranked = CpuSampleAnalytics.RankMethods(root, artifact.TotalSamples, byInclusive: normalizedSort == "inclusive", foldAsync: foldAsync);
        if (normalizedSort == "running")
        {
            ranked = CpuSampleAnalytics.RankMethodsByRunningSelf(ranked);
        }

        var top = ranked.Take(topN).ToList();
        var view = new TopMethodsView(artifact.ProcessId, artifact.TotalSamples, normalizedSort, top.Count, top)
        {
            SelfSamples = artifact.SelfSamples ?? CpuSampleAnalytics.TotalSelfSamples(root),
        };

        var summary = top.Count == 0
            ? "No methods aggregated — the trace captured no attributable frames."
            : normalizedSort == "running"
                ? $"Top {top.Count} method(s) by running (busy) self-time (of {ranked.Count} total). Busiest: {top[0].Method} ({top[0].SelfSamples?.RunningSamples ?? top[0].ExclusiveSamples} running / {top[0].ExclusiveSamples} exclusive){FormatSelfSamples(top[0].SelfSamples)}{FormatWaitReason(top[0].WaitReason)}."
                : $"Top {top.Count} method(s) by {normalizedSort} samples (of {ranked.Count} total). Hottest: {top[0].Method} ({top[0].ExclusiveSamples} exclusive / {top[0].InclusiveSamples} inclusive){FormatSelfSamples(top[0].SelfSamples)}{FormatWaitReason(top[0].WaitReason)}.";

        return top.Count == 0
            ? DiagnosticResult.Ok(view, summary)
            : DiagnosticResult.Ok(view, summary,
                new NextActionHint("query_snapshot", "Drill into the hottest method's callers/callees.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CallerCalleeView, ["rootMethodFilter"] = top[0].Method }));
    }

    /// <summary>Renders the <c>by-module</c> view: samples aggregated per assembly.</summary>
    public static DiagnosticResult<GroupedSamplesView> RenderByModule(CpuSampleTraceArtifact artifact, string handle, int topN)
        => RenderGrouped(artifact, handle, "module", CpuSampleAnalytics.ModuleOf, topN);

    /// <summary>Renders the <c>by-namespace</c> view: samples aggregated per namespace.</summary>
    public static DiagnosticResult<GroupedSamplesView> RenderByNamespace(CpuSampleTraceArtifact artifact, string handle, int topN)
        => RenderGrouped(artifact, handle, "namespace", CpuSampleAnalytics.NamespaceOf, topN);

    private static DiagnosticResult<GroupedSamplesView> RenderGrouped(
        CpuSampleTraceArtifact artifact, string handle, string groupBy, Func<CallTreeNode, string> keySelector, int topN)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (topN < 1) return InvalidArg<GroupedSamplesView>(nameof(topN), "must be >= 1");

        var root = CallTreeIdentityProjector.Stamp(artifact.Root, artifact.MethodIdentities);
        var ranked = CpuSampleAnalytics.RankGroups(root, artifact.TotalSamples, keySelector);
        var top = ranked.Take(topN).ToList();
        var view = new GroupedSamplesView(artifact.ProcessId, artifact.TotalSamples, groupBy, top.Count, top)
        {
            SelfSamples = artifact.SelfSamples ?? CpuSampleAnalytics.TotalSelfSamples(root),
        };

        var summary = top.Count == 0
            ? $"No {groupBy} groups aggregated."
            : $"Top {top.Count} {groupBy}(s) by exclusive samples (of {ranked.Count}). Hottest: {top[0].Group} ({top[0].ExclusiveSamples} exclusive / {top[0].InclusiveSamples} inclusive){FormatSelfSamples(top[0].SelfSamples)}.";

        return DiagnosticResult.Ok(view, summary,
            new NextActionHint("query_snapshot", "Rank individual methods.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = TopMethodsView }));
    }

    /// <summary>Renders the <c>hot-path</c> view: the dominant call chain from the root.</summary>
    public static DiagnosticResult<HotPathView> RenderHotPath(CpuSampleTraceArtifact artifact, string handle, double thresholdPercent)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (thresholdPercent <= 0d || thresholdPercent > 100d)
        {
            return InvalidArg<HotPathView>(nameof(thresholdPercent), "must be > 0 and <= 100");
        }

        var root = CallTreeIdentityProjector.Stamp(artifact.Root, artifact.MethodIdentities);
        var (frames, depth) = CpuSampleAnalytics.BuildHotPath(root, artifact.TotalSamples, thresholdPercent / 100d);
        var view = new HotPathView(artifact.ProcessId, artifact.TotalSamples, thresholdPercent, depth, frames)
        {
            SelfSamples = artifact.SelfSamples ?? CpuSampleAnalytics.TotalSelfSamples(root),
        };

        var summary = frames.Count == 0
            ? "No dominant call chain — the root has no children."
            : $"Hot path is {depth} frame(s) deep at a {thresholdPercent:0.#}% threshold. Leaf: {frames[^1].Method} ({frames[^1].InclusivePercent:0.#}% inclusive{FormatSelfSamples(frames[^1].SelfSamples)}).";

        var hintArguments = new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CallTreeView };
        if (frames.Count > 0)
        {
            hintArguments["rootMethodFilter"] = frames[^1].Method;
        }

        return DiagnosticResult.Ok(view, summary,
            new NextActionHint("query_snapshot",
                frames.Count == 0
                    ? "Inspect the full call tree to choose a concrete method."
                    : "Lower the threshold to extend the chain, or anchor the full tree at the leaf.",
                hintArguments));
    }

    /// <summary>
    /// Renders the <c>triage</c> view (issue #812): the top "busy user code" hotspots (<c>rankBy=
    /// "running"</c> order), the top wait/noise categories (grouped by <see cref="MethodSampleStat.WaitReason"/>,
    /// summed by exclusive samples), and the dominant hot-path leaf — the same evidence an operator
    /// would otherwise gather across <see cref="TopMethodsView"/> and <see cref="HotPathView"/> in two
    /// separate round trips, bundled into one.
    /// </summary>
    public static DiagnosticResult<TriageView> RenderTriage(
        CpuSampleTraceArtifact artifact, string handle, int topN, double hotPathThresholdPercent)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (topN < 1) return InvalidArg<TriageView>(nameof(topN), "must be >= 1");
        if (hotPathThresholdPercent <= 0d || hotPathThresholdPercent > 100d)
        {
            return InvalidArg<TriageView>(nameof(hotPathThresholdPercent), "must be > 0 and <= 100");
        }

        var root = CallTreeIdentityProjector.Stamp(artifact.Root, artifact.MethodIdentities);
        var exclusiveRanked = CpuSampleAnalytics.RankMethods(root, artifact.TotalSamples, byInclusive: false);
        var topBusy = CpuSampleAnalytics.RankMethodsByRunningSelf(exclusiveRanked).Take(topN).ToList();

        var topWaitCategories = exclusiveRanked
            .Where(m => m.WaitReason is not null)
            .GroupBy(m => m.WaitReason!, StringComparer.Ordinal)
            .Select(g =>
            {
                var exclusiveSamples = g.Sum(m => m.ExclusiveSamples);
                return new CpuWaitCategoryStat(
                    g.Key,
                    exclusiveSamples,
                    CpuSampleAnalytics.Percent(exclusiveSamples, artifact.TotalSamples),
                    g.Count());
            })
            .OrderByDescending(w => w.ExclusiveSamples)
            .Take(topN)
            .ToList();

        var (hotPathFrames, hotPathDepth) = CpuSampleAnalytics.BuildHotPath(root, artifact.TotalSamples, hotPathThresholdPercent / 100d);
        var hotPathLeaf = hotPathFrames.Count > 0 ? hotPathFrames[^1] : null;
        var selfSamples = artifact.SelfSamples ?? CpuSampleAnalytics.TotalSelfSamples(root);
        var verdict = ClassifyTriageVerdict(selfSamples);

        var view = new TriageView(artifact.ProcessId, artifact.TotalSamples, verdict, topBusy, topWaitCategories, hotPathLeaf, hotPathDepth)
        {
            SelfSamples = selfSamples,
        };

        var summary = BuildTriageSummary(verdict, topBusy, topWaitCategories, hotPathLeaf);
        var hint = topBusy.Count > 0
            ? new NextActionHint("query_snapshot", "Drill into the top busy method's callers/callees.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CallerCalleeView, ["rootMethodFilter"] = topBusy[0].Method })
            : new NextActionHint("query_snapshot", "Inspect the full call tree — no attributable busy method was found.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CallTreeView });

        return DiagnosticResult.Ok(view, summary, hint);
    }

    /// <summary>
    /// Neutral, diagnosis-agnostic verdict derived from the whole-capture running/waiting self-sample
    /// split: <c>"cpu-bound"</c> when waiting is a small minority of self time, <c>"wait-bound"</c>
    /// when it dominates, <c>"mixed"</c> otherwise, and <c>"unclassified"</c> when the capture carries
    /// no running/waiting classification at all (e.g. an older trace or a non-CPU sample kind).
    /// </summary>
    private static string ClassifyTriageVerdict(SelfSampleBreakdown? selfSamples)
    {
        if (selfSamples is null)
        {
            return "unclassified";
        }

        var total = selfSamples.RunningSamples + selfSamples.WaitingSamples;
        if (total <= 0)
        {
            return "unclassified";
        }

        var waitingPercent = 100.0 * selfSamples.WaitingSamples / total;
        return waitingPercent switch
        {
            >= 50d => "wait-bound",
            < 20d => "cpu-bound",
            _ => "mixed",
        };
    }

    private static string BuildTriageSummary(
        string verdict,
        List<MethodSampleStat> topBusy,
        List<CpuWaitCategoryStat> topWaitCategories,
        HotPathFrame? hotPathLeaf)
    {
        if (topBusy.Count == 0)
        {
            return "No attributable methods — the trace captured no frames to triage.";
        }

        var busy = topBusy[0];
        var summary = $"Verdict: {verdict}. Busiest user code: {busy.Method} ({busy.SelfSamples?.RunningSamples ?? busy.ExclusiveSamples} running / {busy.ExclusiveSamples} exclusive samples).";
        if (topWaitCategories.Count > 0)
        {
            var wait = topWaitCategories[0];
            summary += $" Top wait/noise category: {wait.WaitReason} ({wait.ExclusivePercent:0.#}% of samples across {wait.MethodCount} method(s)).";
        }

        if (hotPathLeaf is not null)
        {
            summary += $" Hot-path leaf: {hotPathLeaf.Method} ({hotPathLeaf.InclusivePercent:0.#}% inclusive).";
        }

        return summary;
    }

    /// <summary>Renders the <c>caller-callee</c> view for the single method matched by <paramref name="methodFilter"/>.</summary>
    public static DiagnosticResult<CallerCalleeView> RenderCallerCallee(
        CpuSampleTraceArtifact artifact, string handle, string? methodFilter, int topN)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(methodFilter))
        {
            return InvalidArg<CallerCalleeView>(nameof(methodFilter), "is required (a case-insensitive method-name substring)");
        }

        if (topN < 1) return InvalidArg<CallerCalleeView>(nameof(topN), "must be >= 1");

        var root = CallTreeIdentityProjector.Stamp(artifact.Root, artifact.MethodIdentities);
        var matches = CpuSampleAnalytics.MatchMethods(root, methodFilter);
        if (matches.Count == 0)
        {
            return DiagnosticResult.Fail<CallerCalleeView>(
                $"No method matching '{methodFilter}' in handle '{handle}'.",
                new DiagnosticError("NotFound", "No frame in the merged call tree contains the supplied substring.", methodFilter),
                new NextActionHint("query_snapshot", "Rank methods first to find an exact name to anchor on.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = TopMethodsView }));
        }

        if (matches.Count > 1)
        {
            var candidates = matches.Take(10).Select(m => $"{m.Representative.Frame.Method} ({m.Inclusive} inclusive)").ToList();
            return DiagnosticResult.Fail<CallerCalleeView>(
                $"'{methodFilter}' matched {matches.Count} distinct methods; narrow it to one.",
                new DiagnosticError("InvalidArgument", "The caller-callee view resolves a single focus method. Pass a more specific substring.", string.Join("; ", candidates)),
                new NextActionHint("query_snapshot", "Rank methods first to choose a concrete method name.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = TopMethodsView }));
        }

        var focus = matches[0];
        var built = CpuSampleAnalytics.BuildCallerCallee(root, artifact.TotalSamples, focus.Key, focus.Representative, topN);
        var view = built with
        {
            ProcessId = artifact.ProcessId,
        };

        var summary =
            $"{view.Method}: {view.InclusiveSamples} inclusive ({view.InclusivePercent:0.#}%) / {view.ExclusiveSamples} exclusive samples{FormatSelfSamples(view.SelfSamples)} — {view.Callers.Count} caller(s), {view.Callees.Count} callee(s).";

        var nextMethod = view.Callers.Count > 0
            ? view.Callers[0].Method
            : view.Callees.Count > 0
                ? view.Callees[0].Method
                : null;
        return nextMethod is null
            ? DiagnosticResult.Ok(view, summary)
            : DiagnosticResult.Ok(view, summary,
                new NextActionHint("query_snapshot", "Follow the top caller or callee by name.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CallerCalleeView, ["rootMethodFilter"] = nextMethod }));
    }

    private static CallTreeNode? FindHighestRankedDescendant(CallTreeNode node, string substring)
    {
        CallTreeNode? best = null;
        var stack = new Stack<CallTreeNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Frame.Method.Contains(substring, StringComparison.OrdinalIgnoreCase) &&
                (best is null || current.InclusiveSamples > best.InclusiveSamples))
            {
                best = current;
            }

            foreach (var child in current.Children)
            {
                stack.Push(child);
            }
        }

        return best;
    }

    private static (CallTreeNode Pruned, int NodeCount, bool Truncated, SubtreeMetricIndex Traversal) PruneTree(
        CallTreeNode root,
        int maxDepth,
        int maxNodes)
    {
        var traversal = BuildSubtreeMetrics(root, MaxCallTreeTraversalNodes);
        var nodeBudget = maxNodes;
        var truncated = false;
        nodeBudget--;
        var pruned = WalkReserved(root, maxDepth);
        return (pruned, maxNodes - nodeBudget, truncated, traversal);

        CallTreeNode WalkReserved(CallTreeNode n, int depthRemaining)
        {
            if (depthRemaining <= 1 || n.Children.Count == 0)
            {
                if (n.Children.Count > 0) truncated = true;
                return n with { Children = Array.Empty<CallTreeNode>() };
            }

            var kept = new List<CallTreeNode>();
            var candidates = SelectDecisiveChildren(
                n.Children,
                nodeBudget,
                traversal.ClassificationAvailable,
                traversal.MetricsByNode);
            if (candidates.Length < n.Children.Count)
            {
                truncated = true;
            }

            // Reserve one slot for every selected direct child before any descendant walk.
            // This prevents the highest-ranked child's subtree from consuming the budget and
            // hiding decisive sibling branches that already won global child selection.
            nodeBudget -= candidates.Length;
            foreach (var child in candidates)
            {
                kept.Add(WalkReserved(child, depthRemaining - 1));
            }

            return n with { Children = kept };
        }
    }

    private static CallTreeNode[] SelectDecisiveChildren(
        IReadOnlyList<CallTreeNode> children,
        int limit,
        bool classificationAvailable,
        IReadOnlyDictionary<CallTreeNode, SubtreeNodeMetric> metricsByNode)
    {
        if (limit <= 0 || children.Count == 0) return Array.Empty<CallTreeNode>();

        var selected = new List<DecisiveChildCandidate>(Math.Min(limit, children.Count));
        var useClassification = classificationAvailable
            && children.All(child =>
                metricsByNode.TryGetValue(child, out var metric)
                && metric.Complete);
        var comparer = useClassification
            ? DecisiveChildComparer.Classified
            : DecisiveChildComparer.Unclassified;
        foreach (var child in children)
        {
            metricsByNode.TryGetValue(child, out var metric);
            var candidate = new DecisiveChildCandidate(
                child,
                metric?.Samples.RunningSamples ?? 0);
            var index = selected.BinarySearch(candidate, comparer);
            if (index < 0) index = ~index;
            if (index >= limit) continue;

            selected.Insert(index, candidate);
            if (selected.Count > limit)
            {
                selected.RemoveAt(selected.Count - 1);
            }
        }

        return selected.Select(static candidate => candidate.Node).ToArray();
    }

    private static SubtreeMetricIndex BuildSubtreeMetrics(CallTreeNode root, int visitLimit)
    {
        var metrics = new Dictionary<CallTreeNode, SubtreeNodeMetric>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<MetricFrame>();
        stack.Push(new MetricFrame(root));
        var nodesVisited = 1;
        var classificationAvailable = root.SelfSamples is not null;
        var limitReached = false;

        while (stack.Count > 0)
        {
            var frame = stack.Peek();
            if (frame.NextChildIndex < frame.Node.Children.Count && nodesVisited < visitLimit)
            {
                var child = frame.Node.Children[frame.NextChildIndex++];
                nodesVisited++;
                classificationAvailable |= child.SelfSamples is not null;
                stack.Push(new MetricFrame(child));
                continue;
            }

            if (frame.NextChildIndex < frame.Node.Children.Count)
            {
                limitReached = true;
                frame.Complete = false;
            }

            var runningSamples = frame.RunningDescendantSamples + (frame.Node.SelfSamples?.RunningSamples ?? 0);
            var waitingSamples = frame.WaitingDescendantSamples + (frame.Node.SelfSamples?.WaitingSamples ?? 0);
            var total = new SelfSampleBreakdown(runningSamples, waitingSamples);
            metrics[frame.Node] = new SubtreeNodeMetric(total, frame.Complete);
            stack.Pop();
            if (stack.Count > 0)
            {
                stack.Peek().RunningDescendantSamples += runningSamples;
                stack.Peek().WaitingDescendantSamples += waitingSamples;
                stack.Peek().Complete &= frame.Complete;
            }
        }

        var totalSelfSamples = classificationAvailable
            && metrics.TryGetValue(root, out var rootMetric)
            && rootMetric.Complete
            ? rootMetric.Samples
            : null;
        return new SubtreeMetricIndex(metrics, nodesVisited, classificationAvailable, limitReached, totalSelfSamples);
    }

    private sealed record DecisiveChildCandidate(CallTreeNode Node, long RunningSamples);

    private sealed class DecisiveChildComparer(bool classificationAvailable) : IComparer<DecisiveChildCandidate>
    {
        public static DecisiveChildComparer Classified { get; } = new(true);
        public static DecisiveChildComparer Unclassified { get; } = new(false);

        public int Compare(DecisiveChildCandidate? x, DecisiveChildCandidate? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;

            var result = 0;
            if (classificationAvailable)
            {
                var xRunning = x.RunningSamples;
                var yRunning = y.RunningSamples;
                result = (yRunning > 0).CompareTo(xRunning > 0);
                if (result != 0) return result;
                result = yRunning.CompareTo(xRunning);
                if (result != 0) return result;
                result = y.Node.ExclusiveSamples.CompareTo(x.Node.ExclusiveSamples);
                if (result != 0) return result;
            }

            result = y.Node.InclusiveSamples.CompareTo(x.Node.InclusiveSamples);
            if (result != 0) return result;
            result = y.Node.ExclusiveSamples.CompareTo(x.Node.ExclusiveSamples);
            if (result != 0) return result;
            result = string.Compare(x.Node.Frame.Method, y.Node.Frame.Method, StringComparison.Ordinal);
            return result != 0
                ? result
                : string.Compare(x.Node.Frame.Module, y.Node.Frame.Module, StringComparison.Ordinal);
        }
    }

    private sealed class MetricFrame(CallTreeNode node)
    {
        public CallTreeNode Node { get; } = node;
        public int NextChildIndex { get; set; }
        public long RunningDescendantSamples { get; set; }
        public long WaitingDescendantSamples { get; set; }
        public bool Complete { get; set; } = true;
    }

    private sealed record SubtreeNodeMetric(SelfSampleBreakdown Samples, bool Complete);

    private sealed record SubtreeMetricIndex(
        IReadOnlyDictionary<CallTreeNode, SubtreeNodeMetric> MetricsByNode,
        int NodesVisited,
        bool ClassificationAvailable,
        bool LimitReached,
        SelfSampleBreakdown? TotalSelfSamples);

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));

    private static string FormatSelfSamples(SelfSampleBreakdown? selfSamples)
        => selfSamples is null
            ? string.Empty
            : $", self split {selfSamples.RunningSamples} running / {selfSamples.WaitingSamples} waiting";

    /// <summary>
    /// Renders the leader's <see cref="MethodSampleStat.WaitReason"/> (issue #811) as a trailing
    /// summary clause, e.g. <c>" [known wait: Monitor.Wait]"</c>, or empty when the leader is not a
    /// recognized wait/park frame — so a wait-dominated top row is clearly labeled as noise rather
    /// than silently ranked as if it were busy user code.
    /// </summary>
    private static string FormatWaitReason(string? waitReason)
        => string.IsNullOrEmpty(waitReason) ? string.Empty : $" [known wait: {waitReason}]";
}
