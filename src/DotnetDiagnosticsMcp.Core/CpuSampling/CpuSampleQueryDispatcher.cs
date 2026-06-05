using DotnetDiagnosticsMcp.Core;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Host-neutral drill-down engine for <see cref="CpuSampleTraceArtifact"/> handles — the CPU-sampling
/// analogue of <see cref="DotnetDiagnosticsMcp.Core.Dump.HeapSnapshotQueryDispatcher"/>. It renders the
/// merged caller→callee <c>call-tree</c> view (pruned by <c>maxDepth</c>/<c>maxNodes</c>, optionally
/// re-rooted at a method substring) directly from the already-collected trace, so both the MCP server's
/// <c>get_call_tree</c> / <c>query_snapshot(view="call-tree")</c> path and the standalone CLI
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
    /// <summary>The single view name the CPU-sampling drill-down exposes (parity with the server's uniform (handle, view) contract).</summary>
    public const string CallTreeView = "call-tree";

    private static readonly string[] Views = { CallTreeView };

    /// <summary>The view names this dispatcher can render from a trace alone (drill-down without re-sampling).</summary>
    public static IReadOnlyList<string> SessionViews => Views;

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
    /// <c>get_call_tree</c> body verbatim: stamps per-frame <c>MethodIdentity</c>, optionally
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

        return DiagnosticResult.Ok(
            view,
            summary,
            new NextActionHint("query_snapshot", "Drill deeper by anchoring at a specific method.",
                new Dictionary<string, object?> { ["handle"] = handle, ["rootMethodFilter"] = "<method substring>", ["maxDepth"] = 6 }));
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
