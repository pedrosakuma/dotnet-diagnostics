using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Dump;

internal static class ClrMdAssemblyLoadContextAnalyzer
{
    private const int MaxAssemblyLoadContexts = 1024;
    private const int MaxAssembliesPerAssemblyLoadContext = 128;
    private const int AssemblyLoadContextRetentionHintLimit = 16;
    private const int GcRootDepthLimit = 64;
    private const int MaxRetainedGraphObjects = 250_000;
    private const int MaxStringPreviewLength = 256;

    public static void Aggregate(ClrObject obj, long objSize, RawAssemblyLoadContextAggregation sink)
    {
        var type = obj.Type;
        if (type is null)
        {
            return;
        }

        var alcAddress = type.AssemblyLoadContextAddress;
        if (alcAddress != 0)
        {
            var stat = sink.GetOrAdd(alcAddress);
            if (stat.Address == 0)
            {
                return;
            }

            stat.LiveObjectCount++;
            stat.LiveBytes += objSize;
            stat.NoteCollectible(type.IsCollectible);
            if (type.LoaderAllocatorHandle != 0)
            {
                stat.LoaderAllocatorHandle ??= type.LoaderAllocatorHandle;
            }

            AddAssemblyLoadContextAssembly(stat, type.Module, objSize, countLiveObject: true);

            if (type.IsCollectible && obj.Address != alcAddress && stat.SampleObjectAddress is null)
            {
                stat.SampleObjectAddress = obj.Address;
                stat.SampleObjectTypeFullName = type.Name;
            }
        }

        if (!IsAssemblyLoadContextType(type))
        {
            return;
        }

        var context = sink.GetOrAdd(obj.Address);
        if (context.Address == 0)
        {
            return;
        }

        context.TypeFullName = type.Name ?? "<unknown>";
        context.DirectSizeBytes = objSize;
        context.Name ??= TryReadStringField(obj, "_name", "m_name");
        var fieldCollectible = ClrMdHeapObjectReader.TryReadBoolField(obj, "_isCollectible", "m_isCollectible");
        if (fieldCollectible.HasValue)
        {
            context.IsCollectible = fieldCollectible.Value;
            context.CollectibleResolvedFromField = true;
        }

        context.IsDefault = IsDefaultAssemblyLoadContext(context.Name, context.IsCollectible);
    }

    public static AssemblyLoadContextLeakView BuildView(
        ClrRuntime runtime,
        RawAssemblyLoadContextAggregation agg,
        List<string> warnings,
        CancellationToken ct)
    {
        EnrichAssemblyLoadContextsFromModules(runtime, agg, warnings, ct);

        var notes = new List<string>
        {
            $"Retention hints are computed for at most {AssemblyLoadContextRetentionHintLimit} collectible AssemblyLoadContext(s) per snapshot using the bounded GC-root search.",
        };

        if (agg.ContextCapHit)
        {
            notes.Add($"AssemblyLoadContext enumeration hit its cap of {MaxAssemblyLoadContexts:N0} contexts; additional contexts were omitted.");
        }

        var candidates = agg.Contexts.Values
            .Where(c => c.Address != 0)
            .OrderByDescending(c => c.IsCollectible == true)
            .ThenByDescending(c => c.LiveBytes)
            .ThenBy(c => c.Name ?? c.TypeFullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var context in candidates)
        {
            if (context.IsCollectible is null)
            {
                notes.Add($"Could not resolve collectible state for AssemblyLoadContext 0x{context.Address:x}; runtime field names may have changed.");
            }
        }

        var retentionByContext = ResolveRetention(runtime, candidates, notes, ct);

        var rows = candidates
            .Select(context =>
            {
                retentionByContext.TryGetValue(context.Address, out var retention);
                var assemblies = context.Assemblies.Values
                    .OrderByDescending(a => a.LiveBytes)
                    .ThenByDescending(a => a.LiveObjectCount)
                    .ThenBy(a => a.Key.AssemblyName, StringComparer.Ordinal)
                    .Take(MaxAssembliesPerAssemblyLoadContext)
                    .Select(a => new AssemblyLoadContextAssemblyStat(
                        AssemblyName: a.Key.AssemblyName,
                        ModuleName: a.Key.ModulePath is { } mp ? Path.GetFileName(mp) : null,
                        ModulePath: a.Key.ModulePath,
                        LiveObjectCount: a.LiveObjectCount,
                        LiveBytes: a.LiveBytes))
                    .ToArray();

                var rowNotes = new List<string>();
                if (context.Assemblies.Count > assemblies.Length)
                {
                    rowNotes.Add($"Assembly list truncated to the first {MaxAssembliesPerAssemblyLoadContext:N0} modules by live-object bytes.");
                }

                if (context.IsCollectible is null)
                {
                    rowNotes.Add("Collectible state could not be read from the ALC object or inferred from loaded collectible types.");
                }

                return new AssemblyLoadContextStat(
                    Address: context.Address,
                    TypeFullName: context.TypeFullName,
                    Name: context.Name,
                    IsCollectible: context.IsCollectible,
                    IsDefault: context.IsDefault,
                    AssemblyCount: context.Assemblies.Count,
                    Assemblies: assemblies)
                {
                    LoaderAllocatorHandle = context.LoaderAllocatorHandle,
                    LiveObjectCount = context.LiveObjectCount,
                    LiveBytes = context.LiveBytes,
                    SuspectedLeak = context.IsCollectible == true,
                    RetentionTargetKind = retention?.TargetKind,
                    RetentionTargetAddress = retention?.Path.TargetObjectAddress,
                    RetentionTargetTypeFullName = retention?.Path.TargetTypeFullName,
                    RetentionPath = retention?.Path,
                    Notes = rowNotes.Count > 0 ? rowNotes : null,
                };
            })
            .ToArray();

        var collectibleCount = rows.LongCount(r => r.IsCollectible == true);
        var suspectedCount = rows.LongCount(r => r.SuspectedLeak);
        return new AssemblyLoadContextLeakView(
            TotalContexts: rows.LongLength,
            CollectibleContexts: collectibleCount,
            SuspectedLeakedCollectibleContexts: suspectedCount,
            Contexts: rows,
            Notes: notes);
    }

