using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Dump;

internal static class ClrMdHeapWalker
{
    public static HeapWalkResult Walk(
        ClrRuntime runtime,
        DumpInspectionOptions opts,
        List<string> warnings,
        Func<ClrType?, TypeIdentity?> buildTypeIdentity,
        Func<string?, Guid?> tryReadMvid,
        CancellationToken ct)
    {
        var stats = new Dictionary<TypeKey, RawTypeStat>();
        long totalBytes = 0;
        var segmentStats = new List<SegmentStat>(runtime.Heap.Segments.Length);
        Dictionary<DelegateKey, RawDelegateStat>? delegates = opts.IncludeDelegateTargets ? new() : null;
        Dictionary<string, RawStringStat>? strings = opts.IncludeDuplicateStrings ? new(StringComparer.Ordinal) : null;
        var taskTimers = new ClrMdTaskTimerAnalyzer.RawTaskTimerAggregation();
        var assemblyLoadContexts = new ClrMdAssemblyLoadContextAnalyzer.RawAssemblyLoadContextAggregation();
        var delegateCap = opts.IncludeDelegateTargets ? Math.Max(opts.SnapshotDelegateTargetTopN * 32, 4096) : 0;
        var stringCap = opts.IncludeDuplicateStrings ? Math.Max(opts.SnapshotDuplicateStringTopN * 32, 4096) : 0;
        var stringObjectScanCap = opts.IncludeDuplicateStrings ? Math.Max(stringCap * 64L, 1_000_000L) : 0L;
        long stringObjectsScanned = 0;
        var delegateCapHit = false;
        var stringCapHit = false;
        var stringObjectCapHit = false;

        foreach (var segment in runtime.Heap.Segments)
        {
            ct.ThrowIfCancellationRequested();
            long segUsed = 0, segFree = 0, segObjs = 0, segFreeObjs = 0;

            foreach (var obj in segment.EnumerateObjects())
            {
                ct.ThrowIfCancellationRequested();
                if (obj.Type is null) continue;
                var size = (long)obj.Size;
                segObjs++;

                if (obj.IsFree)
                {
                    segFree += size;
                    segFreeObjs++;
                    continue;
                }

                segUsed += size;
                totalBytes += size;

                var key = new TypeKey(obj.Type.Name ?? "<unknown>", obj.Type.Module?.Name);
                if (!stats.TryGetValue(key, out var stat))
                {
                    stat = new RawTypeStat(key.TypeName, key.ModuleName, obj.Type);
                    stats[key] = stat;
                }

                stat.Count++;
                stat.Bytes += size;

                if (delegates is not null && obj.IsDelegate && !delegateCapHit)
                {
                    AggregateDelegate(obj, delegates);
                    if (delegates.Count > delegateCap)
                    {
                        delegateCapHit = true;
                    }
                }

                if (strings is not null && obj.Type.IsString && !stringCapHit && !stringObjectCapHit)
                {
                    AggregateString(obj, size, strings);
                    stringObjectsScanned++;
                    if (strings.Count > stringCap)
                    {
                        stringCapHit = true;
                    }

                    if (stringObjectsScanned > stringObjectScanCap)
                    {
                        stringObjectCapHit = true;
                    }
                }

                ClrMdTaskTimerAnalyzer.Aggregate(obj, size, taskTimers);
                ClrMdAssemblyLoadContextAnalyzer.Aggregate(obj, size, assemblyLoadContexts);
            }

            var length = (long)segment.Length;
            var committed = (long)(segment.CommittedMemory.End - segment.CommittedMemory.Start);
            var reserved = (long)(segment.ReservedMemory.End - segment.ReservedMemory.Start);
            var freePct = length > 0 ? Math.Round(100.0 * segFree / length, 2) : 0.0;

            segmentStats.Add(new SegmentStat(
                LogicalHeap: segment.SubHeap.Index,
                Kind: segment.Kind.ToString(),
                Generation: ClassifySegmentGeneration(segment),
                Start: segment.Start,
                End: segment.End,
                Length: length,
                CommittedBytes: committed,
                ReservedBytes: reserved,
                UsedBytes: segUsed,
                FreeBytes: segFree,
                ObjectCount: segObjs - segFreeObjs,
                FreeObjectCount: segFreeObjs)
            {
                FreePercent = freePct,
            });
        }

        if (delegateCapHit)
        {
            warnings.Add($"Delegate-target aggregation hit cap of {delegateCap} unique entries — results are truncated to the busiest entries seen so far.");
        }

        if (stringCapHit)
        {
            warnings.Add($"Duplicate-string aggregation hit cap of {stringCap} unique entries — results are truncated.");
        }

        if (stringObjectCapHit)
        {
            warnings.Add($"Duplicate-string aggregation hit object-scan cap of {stringObjectScanCap:N0} string instances — results reflect only the strings encountered before the cap.");
        }

        var snapshotTopN = Math.Max(opts.TopTypes, opts.SnapshotTopTypes);
        var byBytes = stats.Values
            .OrderByDescending(s => s.Bytes)
            .Take(snapshotTopN)
            .Select(s => ToTypeStat(s, totalBytes, buildTypeIdentity))
            .ToArray();

        var byInstances = stats.Values
            .OrderByDescending(s => s.Count)
            .Take(snapshotTopN)
            .Select(s => ToTypeStat(s, totalBytes, buildTypeIdentity))
            .ToArray();

        return new HeapWalkResult(
            byBytes,
            byInstances,
            segmentStats,
            delegates is null ? null : BuildDelegateStats(delegates, opts.SnapshotDelegateTargetTopN, tryReadMvid),
            strings is null ? null : BuildDuplicateStringStats(strings, opts.SnapshotDuplicateStringTopN, opts.DuplicateStringPreviewLength),
            taskTimers,
            assemblyLoadContexts);
    }

