using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Diagnostics;
using DotnetDiagnostics.Mcp.Security;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Mcp.Tools;

internal static class DiagnosticToolSampling
{
    private static readonly TimeSpan CpuSampleHandleTtl = TimeSpan.FromMinutes(10);

    internal const string OffCpuHandleKind = "off-cpu-snapshot";
    internal const string NativeAllocHandleKind = "native-alloc-sample";

    public static async Task<DiagnosticResult<CpuSample>> CollectCpuSample(
        ICpuSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of hotspots to return. Must be >= 1. Defaults to 25.")] int topN = 25,
        [Description("If true, attempts to resolve top hotspots to file:line via PDB / SourceLink and stamps the resolved SourceLocation onto each MethodIdentity payload (issue #28 — makes dotnet-assembly-mcp.get_method_source optional when PDBs are reachable). Defaults to true; set to false to skip PDB I/O when symbols are known to be unreachable.")] bool resolveSourceLines = true,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to the symbol reader (e.g. '/symbols' or 'srv*c:\\symcache*https://msdl.microsoft.com/download/symbols'). Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule/module directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on the `Diagnostics:SymbolServerAllowlist` allowlist or the call is rejected with a `SymbolServerNotAllowed` envelope. Local file paths always pass through. Ignored when resolveSourceLines=false.")] string? symbolPath = null,
        [Description("Cap on how many top hotspots get source-resolved. Must be >= 1. Defaults to the requested topN so every emitted MethodIdentity carries its resolved SourceLocation when available.")] int? maxResolvedSources = null,
        [Description("If true, performs an opt-in ClrMD attach after sampling to recover closed generic instantiations for the hottest managed frames (displayed on MethodIdentity as ClosedSignature + GenericTypeArguments.Method). CoreCLR only. On Linux this requires CAP_SYS_PTRACE (or ptrace_scope=0) and briefly suspends the target during the attach. Defaults to false to keep the EventPipe-only path lightweight.")] bool resolveMethodInstantiations = false,
        [Description("Cap on how many top hotspots get ClrMD generic-instantiation enrichment. Must be >= 1. Defaults to the requested topN so the enrichment work stays bounded to the hottest frames.")] int? maxResolvedMethodInstantiations = null,
        [Description("NativeAOT only. Filesystem path to the ILC '*.map.xml' map file produced by publishing with <IlcGenerateMapFile>true</IlcGenerateMapFile> (ilc --map). When supplied, the perf-based AOT sampler emits a name-based MethodIdentity (TypeFullName + MethodName; MVID/metadata token stay null) for hot managed methods so the dotnet-native-mcp 'disassemble this hot AOT function' handoff works. Ignored on CoreCLR. The path is a hint only — the consumer must verify the artifact before loading it.")] string? nativeAotMapFile = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns the top-3 hotspots inline. 'detail' returns the requested topN (default 25). 'raw' is equivalent to detail. The full sample is always retained behind the issued handle — drill in with query_snapshot(view='call-tree').")]
        SamplingDepth depth = SamplingDepth.Summary,
        [Description("If true, persists the raw .nettrace under the artifact root and returns its relative path so it can be fetched with get_bytes(kind='trace') for offline PerfView/Speedscope/Perfetto analysis. Defaults to false (the trace is parsed then deleted).")] bool exportTrace = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<CpuSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<CpuSample>(nameof(topN), "must be >= 1");
        var effectiveMaxResolved = maxResolvedSources ?? topN;
        if (effectiveMaxResolved < 1) return InvalidArg<CpuSample>(nameof(maxResolvedSources), "must be >= 1");
        var effectiveMaxResolvedInstantiations = maxResolvedMethodInstantiations ?? topN;
        if (effectiveMaxResolvedInstantiations < 1) return InvalidArg<CpuSample>(nameof(maxResolvedMethodInstantiations), "must be >= 1");

        if (resolveSourceLines)
        {
            var symbolDenial = ValidateSymbolPath<CpuSample>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
            if (symbolDenial is not null) return symbolDenial;
        }

        var resolved = await ResolveContextAsync<CpuSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        var srcOpts = resolveSourceLines
            ? new SourceResolutionOptions(Enabled: true, SymbolPath: symbolPath, MaxResolved: effectiveMaxResolved)
            : null;
        var instantiationOpts = resolveMethodInstantiations
            ? new MethodInstantiationResolutionOptions(Enabled: true, MaxResolved: effectiveMaxResolvedInstantiations)
            : null;
        var nativeAotOpts = string.IsNullOrWhiteSpace(nativeAotMapFile)
            ? null
            : new NativeAotSymbolResolutionOptions(MapFilePath: nativeAotMapFile);

        CpuSampleResult result;
        try
        {
            result = await CollectionProgressTicker.RunAsync(
                requestContext,
                "collect_sample(kind=\"cpu\")",
                TimeSpan.FromSeconds(durationSeconds),
                TimeSpan.FromSeconds(1),
                ct => sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, srcOpts, instantiationOpts, nativeAotOpts, exportTrace, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WithContext(
                new DiagnosticResult<CpuSample>(
                    $"CPU sampling cancelled by the client after starting against pid {pid}. " +
                    "No samples were retained — restart the collection to capture data.",
                    Array.Empty<NextActionHint>())
                {
                    Cancelled = true,
                },
                ctx);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("elevation", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("NativeAOT", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<CpuSample>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process", "Check capability matrix to confirm what's available for this process.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (Exception ex) when (resolveMethodInstantiations && ex is not OperationCanceledException)
        {
            return WithContext(ClassifyAttachFailure<CpuSample>("collect_sample", pid, ex), ctx);
        }

        var handle = handles.Register(
            pid,
            "cpu-sample",
            result.Artifact,
            CpuSampleHandleTtl,
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live);
        var signals = CpuSampleSignals.Detect(result.Summary, handle.Id);
        var hints = new List<NextActionHint>();

        hints.Add(new NextActionHint("query_snapshot", "Rank methods by self-time (exclusive) — where CPU is actually spent, past the inclusive threadpool/dispatch roots.",
            new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "top-methods", ["rankBy"] = "exclusive" })
        { Priority = NextActionHintPriority.High });
        hints.Add(new NextActionHint("query_snapshot", "Walk the merged caller→callee tree built from the same samples.",
            new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "call-tree", ["maxDepth"] = 8, ["maxNodes"] = 200 })
        { Priority = NextActionHintPriority.High });
        hints.Add(new NextActionHint("collect_events", "Confirm hot path isn't driven by exception-heavy control flow.",
            new Dictionary<string, object?> { ["kind"] = "exceptions", ["processId"] = pid, ["durationSeconds"] = 10 }));

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
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of types to return in each top-N list (TopByBytes and TopByCount). Must be >= 1. Defaults to 25.")] int topN = 25,
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
            CpuSampleHandleTtl,
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

    public static DiagnosticResult<CallTreeView> GetCallTree(
        IDiagnosticHandleStore handles,
        [Description("Handle returned by a prior collect_sample(kind='cpu') call.")] string handle,
        [Description("Optional case-insensitive substring; the tree is re-rooted at the highest-ranked frame whose method name contains this text.")] string? rootMethodFilter = null,
        [Description("Maximum tree depth from the root. Must be >= 1. Defaults to 8.")] int maxDepth = 8,
        [Description("Approximate cap on the number of nodes returned (top children at each level). Must be >= 1. Defaults to 200.")] int maxNodes = 200)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<CallTreeView>(nameof(handle), "is required");
        if (maxDepth < 1) return InvalidArg<CallTreeView>(nameof(maxDepth), "must be >= 1");
        if (maxNodes < 1) return InvalidArg<CallTreeView>(nameof(maxNodes), "must be >= 1");

        var artifact = ResolveTraceArtifact(handles, handle);
        if (artifact is null)
        {
            return DiagnosticResult.Fail<CallTreeView>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Drill-down handles live ~10min and expire by TTL.", handle),
                new NextActionHint("collect_sample", "Re-run the sampler on the same pid to issue a fresh handle.",
                    new Dictionary<string, object?> { ["kind"] = "cpu", ["durationSeconds"] = 10 }));
        }

        return CpuSampleQueryDispatcher.RenderCallTree(artifact, handle, rootMethodFilter, maxDepth, maxNodes);
    }

    public static async Task<DiagnosticResult<OffCpuSnapshot>> CollectOffCpuSample(
        IOffCpuSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of blocking stacks returned inline (the full set lives behind the handle). Defaults to 25.")] int topN = 25,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to symbol-resolving backends. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns the top-3 blocking stacks inline. 'detail' returns the requested topN (default 25). 'raw' is equivalent to detail. The full artifact is always retained behind the issued handle — drill in with query_snapshot.")]
        SamplingDepth depth = SamplingDepth.Summary,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<OffCpuSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<OffCpuSnapshot>(nameof(topN), "must be >= 1");

