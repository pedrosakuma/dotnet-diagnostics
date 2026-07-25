using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Dump;

internal static class ClrMdRetentionAnalyzer
{
    public static IReadOnlyList<RetentionPath> ResolveRetentionPaths(
        ClrRuntime runtime,
        IReadOnlyList<TypeStat> topByBytes,
        int depthLimit,
        int targetCount,
        Func<ClrType?, TypeIdentity?> buildTypeIdentity,
        List<string> warnings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(buildTypeIdentity);

        // Build a reverse map: object → first retainer found during a single roots/refs walk.
        // For each target type we then pick the largest instance and walk back to a root.
        // This is approximate (a real !gcroot does a full search) but cheap and "good enough"
        // to point the LLM at where to dig deeper.
        var targets = topByBytes
            .Take(targetCount)
            .Select(stat => stat.Identity ?? new TypeIdentity(stat.TypeFullName) { ModuleName = stat.ModuleName })
            .ToArray();
        if (targets.Length == 0) return Array.Empty<RetentionPath>();
        var sameNameCounts = targets
            .Select(target => targets.Count(candidate =>
                string.Equals(candidate.TypeFullName, target.TypeFullName, StringComparison.Ordinal)))
            .ToArray();
        var targetNames = targets
            .Select(target => target.TypeFullName)
            .ToHashSet(StringComparer.Ordinal);

        var samples = new ClrObject?[targets.Length];
        var identityByType = new Dictionary<ClrType, TypeIdentity>();
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            var clrType = obj.Type;
            var typeName = clrType?.Name;
            if (clrType is null || typeName is null || !targetNames.Contains(typeName)) continue;

            if (!identityByType.TryGetValue(clrType, out var observedIdentity))
            {
                observedIdentity = buildTypeIdentity(clrType)
                    ?? new TypeIdentity(typeName)
                    {
                        ModuleName = clrType.Module?.Name is { } modulePath ? Path.GetFileName(modulePath) : null,
                        ModulePath = clrType.Module?.Name,
                    };
                identityByType[clrType] = observedIdentity;
            }

            for (var index = 0; index < targets.Length; index++)
            {
                if (!MatchesTarget(targets[index], observedIdentity, sameNameCounts[index]))
                {
                    continue;
                }

                if (samples[index] is not { } existing || existing.Size < obj.Size)
                {
                    samples[index] = obj;
                }
            }
        }

        var targetAddresses = new HashSet<ulong>(samples.Where(sample => sample.HasValue).Select(sample => sample!.Value.Address));
        var ambiguousNames = targets
            .Where((target, index) => samples[index] is null && sameNameCounts[index] > 1 && !HasModuleIdentity(target))
            .Select(target => target.TypeFullName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (ambiguousNames.Length > 0)
        {
            warnings.Add(
                $"Retention paths were omitted for same-named types without a unique MVID/token/module identity: {string.Join(", ", ambiguousNames)}.");
        }
        if (targetAddresses.Count == 0)
        {
            return Array.Empty<RetentionPath>();
        }

        var rootByObject = BuildRootByObjectMap(runtime, targetAddresses, depthLimit, maxRetainedGraphObjects: 250_000, out var bfsCapHit, ct);
        if (bfsCapHit)
        {
            warnings.Add($"Retention-path BFS hit its safety cap before reaching every target type; deeply-retained instances may report Truncated=true with no chain found.");
        }

        var results = new List<RetentionPath>(samples.Length);
        for (var index = 0; index < samples.Length; index++)
        {
            if (samples[index] is not { } instance)
            {
                continue;
            }

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
                TargetTypeFullName: targets[index].TypeFullName,
                TargetObjectAddress: instance.Address,
                Chain: chain,
                Truncated: truncated)
            {
                TargetIdentity = targets[index],
            });
        }

        return results;
    }

    internal static bool MatchesTarget(TypeIdentity target, TypeIdentity observed, int sameNameCount)
    {
        if (!string.Equals(target.TypeFullName, observed.TypeFullName, StringComparison.Ordinal))
        {
            return false;
        }

        if (target.ModuleVersionId is { } moduleVersionId)
        {
            return observed.ModuleVersionId == moduleVersionId &&
                   (target.MetadataToken is not { } token || observed.MetadataToken == token);
        }

        if (!string.IsNullOrWhiteSpace(target.ModulePath))
        {
            return PathEquals(target.ModulePath, observed.ModulePath) &&
                   (target.MetadataToken is not { } token || observed.MetadataToken == token);
        }

        if (!string.IsNullOrWhiteSpace(target.ModuleName))
        {
            return PathEquals(target.ModuleName, observed.ModuleName) &&
                   (target.MetadataToken is not { } token || observed.MetadataToken == token);
        }

        return sameNameCount == 1;
    }

    private static bool PathEquals(string left, string? right)
        => string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool HasModuleIdentity(TypeIdentity identity)
        => identity.ModuleVersionId is not null ||
           !string.IsNullOrWhiteSpace(identity.ModulePath) ||
           !string.IsNullOrWhiteSpace(identity.ModuleName);

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
        => BuildRetentionChain(
            instance.Type?.Name ?? "<unknown>",
            instance.Address,
            retainerMap,
            depthLimit,
            address => ResolveTypeName(instance.Type?.Heap, address),
            out truncated);

    internal static List<RetentionFrame> BuildRetentionChain(
        string targetTypeFullName,
        ulong targetAddress,
        IReadOnlyDictionary<ulong, (ulong From, string? RootKind)> retainerMap,
        int depthLimit,
        Func<ulong, string?> resolveTypeName,
        out bool truncated)
    {
        var chain = new List<RetentionFrame>(depthLimit + 1);
        var current = targetAddress;
        var visited = new HashSet<ulong> { current };
        truncated = false;

        chain.Add(new RetentionFrame(targetTypeFullName, current));

        for (var i = 0; i < depthLimit; i++)
        {
            if (!retainerMap.TryGetValue(current, out var step)) break;
            if (step.From == 0)
            {
                chain.Add(new RetentionFrame("<root>", 0) { RootKind = step.RootKind ?? "Unknown" });
                return chain;
            }

            if (!visited.Add(step.From)) break;
            chain.Add(new RetentionFrame(resolveTypeName(step.From) ?? "<unknown>", step.From));
            current = step.From;
        }

        truncated = retainerMap.ContainsKey(current);
        return chain;
    }

    private static string? ResolveTypeName(ClrHeap? heap, ulong address)
    {
        if (heap is null) return null;
        try
        {
            return heap.GetObject(address).Type?.Name;
        }
        catch
        {
            return null;
        }
    }
}
