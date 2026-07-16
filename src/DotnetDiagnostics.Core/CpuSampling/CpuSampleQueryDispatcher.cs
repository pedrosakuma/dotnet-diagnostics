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

    /// <summary>Default number of rows returned by the ranked CPU views.</summary>
    public const int DefaultTopN = 20;

    /// <summary>Default hot-path threshold: a child must carry at least this % of its parent to extend the chain.</summary>
    public const double DefaultHotPathThresholdPercent = 50d;

    private static readonly string[] Views =
    {
        CallTreeView, TopMethodsView, ByModuleView, ByNamespaceView, HotPathView, CallerCalleeView,
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

        var root = CallTreeIdentityProjector.Stamp(artifact.Root, artifact.MethodIdentities);
        if (!string.IsNullOrWhiteSpace(rootMethodFilter))
        {
            var match = FindHighestRankedDescendant(root, rootMethodFilter);
            if (match is null)
            {
                return DiagnosticResult.Fail<CallTreeView>(
                    $"No frame matching '{rootMethodFilter}' in handle '{handle}'.",
                    new DiagnosticError("NotFound", "No frame in the merged call tree contains the supplied substring.", rootMethodFilter),
                    new NextActionHint("query_snapshot", "Re-issue without rootMethodFilter to inspect the full tree first.",
                        new Dictionary<string, object?> { ["handle"] = handle, ["maxDepth"] = maxDepth, ["maxNodes"] = maxNodes }));
            }
            root = match;
        }

        var (pruned, nodeCount, truncated) = PruneTree(root, maxDepth, maxNodes);
        var view = new CallTreeView(artifact.ProcessId, artifact.TotalSamples, nodeCount, truncated, pruned);
        var summary = truncated
            ? $"Showing {nodeCount} nodes (truncated; raise maxNodes or maxDepth, or narrow with rootMethodFilter). Root: {root.Frame.Method} — {root.InclusiveSamples} inclusive samples."
            : $"Showing the full sub-tree rooted at {root.Frame.Method} ({nodeCount} nodes, {root.InclusiveSamples} inclusive samples).";

        var drilldownMethod = pruned.Children.Count > 0 ? pruned.Children[0].Frame.Method : null;
        return drilldownMethod is null
            ? DiagnosticResult.Ok(view, summary)
            : DiagnosticResult.Ok(
                view,
                summary,
                new NextActionHint("query_snapshot", "Drill deeper by anchoring at the hottest child method.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CallTreeView, ["rootMethodFilter"] = drilldownMethod, ["maxDepth"] = 6 }));
    }

    /// <summary>Renders the <c>top-methods</c> view: per-method exclusive/inclusive aggregation, ranked and capped.</summary>
    public static DiagnosticResult<TopMethodsView> RenderTopMethods(
        CpuSampleTraceArtifact artifact, string handle, string? sortBy, int topN)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (topN < 1) return InvalidArg<TopMethodsView>(nameof(topN), "must be >= 1");

        var normalizedSort = string.IsNullOrWhiteSpace(sortBy) ? "exclusive" : sortBy.Trim().ToLowerInvariant();
        if (normalizedSort is not ("exclusive" or "inclusive"))
        {
            return InvalidArg<TopMethodsView>(nameof(sortBy), "must be 'exclusive' or 'inclusive'");
        }

        var root = CallTreeIdentityProjector.Stamp(artifact.Root, artifact.MethodIdentities);
        var ranked = CpuSampleAnalytics.RankMethods(root, artifact.TotalSamples, byInclusive: normalizedSort == "inclusive");
        var top = ranked.Take(topN).ToList();
        var view = new TopMethodsView(artifact.ProcessId, artifact.TotalSamples, normalizedSort, top.Count, top);

        var summary = top.Count == 0
            ? "No methods aggregated — the trace captured no attributable frames."
            : $"Top {top.Count} method(s) by {normalizedSort} samples (of {ranked.Count} total). Hottest: {top[0].Method} ({top[0].ExclusiveSamples} exclusive / {top[0].InclusiveSamples} inclusive).";

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
        var view = new GroupedSamplesView(artifact.ProcessId, artifact.TotalSamples, groupBy, top.Count, top);

        var summary = top.Count == 0
            ? $"No {groupBy} groups aggregated."
            : $"Top {top.Count} {groupBy}(s) by exclusive samples (of {ranked.Count}). Hottest: {top[0].Group} ({top[0].ExclusiveSamples} exclusive / {top[0].InclusiveSamples} inclusive).";

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
        var view = new HotPathView(artifact.ProcessId, artifact.TotalSamples, thresholdPercent, depth, frames);

        var summary = frames.Count == 0
            ? "No dominant call chain — the root has no children."
            : $"Hot path is {depth} frame(s) deep at a {thresholdPercent:0.#}% threshold. Leaf: {frames[^1].Method} ({frames[^1].InclusivePercent:0.#}% inclusive).";

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
        var view = built with { ProcessId = artifact.ProcessId };

        var summary =
            $"{view.Method}: {view.InclusiveSamples} inclusive ({view.InclusivePercent:0.#}%) / {view.ExclusiveSamples} exclusive samples — {view.Callers.Count} caller(s), {view.Callees.Count} callee(s).";

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

    private static (CallTreeNode Pruned, int NodeCount, bool Truncated) PruneTree(CallTreeNode root, int maxDepth, int maxNodes)
    {
        var nodeBudget = maxNodes;
        var truncated = false;
        var pruned = Walk(root, maxDepth);
        return (pruned, maxNodes - nodeBudget, truncated);

        CallTreeNode Walk(CallTreeNode n, int depthRemaining)
        {
            if (nodeBudget <= 0)
            {
                truncated = true;
                return n with { Children = Array.Empty<CallTreeNode>() };
            }
            nodeBudget--;

            if (depthRemaining <= 1 || n.Children.Count == 0)
            {
                if (n.Children.Count > 0) truncated = true;
                return n with { Children = Array.Empty<CallTreeNode>() };
            }

            var kept = new List<CallTreeNode>();
            foreach (var child in n.Children)
            {
                if (nodeBudget <= 0)
                {
                    truncated = true;
                    break;
                }
                kept.Add(Walk(child, depthRemaining - 1));
            }

            return n with { Children = kept };
        }
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