        var symbolDenial = ValidateSymbolPath<OffCpuSnapshot>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
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
            CpuSampleHandleTtl,
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
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Sampling window in seconds. Must be >= 1. Defaults to 10. Keep short on allocator-hot workloads — uprobe overhead is per-call.")] int durationSeconds = 10,
        [Description("Maximum number of allocator hotspots returned inline (the full call tree lives behind the handle). Defaults to 25.")] int topN = 25,
        [Description("perf sample period — record one callchain per this many allocator hits. Must be >= 1. Defaults to 1000. Higher reduces overhead and resolution; it throttles recorded samples but not the per-call trap cost.")] long samplePeriod = 1000,
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
            CpuSampleHandleTtl,
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

    public static DiagnosticResult<OffCpuQueryView> QueryOffCpuSnapshot(
        IDiagnosticHandleStore handles,
        [Description("Handle returned by a prior collect_sample(kind='off_cpu') call.")] string handle,
        [Description("View name: topStacks (default), byThread, stack.")] string view = "topStacks",
        [Description("Maximum items returned for topStacks/byThread. Defaults to 25.")] int topN = 25,
        [Description("Required when view='stack' — 1-based rank of the stack in the top-stacks list.")] int? stackRank = null)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<OffCpuQueryView>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<OffCpuQueryView>(nameof(topN), "must be >= 1");

