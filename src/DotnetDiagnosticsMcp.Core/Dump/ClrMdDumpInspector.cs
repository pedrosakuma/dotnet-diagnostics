using System.Globalization;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Dump;

/// <summary>
/// ClrMD-backed implementation of <see cref="IDumpInspector"/>. Walks the managed heap
/// of a <c>.dmp</c> file produced by <see cref="IProcessDumper"/> (or any compatible
/// dump source) and reports aggregated statistics + the <see cref="TypeIdentity"/>
/// handoff payload for <c>dotnet-assembly-mcp</c>.
/// </summary>
/// <remarks>
/// Inspection is metadata-only and read-only: the dump file is opened with
/// <c>DataTarget.LoadDump</c> and never mutated. MVIDs are read directly from the PE on
/// disk via <see cref="System.Reflection.Metadata.MetadataReader"/> rather than from
/// ClrMD's module signature, which is sometimes a PDB signature rather than the MVID.
/// </remarks>
public sealed class ClrMdDumpInspector : IDumpInspector
{
    private readonly ILogger<ClrMdDumpInspector> _logger;

    public ClrMdDumpInspector(ILogger<ClrMdDumpInspector>? logger = null)
    {
        _logger = logger ?? NullLogger<ClrMdDumpInspector>.Instance;
    }

    public Task<DumpInspection> InspectAsync(
        string dumpFilePath,
        DumpInspectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dumpFilePath);
        if (!File.Exists(dumpFilePath))
        {
            throw new FileNotFoundException("Dump file not found.", dumpFilePath);
        }

        var opts = options ?? new DumpInspectionOptions();
        if (opts.TopTypes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TopTypes must be positive.");
        }

