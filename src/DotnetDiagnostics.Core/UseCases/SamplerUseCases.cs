using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Core.Threads;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral CPU / allocation / off-CPU / native-allocation / thread-snapshot collection use
/// cases. Owns the full <see cref="DiagnosticResult{T}"/> orchestration — argument validation,
/// symbol-path validation, process resolution, attach guarding, handle registration, summary text,
/// signals and next-action hints — while depending only on Core abstractions, so any front-end can
/// compose these samplers without referencing the MCP assembly.
/// </summary>
public static class SamplerUseCases
{
    private static readonly TimeSpan SampleHandleTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ThreadSnapshotHandleTtl = TimeSpan.FromMinutes(10);

    public const string OffCpuHandleKind = "off-cpu-snapshot";
    public const string NativeAllocHandleKind = "native-alloc-sample";
    public const string ThreadSnapshotKind = "thread-snapshot";

    public static async Task<DiagnosticResult<CpuSample>> CollectCpuSample(
        ICpuSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        bool principalAllowsSymbolsRemote,
        int? processId = null,
        int durationSeconds = 10,
        int topN = 25,
        bool resolveSourceLines = true,
        string? symbolPath = null,
        bool resolveMethodInstantiations = false,
        string? nativeAotMapFile = null,
        SamplingDepth depth = SamplingDepth.Summary,
        bool exportTrace = false,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<CpuSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<CpuSample>(nameof(topN), "must be >= 1");

        if (resolveSourceLines)
        {
            var symbolDenial = SymbolPathValidation.Validate<CpuSample>(
                symbolServerAllowlist, symbolPath, principalAllowsSymbolsRemote);
            if (symbolDenial is not null) return symbolDenial;
        }

        var resolved = await ResolveContextAsync<CpuSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        var srcOpts = resolveSourceLines
            ? new SourceResolutionOptions(Enabled: true, SymbolPath: symbolPath, MaxResolved: topN)
            : null;
        var instantiationOpts = resolveMethodInstantiations
            ? new MethodInstantiationResolutionOptions(Enabled: true, MaxResolved: topN)
            : null;
        var nativeAotOpts = string.IsNullOrWhiteSpace(nativeAotMapFile)
            ? null
            : new NativeAotSymbolResolutionOptions(MapFilePath: nativeAotMapFile);

        CpuSampleResult result;
        try
        {
            result = await sampler.SampleAsync(
                pid,
                TimeSpan.FromSeconds(durationSeconds),
                topN,
                srcOpts,
                instantiationOpts,
                nativeAotOpts,
                exportTrace,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("elevation", StringComparison.OrdinalIgnoreCase)
                                                    || ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase)
                                                    || ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase)
                                                    || ex.Message.Contains("NativeAOT", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<CpuSample>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process", "Check capability matrix to confirm what's available for this process.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (Exception ex) when (resolveMethodInstantiations && ex is not OperationCanceledException)
        {
            return WithContext(AttachGuard.ClassifyAttachFailure<CpuSample>("collect_sample", pid, ex), ctx);
        }

        var handle = handles.Register(
            pid,
            "cpu-sample",
            result.Artifact,
            SampleHandleTtl,
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live);
        var signals = CpuSampleSignals.Detect(result.Summary, handle.Id);

        var hints = new List<NextActionHint>
        {
            new("query_snapshot", "Rank methods by self-time (exclusive) — where CPU is actually spent, past the inclusive threadpool/dispatch roots.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "top-methods", ["rankBy"] = "exclusive" })
            { Priority = NextActionHintPriority.High },
            new("query_snapshot", "Walk the merged caller→callee tree built from the same samples.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["maxDepth"] = 8, ["maxNodes"] = 200 })
            { Priority = NextActionHintPriority.High },
            new("collect_events", "Confirm hot path isn't driven by exception-heavy control flow.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "exceptions",
                    ["processId"] = pid,
                    ["durationSeconds"] = 10,
                }),
        };

        if (!string.IsNullOrEmpty(result.Artifact.TracePath))
        {
            hints.Add(new NextActionHint("get_bytes", "Fetch the raw .nettrace for offline PerfView/Speedscope/Perfetto analysis.",
                new Dictionary<string, object?> { ["kind"] = "trace", ["traceFilePath"] = result.Artifact.TracePath }));
        }

        var ok = BuildCpuSampleResult(
            result.Summary,
            durationSeconds,
            handle.Id,
            handle.ExpiresAt,
            depth,
            result.Artifact.TracePath,
            signals,
            [.. hints]);
        return WithContext(ok, ctx);
    }

    public static async Task<DiagnosticResult<AllocationSample>> CollectAllocationSample(
        EventPipeAllocationSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        int? processId = null,
        int durationSeconds = 10,
        int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<AllocationSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<AllocationSample>(nameof(topN), "must be >= 1");

        var resolved = await ResolveContextAsync<AllocationSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        AllocationSampleResult result;
        try
        {
            result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return DiagnosticResult.Fail<AllocationSample>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process", "Check capability matrix to confirm what's available for this process.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }

        var sample = result.Summary;
        var handle = handles.Register(
            pid,
            "allocation-sample",
            new AllocationSampleArtifact(sample, result.Artifact),
            SampleHandleTtl,
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live);
        var signals = AllocationSignals.Detect(sample, handle.Id);

        var topType = sample.TopByBytes.Count > 0 ? sample.TopByBytes[0] : null;
        var unknownOnly = topType?.TypeName == "<unknown>" && sample.TopByBytes.Count == 1;
        var summaryText = unknownOnly
            ? $"Captured {sample.TotalEvents} allocation events ({sample.TotalBytes:N0} bytes total) over {durationSeconds}s, " +
              $"but TypeName was empty for all events (expected on NativeAOT). " +
              $"Drill into allocation call sites with query_snapshot(handle=\"{handle.Id}\", view=\"call-tree\") to see native allocation frames."
            : topType is not null
                ? $"Captured {sample.TotalEvents} allocation events ({sample.TotalBytes:N0} bytes total) over {durationSeconds}s. " +
                  $"Top type by bytes: {topType.TypeName} ({topType.TotalBytes:N0} bytes, {topType.EventCount} events). " +
                  $"Drill into allocation call sites with query_snapshot(handle=\"{handle.Id}\", view=\"call-tree\")."
                : $"Captured {sample.TotalEvents} allocation events but no type aggregation surfaced — " +
                  $"increase durationSeconds or drive a workload that allocates during the window.";

        var ok = DiagnosticResult.OkWithHandle(
            sample,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot", "Walk the merged allocation call-site tree to find which code paths are allocating the most.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["maxDepth"] = 8, ["maxNodes"] = 200 })
            { Priority = NextActionHintPriority.High },
            new NextActionHint("collect_sample", "Cross-reference: identify hot CPU paths that correlate with the top allocating types.",
                new Dictionary<string, object?> { ["kind"] = "cpu", ["processId"] = pid, ["durationSeconds"] = durationSeconds }),
            new NextActionHint("collect_events", "Observe GC pause frequency and generation distribution caused by this allocation load.",
                new Dictionary<string, object?> { ["kind"] = "gc", ["processId"] = pid, ["durationSeconds"] = durationSeconds }))
            with
        { Signals = signals.Count > 0 ? signals : null };
        return WithContext(ok, ctx);
    }

    public static async Task<DiagnosticResult<OffCpuSnapshot>> CollectOffCpuSample(
        IOffCpuSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        bool principalAllowsSymbolsRemote,
        int? processId = null,
        int durationSeconds = 10,
        int topN = 25,
        string? symbolPath = null,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<OffCpuSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<OffCpuSnapshot>(nameof(topN), "must be >= 1");

        var symbolDenial = SymbolPathValidation.Validate<OffCpuSnapshot>(
            symbolServerAllowlist, symbolPath, principalAllowsSymbolsRemote);
        if (symbolDenial is not null) return symbolDenial;

        var resolved = await ResolveContextAsync<OffCpuSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        OffCpuSampleResult result;
        try
        {
            result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, symbolPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                ex.Message,
                new DiagnosticError("NotSupported", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "Confirm which signals are available on this host before retrying.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                $"collect_sample(kind=\"off_cpu\") could not start NT Kernel Logger capture for pid {pid}: Windows denied access to the ContextSwitch provider.",
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "After granting either BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance') to the sidecar account and restarting the Windows service, re-check capabilities before retrying.",
                    new Dictionary<string, object?> { ["processId"] = pid }),
                new NextActionHint("collect_sample",
                    "Retry after the sidecar account has one of the two supported Windows paths: BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance').",
                    new Dictionary<string, object?> { ["kind"] = "off_cpu", ["processId"] = pid, ["durationSeconds"] = durationSeconds, ["topN"] = topN }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("CAP_", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("paranoid", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "Check capability matrix; install linux-perf and add CAP_PERFMON to the sidecar securityContext.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }

        var summary = result.Summary;
        var handle = handles.Register(
            pid,
            OffCpuHandleKind,
            result.Artifact,
            SampleHandleTtl,
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live);

        var inlineSummary = summary;
        var droppedStacks = 0;
        if (depth == SamplingDepth.Summary && summary.TopBlockingStacks.Count > 3)
        {
            droppedStacks = summary.TopBlockingStacks.Count - 3;
            inlineSummary = summary with { TopBlockingStacks = summary.TopBlockingStacks.Take(3).ToArray() };
        }

        var topStack = summary.TopBlockingStacks.Count > 0 ? summary.TopBlockingStacks[0] : null;
        var summaryText = topStack is not null
            ? (depth == SamplingDepth.Summary && droppedStacks > 0
                ? $"Captured {summary.SchedSwitches} switches across {summary.DistinctThreads} threads over {durationSeconds}s — showing top {inlineSummary.TopBlockingStacks.Count} of {summary.TopBlockingStacks.Count} blocking stack(s) (dropped {droppedStacks}; handle has all). " +
                  $"Total off-CPU: {summary.TotalOffCpuMicros / 1000.0:F1} ms. " +
                  $"Top blocker: {topStack.LeafFrame} ({topStack.OffCpuMicros / 1000.0:F1} ms, state={topStack.DominantState}). " +
                  $"Drill with query_snapshot(handle=\"{handle.Id}\")."
                : $"Captured {summary.SchedSwitches} switches across {summary.DistinctThreads} threads over {durationSeconds}s. " +
                  $"Total off-CPU: {summary.TotalOffCpuMicros / 1000.0:F1} ms. " +
                  $"Top blocker: {topStack.LeafFrame} ({topStack.OffCpuMicros / 1000.0:F1} ms, state={topStack.DominantState}). " +
                  $"Drill with query_snapshot(handle=\"{handle.Id}\").")
            : $"Captured {summary.SchedSwitches} switches but no off-CPU spans closed within the window. " +
              "Either no thread blocked, or wakeups landed outside the capture — try a longer durationSeconds.";

        var ok = DiagnosticResult.OkWithHandle(
            inlineSummary,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot", "Drill into per-thread off-CPU view or a specific stack.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byThread" }),
            new NextActionHint("collect_sample", "Cross-reference with on-CPU hotspots to separate compute from wait.",
                new Dictionary<string, object?> { ["kind"] = "cpu", ["processId"] = pid, ["durationSeconds"] = 10 }));
        return WithContext(ok, resolved.Context);
    }

    public static async Task<DiagnosticResult<NativeAllocSample>> CollectNativeAllocSample(
        INativeAllocSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        int? processId = null,
        int durationSeconds = 10,
        int topN = 25,
        long samplePeriod = 1000,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<NativeAllocSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<NativeAllocSample>(nameof(topN), "must be >= 1");
        if (samplePeriod < 1) return InvalidArg<NativeAllocSample>(nameof(samplePeriod), "must be >= 1");

        var resolved = await ResolveContextAsync<NativeAllocSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        NativeAllocSampleResult result;
        try
        {
            result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, samplePeriod, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return DiagnosticResult.Fail<NativeAllocSample>(
                ex.Message,
                new DiagnosticError("NotSupported", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "Confirm the target is a dynamically-linked glibc/musl process; statically-linked or custom-allocator (jemalloc/tcmalloc) targets aren't supported by the libc uprobe path.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return DiagnosticResult.Fail<NativeAllocSample>(
                $"collect_sample(kind=\"native-alloc\") could not start NT Kernel Logger VirtualAlloc capture for pid {pid}: Windows denied access to the provider.",
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "After granting either BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance') to the sidecar account and restarting the Windows service, re-check capabilities before retrying.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("uprobe", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("tracefs", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("CAP_", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("ETW", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("paranoid", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<NativeAllocSample>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "Check the capability matrix; on Linux install linux-perf and add CAP_SYS_ADMIN to the sidecar securityContext, on Windows run the sidecar elevated.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }

        var sample = result.Summary;
        var handle = handles.Register(
            pid,
            NativeAllocHandleKind,
            result.Artifact,
            SampleHandleTtl,
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live);

        var topAllocator = sample.TopAllocators.Count > 0 ? sample.TopAllocators[0] : null;
        var summaryText = topAllocator is not null
            ? $"Captured {sample.TotalSampledAllocations} sampled native allocator-call(s) over {durationSeconds}s " +
              $"(probed {string.Join("/", sample.ProbedFunctions)} in {sample.LibcPath}, samplePeriod={sample.SamplePeriod}). " +
              $"Top allocator stack: {topAllocator.Frame.Method} ({topAllocator.InclusiveSamples} inclusive hits). " +
              $"Counts are calls, not bytes. Drill into call sites with query_snapshot(handle=\"{handle.Id}\", view=\"call-tree\")."
            : $"Probed {string.Join("/", sample.ProbedFunctions)} in {sample.LibcPath} but captured no native " +
              $"allocator-call samples in {durationSeconds}s — the workload may not allocate natively, or samplePeriod " +
              "is too high. Drive the suspect load during the window or lower samplePeriod.";

        var ok = DiagnosticResult.OkWithHandle(
            sample,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot", "Walk the native allocation call tree to find which code paths allocate the most.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "call-tree", ["maxDepth"] = 8, ["maxNodes"] = 200 }),
            new NextActionHint("inspect_process", "Correlate with the memory trend (RSS / anonymous pages) to confirm native growth.",
                new Dictionary<string, object?> { ["processId"] = pid, ["view"] = "memory_trend" }));
        return WithContext(ok, resolved.Context);
    }

    public static async Task<DiagnosticResult<ThreadSnapshotQueryResult>> CollectThreadSnapshot(
        IThreadSnapshotInspector inspector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        bool principalAllowsSymbolsRemote,
        int? processId = null,
        string? dumpFilePath = null,
        int maxFramesPerThread = 64,
        bool includeRuntimeFrames = false,
        bool includeNativeFrames = false,
        string? symbolPath = null,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        var hasExplicitPid = processId.HasValue && processId.Value != 0;
        var hasDump = !string.IsNullOrWhiteSpace(dumpFilePath);
        if (hasExplicitPid && hasDump)
        {
            return InvalidArg<ThreadSnapshotQueryResult>(nameof(dumpFilePath), "processId and dumpFilePath are mutually exclusive");
        }
        if (maxFramesPerThread < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(maxFramesPerThread), "must be >= 1");
        if (maxFramesPerThread > ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap)
        {
            return InvalidArg<ThreadSnapshotQueryResult>(
                nameof(maxFramesPerThread),
                $"must be <= {ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap} (bounds the live-attach suspend window)");
        }

        var symbolDenial = SymbolPathValidation.Validate<ThreadSnapshotQueryResult>(
            symbolServerAllowlist, symbolPath, principalAllowsSymbolsRemote);
        if (symbolDenial is not null) return symbolDenial;

        int livePid = 0;
        ProcessContext? liveCtx = null;
        if (!hasDump)
        {
            var resolved = await ResolveContextAsync<ThreadSnapshotQueryResult>(resolver, processId, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            livePid = resolved.ProcessId;
            liveCtx = resolved.Context;
        }

        return await AttachGuard.GuardAttachAsync("collect_thread_snapshot", hasDump ? (int?)null : livePid, async () =>
        {
            var opts = new ThreadSnapshotOptions(maxFramesPerThread, includeRuntimeFrames, includeNativeFrames, symbolPath);
            ThreadSnapshotArtifact snapshot;
            if (hasDump)
            {
                snapshot = await inspector.InspectDumpAsync(dumpFilePath!, opts, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                snapshot = await inspector.InspectLiveAsync(livePid, opts, cancellationToken).ConfigureAwait(false);
            }

            var handle = handles.Register(
                snapshot.ProcessId,
                ThreadSnapshotKind,
                snapshot,
                ThreadSnapshotHandleTtl,
                evictWhenProcessExits: false,
                origin: snapshot.Origin == ThreadSnapshotOrigin.Live ? HandleOrigin.Live : HandleOrigin.Dump);
            var origin = snapshot.Origin.ToString().ToLowerInvariant();
            var blocked = snapshot.Threads.Count(t => t.IsLikelyBlocked);
            var contended = snapshot.Locks.Count(l => l.IsContended);
            var signals = ThreadWaitSignals.Detect(snapshot, handle.Id);

            ThreadSnapshotQueryResult summaryView;
            string summary;
            if (depth == SamplingDepth.Summary)
            {
                var topBlocked = snapshot.Threads
                    .OrderByDescending(t => t.IsLikelyBlocked)
                    .ThenByDescending(t => t.LockCount)
                    .Take(3)
                    .ToArray();
                summaryView = new ThreadSnapshotQueryResult(handle.Id, "top-blocked", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
                {
                    Threads = topBlocked,
                    Locks = Array.Empty<MonitorLockState>(),
                };
                var droppedThreads = snapshot.Threads.Count - topBlocked.Length;
                summary = $"{origin} thread snapshot of pid {snapshot.ProcessId}: {snapshot.Threads.Count} thread(s) ({blocked} likely blocked), {snapshot.Locks.Count} SyncBlock(s) ({contended} contended). Showing top {topBlocked.Length} blocked inline (dropped {droppedThreads} thread(s) and {snapshot.Locks.Count} lock(s); handle has all). Walk {snapshot.WalkDuration.TotalMilliseconds:N0} ms. Handle `{handle.Id}` (~10 min). Views: top-blocked|threads-summary|stack|lock-graph|deadlocks|unique-stacks|async-stalls|threadpool.";
            }
            else
            {
                summaryView = new ThreadSnapshotQueryResult(handle.Id, "threads-summary", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
                {
                    Threads = snapshot.Threads.Take(25).ToArray(),
                    Locks = snapshot.Locks.Take(25).ToArray(),
                };
                summary = $"{origin} thread snapshot of pid {snapshot.ProcessId}: {snapshot.Threads.Count} thread(s) ({blocked} likely blocked), {snapshot.Locks.Count} SyncBlock(s) ({contended} contended). Walk {snapshot.WalkDuration.TotalMilliseconds:N0} ms. Handle `{handle.Id}` (~10 min). Views: top-blocked|threads-summary|stack|lock-graph|deadlocks|unique-stacks|async-stalls|threadpool.";
            }

            if (snapshot.SnapshotKind is not "exact")
            {
                summary += $" SnapshotKind={snapshot.SnapshotKind}";
                if (snapshot.WindowSeconds is int w)
                {
                    summary += $" over {w}s window";
                }

                summary += ".";
            }
            if (snapshot.Warnings is { Count: > 0 })
            {
                summary += $" Caveats: {string.Join(" ", snapshot.Warnings.Take(3))}";
            }

            var hint = contended > 0
                ? new NextActionHint("query_snapshot",
                    "Check the captured lock graph for wait-for cycles before drilling into individual stacks.",
                    new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "deadlocks" })
                : blocked > 0
                    ? new NextActionHint("query_snapshot",
                        "Drill into the top blocked threads.",
                        new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "top-blocked" })
                    : null;

            var result = hint is null
                ? DiagnosticResult.Ok(summaryView, summary)
                : DiagnosticResult.Ok(summaryView, summary, hint);
            return WithContext(result with { Signals = signals.Count > 0 ? signals : null }, liveCtx);
        }, cancellationToken, retryArguments: hasDump
            ? null
            : new Dictionary<string, object?>
            {
                ["processId"] = livePid,
                ["maxFramesPerThread"] = maxFramesPerThread,
                ["includeRuntimeFrames"] = includeRuntimeFrames,
                ["includeNativeFrames"] = includeNativeFrames,
                ["symbolPath"] = symbolPath,
                ["depth"] = depth,
            }).ConfigureAwait(false);
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Invalid argument '{parameterName}': {requirement}.",
            new DiagnosticError("InvalidArgument", $"Parameter '{parameterName}' {requirement}.", parameterName));

    private static DiagnosticResult<CpuSample> BuildCpuSampleResult(
        CpuSample sample,
        int durationSeconds,
        string handleId,
        DateTimeOffset handleExpiresAt,
        SamplingDepth depth,
        string? tracePath,
        IReadOnlyList<SignalGroup> signals,
        params NextActionHint[] hints)
    {
        var top = sample.TopHotspots.Count > 0 ? sample.TopHotspots[0] : null;
        var topSelfTime = sample.TopSelfTime
            ?? (sample.TopHotspots.Count > 0
                ? sample.TopHotspots.Aggregate((a, b) => b.ExclusiveSamples > a.ExclusiveSamples ? b : a)
                : null);
        var overallSelfSplit = sample.SelfSamples is { } overall
            ? $" Self split: {overall.RunningSamples} running / {overall.WaitingSamples} waiting."
            : string.Empty;
        var inlineSample = sample;
        var droppedHotspots = 0;
        if (depth == SamplingDepth.Summary && sample.TopHotspots.Count > 3)
        {
            droppedHotspots = sample.TopHotspots.Count - 3;
            inlineSample = sample with { TopHotspots = sample.TopHotspots.Take(3).ToArray() };
        }

        string leadPhrase;
        if (topSelfTime is not null && topSelfTime.ExclusiveSamples > 0)
        {
            var selfPercent = sample.TotalSamples > 0 ? topSelfTime.ExclusiveSamples * 100.0 / sample.TotalSamples : 0;
            var splitSuffix = topSelfTime.SelfSamples is { } self
                ? $" Self split: {self.RunningSamples} running / {self.WaitingSamples} waiting."
                : string.Empty;
            leadPhrase =
                $"Hottest self-time method: {topSelfTime.Frame.Method} ({topSelfTime.ExclusiveSamples} exclusive, {selfPercent:0.#}% of samples).{splitSuffix} " +
                $"Rank self-time with query_snapshot(handle=\"{handleId}\", view=\"top-methods\") or walk the call path with view=\"call-tree\".";
        }
        else if (top is not null)
        {
            var splitSuffix = top.SelfSamples is { } self
                ? $" Self split: {self.RunningSamples} running / {self.WaitingSamples} waiting."
                : string.Empty;
            leadPhrase =
                $"Top inclusive method: {top.Frame.Method} ({top.InclusiveSamples} inclusive / {top.ExclusiveSamples} exclusive).{splitSuffix} " +
                "That top entry may reflect a wait/blocking primitive on CoreCLR EventPipe captures — " +
                $"no dominant self-time frame (the workload looks blocked/wait-bound or symbols are unresolved). " +
                $"Walk the call path with query_snapshot(handle=\"{handleId}\", view=\"call-tree\").";
        }
        else
        {
            leadPhrase = string.Empty;
        }

        var summary = top is not null
            ? (depth == SamplingDepth.Summary && droppedHotspots > 0
                ? $"Captured {sample.TotalSamples} samples over {durationSeconds}s — showing top {inlineSample.TopHotspots.Count} of {sample.TopHotspots.Count} hotspot(s) (dropped {droppedHotspots}; handle has all).{overallSelfSplit} {leadPhrase}"
                : $"Captured {sample.TotalSamples} samples over {durationSeconds}s.{overallSelfSplit} {leadPhrase}")
            : $"Captured {sample.TotalSamples} samples but no method aggregation surfaced — increase durationSeconds or verify the target is under load.";
        if (!string.IsNullOrEmpty(tracePath))
        {
            summary += $" Raw trace exported to '{tracePath}' — fetch with get_bytes(kind=\"trace\").";
        }

        return DiagnosticResult.OkWithHandle(inlineSample, summary, handleId, handleExpiresAt, hints)
            with
        { Signals = signals.Count > 0 ? signals : null };
    }
}