        var artifact = handles.TryGet<OffCpuSnapshotArtifact>(handle);
        if (artifact is null)
        {
            return DiagnosticResult.Fail<OffCpuQueryView>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Off-CPU handles live ~10min and expire by TTL.", handle),
                new NextActionHint("collect_sample", "Re-run the off-CPU sampler to issue a fresh handle.",
                    new Dictionary<string, object?> { ["kind"] = "off_cpu", ["durationSeconds"] = 10 }));
        }

        return OffCpuQueryDispatcher.Dispatch(artifact, view, topN, stackRank);
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));

    private static DiagnosticResult<T>? ValidateSymbolPath<T>(
        SymbolServerAllowlist allowlist,
        string? symbolPath,
        IPrincipalAccessor? principalAccessor = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null)
        => SymbolPathValidation.Validate<T>(
            allowlist,
            symbolPath,
            principalAccessor?.Current?.HasExplicitScope("symbols-remote") == true,
            deprecation);

    private static DiagnosticResult<T> ClassifyAttachFailure<T>(string tool, int? processId, Exception ex)
        => AttachGuard.ClassifyAttachFailure<T>(tool, processId, ex);

    private static CpuSampleTraceArtifact? ResolveTraceArtifact(IDiagnosticHandleStore handles, string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        return handles.TryGet<CpuSampleTraceArtifact>(handle)
            ?? handles.TryGet<AllocationSampleArtifact>(handle)?.TraceArtifact;
    }

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