        // ClrMD is fully synchronous; wrap in Task.Run so the caller's async context isn't
        // blocked on a multi-second heap walk for a large dump.
        return Task.Run(() => Inspect(dumpFilePath, opts, cancellationToken), cancellationToken);
    }

    private DumpInspection Inspect(string dumpFilePath, DumpInspectionOptions opts, CancellationToken ct)
    {
        var fileInfo = new FileInfo(dumpFilePath);
        var warnings = new List<string>();

        using var target = DataTarget.LoadDump(dumpFilePath);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException("Dump does not contain a CLR runtime.");
        using var runtime = clrInfo.CreateRuntime();

        var runtimeInfo = new DumpRuntimeInfo(
            Name: clrInfo.Flavor.ToString(),
            Version: clrInfo.Version.ToString(),
            Architecture: target.DataReader.Architecture.ToString(),
            IsServerGC: runtime.Heap.IsServer,
            HeapCount: runtime.Heap.SubHeaps.Length);

        if (!runtime.Heap.CanWalkHeap)
        {
            warnings.Add("Heap walk is unavailable for this dump (CanWalkHeap=false). " +
                "Capture a WithHeap or Full dump for full inspection.");
            return new DumpInspection(
                FilePath: dumpFilePath,
                FileSizeBytes: fileInfo.Length,
                Runtime: runtimeInfo,
                Heap: EmptyHeap(),
                TopTypesByBytes: Array.Empty<TypeStat>(),
                TopTypesByInstances: Array.Empty<TypeStat>(),
                RetentionPaths: null,
                Warnings: warnings);
        }

        var (typeStats, totalBytes) = WalkHeap(runtime, ct);

        var heapSummary = SummarizeHeap(runtime);

        var byBytes = typeStats.Values
            .OrderByDescending(s => s.Bytes)
            .Take(opts.TopTypes)
            .Select(s => ToTypeStat(s, totalBytes))
            .ToArray();

        var byInstances = typeStats.Values
            .OrderByDescending(s => s.Count)
            .Take(opts.TopTypes)
            .Select(s => ToTypeStat(s, totalBytes))
            .ToArray();

        IReadOnlyList<RetentionPath>? retention = null;
        if (opts.IncludeRetentionPaths)
        {
            retention = ResolveRetentionPaths(runtime, byBytes, opts.RetentionPathLimit, warnings, ct);
        }

        return new DumpInspection(
            FilePath: dumpFilePath,
            FileSizeBytes: fileInfo.Length,
            Runtime: runtimeInfo,
            Heap: heapSummary,
            TopTypesByBytes: byBytes,
            TopTypesByInstances: byInstances,
            RetentionPaths: retention,
            Warnings: warnings.Count > 0 ? warnings : null);
    }

    private static (Dictionary<TypeKey, RawTypeStat> Stats, long TotalBytes) WalkHeap(ClrRuntime runtime, CancellationToken ct)
    {
        var stats = new Dictionary<TypeKey, RawTypeStat>();
        long total = 0;
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            if (obj.Type is null) continue;
            var size = (long)obj.Size;
            total += size;

            var key = new TypeKey(obj.Type.Name ?? "<unknown>", obj.Type.Module?.Name);
            if (!stats.TryGetValue(key, out var s))
            {
                s = new RawTypeStat(key.TypeName, key.ModuleName, obj.Type);
                stats[key] = s;
            }
            s.Count++;
            s.Bytes += size;
        }
        return (stats, total);
    }

    private TypeStat ToTypeStat(RawTypeStat raw, long totalBytes)
    {
        var pct = totalBytes > 0 ? Math.Round(100.0 * raw.Bytes / totalBytes, 2) : 0.0;
        return new TypeStat(
            TypeFullName: raw.TypeName,
            ModuleName: raw.ModuleName is { } mn ? Path.GetFileName(mn) : null,
            InstanceCount: raw.Count,
            TotalBytes: raw.Bytes,
            TotalBytesPercent: pct,
            Identity: BuildTypeIdentity(raw));
    }

    private TypeIdentity? BuildTypeIdentity(RawTypeStat raw)
    {
        var clrType = raw.ClrType;
        if (clrType is null) return null;

        var modulePath = clrType.Module?.Name;
        var moduleFileName = !string.IsNullOrEmpty(modulePath) ? Path.GetFileName(modulePath) : null;
        var token = (int)clrType.MetadataToken;
        var mvid = TryReadMvid(modulePath);

        if (mvid is null && token == 0 && string.IsNullOrEmpty(modulePath) && string.IsNullOrEmpty(moduleFileName))
        {
            return null;
        }

        return new TypeIdentity(
            ModuleName: moduleFileName,
            ModulePath: modulePath,
            ModuleVersionId: mvid,
            MetadataToken: token > 0 ? token : null,
            TypeFullName: raw.TypeName);
    }

    private Guid? TryReadMvid(string? assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath)) return null;
        if (!File.Exists(assemblyPath)) return null;
        if (_mvidCache.TryGetValue(assemblyPath, out var cached)) return cached;
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                _mvidCache[assemblyPath] = null;
                return null;
            }
            var metadata = peReader.GetMetadataReader();
            var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            _mvidCache[assemblyPath] = mvid;
            return mvid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MVID read failed for {Path}", assemblyPath);
            _mvidCache[assemblyPath] = null;
            return null;
        }
    }

    private readonly Dictionary<string, Guid?> _mvidCache = new(StringComparer.Ordinal);

    private static DumpHeapSummary SummarizeHeap(ClrRuntime runtime)
    {
        long total = 0, gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poh = 0, committed = 0;
        foreach (var segment in runtime.Heap.Segments)
        {
            var len = (long)segment.Length;
            total += len;
            committed += (long)(segment.CommittedMemory.End - segment.CommittedMemory.Start);
            switch (segment.Kind)
            {
                case GCSegmentKind.Generation0: gen0 += len; break;
                case GCSegmentKind.Generation1: gen1 += len; break;
                case GCSegmentKind.Generation2: gen2 += len; break;
                case GCSegmentKind.Large: loh += len; break;
                case GCSegmentKind.Pinned: poh += len; break;
                case GCSegmentKind.Ephemeral:
                    // Workstation GC keeps gen0/gen1 in a single segment whose bytes are
                    // reported per generation by Length-of-region rather than per-segment.
                    // Bucket into gen0 — close enough for the LLM-facing summary.
                    gen0 += len;
                    break;
            }
        }
        return new DumpHeapSummary(total, gen0, gen1, gen2, loh, poh, committed);
    }

    private static DumpHeapSummary EmptyHeap() => new(0, 0, 0, 0, 0, 0, 0);

    private static IReadOnlyList<RetentionPath> ResolveRetentionPaths(
        ClrRuntime runtime,
        IReadOnlyList<TypeStat> topByBytes,
        int depthLimit,
        List<string> warnings,
        CancellationToken ct)
    {
        // Build a reverse map: object → first retainer found during a single roots/refs walk.
        // For each target type we then pick the largest instance and walk back to a root.
        // This is approximate (a real !gcroot does a full search) but cheap and "good enough"
        // to point the LLM at where to dig deeper.
        var targets = new HashSet<string>(topByBytes.Take(5).Select(t => t.TypeFullName), StringComparer.Ordinal);
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

        var rootByObject = BuildRootByObjectMap(runtime, sampleInstances.Values, depthLimit, ct);

        var results = new List<RetentionPath>(sampleInstances.Count);
        foreach (var (typeName, instance) in sampleInstances)
        {
            ct.ThrowIfCancellationRequested();
            var chain = WalkUp(instance, rootByObject, depthLimit, out var truncated);
            results.Add(new RetentionPath(
                TargetTypeFullName: typeName,
                TargetObjectAddress: instance.Address,
                Chain: chain,
                Truncated: truncated));
        }
        return results;
    }

    private static Dictionary<ulong, (ulong From, string? RootKind)> BuildRootByObjectMap(
        ClrRuntime runtime,
        IEnumerable<ClrObject> _,
        int depthLimit,
        CancellationToken ct)
    {
        // Map each reachable object to its first-seen retainer (object address or root).
        var retainer = new Dictionary<ulong, (ulong From, string? RootKind)>();
        var visited = new HashSet<ulong>();
        var queue = new Queue<(ulong Address, int Depth)>();

        foreach (var root in runtime.Heap.EnumerateRoots())
        {
            ct.ThrowIfCancellationRequested();
            var addr = root.Object.Address;
            if (addr == 0 || visited.Contains(addr)) continue;
            visited.Add(addr);
            retainer[addr] = (0UL, root.RootKind.ToString());
            queue.Enqueue((addr, 0));
        }

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (addr, depth) = queue.Dequeue();
            if (depth >= depthLimit * 8) continue; // safety cap on the BFS itself
            ClrObject obj;
            try { obj = runtime.Heap.GetObject(addr); }
            catch { continue; }
            if (obj.Type is null) continue;

            foreach (var child in obj.EnumerateReferences())
            {
                if (child.Address == 0 || !visited.Add(child.Address)) continue;
                retainer[child.Address] = (addr, null);
                queue.Enqueue((child.Address, depth + 1));
            }
        }
        return retainer;
    }

    private static List<RetentionFrame> WalkUp(
        ClrObject instance,
        Dictionary<ulong, (ulong From, string? RootKind)> retainerMap,
        int depthLimit,
        out bool truncated)
    {
        var chain = new List<RetentionFrame>(depthLimit + 1);
        var current = instance.Address;
        var visited = new HashSet<ulong> { current };
        truncated = false;

        chain.Add(new RetentionFrame(instance.Type?.Name ?? "<unknown>", current, null));

        for (var i = 0; i < depthLimit; i++)
        {
            if (!retainerMap.TryGetValue(current, out var step)) break;
            if (step.From == 0)
            {
                chain.Add(new RetentionFrame("<root>", 0, step.RootKind ?? "Unknown"));
                return chain;
            }
            if (!visited.Add(step.From)) break;
            // We don't have the ClrObject in hand here; just record the address. Resolving
            // the type name requires another GetObject which we skip for cost — agent can
            // call back into the dump for the specific address if needed.
            chain.Add(new RetentionFrame("<retainer>", step.From, null));
            current = step.From;
        }

        truncated = chain.Count > depthLimit;
        return chain;
    }

    private readonly record struct TypeKey(string TypeName, string? ModuleName);

    private sealed class RawTypeStat
    {
        public RawTypeStat(string typeName, string? moduleName, ClrType clrType)
        {
            TypeName = typeName;
            ModuleName = moduleName;
            ClrType = clrType;
        }
        public string TypeName { get; }
        public string? ModuleName { get; }
        public ClrType ClrType { get; }
        public long Count;
        public long Bytes;
    }
}
