using System.IO;
using DotnetDiagnostics.Core.CpuSampling;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Dump;

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
    private const int MaxArraySampleCount = 8;
    private const int MaxStringPreviewLength = 256;
    private const int MaxFieldDepth = 3;
    private const int MaxFieldCount = 256;
    private const int GcRootDepthLimit = 64;
    private const int MaxRetainedGraphObjects = 250_000;
    private readonly ILogger<ClrMdDumpInspector> _logger;
    private readonly MvidReader _mvidReader = new();

    public ClrMdDumpInspector(ILogger<ClrMdDumpInspector>? logger = null)
    {
        _logger = logger ?? NullLogger<ClrMdDumpInspector>.Instance;
    }

    public Task<HeapSnapshotArtifact> InspectAsync(
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
        ValidateOptions(opts);

        // ClrMD is fully synchronous; wrap in Task.Run so the caller's async context isn't
        // blocked on a multi-second heap walk for a large dump.
        return Task.Run(() => Inspect(dumpFilePath, opts, cancellationToken), cancellationToken);
    }

    public Task<HeapSnapshotArtifact> InspectLiveAsync(
        int processId,
        DumpInspectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }

        var opts = options ?? new DumpInspectionOptions();
        ValidateOptions(opts);

        return Task.Run(() => InspectLive(processId, opts, cancellationToken), cancellationToken);
    }

    public Task<HeapObjectInspection> InspectObjectAsync(
        HeapSnapshotArtifact snapshot,
        ulong address,
        CancellationToken cancellationToken = default)
        => InspectSnapshotAsync(snapshot, runtime => ClrMdHeapDrilldownService.InspectObject(runtime, address), cancellationToken);

    public Task<HeapGcRootInspection> InspectGcRootAsync(
        HeapSnapshotArtifact snapshot,
        ulong address,
        CancellationToken cancellationToken = default)
        => InspectSnapshotAsync(snapshot, runtime => ClrMdHeapDrilldownService.InspectGcRoot(runtime, address), cancellationToken);

    public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(
        HeapSnapshotArtifact snapshot,
        ulong address,
        CancellationToken cancellationToken = default)
        => InspectSnapshotAsync(snapshot, runtime => ClrMdHeapDrilldownService.InspectObjectSize(runtime, address), cancellationToken);

    private static void ValidateOptions(DumpInspectionOptions opts)
    {
        if (opts.TopTypes <= 0) throw new ArgumentOutOfRangeException(nameof(opts), "TopTypes must be positive.");
        if (opts.SnapshotTopTypes <= 0) throw new ArgumentOutOfRangeException(nameof(opts), "SnapshotTopTypes must be positive.");
        if (opts.RetentionPathLimit <= 0) throw new ArgumentOutOfRangeException(nameof(opts), "RetentionPathLimit must be positive.");
        if (opts.SnapshotRetentionPathTargets <= 0) throw new ArgumentOutOfRangeException(nameof(opts), "SnapshotRetentionPathTargets must be positive.");
    }

    private static Task<T> InspectSnapshotAsync<T>(
        HeapSnapshotArtifact snapshot,
        Func<ClrRuntime, T> inspector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(inspector);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var session = ClrMdRuntimeSession.OpenSnapshot(snapshot);
            if (!session.Runtime.Heap.CanWalkHeap)
            {
                throw new InvalidOperationException("Heap walk is unavailable for this snapshot source (CanWalkHeap=false).");
            }

            return inspector(session.Runtime);
        }, cancellationToken);
    }

    private HeapSnapshotArtifact InspectLive(int processId, DumpInspectionOptions opts, CancellationToken ct)
    {
        var warnings = new List<string>();
        var capturedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // suspend=true pauses the target for the lifetime of the DataTarget. We dispose
        // ASAP after the walk so the suspend window is bounded by walk duration.
        using var session = ClrMdRuntimeSession.AttachLive(processId);
        var runtime = session.Runtime;

        var runtimeInfo = BuildRuntimeInfo(session.Target, session.ClrInfo, runtime);

        if (!runtime.Heap.CanWalkHeap)
        {
            warnings.Add("Heap walk is unavailable for this runtime state (CanWalkHeap=false). Retry once the GC is in a quiescent state.");
            sw.Stop();
            return new HeapSnapshotArtifact(
                Origin: HeapSnapshotOrigin.Live,
                ProcessId: processId,
                CapturedAt: capturedAt,
                WalkDuration: sw.Elapsed,
                Runtime: runtimeInfo,
                Heap: EmptyHeap(),
                TopTypesByBytes: Array.Empty<TypeStat>(),
                TopTypesByInstances: Array.Empty<TypeStat>())
            {
                Warnings = warnings,
            };
        }

        var summary = SummarizeRuntime(runtime, opts, warnings, ct);
        sw.Stop();

        return new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: processId,
            CapturedAt: capturedAt,
            WalkDuration: sw.Elapsed,
            Runtime: runtimeInfo,
            Heap: summary.Heap,
            TopTypesByBytes: summary.ByBytes,
            TopTypesByInstances: summary.ByInstances)
        {
            RetentionPaths = summary.Retention,
            RootsByKind = summary.Roots,
            FinalizableObjectsByType = summary.Finalizable,
            Segments = summary.Segments,
            StaticFields = summary.StaticFields,
            DelegateTargets = summary.DelegateTargets,
            DuplicateStrings = summary.DuplicateStrings,
            GcHandles = summary.GcHandles,
            AsyncOperations = summary.AsyncOperations,
            Timers = summary.Timers,
            AssemblyLoadContexts = summary.AssemblyLoadContexts,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private static DumpRuntimeInfo BuildRuntimeInfo(DataTarget target, ClrInfo clrInfo, ClrRuntime runtime) =>
        new(
            Name: clrInfo.Flavor.ToString(),
            Version: clrInfo.Version.ToString(),
            Architecture: target.DataReader.Architecture.ToString(),
            IsServerGC: runtime.Heap.IsServer,
            HeapCount: runtime.Heap.SubHeaps.Length);

    private HeapSnapshotArtifact Inspect(string dumpFilePath, DumpInspectionOptions opts, CancellationToken ct)
    {
        var fileInfo = new FileInfo(dumpFilePath);
        var warnings = new List<string>();
        var capturedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var session = ClrMdRuntimeSession.LoadDump(dumpFilePath);
        var runtime = session.Runtime;

        var runtimeInfo = BuildRuntimeInfo(session.Target, session.ClrInfo, runtime);
        var processIdFromDump = session.ProcessId;

        if (!runtime.Heap.CanWalkHeap)
        {
            warnings.Add("Heap walk is unavailable for this dump (CanWalkHeap=false). " +
                "Capture a WithHeap or Full dump for full inspection.");
            sw.Stop();
            return new HeapSnapshotArtifact(
                Origin: HeapSnapshotOrigin.Dump,
                ProcessId: processIdFromDump,
                CapturedAt: capturedAt,
                WalkDuration: sw.Elapsed,
                Runtime: runtimeInfo,
                Heap: EmptyHeap(),
                TopTypesByBytes: Array.Empty<TypeStat>(),
                TopTypesByInstances: Array.Empty<TypeStat>())
            {
                DumpFilePath = dumpFilePath,
                DumpFileSizeBytes = fileInfo.Length,
                Warnings = warnings,
            };
        }

        var summary = SummarizeRuntime(runtime, opts, warnings, ct);
        sw.Stop();

        return new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Dump,
            ProcessId: processIdFromDump,
            CapturedAt: capturedAt,
            WalkDuration: sw.Elapsed,
            Runtime: runtimeInfo,
            Heap: summary.Heap,
            TopTypesByBytes: summary.ByBytes,
            TopTypesByInstances: summary.ByInstances)
        {
            DumpFilePath = dumpFilePath,
            DumpFileSizeBytes = fileInfo.Length,
            RetentionPaths = summary.Retention,
            RootsByKind = summary.Roots,
            FinalizableObjectsByType = summary.Finalizable,
            Segments = summary.Segments,
            StaticFields = summary.StaticFields,
            DelegateTargets = summary.DelegateTargets,
            DuplicateStrings = summary.DuplicateStrings,
            GcHandles = summary.GcHandles,
            AsyncOperations = summary.AsyncOperations,
            Timers = summary.Timers,
            AssemblyLoadContexts = summary.AssemblyLoadContexts,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private RuntimeSummary SummarizeRuntime(
        ClrRuntime runtime, DumpInspectionOptions opts, List<string> warnings, CancellationToken ct)
    {
        var (typeStats, totalBytes, segments, delegateAgg, stringAgg, taskTimerAgg, alcAgg) = WalkHeap(runtime, opts, warnings, ct);
        var heapSummary = SummarizeHeapFromSegments(segments);

        // The snapshot retains a richer top-N so follow-up drilldown queries (e.g. ask for top-100
        // when the tool returned top-20 inline) don't pay the walk cost a second time.
        var snapshotTopN = Math.Max(opts.TopTypes, opts.SnapshotTopTypes);

        var byBytes = typeStats.Values
            .OrderByDescending(s => s.Bytes)
            .Take(snapshotTopN)
            .Select(s => ToTypeStat(s, totalBytes))
            .ToArray();

        var byInstances = typeStats.Values
            .OrderByDescending(s => s.Count)
            .Take(snapshotTopN)
            .Select(s => ToTypeStat(s, totalBytes))
            .ToArray();

        IReadOnlyList<RetentionPath>? retention = null;
        if (opts.IncludeRetentionPaths)
        {
            retention = ClrMdRetentionAnalyzer.ResolveRetentionPaths(runtime, byBytes, opts.RetentionPathLimit, opts.SnapshotRetentionPathTargets, warnings, ct);
        }

        var roots = WalkRoots(runtime, warnings, ct);
        var finalizable = WalkFinalizableObjects(runtime, opts.SnapshotFinalizerQueueTopTypes, warnings, ct);

        IReadOnlyList<StaticFieldStat>? statics = null;
        if (opts.IncludeStaticFields)
        {
            statics = WalkStaticFields(runtime, opts.SnapshotStaticFieldTopN, warnings, ct);
        }

        IReadOnlyList<DelegateTargetStat>? delegates = null;
        if (opts.IncludeDelegateTargets && delegateAgg is not null)
        {
            delegates = BuildDelegateStats(delegateAgg, opts.SnapshotDelegateTargetTopN);
        }

        IReadOnlyList<DuplicateStringStat>? duplicates = null;
        if (opts.IncludeDuplicateStrings && stringAgg is not null)
        {
            duplicates = BuildDuplicateStringStats(stringAgg, opts.SnapshotDuplicateStringTopN, opts.DuplicateStringPreviewLength);
        }

        var gcHandles = WalkGcHandles(runtime, ct);
        var asyncOperations = ClrMdAsyncStateMachineWalker.WalkPendingAsyncOperations(runtime, warnings, ct);
        var timers = ClrMdTaskTimerAnalyzer.BuildView(taskTimerAgg, opts.SnapshotTopTypes, BuildTypeIdentity, TryReadMvid);
        var assemblyLoadContexts = ClrMdAssemblyLoadContextAnalyzer.BuildView(runtime, alcAgg, warnings, ct);

        return new RuntimeSummary(byBytes, byInstances, heapSummary, retention, roots, finalizable, segments, statics, delegates, duplicates, gcHandles, asyncOperations, timers, assemblyLoadContexts);
    }

    private readonly record struct RuntimeSummary(
        IReadOnlyList<TypeStat> ByBytes,
        IReadOnlyList<TypeStat> ByInstances,
        DumpHeapSummary Heap,
        IReadOnlyList<RetentionPath>? Retention,
        IReadOnlyList<RootKindStat> Roots,
        IReadOnlyList<FinalizableTypeStat> Finalizable,
        IReadOnlyList<SegmentStat> Segments,
        IReadOnlyList<StaticFieldStat>? StaticFields,
        IReadOnlyList<DelegateTargetStat>? DelegateTargets,
        IReadOnlyList<DuplicateStringStat>? DuplicateStrings,
        GcHandlesView GcHandles,
        IReadOnlyList<AsyncOperationStat> AsyncOperations,
        TaskTimerLeakView Timers,
        AssemblyLoadContextLeakView AssemblyLoadContexts);

    private static (Dictionary<TypeKey, RawTypeStat> Stats, long TotalBytes, IReadOnlyList<SegmentStat> Segments, Dictionary<DelegateKey, RawDelegateStat>? Delegates, Dictionary<string, RawStringStat>? Strings, ClrMdTaskTimerAnalyzer.RawTaskTimerAggregation TaskTimers, ClrMdAssemblyLoadContextAnalyzer.RawAssemblyLoadContextAggregation AssemblyLoadContexts) WalkHeap(
        ClrRuntime runtime, DumpInspectionOptions opts, List<string> warnings, CancellationToken ct)
    {
        var stats = new Dictionary<TypeKey, RawTypeStat>();
        long total = 0;
        var segmentStats = new List<SegmentStat>(runtime.Heap.Segments.Length);
        Dictionary<DelegateKey, RawDelegateStat>? delegates = opts.IncludeDelegateTargets ? new() : null;
        Dictionary<string, RawStringStat>? strings = opts.IncludeDuplicateStrings ? new(StringComparer.Ordinal) : null;
        var taskTimers = new ClrMdTaskTimerAnalyzer.RawTaskTimerAggregation();
        var assemblyLoadContexts = new ClrMdAssemblyLoadContextAnalyzer.RawAssemblyLoadContextAggregation();
        // Wall-clock safety net on a per-object basis: a runaway delegate/string walk on a multi-GB
        // heap could grow these dictionaries unbounded. We accept the dictionaries and cap their
        // size to (snapshot top-N * 32) entries — far above the top-N we'll surface but bounded.
        var delegateCap = opts.IncludeDelegateTargets ? Math.Max(opts.SnapshotDelegateTargetTopN * 32, 4096) : 0;
        var stringCap = opts.IncludeDuplicateStrings ? Math.Max(opts.SnapshotDuplicateStringTopN * 32, 4096) : 0;
        // Independent hard cap on the number of string OBJECTS scanned (vs unique entries). Without
        // this, a heap holding millions of identical strings collapses to a single dictionary entry
        // but still pays AsString(maxLength) per object — under the live suspend window that's
        // unbounded. 1M objects is enough to surface duplicates while keeping the worst case bounded.
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
                total += size;

                var key = new TypeKey(obj.Type.Name ?? "<unknown>", obj.Type.Module?.Name);
                if (!stats.TryGetValue(key, out var s))
                {
                    s = new RawTypeStat(key.TypeName, key.ModuleName, obj.Type);
                    stats[key] = s;
                }
                s.Count++;
                s.Bytes += size;

                if (delegates is not null && obj.IsDelegate && !delegateCapHit)
                {
                    AggregateDelegate(obj, delegates);
                    if (delegates.Count > delegateCap) delegateCapHit = true;
                }

                if (strings is not null && obj.Type.IsString && !stringCapHit && !stringObjectCapHit)
                {
                    AggregateString(obj, size, strings);
                    stringObjectsScanned++;
                    if (strings.Count > stringCap) stringCapHit = true;
                    if (stringObjectsScanned > stringObjectScanCap) stringObjectCapHit = true;
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

        if (delegateCapHit) warnings.Add($"Delegate-target aggregation hit cap of {delegateCap} unique entries — results are truncated to the busiest entries seen so far.");
        if (stringCapHit) warnings.Add($"Duplicate-string aggregation hit cap of {stringCap} unique entries — results are truncated.");
        if (stringObjectCapHit) warnings.Add($"Duplicate-string aggregation hit object-scan cap of {stringObjectScanCap:N0} string instances — results reflect only the strings encountered before the cap.");

        return (stats, total, segmentStats, delegates, strings, taskTimers, assemblyLoadContexts);
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
            // ClrMD can throw on corrupt delegate instances; skip without polluting warnings on every miss.
        }
    }

    private static void AggregateString(ClrObject obj, long objSize, Dictionary<string, RawStringStat> sink)
    {
        try
        {
            // Bound the read length so a single oversized string can't dominate aggregation cost.
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
            // Ignore malformed strings.
        }
    }

    private StaticFieldStat[] WalkStaticFields(
        ClrRuntime runtime, int topN, List<string> warnings, CancellationToken ct)
    {
        var results = new List<StaticFieldStat>(capacity: Math.Min(topN * 4, 1024));
        var visitedTypes = new HashSet<(int AppDomainId, ulong MethodTable)>();
        try
        {
            foreach (var domain in runtime.AppDomains)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var module in domain.Modules)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!visitedTypes.Add((domain.Id, mt))) continue;
                        ClrType? type;
                        try { type = runtime.GetTypeByMethodTable(mt); }
                        catch { continue; }
                        if (type is null) continue;
                        if (type.StaticFields.IsDefaultOrEmpty) continue;

                        foreach (var field in type.StaticFields)
                        {
                            if (!field.IsObjectReference) continue;
                            ClrObject value = default;
                            try
                            {
                                if (!field.IsInitialized(domain)) continue;
                                value = field.ReadObject(domain);
                            }
                            catch { continue; }
                            if (value.IsNull || !value.IsValid) continue;

                            var size = (long)value.Size;
                            if (size <= 0) continue;

                            var raw = new RawTypeStat(type.Name ?? "<unknown>", type.Module?.Name, type);
                            var identity = BuildTypeIdentity(raw.ClrType);
                            results.Add(new StaticFieldStat(
                                ContainingTypeFullName: type.Name ?? "<unknown>",
                                ModuleName: type.Module?.Name is { } mn ? Path.GetFileName(mn) : null,
                                FieldName: field.Name ?? "<unknown>",
                                FieldToken: field.Token,
                                ValueAddress: value.Address,
                                ValueTypeFullName: value.Type?.Name,
                                DirectlyReferencedBytes: size,
                                AppDomainId: domain.Id)
                            {
                                ContainingTypeIdentity = identity,
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Static-field walk aborted partway through: {ex.GetType().Name} ({ex.Message}).");
        }

        return results
            .OrderByDescending(s => s.DirectlyReferencedBytes)
            .Take(topN)
            .ToArray();
    }

    private DelegateTargetStat[] BuildDelegateStats(Dictionary<DelegateKey, RawDelegateStat> agg, int topN)
    {
        return agg.Values
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
                        ModuleVersionId: TryReadMvid(d.Key.ModulePath),
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
    }

    private static DuplicateStringStat[] BuildDuplicateStringStats(
        Dictionary<string, RawStringStat> agg, int topN, int previewLength)
    {
        return agg.Values
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

    private static RootKindStat[] WalkRoots(ClrRuntime runtime, List<string> warnings, CancellationToken ct)
    {
        // Bucket every reachable root by ClrRootKind. We deliberately do NOT do a per-root
        // retention walk here (that's O(roots × heap) and would dwarf the heap walk itself).
        // DirectlyReferencedBytes is the sum of the IMMEDIATE target object's size, summed across
        // distinct objects per kind. Useful for spotting "I have 50k pinning handles holding X MB".
        var byKind = new Dictionary<string, RawRootStat>(StringComparer.Ordinal);

        try
        {
            foreach (var root in runtime.Heap.EnumerateRoots())
            {
                ct.ThrowIfCancellationRequested();
                var kind = root.RootKind.ToString();
                if (!byKind.TryGetValue(kind, out var bucket))
                {
                    bucket = new RawRootStat();
                    byKind[kind] = bucket;
                }
                bucket.RootCount++;
                if (root.IsPinned) bucket.PinnedCount++;
                if (root.IsInterior) bucket.InteriorCount++;

                var addr = root.Object.Address;
                if (addr != 0 && bucket.SeenObjects.Add(addr))
                {
                    bucket.DistinctTargets++;
                    if (!root.Object.IsNull && root.Object.Type is not null)
                    {
                        bucket.DirectBytes += (long)root.Object.Size;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Root enumeration aborted partway through: {ex.GetType().Name} ({ex.Message}).");
        }

        return byKind
            .Select(kvp => new RootKindStat(
                RootKind: kvp.Key,
                RootCount: kvp.Value.RootCount,
                DistinctTargetObjects: kvp.Value.DistinctTargets,
                DirectlyReferencedBytes: kvp.Value.DirectBytes,
                PinnedRootCount: kvp.Value.PinnedCount,
                InteriorRootCount: kvp.Value.InteriorCount))
            .OrderByDescending(r => r.DirectlyReferencedBytes)
            .ThenByDescending(r => r.RootCount)
            .ToArray();
    }

    private static FinalizableTypeStat[] WalkFinalizableObjects(
        ClrRuntime runtime, int topN, List<string> warnings, CancellationToken ct)
    {
        var byType = new Dictionary<TypeKey, RawTypeStat>();
        try
        {
            foreach (var addr in runtime.Heap.EnumerateFinalizableObjects())
            {
                ct.ThrowIfCancellationRequested();
                var obj = runtime.Heap.GetObject(addr);
                if (obj.Type is null) continue;
                var key = new TypeKey(obj.Type.Name ?? "<unknown>", obj.Type.Module?.Name);
                if (!byType.TryGetValue(key, out var bucket))
                {
                    bucket = new RawTypeStat(key.TypeName, key.ModuleName, obj.Type);
                    byType[key] = bucket;
                }
                bucket.Count++;
                bucket.Bytes += (long)obj.Size;
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Finalizer queue enumeration aborted partway through: {ex.GetType().Name} ({ex.Message}).");
        }

        return byType.Values
            .OrderByDescending(b => b.Bytes)
            .ThenByDescending(b => b.Count)
            .Take(topN)
            .Select(b => new FinalizableTypeStat(
                TypeFullName: b.TypeName,
                ModuleName: b.ModuleName is { } mn ? Path.GetFileName(mn) : null,
                InstanceCount: b.Count,
                TotalBytes: b.Bytes))
            .ToArray();
    }

    private GcHandlesView WalkGcHandles(ClrRuntime runtime, CancellationToken ct)
    {
        var samples = new List<GcHandleAggregation.GcHandleSample>();
        var notes = new List<string>();

        try
        {
            foreach (var handle in runtime.EnumerateHandles())
            {
                ct.ThrowIfCancellationRequested();

                var target = handle.Object;
                var targetType = target.Type;
                var typeName = targetType?.Name ?? (target.IsNull ? null : "<unknown>");
                var retainedBytes = !target.IsNull && targetType is not null ? (long)target.Size : 0L;
                var kind = handle.IsPinned && handle.HandleKind == ClrHandleKind.Strong
                    ? ClrHandleKind.Pinned
                    : handle.HandleKind;

                samples.Add(new GcHandleAggregation.GcHandleSample(
                    kind,
                    typeName,
                    retainedBytes,
                    BuildTypeIdentity(targetType)));
            }
        }
        catch (Exception ex)
        {
            notes.Add($"GCHandle enumeration aborted partway through: {ex.GetType().Name} ({ex.Message}).");
        }

        var aggregated = GcHandleAggregation.Aggregate(samples);
        if (notes.Count == 0)
        {
            return aggregated;
        }

        return aggregated with
        {
            Notes = aggregated.Notes.AddRange(notes),
        };
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
            Identity: BuildTypeIdentity(raw.ClrType));
    }

    private TypeIdentity? BuildTypeIdentity(ClrType? clrType)
    {
        if (clrType is null) return null;

        var modulePath = clrType.Module?.Name;
        var moduleFileName = !string.IsNullOrEmpty(modulePath) ? Path.GetFileName(modulePath) : null;
        var token = (int)clrType.MetadataToken;
        var mvid = TryReadMvid(modulePath);

        if (mvid is null && token == 0 && string.IsNullOrEmpty(modulePath) && string.IsNullOrEmpty(moduleFileName))
        {
            return null;
        }

        return new TypeIdentity(clrType.Name ?? "<unknown>")
        {
            ModuleName = moduleFileName,
            ModulePath = modulePath,
            ModuleVersionId = mvid,
            MetadataToken = token > 0 ? token : null,
        };
    }

    private Guid? TryReadMvid(string? assemblyPath)
    {
        try
        {
            return _mvidReader.TryRead(assemblyPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MVID read failed for {Path}", assemblyPath);
            return null;
        }
    }

    private static DumpHeapSummary SummarizeHeapFromSegments(IReadOnlyList<SegmentStat> segments)
    {
        long total = 0, gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poh = 0, committed = 0;
        foreach (var s in segments)
        {
            total += s.Length;
            committed += s.CommittedBytes;
            switch (s.Generation)
            {
                case "Gen0": gen0 += s.Length; break;
                case "Gen1": gen1 += s.Length; break;
                case "Gen2": gen2 += s.Length; break;
                case "LOH": loh += s.Length; break;
                case "POH": poh += s.Length; break;
                case "Ephemeral":
                    // Workstation GC keeps gen0/gen1 in a single segment; bucket into gen0 for the LLM-facing summary.
                    gen0 += s.Length;
                    break;
            }
        }
        return new DumpHeapSummary(total, gen0, gen1, gen2, loh, poh, committed);
    }

    private static DumpHeapSummary EmptyHeap() => new(0, 0, 0, 0, 0, 0, 0);

    private readonly record struct TypeKey(string TypeName, string? ModuleName);

    private sealed class RawRootStat
    {
        public long RootCount;
        public long DistinctTargets;
        public long DirectBytes;
        public long PinnedCount;
        public long InteriorCount;
        public HashSet<ulong> SeenObjects { get; } = new();
    }

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
            _ = firstObjBytes; // consumed by AggregateString incrementally
        }
        public string Content { get; }
        public int Length { get; }
        public long Count;
        public long TotalBytes;
    }

}
