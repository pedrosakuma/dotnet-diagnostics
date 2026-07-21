using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>A node in a merged caller→callee tree built from CPU samples.</summary>
public sealed record CallTreeNode(
    SampledFrame Frame,
    long InclusiveSamples,
    long ExclusiveSamples,
    IReadOnlyList<CallTreeNode> Children,
    MethodIdentity? Identity = null)
{
    /// <summary>
    /// Optional split of this node's <see cref="ExclusiveSamples"/> into running vs waiting
    /// observations. Populated for CPU-sample trees; omitted for allocation/native-alloc trees
    /// that reuse the same call-tree shape.
    /// </summary>
    public SelfSampleBreakdown? SelfSamples { get; init; }
}

/// <summary>Reprojects a call tree with per-frame <see cref="MethodIdentity"/> payloads.</summary>
public static class CallTreeIdentityProjector
{
    public static CallTreeNode Stamp(
        CallTreeNode root,
        IReadOnlyDictionary<SymbolRef, MethodIdentity>? identities)
    {
        if (identities is null || identities.Count == 0)
        {
            return root;
        }

        return Walk(root);

        CallTreeNode Walk(CallTreeNode node)
        {
            identities.TryGetValue(new SymbolRef(node.Frame.Module, node.Frame.Method), out var identity);
            if (node.Children.Count == 0)
            {
                return node with { Identity = identity };
            }

            var children = node.Children.Select(Walk).ToList();
            return node with { Identity = identity, Children = children };
        }
    }
}

/// <summary>Bounded view of a <see cref="CallTreeNode"/> returned by the drill-down tool.</summary>
public sealed record CallTreeView(
    int ProcessId,
    long TotalSamples,
    int NodeCount,
    bool Truncated,
    CallTreeNode Root)
{
    /// <summary>
    /// Overall split of sampled leaf/self observations for this call-tree capture, when the
    /// originating artifact carried wait/run classification.
    /// </summary>
    public SelfSampleBreakdown? SelfSamples { get; init; }
}

/// <summary>
/// In-memory artifact registered under a handle when the CPU sampler completes. The summary
/// (returned to the LLM by <c>collect_sample(kind="cpu")</c>) is intentionally compact; the full tree
/// here is what <c>query_snapshot(view="call-tree")</c> walks on follow-up calls. <see cref="ResolvedSources"/>
/// holds optional source-level resolution (file:line, SourceLink) for top-N hotspots — keyed
/// by <c>(module, methodFullName)</c> so the exporter can attach the location without walking
/// the whole tree. <see cref="TracePath"/>, when set, is the path of the persisted raw
/// <c>.nettrace</c> under the artifact root (PerfView/Speedscope/Perfetto export, issue #445).
/// </summary>
public sealed record CpuSampleTraceArtifact(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalSamples,
    CallTreeNode Root,
    IReadOnlyDictionary<SymbolRef, SourceLocation>? ResolvedSources = null,
    IReadOnlyDictionary<SymbolRef, MethodIdentity>? MethodIdentities = null,
    NativeAotSymbolDemangler.SymbolSource SymbolSource = NativeAotSymbolDemangler.SymbolSource.Unknown,
    string? TracePath = null)
{
    public IReadOnlyDictionary<SymbolRef, SourceLocation> ResolvedSources { get; init; }
        = ResolvedSources ?? EmptyResolved;

    public IReadOnlyDictionary<SymbolRef, MethodIdentity> MethodIdentities { get; init; }
        = MethodIdentities ?? EmptyIdentities;

    /// <summary>
    /// Overall split of sampled leaf/self observations for CPU captures, when available. Omitted
    /// for non-CPU artifacts that reuse this call-tree container.
    /// </summary>
    public SelfSampleBreakdown? SelfSamples { get; init; }

    private static readonly IReadOnlyDictionary<SymbolRef, SourceLocation> EmptyResolved
        = new Dictionary<SymbolRef, SourceLocation>();

    private static readonly IReadOnlyDictionary<SymbolRef, MethodIdentity> EmptyIdentities
        = new Dictionary<SymbolRef, MethodIdentity>();
}