    private static Dictionary<ulong, RawAssemblyLoadContextRetention> ResolveRetention(
        ClrRuntime runtime,
        IReadOnlyList<RawAssemblyLoadContextStat> contexts,
        List<string> notes,
        CancellationToken ct)
    {
        var selected = contexts
            .Where(c => c.IsCollectible == true)
            .OrderByDescending(c => c.LiveBytes)
            .Take(AssemblyLoadContextRetentionHintLimit)
            .ToArray();

        if (selected.Length == 0)
        {
            return new Dictionary<ulong, RawAssemblyLoadContextRetention>();
        }

        var targets = new HashSet<ulong>();
        var targetByContext = new Dictionary<ulong, (ulong Address, string TypeFullName, string Kind)>();
        foreach (var context in selected)
        {
            var targetAddress = context.SampleObjectAddress ?? context.Address;
            var targetType = context.SampleObjectTypeFullName ?? context.TypeFullName;
            var targetKind = context.SampleObjectAddress.HasValue ? "sample-object-from-alc" : "assembly-load-context";
            if (targetAddress == 0)
            {
                continue;
            }

            targets.Add(targetAddress);
            targetByContext[context.Address] = (targetAddress, targetType ?? "<unknown>", targetKind);
        }

        if (targets.Count == 0)
        {
            return new Dictionary<ulong, RawAssemblyLoadContextRetention>();
        }

        var rootByObject = ClrMdRetentionAnalyzer.BuildRootByObjectMap(runtime, targets, GcRootDepthLimit, MaxRetainedGraphObjects, out var bfsCapHit, ct);
        if (bfsCapHit)
        {
            notes.Add("AssemblyLoadContext retention BFS hit its safety cap; some retention hints may be truncated.");
        }

        var result = new Dictionary<ulong, RawAssemblyLoadContextRetention>();
        foreach (var context in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (!targetByContext.TryGetValue(context.Address, out var target))
            {
                continue;
            }

            try
            {
                var reachedByBfs = rootByObject.ContainsKey(target.Address);
                var chain = ClrMdRetentionAnalyzer.BuildTypedRootChain(runtime, target.Address, rootByObject, GcRootDepthLimit, out var truncated);
                if (!reachedByBfs)
                {
                    truncated = true;
                }

                var path = new RetentionPath(
                    TargetTypeFullName: target.TypeFullName,
                    TargetObjectAddress: target.Address,
                    Chain: chain,
                    Truncated: truncated || bfsCapHit);
                result[context.Address] = new RawAssemblyLoadContextRetention(target.Kind, path);
            }
            catch (Exception ex)
            {
                notes.Add($"AssemblyLoadContext retention hint failed for 0x{context.Address:x}: {ex.GetType().Name} ({ex.Message}).");
            }
        }

        if (contexts.Count(c => c.IsCollectible == true) > selected.Length)
        {
            notes.Add($"Retention hints were skipped for {contexts.Count(c => c.IsCollectible == true) - selected.Length:N0} additional collectible AssemblyLoadContext(s) beyond the per-snapshot cap.");
        }

        return result;
    }

