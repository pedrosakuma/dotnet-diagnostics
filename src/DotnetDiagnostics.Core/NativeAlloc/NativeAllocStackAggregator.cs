using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.NativeAlloc;

/// <summary>
/// OS-agnostic aggregation of native-allocation call stacks into the shared
/// <see cref="CpuSampleTraceArtifact"/> / <see cref="Hotspot"/> shape. Both the Linux
/// <see cref="PerfNativeAllocSampler"/> (via the perf-script path it shares with the CPU sampler)
/// and the Windows <see cref="EtwNativeAllocSampler"/> (via ETW VirtualAlloc stacks) feed their
/// captured stacks here so the resulting call tree — and therefore the
/// <c>query_snapshot(view="call-tree")</c> drilldown — is identical on both platforms.
/// </summary>
/// <remarks>
/// This type has no platform dependency on purpose: it operates on already-extracted frames so it
/// can be unit-tested with synthetic stacks on any host (the live ETW / perf capture is what is
/// platform-specific, not the attribution math). The aggregation mirrors
/// <c>EtwNativeAotCpuSampler.AggregateFromTraceLog</c>: one count per recorded allocation event,
/// inclusive counts deduplicated per stack so recursion does not inflate a frame.
/// </remarks>
internal static class NativeAllocStackAggregator
{
    /// <summary>
    /// Aggregates the supplied allocation stacks into a call tree plus ranked hotspots.
    /// </summary>
    /// <param name="leafToRootStacks">
    /// One entry per recorded allocation event; each entry is the call stack ordered leaf→root
    /// (the allocator call site first), matching the order TraceLog and perf emit frames.
    /// </param>
    /// <param name="topN">Maximum number of hotspots returned in <see cref="Result.Hotspots"/>.</param>
    public static Result Aggregate(
        IEnumerable<IReadOnlyList<(string Module, string Method)>> leafToRootStacks,
        int topN)
    {
        ArgumentNullException.ThrowIfNull(leafToRootStacks);
        if (topN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), "topN must be positive.");
        }

        var inclusive = new Dictionary<string, long>(StringComparer.Ordinal);
        var exclusive = new Dictionary<string, long>(StringComparer.Ordinal);
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        var builder = new CallTreeBuilder();
        long total = 0;

        foreach (var stack in leafToRootStacks)
        {
            if (stack is null || stack.Count == 0)
            {
                continue;
            }

            // Build the keyed leaf→root frame list, then reverse to root→leaf for CallTreeBuilder.
            var frames = new List<(string Key, string Module, string Display)>(stack.Count);
            foreach (var (module, method) in stack)
            {
                var moduleName = module ?? string.Empty;
                var methodName = string.IsNullOrEmpty(method) ? "[unknown]" : method;
                var key = string.IsNullOrEmpty(moduleName) ? methodName : moduleName + "!" + methodName;
                frames.Add((key, moduleName, methodName));
                modules.TryAdd(key, moduleName);
            }

            if (frames.Count == 0)
            {
                continue;
            }

            total++;
            frames.Reverse();

            var leafKey = frames[^1].Key;
            exclusive[leafKey] = exclusive.GetValueOrDefault(leafKey) + 1;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (k, _, _) in frames)
            {
                if (seen.Add(k))
                {
                    inclusive[k] = inclusive.GetValueOrDefault(k) + 1;
                }
            }

            builder.AddStack(frames, leafKey);
        }

        var hotspots = inclusive
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv =>
            {
                var module = modules.GetValueOrDefault(kv.Key, string.Empty);
                var display = !string.IsNullOrEmpty(module) && kv.Key.StartsWith(module + "!", StringComparison.Ordinal)
                    ? kv.Key[(module.Length + 1)..]
                    : kv.Key;
                return new Hotspot(
                    Frame: new SampledFrame(module, display),
                    InclusiveSamples: kv.Value,
                    ExclusiveSamples: exclusive.GetValueOrDefault(kv.Key),
                    Identity: null);
            })
            .ToList();

        var hasResolved = hotspots.Any(h =>
            !h.Frame.Method.StartsWith("0x", StringComparison.Ordinal) &&
            !h.Frame.Method.StartsWith("[0x", StringComparison.Ordinal) &&
            h.Frame.Method != "[unknown]");
        var symbolSource = total == 0
            ? NativeAotSymbolDemangler.SymbolSource.Unknown
            : hasResolved
                ? NativeAotSymbolDemangler.SymbolSource.PdbResolved
                : NativeAotSymbolDemangler.SymbolSource.Stripped;

        return new Result(total, hotspots, builder.Build(), symbolSource);
    }

    /// <summary>Aggregation output: the total recorded allocations, ranked hotspots, the merged
    /// call tree, and the aggregate symbol-resolution quality.</summary>
    public sealed record Result(
        long TotalSampledAllocations,
        IReadOnlyList<Hotspot> Hotspots,
        CallTreeNode Root,
        NativeAotSymbolDemangler.SymbolSource SymbolSource);
}
