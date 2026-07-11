using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Dump;

internal static class ClrMdRetentionAnalyzer
{
    public static IReadOnlyList<RetentionPath> ResolveRetentionPaths(
        ClrRuntime runtime,
        IReadOnlyList<TypeStat> topByBytes,
        int depthLimit,
        int targetCount,
        List<string> warnings,
        CancellationToken ct)
    {
        // Build a reverse map: object → first retainer found during a single roots/refs walk.
        // For each target type we then pick the largest instance and walk back to a root.
        // This is approximate (a real !gcroot does a full search) but cheap and "good enough"
        // to point the LLM at where to dig deeper.
        var targets = new HashSet<string>(topByBytes.Take(targetCount).Select(t => t.TypeFullName), StringComparer.Ordinal);
        if (targets.Count == 0) return Array.Empty<RetentionPath>();

        var sampleInstances = new Dictionary<string, ClrObject>(StringComparer.Ordinal);
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            var typeName = obj.Type?.Name;
            if (typeName is null || !targets.Contains(typeName)) continue;
            if (sampleInstances.TryGetValue(typeName, out var existing) && existing.Size >= obj.Size) continue;
            sampleInstances[typeName] = obj;
        }

        var targetAddresses = new HashSet<ulong>(sampleInstances.Values.Select(o => o.Address));
        var rootByObject = BuildRootByObjectMap(runtime, targetAddresses, depthLimit, maxRetainedGraphObjects: 250_000, out var bfsCapHit, ct);
        if (bfsCapHit)
        {
            warnings.Add($"Retention-path BFS hit its safety cap before reaching every target type; deeply-retained instances may report Truncated=true with no chain found.");
        }

        var results = new List<RetentionPath>(sampleInstances.Count);
        foreach (var (typeName, instance) in sampleInstances)
        {
            ct.ThrowIfCancellationRequested();
            var reachedByBfs = rootByObject.ContainsKey(instance.Address);
            var chain = WalkUp(instance, rootByObject, depthLimit, out var truncated);
            // If the target wasn't reachable from any root within the BFS budget the chain only
            // contains the target itself — surface that as Truncated so the LLM doesn't mistake
            // "no root found" for "this object has no retainer (impossible for a live object)".
            if (!reachedByBfs)
            {
                truncated = true;
            }

            results.Add(new RetentionPath(
                TargetTypeFullName: typeName,
                TargetObjectAddress: instance.Address,
                Chain: chain,
                Truncated: truncated));
        }

        return results;
    }

    public static Dictionary<ulong, (ulong From, string? RootKind)> BuildRootByObjectMap(
        ClrRuntime runtime,
        HashSet<ulong> targets,
        int depthLimit,
        int maxRetainedGraphObjects,
        out bool bfsCapHit,
        CancellationToken ct)
    {
        // Map each reachable object to its first-seen retainer (object address or root).
        // We short-circuit as soon as every target has been observed by the BFS so we don't pay
        // for the rest of the heap.
        bfsCapHit = false;
        var retainer = new Dictionary<ulong, (ulong From, string? RootKind)>();
        var visited = new HashSet<ulong>();
        var queue = new Queue<(ulong Address, int Depth)>();
        var remainingTargets = new HashSet<ulong>(targets);

        foreach (var root in runtime.Heap.EnumerateRoots())
        {
            ct.ThrowIfCancellationRequested();
            var addr = root.Object.Address;
            if (addr == 0 || !visited.Add(addr)) continue;
            retainer[addr] = (0UL, root.RootKind.ToString());
            queue.Enqueue((addr, 0));
            if (remainingTargets.Remove(addr) && remainingTargets.Count == 0) return retainer;
            if (visited.Count >= maxRetainedGraphObjects)
            {
                bfsCapHit = true;
                return retainer;
            }
        }

        // Safety cap: scale with depthLimit but allow enough breathing room to reach a typical
        // managed object (LLM-facing depthLimit defaults to 8; 8 * 32 = 256 BFS depth is generous).
        var bfsDepthCap = Math.Max(depthLimit * 32, 256);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (addr, depth) = queue.Dequeue();
            if (depth >= bfsDepthCap)
            {
                bfsCapHit = true;
                continue;
            }

            ClrObject obj;
            try
            {
                obj = runtime.Heap.GetObject(addr);
            }
            catch
            {
                continue;
            }

            if (obj.Type is null) continue;

            foreach (var child in obj.EnumerateReferences())
            {
                if (child.Address == 0 || !visited.Add(child.Address)) continue;
                retainer[child.Address] = (addr, null);
                queue.Enqueue((child.Address, depth + 1));
                if (remainingTargets.Remove(child.Address) && remainingTargets.Count == 0) return retainer;
                if (visited.Count >= maxRetainedGraphObjects)
                {
                    bfsCapHit = true;
                    return retainer;
                }
            }
        }

        return retainer;
    }

    public static List<RetentionFrame> BuildTypedRootChain(
        ClrRuntime runtime,
        ulong targetAddress,
        Dictionary<ulong, (ulong From, string? RootKind)> retainerMap,
        int depthLimit,
        out bool truncated)
    {
        var reversed = new List<RetentionFrame>(depthLimit + 2);
        var current = targetAddress;
        var visited = new HashSet<ulong>();
        truncated = false;

        for (var depth = 0; depth <= depthLimit; depth++)
        {
            if (!visited.Add(current))
            {
                truncated = true;
                break;
            }

            var obj = runtime.Heap.GetObject(current);
            reversed.Add(new RetentionFrame(obj.Type?.Name ?? "<unknown>", current));
            if (!retainerMap.TryGetValue(current, out var step))
            {
                break;
            }

            if (step.From == 0)
            {
                reversed.Add(new RetentionFrame("<root>", 0) { RootKind = step.RootKind ?? "Unknown" });
                reversed.Reverse();
                return reversed;
            }

            current = step.From;
        }

        truncated = true;
        reversed.Reverse();
        return reversed;
    }

    public static List<RetentionFrame> WalkUp(
        ClrObject instance,
        Dictionary<ulong, (ulong From, string? RootKind)> retainerMap,
        int depthLimit,
        out bool truncated)
    {
        var chain = new List<RetentionFrame>(depthLimit + 1);
        var current = instance.Address;
        var visited = new HashSet<ulong> { current };
        truncated = false;

        chain.Add(new RetentionFrame(instance.Type?.Name ?? "<unknown>", current));

        for (var i = 0; i < depthLimit; i++)
        {
            if (!retainerMap.TryGetValue(current, out var step)) break;
            if (step.From == 0)
            {
                chain.Add(new RetentionFrame("<root>", 0) { RootKind = step.RootKind ?? "Unknown" });
                return chain;
            }

            if (!visited.Add(step.From)) break;
            // We don't have the ClrObject in hand here; just record the address. Resolving
            // the type name requires another GetObject which we skip for cost — agent can
            // call back into the dump for the specific address if needed.
            chain.Add(new RetentionFrame("<retainer>", step.From));
            current = step.From;
        }

        truncated = chain.Count > depthLimit;
        return chain;
    }
}