    private static string ClassifySegmentGeneration(ClrSegment segment) => segment.Kind switch
    {
        GCSegmentKind.Generation0 => "Gen0",
        GCSegmentKind.Generation1 => "Gen1",
        GCSegmentKind.Generation2 => "Gen2",
        GCSegmentKind.Large => "LOH",
        GCSegmentKind.Pinned => "POH",
        GCSegmentKind.Ephemeral => "Ephemeral",
        GCSegmentKind.Frozen => "Frozen",
        _ => segment.Kind.ToString(),
    };

    private static TypeStat ToTypeStat(RawTypeStat raw, long totalBytes, Func<ClrType?, TypeIdentity?> buildTypeIdentity)
    {
        var pct = totalBytes > 0 ? Math.Round(100.0 * raw.Bytes / totalBytes, 2) : 0.0;
        return new TypeStat(
            TypeFullName: raw.TypeName,
            ModuleName: raw.ModuleName is { } mn ? Path.GetFileName(mn) : null,
            InstanceCount: raw.Count,
            TotalBytes: raw.Bytes,
            TotalBytesPercent: pct,
            Identity: buildTypeIdentity(raw.ClrType));
    }

    private static void AggregateDelegate(ClrObject obj, Dictionary<DelegateKey, RawDelegateStat> sink)
    {
        try
        {
            var del = obj.AsDelegate();
            foreach (var target in del.EnumerateDelegateTargets())
            {
                var method = target.Method;
                if (method is null) continue;
                var declaring = method.Type?.Name ?? "<unknown>";
                var targetObj = target.TargetObject;
                var targetType = targetObj.IsNull ? null : targetObj.Type?.Name;
                var modulePath = method.Type?.Module?.Name;
                var key = new DelegateKey(targetType, declaring, method.Name ?? "<unknown>", method.Signature, modulePath, (int)method.MetadataToken);
                if (!sink.TryGetValue(key, out var entry))
                {
                    entry = new RawDelegateStat(key, method, targetObj.IsNull);
                    sink[key] = entry;
                }

                entry.SubscriberCount++;
            }
        }
        catch
        {
        }
    }

