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
        var walk = ClrMdHeapWalker.Walk(runtime, opts, warnings, BuildTypeIdentity, TryReadMvid, ct);
        var heapSummary = SummarizeHeapFromSegments(walk.Segments);
        var byBytes = walk.ByBytes;
        var byInstances = walk.ByInstances;

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

        var delegates = walk.DelegateTargets;
        var duplicates = walk.DuplicateStrings;

        var gcHandles = WalkGcHandles(runtime, ct);
        var asyncOperations = ClrMdAsyncStateMachineWalker.WalkPendingAsyncOperations(runtime, warnings, ct);
        var timers = ClrMdTaskTimerAnalyzer.BuildView(walk.TaskTimers, opts.SnapshotTopTypes, BuildTypeIdentity, TryReadMvid);
        var assemblyLoadContexts = ClrMdAssemblyLoadContextAnalyzer.BuildView(runtime, walk.AssemblyLoadContexts, warnings, ct);

        return new RuntimeSummary(byBytes, byInstances, heapSummary, retention, roots, finalizable, walk.Segments, statics, delegates, duplicates, gcHandles, asyncOperations, timers, assemblyLoadContexts);
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

    private StaticFieldStat[] WalkStaticFields(
        ClrRuntime runtime, int topN, List<string> warnings, CancellationToken ct)
    {
        var results = new StaticFieldTopNAccumulator(topN);
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

        return results.ToArray();
    }

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
        var aggregator = new GcHandleAggregation.Builder();
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

                aggregator.Add(new GcHandleAggregation.GcHandleSample(
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

        var aggregated = aggregator.BuildView();
        if (notes.Count == 0)
        {
            return aggregated;
        }

        return aggregated with
        {
            Notes = aggregated.Notes.AddRange(notes),
        };
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

    internal sealed class StaticFieldTopNAccumulator
    {
        private static readonly IComparer<StaticFieldStat> Comparer = System.Collections.Generic.Comparer<StaticFieldStat>.Create(Compare);
        private readonly int _capacity;
        private readonly List<StaticFieldStat> _items;

        public StaticFieldTopNAccumulator(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);
            _capacity = capacity;
            _items = new List<StaticFieldStat>(capacity);
        }

        public void Add(StaticFieldStat item)
        {
            if (_capacity == 0)
            {
                return;
            }

            var insertAt = _items.BinarySearch(item, Comparer);
            if (insertAt < 0)
            {
                insertAt = ~insertAt;
            }

            if (insertAt >= _capacity)
            {
                return;
            }

            _items.Insert(insertAt, item);
            if (_items.Count > _capacity)
            {
                _items.RemoveAt(_capacity);
            }
        }

        public StaticFieldStat[] ToArray() => _items.ToArray();

        private static int Compare(StaticFieldStat left, StaticFieldStat right)
        {
            var byBytes = right.DirectlyReferencedBytes.CompareTo(left.DirectlyReferencedBytes);
            if (byBytes != 0)
            {
                return byBytes;
            }

            var byType = string.Compare(left.ContainingTypeFullName, right.ContainingTypeFullName, StringComparison.Ordinal);
            if (byType != 0)
            {
                return byType;
            }

            var byField = string.Compare(left.FieldName, right.FieldName, StringComparison.Ordinal);
            if (byField != 0)
            {
                return byField;
            }

            return left.ValueAddress.CompareTo(right.ValueAddress);
        }
    }

}