    private static void EnrichAssemblyLoadContextsFromModules(
        ClrRuntime runtime,
        RawAssemblyLoadContextAggregation agg,
        List<string> warnings,
        CancellationToken ct)
    {
        try
        {
            foreach (var module in runtime.EnumerateModules())
            {
                ct.ThrowIfCancellationRequested();
                ClrType? representative = null;
                try
                {
                    foreach (var (methodTable, _) in module.EnumerateTypeDefToMethodTableMap())
                    {
                        if (methodTable == 0)
                        {
                            continue;
                        }

                        representative = runtime.GetTypeByMethodTable(methodTable);
                        if (representative?.AssemblyLoadContextAddress != 0)
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    continue;
                }

                if (representative?.AssemblyLoadContextAddress is not > 0)
                {
                    continue;
                }

                var context = agg.GetOrAdd(representative.AssemblyLoadContextAddress);
                if (context.Address == 0)
                {
                    continue;
                }

                context.NoteCollectible(representative.IsCollectible);
                if (representative.LoaderAllocatorHandle != 0)
                {
                    context.LoaderAllocatorHandle ??= representative.LoaderAllocatorHandle;
                }

                AddAssemblyLoadContextAssembly(context, module, objSize: 0, countLiveObject: false);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"AssemblyLoadContext module enrichment aborted partway through: {ex.GetType().Name} ({ex.Message}).");
        }
    }

    private static bool IsAssemblyLoadContextType(ClrType type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, "System.Runtime.Loader.AssemblyLoadContext", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDefaultAssemblyLoadContext(string? name, bool? isCollectible)
        => isCollectible == false && string.Equals(name, "Default", StringComparison.Ordinal);

    private static void AddAssemblyLoadContextAssembly(RawAssemblyLoadContextStat context, ClrModule? module, long objSize, bool countLiveObject)
    {
        if (module is null)
        {
            return;
        }

        var assemblyName = string.IsNullOrWhiteSpace(module.AssemblyName)
            ? Path.GetFileNameWithoutExtension(module.Name) ?? "<unknown>"
            : module.AssemblyName;
        var key = new AssemblyLoadContextAssemblyKey(assemblyName, module.Name);
        if (!context.Assemblies.TryGetValue(key, out var assembly))
        {
            assembly = new RawAssemblyLoadContextAssemblyStat(key);
            context.Assemblies[key] = assembly;
        }

        if (countLiveObject)
        {
            assembly.LiveObjectCount++;
            assembly.LiveBytes += objSize;
        }
    }

    private static string? TryReadStringField(ClrObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var field = ClrMdHeapObjectReader.FindFieldByName(obj.Type, name);
            if (field is null || !field.IsObjectReference)
            {
                continue;
            }

            try
            {
                var value = field.ReadObject(obj.Address, interior: false);
                if (value.IsNull || !value.IsValid || value.Type is null || !value.Type.IsString)
                {
                    continue;
                }

                return value.AsString(MaxStringPreviewLength);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    internal sealed class RawAssemblyLoadContextAggregation
    {
        internal Dictionary<ulong, RawAssemblyLoadContextStat> Contexts { get; } = new();
        public bool ContextCapHit { get; private set; }

        public RawAssemblyLoadContextStat GetOrAdd(ulong address)
        {
            if (Contexts.TryGetValue(address, out var context))
            {
                return context;
            }

            if (Contexts.Count >= MaxAssemblyLoadContexts)
            {
                ContextCapHit = true;
                return RawAssemblyLoadContextStat.Ignored;
            }

            context = new RawAssemblyLoadContextStat(address);
            Contexts[address] = context;
            return context;
        }
    }

    internal sealed class RawAssemblyLoadContextStat
    {
        public static RawAssemblyLoadContextStat Ignored { get; } = new(0);

        public RawAssemblyLoadContextStat(ulong address)
        {
            Address = address;
        }

        public ulong Address { get; }
        public string TypeFullName { get; set; } = "System.Runtime.Loader.AssemblyLoadContext";
        public string? Name { get; set; }
        public bool? IsCollectible { get; set; }
        public bool CollectibleResolvedFromField { get; set; }
        public bool IsDefault { get; set; }
        public ulong? LoaderAllocatorHandle { get; set; }
        public ulong? SampleObjectAddress { get; set; }
        public string? SampleObjectTypeFullName { get; set; }
        public long DirectSizeBytes { get; set; }
        public long LiveObjectCount { get; set; }
        public long LiveBytes { get; set; }
        internal Dictionary<AssemblyLoadContextAssemblyKey, RawAssemblyLoadContextAssemblyStat> Assemblies { get; } = new();

        public void NoteCollectible(bool isCollectible)
        {
            if (!CollectibleResolvedFromField || IsCollectible is null)
            {
                IsCollectible = isCollectible;
            }
        }
    }

    internal readonly record struct AssemblyLoadContextAssemblyKey(string AssemblyName, string? ModulePath);

    internal sealed class RawAssemblyLoadContextAssemblyStat
    {
        public RawAssemblyLoadContextAssemblyStat(AssemblyLoadContextAssemblyKey key)
        {
            Key = key;
        }

        public AssemblyLoadContextAssemblyKey Key { get; }
        public long LiveObjectCount;
        public long LiveBytes;
    }

    private sealed record RawAssemblyLoadContextRetention(string TargetKind, RetentionPath Path);
}
