using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>One method's aggregated sample attribution across the whole merged tree.</summary>
public sealed record MethodSampleStat(
    string Method,
    string Module,
    string Namespace,
    long ExclusiveSamples,
    long InclusiveSamples,
    double ExclusivePercent,
    double InclusivePercent,
    MethodIdentity? Identity)
{
    /// <summary>
    /// Optional split of <see cref="ExclusiveSamples"/> into running vs waiting observations.
    /// </summary>
    public SelfSampleBreakdown? SelfSamples { get; init; }
}

/// <summary>Top-N methods ranked by exclusive (default) or inclusive samples.</summary>
public sealed record TopMethodsView(
    int ProcessId,
    long TotalSamples,
    string SortedBy,
    int Count,
    IReadOnlyList<MethodSampleStat> Methods)
{
    public SelfSampleBreakdown? SelfSamples { get; init; }
}

/// <summary>One group's (module or namespace) aggregated sample attribution.</summary>
public sealed record GroupSampleStat(
    string Group,
    long ExclusiveSamples,
    long InclusiveSamples,
    double ExclusivePercent,
    double InclusivePercent)
{
    public SelfSampleBreakdown? SelfSamples { get; init; }
}

/// <summary>Samples aggregated by module or namespace.</summary>
public sealed record GroupedSamplesView(
    int ProcessId,
    long TotalSamples,
    string GroupBy,
    int Count,
    IReadOnlyList<GroupSampleStat> Groups)
{
    public SelfSampleBreakdown? SelfSamples { get; init; }
}

/// <summary>One frame on the dominant call chain.</summary>
public sealed record HotPathFrame(
    string Method,
    string Module,
    long InclusiveSamples,
    long ExclusiveSamples,
    double InclusivePercent,
    double FractionOfParentPercent,
    MethodIdentity? Identity)
{
    public SelfSampleBreakdown? SelfSamples { get; init; }
}

/// <summary>The dominant call chain — follow the heaviest child until it drops below the threshold.</summary>
public sealed record HotPathView(
    int ProcessId,
    long TotalSamples,
    double ThresholdPercent,
    int Depth,
    IReadOnlyList<HotPathFrame> Frames)
{
    public SelfSampleBreakdown? SelfSamples { get; init; }
}

/// <summary>One caller or callee edge of a focus method.</summary>
public sealed record CallerCalleeEdge(
    string Method,
    string Module,
    long Samples,
    double Percent,
    MethodIdentity? Identity);

/// <summary>Callers and callees of a single focus method (PerfView-style).</summary>
public sealed record CallerCalleeView(
    string Method,
    string Module,
    string Namespace,
    int ProcessId,
    long TotalSamples,
    long InclusiveSamples,
    long ExclusiveSamples,
    double InclusivePercent,
    double ExclusivePercent,
    IReadOnlyList<CallerCalleeEdge> Callers,
    IReadOnlyList<CallerCalleeEdge> Callees,
    MethodIdentity? Identity)
{
    public SelfSampleBreakdown? SelfSamples { get; init; }
}

/// <summary>
/// Pure, host-neutral aggregations over an already-merged CPU <see cref="CallTreeNode"/> tree —
/// the building blocks behind the <c>top-methods</c> / <c>by-module</c> / <c>by-namespace</c> /
/// <c>hot-path</c> / <c>caller-callee</c> drill-down views (issue #313). No I/O, no re-sampling.
/// </summary>
/// <remarks>
/// <para><b>Exclusive</b> aggregation is an exact sum of per-node <c>ExclusiveSamples</c> by key (it
/// sums to <c>TotalSamples</c> because exclusive samples land on leaf frames).</para>
/// <para><b>Inclusive</b> aggregation counts each sampled stack at most once per key: a DFS tracks the
/// set of ancestor keys and only credits a node's inclusive count when its key is not already on the
/// path. This sums distinct call paths (disjoint sample sets) while collapsing recursion
/// (ancestor→descendant repeats), matching PerfView's "samples where the frame appears on the stack".</para>
/// <para>The aggregation key prefers the semantic method identity (<c>ModuleVersionId</c> +
/// <c>MetadataToken</c>) so overloads / same-named methods in different types don't merge; it falls back
/// to <c>(Module, display)</c> when no identity was resolved.</para>
/// </remarks>
internal static class CpuSampleAnalytics
{
    internal const string RootMethod = "<root>";

