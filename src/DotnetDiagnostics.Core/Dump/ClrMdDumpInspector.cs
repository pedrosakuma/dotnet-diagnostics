using System.Globalization;
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
    private const int MaxAssemblyLoadContexts = 1024;
    private const int MaxAssembliesPerAssemblyLoadContext = 128;
    private const int AssemblyLoadContextRetentionHintLimit = 16;

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
        => InspectSnapshotAsync(snapshot, runtime => InspectObject(runtime, address), cancellationToken);

    public Task<HeapGcRootInspection> InspectGcRootAsync(
        HeapSnapshotArtifact snapshot,
        ulong address,
        CancellationToken cancellationToken = default)
        => InspectSnapshotAsync(snapshot, runtime => InspectGcRoot(runtime, address), cancellationToken);

    public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(
        HeapSnapshotArtifact snapshot,
        ulong address,
        CancellationToken cancellationToken = default)
        => InspectSnapshotAsync(snapshot, runtime => InspectObjectSize(runtime, address), cancellationToken);

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

            using var target = snapshot.Origin switch
            {
                HeapSnapshotOrigin.Dump when !string.IsNullOrWhiteSpace(snapshot.DumpFilePath) && File.Exists(snapshot.DumpFilePath)
                    => DataTarget.LoadDump(snapshot.DumpFilePath),
                HeapSnapshotOrigin.Live when snapshot.ProcessId > 0
                    => DataTarget.AttachToProcess(snapshot.ProcessId, suspend: true),
                HeapSnapshotOrigin.Dump => throw new InvalidOperationException("The originating dump file is unavailable for this heap snapshot handle."),
                _ => throw new InvalidOperationException("The originating live process is unavailable for this heap snapshot handle."),
            };

            var clrInfo = target.ClrVersions.FirstOrDefault()
                ?? throw new InvalidOperationException("The heap snapshot source does not expose a CLR runtime.");
            using var runtime = clrInfo.CreateRuntime();
            if (!runtime.Heap.CanWalkHeap)
            {
                throw new InvalidOperationException("Heap walk is unavailable for this snapshot source (CanWalkHeap=false).");
            }

            return inspector(runtime);
        }, cancellationToken);
    }

    private HeapSnapshotArtifact InspectLive(int processId, DumpInspectionOptions opts, CancellationToken ct)
    {
        var warnings = new List<string>();
        var capturedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // suspend=true pauses the target for the lifetime of the DataTarget. We dispose
        // ASAP after the walk so the suspend window is bounded by walk duration.
        using var target = DataTarget.AttachToProcess(processId, suspend: true);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException($"Process {processId} does not expose a CLR runtime (NativeAOT or non-managed).");
        using var runtime = clrInfo.CreateRuntime();

        var runtimeInfo = BuildRuntimeInfo(target, clrInfo, runtime);

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

        using var target = DataTarget.LoadDump(dumpFilePath);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException("Dump does not contain a CLR runtime.");
        using var runtime = clrInfo.CreateRuntime();

        var runtimeInfo = BuildRuntimeInfo(target, clrInfo, runtime);
        var processIdFromDump = unchecked((int)target.DataReader.ProcessId);

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
        var timers = BuildTaskTimerLeakView(taskTimerAgg, opts.SnapshotTopTypes);
        var assemblyLoadContexts = BuildAssemblyLoadContextLeakView(runtime, alcAgg, warnings, ct);

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

    private static (Dictionary<TypeKey, RawTypeStat> Stats, long TotalBytes, IReadOnlyList<SegmentStat> Segments, Dictionary<DelegateKey, RawDelegateStat>? Delegates, Dictionary<string, RawStringStat>? Strings, RawTaskTimerAggregation TaskTimers, RawAssemblyLoadContextAggregation AssemblyLoadContexts) WalkHeap(
        ClrRuntime runtime, DumpInspectionOptions opts, List<string> warnings, CancellationToken ct)
    {
        var stats = new Dictionary<TypeKey, RawTypeStat>();
        long total = 0;
        var segmentStats = new List<SegmentStat>(runtime.Heap.Segments.Length);
        Dictionary<DelegateKey, RawDelegateStat>? delegates = opts.IncludeDelegateTargets ? new() : null;
        Dictionary<string, RawStringStat>? strings = opts.IncludeDuplicateStrings ? new(StringComparer.Ordinal) : null;
        var taskTimers = new RawTaskTimerAggregation();
        var assemblyLoadContexts = new RawAssemblyLoadContextAggregation();
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

                AggregateTaskTimer(obj, size, taskTimers);
                AggregateAssemblyLoadContext(obj, size, assemblyLoadContexts);
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

    private static void AggregateTaskTimer(ClrObject obj, long objSize, RawTaskTimerAggregation sink)
    {
        var type = obj.Type;
        if (type is null)
        {
            return;
        }

        var typeName = type.Name ?? "<unknown>";
        if (IsTimerContainerType(typeName))
        {
            var timerObject = TryResolveScheduledTimer(obj);
            if (timerObject is null)
            {
                return;
            }

            var scheduledTimer = timerObject.Value;
            if (!sink.SeenTimerAddresses.Add(scheduledTimer.Address))
            {
                return;
            }

            var timerTypeName = scheduledTimer.Type?.Name ?? typeName;
            sink.TotalTimers++;
            var callback = TryReadTimerCallback(scheduledTimer);
            var isCanceled = TryReadBoolField(scheduledTimer, "_canceled", "m_canceled", "_disposed");
            var key = new TimerCallbackKey(
                TimerTypeFullName: timerTypeName,
                CallbackTargetTypeFullName: callback?.TargetTypeFullName,
                DeclaringTypeFullName: callback?.DeclaringTypeFullName,
                MethodName: callback?.MethodName,
                MethodSignature: callback?.MethodSignature,
                ModulePath: callback?.ModulePath,
                MetadataToken: callback?.MetadataToken ?? 0,
                IsCanceled: isCanceled);

            if (!sink.TimersByCallback.TryGetValue(key, out var timer))
            {
                timer = new RawTimerCallbackStat(key, callback?.Method);
                sink.TimersByCallback[key] = timer;
            }

            timer.Count++;
        }

        if (IsTaskType(type))
        {
            sink.TotalTasks++;
            AggregateTaskType(type, objSize, sink.TasksByType);
        }

        if (typeName.StartsWith("System.Threading.Tasks.TaskCompletionSource", StringComparison.Ordinal))
        {
            sink.TotalTaskCompletionSources++;
            AggregateTaskType(type, objSize, sink.TaskCompletionSourcesByType);
        }
    }

    private static void AggregateAssemblyLoadContext(ClrObject obj, long objSize, RawAssemblyLoadContextAggregation sink)
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
        var fieldCollectible = TryReadBoolField(obj, "_isCollectible", "m_isCollectible");
        if (fieldCollectible.HasValue)
        {
            context.IsCollectible = fieldCollectible.Value;
            context.CollectibleResolvedFromField = true;
        }

        context.IsDefault = IsDefaultAssemblyLoadContext(context.Name, context.IsCollectible);
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

    private static bool IsTimerContainerType(string typeName)
        => typeName.Equals("System.Threading.Timer", StringComparison.Ordinal) ||
           typeName.StartsWith("System.Threading.TimerQueue", StringComparison.Ordinal) ||
           typeName.StartsWith("System.Threading.TimerHolder", StringComparison.Ordinal);

    private static bool IsScheduledTimerType(string typeName)
        => typeName.Equals("System.Threading.TimerQueueTimer", StringComparison.Ordinal);

    private static bool IsTaskType(ClrType type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, "System.Threading.Tasks.Task", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AggregateTaskType(ClrType type, long objSize, Dictionary<TypeKey, RawTypeStat> sink)
    {
        var key = new TypeKey(type.Name ?? "<unknown>", type.Module?.Name);
        if (!sink.TryGetValue(key, out var stat))
        {
            stat = new RawTypeStat(key.TypeName, key.ModuleName, type);
            sink[key] = stat;
        }

        stat.Count++;
        stat.Bytes += objSize;
    }

    private static RawTimerCallback? TryReadTimerCallback(ClrObject timer)
        => TryReadTimerCallback(timer, depth: 0);

    private static RawTimerCallback? TryReadTimerCallback(ClrObject timer, int depth)
    {
        if (timer.IsNull || !timer.IsValid || timer.Type is null || depth > 2)
        {
            return null;
        }

        foreach (var field in EnumerateInstanceFields(timer.Type))
        {
            if (!field.IsObjectReference)
            {
                continue;
            }

            ClrObject value;
            try
            {
                value = field.ReadObject(timer.Address, interior: false);
            }
            catch
            {
                continue;
            }

            if (value.IsNull || !value.IsValid || value.Type is null)
            {
                continue;
            }

            if (value.IsDelegate)
            {
                var callback = TryBuildTimerCallback(value);
                if (callback is not null)
                {
                    return callback;
                }
            }

            var nestedTypeName = value.Type.Name;
            if (nestedTypeName is not null && IsTimerContainerType(nestedTypeName))
            {
                var nested = TryReadTimerCallback(value, depth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static ClrObject? TryResolveScheduledTimer(ClrObject timer)
        => TryResolveScheduledTimer(timer, depth: 0);

    private static ClrObject? TryResolveScheduledTimer(ClrObject timer, int depth)
    {
        if (timer.IsNull || !timer.IsValid || timer.Type is null || depth > 2)
        {
            return null;
        }

        var typeName = timer.Type.Name;
        if (typeName is not null && IsScheduledTimerType(typeName))
        {
            return timer;
        }

        foreach (var field in EnumerateInstanceFields(timer.Type))
        {
            if (!field.IsObjectReference)
            {
                continue;
            }

            ClrObject value;
            try
            {
                value = field.ReadObject(timer.Address, interior: false);
            }
            catch
            {
                continue;
            }

            var nestedTypeName = value.Type?.Name;
            if (nestedTypeName is null || !IsTimerContainerType(nestedTypeName))
            {
                continue;
            }

            var nested = TryResolveScheduledTimer(value, depth + 1);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static RawTimerCallback? TryBuildTimerCallback(ClrObject callbackDelegate)
    {
        try
        {
            var del = callbackDelegate.AsDelegate();
            foreach (var target in del.EnumerateDelegateTargets())
            {
                var method = target.Method;
                if (method is null)
                {
                    continue;
                }

                var targetObj = target.TargetObject;
                return new RawTimerCallback(
                    TargetTypeFullName: targetObj.IsNull ? null : targetObj.Type?.Name,
                    DeclaringTypeFullName: method.Type?.Name ?? "<unknown>",
                    MethodName: method.Name ?? "<unknown>",
                    MethodSignature: method.Signature,
                    ModulePath: method.Type?.Module?.Name,
                    MetadataToken: (int)method.MetadataToken,
                    Method: method);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool? TryReadBoolField(ClrObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var field = FindFieldByName(obj.Type, name);
            if (field is null || !field.IsPrimitive || field.ElementType != ClrElementType.Boolean)
            {
                continue;
            }

            try
            {
                return field.Read<bool>(obj.Address, interior: false);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string? TryReadStringField(ClrObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var field = FindFieldByName(obj.Type, name);
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

    private static IEnumerable<ClrInstanceField> EnumerateInstanceFields(ClrType? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var field in current.Fields)
            {
                yield return field;
            }
        }
    }

    private static ClrInstanceField? FindFieldByName(ClrType? type, string fieldName)
        => EnumerateInstanceFields(type).FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal));

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

    private TaskTimerLeakView BuildTaskTimerLeakView(RawTaskTimerAggregation agg, int topN)
    {
        var timerRows = agg.TimersByCallback.Values
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Key.TimerTypeFullName, StringComparer.Ordinal)
            .Take(topN)
            .Select(t =>
            {
                Memory.MethodIdentity? method = null;
                if (t.Method is not null && t.Key.MetadataToken != 0)
                {
                    method = new Memory.MethodIdentity(
                        ModuleName: t.Key.ModulePath is { } mp ? Path.GetFileName(mp) : null,
                        ModulePath: t.Key.ModulePath,
                        ModuleVersionId: TryReadMvid(t.Key.ModulePath),
                        MetadataToken: t.Key.MetadataToken,
                        TypeFullName: t.Key.DeclaringTypeFullName ?? "<unknown>",
                        MethodName: t.Key.MethodName ?? "<unknown>",
                        GenericArity: 0);
                }

                return new TimerCallbackStat(
                    TimerTypeFullName: t.Key.TimerTypeFullName,
                    CallbackTargetTypeFullName: t.Key.CallbackTargetTypeFullName,
                    DeclaringTypeFullName: t.Key.DeclaringTypeFullName,
                    MethodName: t.Key.MethodName,
                    MethodSignature: t.Key.MethodSignature,
                    Count: t.Count)
                {
                    IsCanceled = t.Key.IsCanceled,
                    Method = method,
                };
            })
            .ToArray();

        var notes = new List<string>();
        if (agg.TotalTimers > timerRows.Sum(row => row.Count))
        {
            notes.Add("Timer callback rows are truncated to the top groups retained in the snapshot.");
        }

        return new TaskTimerLeakView(
            TotalTimers: agg.TotalTimers,
            TotalTasks: agg.TotalTasks,
            TotalTaskCompletionSources: agg.TotalTaskCompletionSources,
            TimersByCallback: timerRows,
            TasksByType: BuildTaskTypeStats(agg.TasksByType, topN),
            TaskCompletionSourcesByType: BuildTaskTypeStats(agg.TaskCompletionSourcesByType, topN),
            Notes: notes);
    }

    private TaskTypeStat[] BuildTaskTypeStats(Dictionary<TypeKey, RawTypeStat> agg, int topN)
        => agg.Values
            .OrderByDescending(t => t.Count)
            .ThenByDescending(t => t.Bytes)
            .Take(topN)
            .Select(t => new TaskTypeStat(
                TypeFullName: t.TypeName,
                ModuleName: t.ModuleName is { } mn ? Path.GetFileName(mn) : null,
                Count: t.Count,
                TotalBytes: t.Bytes)
            {
                Identity = BuildTypeIdentity(t.ClrType),
            })
            .ToArray();

    private static AssemblyLoadContextLeakView BuildAssemblyLoadContextLeakView(
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

        var retentionByContext = ResolveAssemblyLoadContextRetention(runtime, candidates, notes, ct);

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

    private static Dictionary<ulong, RawAssemblyLoadContextRetention> ResolveAssemblyLoadContextRetention(
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

    private static HeapObjectInspection InspectObject(ClrRuntime runtime, ulong address)
    {
        var obj = GetRequiredObject(runtime, address);
        var segment = runtime.Heap.GetSegmentByAddress(address)
            ?? throw new InvalidOperationException($"No GC segment contains object 0x{address:x}.");
        var warnings = new List<string>();

        var inspection = new HeapObjectInspection(
            Address: obj.Address,
            TypeFullName: obj.Type!.Name ?? "<unknown>",
            Size: (long)obj.Size,
            SegmentKind: segment.Kind.ToString(),
            Generation: segment.GetGeneration(obj.Address).ToString());

        if (obj.Type.IsString)
        {
            var value = obj.AsString(MaxStringPreviewLength);
            inspection = inspection with
            {
                IsString = true,
                StringValue = value,
                StringValueTruncated = value is not null && value.Length >= MaxStringPreviewLength,
            };
        }

        if (obj.IsArray)
        {
            inspection = inspection with
            {
                IsArray = true,
                ArrayLength = obj.AsArray().Length,
                ArraySample = SampleArray(obj, warnings),
            };
        }
        else
        {
            inspection = inspection with
            {
                Fields = ReadObjectFields(obj, warnings),
            };
        }

        return warnings.Count > 0
            ? inspection with { Warnings = warnings }
            : inspection;
    }

    private static HeapGcRootInspection InspectGcRoot(ClrRuntime runtime, ulong address)
    {
        var obj = GetRequiredObject(runtime, address);
        var target = new HashSet<ulong> { address };
        var warnings = new List<string>();
        var rootByObject = ClrMdRetentionAnalyzer.BuildRootByObjectMap(runtime, target, GcRootDepthLimit, MaxRetainedGraphObjects, out var bfsCapHit, CancellationToken.None);
        if (bfsCapHit)
        {
            warnings.Add($"GC-root BFS hit its safety cap before exhausting the search space for 0x{address:x}; chain may be truncated.");
        }

        var reachedByBfs = rootByObject.ContainsKey(address);
        var chain = ClrMdRetentionAnalyzer.BuildTypedRootChain(runtime, address, rootByObject, GcRootDepthLimit, out var truncated);
        if (!reachedByBfs || chain.Count == 0 || chain[0].RootKind is null)
        {
            throw new InvalidOperationException($"No GC root path could be found for object 0x{address:x}. If this came from a live snapshot, the object may have moved or been collected since capture.");
        }

        return new HeapGcRootInspection(
            Address: obj.Address,
            TypeFullName: obj.Type!.Name ?? "<unknown>",
            Chain: chain,
            Truncated: truncated || bfsCapHit)
        {
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private static HeapObjectSizeInspection InspectObjectSize(ClrRuntime runtime, ulong address)
    {
        var obj = GetRequiredObject(runtime, address);
        var visited = new HashSet<ulong>();
        var queue = new Queue<ClrObject>();
        queue.Enqueue(obj);
        long retainedBytes = 0;
        var truncated = false;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.IsNull || !current.IsValid || current.Type is null) continue;
            if (!visited.Add(current.Address)) continue;

            retainedBytes += (long)current.Size;
            if (visited.Count >= MaxRetainedGraphObjects)
            {
                truncated = true;
                break;
            }

            foreach (var child in current.EnumerateReferences(carefully: true, considerDependantHandles: true))
            {
                if (!child.IsNull && child.IsValid && child.Type is not null && !visited.Contains(child.Address))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return new HeapObjectSizeInspection(
            Address: obj.Address,
            TypeFullName: obj.Type!.Name ?? "<unknown>",
            RetainedBytes: retainedBytes,
            ObjectCount: visited.Count,
            Truncated: truncated)
        {
            Warnings = truncated
                ? [$"Object graph walk hit its safety cap of {MaxRetainedGraphObjects:N0} objects; retained bytes are a lower bound."]
                : null,
        };
    }

    private static ClrObject GetRequiredObject(ClrRuntime runtime, ulong address)
    {
        if (address == 0)
        {
            throw new ArgumentException("Object address must be a non-zero managed object reference.", nameof(address));
        }

        var obj = runtime.Heap.GetObject(address);
        if (obj.IsNull || !obj.IsValid || obj.Type is null || obj.IsFree)
        {
            throw new InvalidOperationException($"No valid managed object exists at address 0x{address:x}. If this came from a live snapshot, the object may have moved or been collected since capture.");
        }

        return obj;
    }

    private static List<HeapObjectField> ReadObjectFields(ClrObject obj, List<string> warnings)
    {
        var fields = new List<HeapObjectField>();
        AppendFields(obj.Address, interior: false, obj.Type!, prefix: null, depth: 0, fields, warnings);
        return fields;
    }

    private static void AppendFields(
        ulong address,
        bool interior,
        ClrType type,
        string? prefix,
        int depth,
        List<HeapObjectField> fields,
        List<string> warnings)
    {
        if (depth > MaxFieldDepth || fields.Count >= MaxFieldCount) return;

        foreach (var field in type.Fields)
        {
            if (fields.Count >= MaxFieldCount)
            {
                warnings.Add($"Field expansion hit its cap of {MaxFieldCount} rows; nested fields are truncated.");
                return;
            }

            var baseName = field.Name ?? $"<offset+0x{field.Offset:x}>";
            var fieldName = string.IsNullOrEmpty(prefix) ? baseName : $"{prefix}.{baseName}";

            try
            {
                if (field.IsObjectReference)
                {
                    var reference = field.ReadObject(address, interior);
                    fields.Add(FormatReferenceField(fieldName, field.Type, reference));
                    continue;
                }

                if (field.IsPrimitive)
                {
                    fields.Add(new HeapObjectField(fieldName, field.Type?.Name ?? field.ElementType.ToString(), ReadPrimitive(field, address, interior)));
                    continue;
                }

                if (field.IsValueType && field.Type is not null)
                {
                    var structValue = field.ReadStruct(address, interior);
                    if (!structValue.IsValid || structValue.Type is null)
                    {
                        fields.Add(new HeapObjectField(fieldName, field.Type.Name ?? "<value-type>", "<invalid value-type>"));
                        continue;
                    }

                    if (depth >= MaxFieldDepth || structValue.Type.Fields.Length == 0)
                    {
                        fields.Add(new HeapObjectField(fieldName, structValue.Type.Name ?? "<value-type>", $"<value-type size={structValue.Size}>"));
                        continue;
                    }

                    AppendFields(structValue.Address, interior: true, structValue.Type, fieldName, depth + 1, fields, warnings);
                    continue;
                }

                fields.Add(new HeapObjectField(fieldName, field.Type?.Name ?? field.ElementType.ToString(), $"<{field.ElementType}>"));
            }
            catch (Exception ex)
            {
                warnings.Add($"Reading field '{fieldName}' failed: {ex.GetType().Name} ({ex.Message}).");
            }
        }
    }

    private static HeapObjectField FormatReferenceField(string name, ClrType? declaredType, ClrObject reference)
    {
        if (reference.IsNull || !reference.IsValid || reference.Type is null)
        {
            return new HeapObjectField(name, declaredType?.Name ?? "<object>", "null");
        }

        if (reference.Type.IsString)
        {
            var preview = reference.AsString(MaxStringPreviewLength);
            var value = preview is null ? $"0x{reference.Address:x}" : $"0x{reference.Address:x} \"{preview}\"";
            return new HeapObjectField(name, declaredType?.Name ?? reference.Type.Name ?? "System.String", value)
            {
                ObjectAddress = reference.Address,
                ReferencedTypeFullName = reference.Type.Name,
            };
        }

        return new HeapObjectField(name, declaredType?.Name ?? reference.Type.Name ?? "<object>", $"0x{reference.Address:x}")
        {
            ObjectAddress = reference.Address,
            ReferencedTypeFullName = reference.Type.Name,
        };
    }

    private static List<HeapArrayElement> SampleArray(ClrObject obj, List<string> warnings)
    {
        var array = obj.AsArray();
        var count = Math.Min(array.Length, MaxArraySampleCount);
        var elementType = array.Type.ComponentType;
        var elements = new List<HeapArrayElement>(count);

        for (var i = 0; i < count; i++)
        {
            try
            {
                elements.Add(ReadArrayElement(array, elementType, i));
            }
            catch (Exception ex)
            {
                warnings.Add($"Reading array element [{i}] failed: {ex.GetType().Name} ({ex.Message}).");
            }
        }

        return elements;
    }

    private static HeapArrayElement ReadArrayElement(ClrArray array, ClrType? elementType, int index)
    {
        var typeName = elementType?.Name ?? "<unknown>";
        if (elementType?.IsObjectReference == true || elementType?.IsString == true || elementType?.IsArray == true)
        {
            var value = array.GetObjectValue(index);
            if (value.IsNull || !value.IsValid || value.Type is null)
            {
                return new HeapArrayElement(index, typeName, "null");
            }

            if (value.Type.IsString)
            {
                var preview = value.AsString(MaxStringPreviewLength);
                return new HeapArrayElement(index, typeName, preview is null ? $"0x{value.Address:x}" : $"0x{value.Address:x} \"{preview}\"")
                {
                    ObjectAddress = value.Address,
                    ReferencedTypeFullName = value.Type.Name,
                };
            }

            return new HeapArrayElement(index, typeName, $"0x{value.Address:x}")
            {
                ObjectAddress = value.Address,
                ReferencedTypeFullName = value.Type.Name,
            };
        }

        if (elementType?.IsValueType == true && !elementType.IsPrimitive)
        {
            var structValue = array.GetStructValue(index);
            return new HeapArrayElement(index, typeName, structValue.IsValid ? $"<value-type size={structValue.Size}>" : "<invalid value-type>");
        }

        return new HeapArrayElement(index, typeName, ReadPrimitiveArrayValue(array, elementType, index));
    }

    private static string ReadPrimitiveArrayValue(ClrArray array, ClrType? elementType, int index) => elementType?.ElementType switch
    {
        ClrElementType.Boolean => array.GetValue<bool>(index) ? "true" : "false",
        ClrElementType.Char => $"'{array.GetValue<char>(index)}'",
        ClrElementType.Int8 => array.GetValue<sbyte>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt8 => array.GetValue<byte>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int16 => array.GetValue<short>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt16 => array.GetValue<ushort>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int32 => array.GetValue<int>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt32 => array.GetValue<uint>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int64 => array.GetValue<long>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt64 => array.GetValue<ulong>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeInt => ((long)array.GetValue<nint>(index)).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeUInt => ((ulong)array.GetValue<nuint>(index)).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Float => array.GetValue<float>(index).ToString("R", CultureInfo.InvariantCulture),
        ClrElementType.Double => array.GetValue<double>(index).ToString("R", CultureInfo.InvariantCulture),
        _ => $"<{elementType?.ElementType.ToString() ?? "Unknown"}>",
    };

    private static string ReadPrimitive(ClrInstanceField field, ulong address, bool interior) => field.ElementType switch
    {
        ClrElementType.Boolean => field.Read<bool>(address, interior) ? "true" : "false",
        ClrElementType.Char => $"'{field.Read<char>(address, interior)}'",
        ClrElementType.Int8 => field.Read<sbyte>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt8 => field.Read<byte>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int16 => field.Read<short>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt16 => field.Read<ushort>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int32 => field.Read<int>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt32 => field.Read<uint>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int64 => field.Read<long>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt64 => field.Read<ulong>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeInt => ((long)field.Read<nint>(address, interior)).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeUInt => ((ulong)field.Read<nuint>(address, interior)).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Float => field.Read<float>(address, interior).ToString("R", CultureInfo.InvariantCulture),
        ClrElementType.Double => field.Read<double>(address, interior).ToString("R", CultureInfo.InvariantCulture),
        _ => $"<{field.ElementType}>",
    };

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

    private sealed class RawTaskTimerAggregation
    {
        public long TotalTimers;
        public long TotalTasks;
        public long TotalTaskCompletionSources;
        public HashSet<ulong> SeenTimerAddresses { get; } = new();
        public Dictionary<TimerCallbackKey, RawTimerCallbackStat> TimersByCallback { get; } = new();
        public Dictionary<TypeKey, RawTypeStat> TasksByType { get; } = new();
        public Dictionary<TypeKey, RawTypeStat> TaskCompletionSourcesByType { get; } = new();
    }

    private readonly record struct TimerCallbackKey(
        string TimerTypeFullName,
        string? CallbackTargetTypeFullName,
        string? DeclaringTypeFullName,
        string? MethodName,
        string? MethodSignature,
        string? ModulePath,
        int MetadataToken,
        bool? IsCanceled);

    private sealed class RawTimerCallbackStat
    {
        public RawTimerCallbackStat(TimerCallbackKey key, ClrMethod? method)
        {
            Key = key;
            Method = method;
        }

        public TimerCallbackKey Key { get; }
        public ClrMethod? Method { get; }
        public long Count;
    }

    private sealed record RawTimerCallback(
        string? TargetTypeFullName,
        string DeclaringTypeFullName,
        string MethodName,
        string? MethodSignature,
        string? ModulePath,
        int MetadataToken,
        ClrMethod Method);

    private sealed class RawAssemblyLoadContextAggregation
    {
        public Dictionary<ulong, RawAssemblyLoadContextStat> Contexts { get; } = new();
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

    private sealed class RawAssemblyLoadContextStat
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
        public Dictionary<AssemblyLoadContextAssemblyKey, RawAssemblyLoadContextAssemblyStat> Assemblies { get; } = new();

        public void NoteCollectible(bool isCollectible)
        {
            if (!CollectibleResolvedFromField || IsCollectible is null)
            {
                IsCollectible = isCollectible;
            }
        }
    }

    private readonly record struct AssemblyLoadContextAssemblyKey(string AssemblyName, string? ModulePath);

    private sealed class RawAssemblyLoadContextAssemblyStat
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