    private static DelegateTargetStat[] BuildDelegateStats(
        Dictionary<DelegateKey, RawDelegateStat> agg,
        int topN,
        Func<string?, Guid?> tryReadMvid)
        => agg.Values
            .OrderByDescending(d => d.SubscriberCount)
            .Take(topN)
            .Select(d =>
            {
                var moduleFile = d.Key.ModulePath is { } mp ? Path.GetFileName(mp) : null;
                Memory.MethodIdentity? method = null;
                if (d.Method is not null && d.Key.MetadataToken != 0)
                {
                    method = new Memory.MethodIdentity(
                        ModuleName: moduleFile,
                        ModulePath: d.Key.ModulePath,
                        ModuleVersionId: tryReadMvid(d.Key.ModulePath),
                        MetadataToken: d.Key.MetadataToken,
                        TypeFullName: d.Key.DeclaringTypeFullName,
                        MethodName: d.Key.MethodName,
                        GenericArity: 0);
                }

                return new DelegateTargetStat(
                    TargetTypeFullName: d.Key.TargetTypeFullName,
                    DeclaringTypeFullName: d.Key.DeclaringTypeFullName,
                    MethodName: d.Key.MethodName,
                    MethodSignature: d.Key.MethodSignature,
                    ModuleName: moduleFile,
                    SubscriberCount: d.SubscriberCount)
                {
                    Method = method,
                    IsStaticTarget = d.IsStaticTarget,
                };
            })
            .ToArray();

    private static void AggregateString(ClrObject obj, long objSize, Dictionary<string, RawStringStat> sink)
    {
        try
        {
            var content = obj.AsString(maxLength: 4096);
            if (content is null) return;
            if (!sink.TryGetValue(content, out var entry))
            {
                entry = new RawStringStat(content, content.Length, objSize);
                sink[content] = entry;
            }

            entry.Count++;
            entry.TotalBytes += objSize;
        }
        catch
        {
        }
    }

    private static DuplicateStringStat[] BuildDuplicateStringStats(
        Dictionary<string, RawStringStat> agg,
        int topN,
        int previewLength)
        => agg.Values
            .Where(s => s.Count > 1)
            .OrderByDescending(s => s.TotalBytes)
            .Take(topN)
            .Select(s =>
            {
                var truncated = s.Content.Length > previewLength;
                var preview = truncated ? s.Content[..previewLength] : s.Content;
                return new DuplicateStringStat(
                    Preview: preview,
                    StringLength: s.Length,
                    InstanceCount: s.Count,
                    TotalBytes: s.TotalBytes,
                    PreviewTruncated: truncated);
            })
            .ToArray();

    internal readonly record struct HeapWalkResult(
        IReadOnlyList<TypeStat> ByBytes,
        IReadOnlyList<TypeStat> ByInstances,
        IReadOnlyList<SegmentStat> Segments,
        IReadOnlyList<DelegateTargetStat>? DelegateTargets,
        IReadOnlyList<DuplicateStringStat>? DuplicateStrings,
        ClrMdTaskTimerAnalyzer.RawTaskTimerAggregation TaskTimers,
        ClrMdAssemblyLoadContextAnalyzer.RawAssemblyLoadContextAggregation AssemblyLoadContexts);

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

    private readonly record struct DelegateKey(
        string? TargetTypeFullName,
        string DeclaringTypeFullName,
        string MethodName,
        string? MethodSignature,
        string? ModulePath,
        int MetadataToken);

    private sealed class RawDelegateStat
    {
        public RawDelegateStat(DelegateKey key, ClrMethod? method, bool isStaticTarget)
        {
            Key = key;
            Method = method;
            IsStaticTarget = isStaticTarget;
        }

        public DelegateKey Key { get; }
        public ClrMethod? Method { get; }
        public bool IsStaticTarget { get; }
        public long SubscriberCount;
    }

    private sealed class RawStringStat
    {
        public RawStringStat(string content, int length, long firstObjBytes)
        {
            Content = content;
            Length = length;
            TotalBytes = 0;
            _ = firstObjBytes;
        }

        public string Content { get; }
        public int Length { get; }
        public long Count;
        public long TotalBytes;
    }
}