    private sealed class Agg
    {
        public long Exclusive;
        public long Inclusive;
        public long RunningSelf;
        public long WaitingSelf;
        public CallTreeNode Representative = null!;
    }

    internal static bool IsSyntheticRoot(CallTreeNode node)
        => node.Frame.Module.Length == 0 && string.Equals(node.Frame.Method, RootMethod, StringComparison.Ordinal);

    internal static string MethodKey(CallTreeNode node)
    {
        var id = node.Identity;
        if (id?.ModuleVersionId is Guid mvid && id.MetadataToken is int token)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"t:{mvid:N}:{token}");
        }

        return string.Concat("d:", node.Frame.Module, "\u0000", node.Frame.Method);
    }

    internal static string ModuleOf(CallTreeNode node)
    {
        var module = node.Identity?.ModuleName;
        if (string.IsNullOrEmpty(module))
        {
            module = node.Frame.Module;
        }

        return string.IsNullOrEmpty(module) ? "(unknown module)" : module;
    }

    internal static string NamespaceOf(CallTreeNode node)
    {
        var typeName = node.Identity?.TypeFullName;
        if (string.IsNullOrEmpty(typeName))
        {
            var method = node.Frame.Method;
            var paren = method.IndexOf('(', StringComparison.Ordinal);
            if (paren >= 0)
            {
                method = method[..paren];
            }

            var lastDot = method.LastIndexOf('.');
            if (lastDot <= 0)
            {
                return "(global)";
            }

            typeName = method[..lastDot];
        }

        var nsDot = typeName.LastIndexOf('.');
        return nsDot <= 0 ? "(global)" : typeName[..nsDot];
    }

    internal static double Percent(long samples, long total)
        => total <= 0 ? 0d : Math.Round(samples * 100d / total, 2);

    internal static SelfSampleBreakdown? TotalSelfSamples(CallTreeNode root)
    {
        long running = 0;
        long waiting = 0;
        var sawClassification = false;
        var stack = new Stack<CallTreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.SelfSamples is { } self)
            {
                running += self.RunningSamples;
                waiting += self.WaitingSamples;
                sawClassification = true;
            }

            foreach (var child in current.Children)
            {
                stack.Push(child);
            }
        }

        return sawClassification ? new SelfSampleBreakdown(running, waiting) : null;
    }

    /// <summary>Aggregates exclusive/inclusive samples by a caller-chosen key over the whole tree.</summary>
    private static Dictionary<string, Agg> Aggregate(CallTreeNode root, Func<CallTreeNode, string> keySelector)
    {
        var map = new Dictionary<string, Agg>(StringComparer.Ordinal);
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        Visit(root);
        return map;

        void Visit(CallTreeNode node)
        {
            string? key = null;
            var addedToAncestors = false;
            if (!IsSyntheticRoot(node))
            {
                key = keySelector(node);
                if (!map.TryGetValue(key, out var agg))
                {
                    agg = new Agg { Representative = node };
                    map[key] = agg;
                }

                agg.Exclusive += node.ExclusiveSamples;
                if (node.SelfSamples is { } self)
                {
                    agg.RunningSelf += self.RunningSamples;
                    agg.WaitingSelf += self.WaitingSamples;
                }

                if (ancestors.Add(key))
                {
                    agg.Inclusive += node.InclusiveSamples;
                    addedToAncestors = true;
                }
            }

            foreach (var child in node.Children)
            {
                Visit(child);
            }

            if (addedToAncestors)
            {
                ancestors.Remove(key!);
            }
        }
    }

    internal static IReadOnlyList<MethodSampleStat> RankMethods(CallTreeNode root, long total, bool byInclusive)
    {
        var aggregated = Aggregate(root, MethodKey);
        var totalSelf = TotalSelfSamples(root);
        var stats = new List<MethodSampleStat>(aggregated.Count);
        foreach (var agg in aggregated.Values)
        {
            var rep = agg.Representative;
            stats.Add(new MethodSampleStat(
                rep.Frame.Method,
                ModuleOf(rep),
                NamespaceOf(rep),
                agg.Exclusive,
                agg.Inclusive,
                Percent(agg.Exclusive, total),
                Percent(agg.Inclusive, total),
                rep.Identity)
            {
                SelfSamples = totalSelf is null ? null : new SelfSampleBreakdown(agg.RunningSelf, agg.WaitingSelf),
            });
        }

        stats.Sort((a, b) => byInclusive
            ? b.InclusiveSamples.CompareTo(a.InclusiveSamples)
            : b.ExclusiveSamples.CompareTo(a.ExclusiveSamples));
        return stats;
    }

    /// <summary>
    /// The global self-time (exclusive) leader across the whole merged tree, or <c>null</c> when there
    /// is no on-CPU leaf to attribute. Ranks over the full tree (not the inclusive-capped hotspots), so
    /// the true leaf is found even on a deep stack. Shared by every sampler so the inline
    /// <see cref="CpuSample.TopSelfTime"/> and the stored-artifact / Resource path agree.
    /// </summary>
    internal static Hotspot? TopSelfTime(CallTreeNode root, long total)
    {
        var ranked = RankMethods(root, total, byInclusive: false);
        if (ranked.Count == 0 || ranked[0].ExclusiveSamples <= 0)
        {
            return null;
        }

        var m = ranked[0];
        return new Hotspot(new SampledFrame(m.Module, m.Method), m.InclusiveSamples, m.ExclusiveSamples, m.Identity)
        {
            SelfSamples = m.SelfSamples,
        };
    }

    internal static Hotspot? TopRunningSelfTime(CallTreeNode root, long total)
    {
        var ranked = RankMethods(root, total, byInclusive: false)
            .OrderByDescending(m => m.SelfSamples?.RunningSamples ?? m.ExclusiveSamples)
            .ThenByDescending(m => m.ExclusiveSamples)
            .ToList();
        if (ranked.Count == 0)
        {
            return null;
        }

        var top = ranked[0];
        var running = top.SelfSamples?.RunningSamples ?? top.ExclusiveSamples;
        if (running <= 0)
        {
            return null;
        }

        return new Hotspot(new SampledFrame(top.Module, top.Method), top.InclusiveSamples, top.ExclusiveSamples, top.Identity)
        {
            SelfSamples = top.SelfSamples,
        };
    }

    internal static IReadOnlyList<GroupSampleStat> RankGroups(CallTreeNode root, long total, Func<CallTreeNode, string> keySelector)
    {
        var aggregated = Aggregate(root, keySelector);
        var totalSelf = TotalSelfSamples(root);
        var groups = new List<GroupSampleStat>(aggregated.Count);
        foreach (var (group, agg) in aggregated)
        {
            groups.Add(new GroupSampleStat(
                group,
                agg.Exclusive,
                agg.Inclusive,
                Percent(agg.Exclusive, total),
                Percent(agg.Inclusive, total))
            {
                SelfSamples = totalSelf is null ? null : new SelfSampleBreakdown(agg.RunningSelf, agg.WaitingSelf),
            });
        }

        groups.Sort((a, b) => b.ExclusiveSamples.CompareTo(a.ExclusiveSamples));
        return groups;
    }

    internal static (IReadOnlyList<HotPathFrame> Frames, int Depth) BuildHotPath(CallTreeNode root, long total, double thresholdFraction)
    {
        var frames = new List<HotPathFrame>();
        var totalSelf = TotalSelfSamples(root);
        var current = root;
        var parentInclusive = root.InclusiveSamples;

        while (current.Children.Count > 0)
        {
            CallTreeNode? best = null;
            foreach (var child in current.Children)
            {
                if (best is null || child.InclusiveSamples > best.InclusiveSamples)
                {
                    best = child;
                }
            }

            if (best is null)
            {
                break;
            }

            var fraction = parentInclusive <= 0 ? 0d : (double)best.InclusiveSamples / parentInclusive;
            if (frames.Count > 0 && fraction < thresholdFraction)
            {
                break;
            }

            frames.Add(new HotPathFrame(
                best.Frame.Method,
                ModuleOf(best),
                best.InclusiveSamples,
                best.ExclusiveSamples,
                Percent(best.InclusiveSamples, total),
                Math.Round(fraction * 100d, 2),
                best.Identity)
            {
                SelfSamples = totalSelf is null ? null : best.SelfSamples ?? new SelfSampleBreakdown(0, 0),
            });

            current = best;
            parentInclusive = best.InclusiveSamples;
        }

        return (frames, frames.Count);
    }

    /// <summary>Distinct focus-method keys whose display name contains <paramref name="substring"/>.</summary>
    internal static IReadOnlyList<(string Key, CallTreeNode Representative, long Inclusive)> MatchMethods(CallTreeNode root, string substring)
    {
        var byKey = new Dictionary<string, Agg>(StringComparer.Ordinal);
        Visit(root);
        return byKey
            .Select(kv => (kv.Key, kv.Value.Representative, kv.Value.Inclusive))
            .OrderByDescending(t => t.Inclusive)
            .ToList();

        void Visit(CallTreeNode node)
        {
            if (!IsSyntheticRoot(node) && node.Frame.Method.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                var key = MethodKey(node);
                if (!byKey.TryGetValue(key, out var agg))
                {
                    agg = new Agg { Representative = node };
                    byKey[key] = agg;
                }

                agg.Inclusive += node.InclusiveSamples;
            }

            foreach (var child in node.Children)
            {
                Visit(child);
            }
        }
    }

    internal static CallerCalleeView BuildCallerCallee(CallTreeNode root, long total, string focusKey, CallTreeNode representative, int topN)
    {
        long focusInclusive = 0;
        long focusExclusive = 0;
        long focusRunning = 0;
        long focusWaiting = 0;
        var callers = new Dictionary<string, Agg>(StringComparer.Ordinal);
        var callees = new Dictionary<string, Agg>(StringComparer.Ordinal);
        var totalSelf = TotalSelfSamples(root);

        Visit(root, parent: null, focusSeen: false);

        var callerEdges = ToEdges(callers, total, topN);
        var calleeEdges = ToEdges(callees, total, topN);

        return new CallerCalleeView(
            representative.Frame.Method,
            ModuleOf(representative),
            NamespaceOf(representative),
            0, // ProcessId filled by the dispatcher
            total,
            focusInclusive,
            focusExclusive,
            Percent(focusInclusive, total),
            Percent(focusExclusive, total),
            callerEdges,
            calleeEdges,
            representative.Identity)
        {
            SelfSamples = totalSelf is null ? null : new SelfSampleBreakdown(focusRunning, focusWaiting),
        };

        void Visit(CallTreeNode node, CallTreeNode? parent, bool focusSeen)
        {
            var isFocus = !IsSyntheticRoot(node) && string.Equals(MethodKey(node), focusKey, StringComparison.Ordinal);
            if (isFocus)
            {
                focusExclusive += node.ExclusiveSamples;
                if (node.SelfSamples is { } self)
                {
                    focusRunning += self.RunningSamples;
                    focusWaiting += self.WaitingSamples;
                }

                if (!focusSeen)
                {
                    focusInclusive += node.InclusiveSamples;
                    if (parent is not null)
                    {
                        Credit(callers, parent, node.InclusiveSamples);
                    }

                    foreach (var child in node.Children)
                    {
                        Credit(callees, child, child.InclusiveSamples);
                    }
                }
            }

            var childFocusSeen = focusSeen || isFocus;
            foreach (var child in node.Children)
            {
                Visit(child, node, childFocusSeen);
            }
        }
    }

    private static void Credit(Dictionary<string, Agg> map, CallTreeNode node, long samples)
    {
        // A top-level focus method's parent is the synthetic root; surface it as a "<root>" caller
        // to mark a top-level entry point (PerfView's ROOT pseudo-node), rather than dropping it.
        var key = IsSyntheticRoot(node) ? "d:\u0000<root>" : MethodKey(node);
        if (!map.TryGetValue(key, out var agg))
        {
            agg = new Agg { Representative = node };
            map[key] = agg;
        }

        agg.Inclusive += samples;
    }

    private static List<CallerCalleeEdge> ToEdges(Dictionary<string, Agg> map, long total, int topN)
        => map.Values
            .OrderByDescending(a => a.Inclusive)
            .Take(topN)
            .Select(a => new CallerCalleeEdge(
                a.Representative.Frame.Method,
                ModuleOf(a.Representative),
                a.Inclusive,
                Percent(a.Inclusive, total),
                a.Representative.Identity))
            .ToList();
}
