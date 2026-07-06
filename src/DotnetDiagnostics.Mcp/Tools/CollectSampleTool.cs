using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Tools.Dispatch;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// Consolidation: single MCP entry-point for the bounded-time sampling
/// family — <c>cpu</c>, <c>off_cpu</c>, <c>allocation</c>. Delegates to the legacy
/// <see cref="DiagnosticTools"/> methods so per-kind behaviour (provider/keyword setup,
/// SSRF guards, ClrMD enrichment) is preserved verbatim — asserted byte-for-byte by the
/// dual-entrypoint compatibility tests.
/// </summary>
/// <remarks>
/// <para>#213 — the legacy tools (<c>collect_cpu_sample</c>,
/// <c>collect_off_cpu_sample</c>, <c>collect_allocation_sample</c>) have been deleted in
/// the alias removal wave; this is now the sole entry-point for bounded-time sampling.</para>
/// </remarks>
[McpServerToolType]
public sealed class CollectSampleTool
{
    internal const string ToolName = "collect_sample";
    internal const string KindCpu = "cpu";
    internal const string KindOffCpu = "off_cpu";
    internal const string KindAllocation = "allocation";
    internal const string KindNativeAlloc = "native-alloc";
    internal const string KindMethodParams = "method-params";

    /// <summary>Allowed values for the <c>kind</c> discriminator. Order is preserved when
    /// rendered by <see cref="DiscriminatorDispatch"/> in failure envelopes.</summary>
    internal static readonly IReadOnlyList<string> AllowedKinds = new[]
    {
        KindCpu,
        KindOffCpu,
        KindAllocation,
        KindNativeAlloc,
        KindMethodParams,
    };

    [RequireScope("eventpipe")]
    [McpServerTool(
        Name = ToolName,
        Title = "Collect a bounded-time sample (cpu | off_cpu | allocation | native-alloc | method-params)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true,
        TaskSupport = ToolTaskSupport.Optional)]
    [Description(
        "Unified bounded-time sampler. Choose the sampler via the 'kind' parameter " +
        "(cpu, off_cpu, allocation, native-alloc, method-params). Returns a drilldown handle.")]
    public static async Task<DiagnosticResult<CollectSampleEnvelope>> CollectSample(
        ICpuSampler cpuSampler,
        IOffCpuSampler offCpuSampler,
        EventPipeAllocationSampler allocationSampler,
        INativeAllocSampler nativeAllocSampler,
        IMethodParameterCaptureCollector methodParameterCollector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        SecurityOptions securityOptions,
        IPrincipalAccessor principalAccessor,
        ILoggerFactory? loggerFactory = null,
        [Description(
            "Which sampler to run (default 'cpu'): " +
            "'cpu' (on-CPU SampleProfiler / perf — top managed hotspots with MethodIdentity handoff), " +
            "'off_cpu' (where threads are blocked and for how long — Linux sched_switch via perf, Windows ContextSwitch via NT Kernel Logger), " +
            "'allocation' (managed GCAllocationTick rolled up by type — TypeName is empty on NativeAOT), " +
            "'native-alloc' (unmanaged allocations — Linux uprobes libc malloc/calloc/realloc via perf (needs CAP_SYS_ADMIN); Windows captures NT Kernel Logger VirtualAlloc via ETW (needs admin elevation); sampled call counts, not bytes), " +
            "'method-params' (security-sensitive live parameter capture for explicitly-filtered methods on .NET 8+ CoreCLR; requires `sensitive-parameter-read`, `Diagnostics:AllowMethodParameterCapture=true`, and `includeSensitiveValues=true`). " +
            "Long-running collections expose MCP-native progress/cancellation or can be promoted to an MCP Task.")]
        string kind = KindCpu,
        // Shared options.
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")]
        int? processId = null,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")]
        int durationSeconds = 10,
        [Description("Maximum number of items returned (top hotspots, top blocking stacks, or top types depending on kind). Must be >= 1. Defaults to 25.")]
        int topN = 25,
        [Description("kind='method-params' only. Maximum captured invocation rows retained in the live artifact. Must be between 1 and 500. Defaults to 100.")]
        int maxEvents = 100,
        [Description("kind='method-params' only. Number of retained invocation rows surfaced inline in the collect_sample response. Must be between 1 and 25. Defaults to 10.")]
        int previewCount = 10,
        [Description("kind='method-params' only. Required explicit acknowledgement for returning sensitive parameter values. The only accepted V1 value is true.")]
        bool includeSensitiveValues = false,
        [Description("kind='method-params' only. Explicit method filters to instrument. Each filter requires moduleName, typeName, methodName, and may optionally add genericArity, signature, and moduleVersionId. Between 1 and 10 filters are allowed.")]
        IReadOnlyList<MethodFilter>? methods = null,
        [Description("Verbosity (summary|detail|raw). Applies to kind='cpu' and kind='off_cpu' — see the legacy collectors for semantics. Ignored by kind='allocation'.")]
        SamplingDepth depth = SamplingDepth.Summary,
        // kind=cpu / kind=off_cpu
        [Description("kind='cpu' or kind='off_cpu'. Optional NT_SYMBOL_PATH-style search path forwarded to symbol-resolving backends. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`. Ignored by kind='allocation' and by kind='cpu' when resolveSourceLines=false.")]
        string? symbolPath = null,
        // kind=cpu only
        [Description("kind='cpu' only. If true, attempts to resolve top hotspots to file:line via PDB / SourceLink and stamps the resolved SourceLocation onto each MethodIdentity payload. Defaults to true; set to false to skip PDB I/O when symbols are known to be unreachable.")]
        bool resolveSourceLines = true,
        [Description("kind='cpu' only. Cap on how many top hotspots get source-resolved. Must be >= 1. Defaults to the requested topN so every emitted MethodIdentity carries its resolved SourceLocation when available.")]
        int? maxResolvedSources = null,
        [Description("kind='cpu' only. If true, performs an opt-in ClrMD attach after sampling to recover closed generic instantiations for the hottest managed frames. CoreCLR only. On Linux requires CAP_SYS_PTRACE (or ptrace_scope=0) and briefly suspends the target. Defaults to false.")]
        bool resolveMethodInstantiations = false,
        [Description("kind='cpu' only. Cap on how many top hotspots get ClrMD generic-instantiation enrichment. Must be >= 1. Defaults to the requested topN.")]
        int? maxResolvedMethodInstantiations = null,
        [Description("kind='cpu' on NativeAOT only. Filesystem path to the ILC '*.map.xml' map file (publish with <IlcGenerateMapFile>true</IlcGenerateMapFile>). Enables a name-based MethodIdentity (TypeFullName + MethodName; MVID/token null) for hot managed AOT methods so the dotnet-native-mcp disassembly handoff works. Ignored on CoreCLR and by other kinds. Path is a hint only.")]
        string? nativeAotMapFile = null,
        [Description("kind='cpu' only. If true, persists the raw .nettrace under the artifact root and returns its relative path so it can be fetched with get_bytes(kind='trace') for offline PerfView/Speedscope/Perfetto analysis. Defaults to false.")]
        bool exportTrace = false,
        [Description("kind='native-alloc' on Linux only. perf sample period — record one callchain per this many allocator hits. Must be >= 1. Defaults to 1000. Higher reduces overhead and resolution; throttles recorded samples but not the per-call uprobe trap cost. Ignored by the Windows ETW VirtualAlloc backend, which records every allocation.")]
        long nativeAllocSamplePeriod = 1000,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (!DiscriminatorDispatch.TryValidate<CollectSampleEnvelope>(
                kind, AllowedKinds, nameof(kind), out var canonicalKind, out var dispatchFailure))
        {
            return dispatchFailure!;
        }

        return canonicalKind switch
        {
            KindCpu => Project(
                await DiagnosticTools.CollectCpuSample(
                    cpuSampler,
                    handles,
                    resolver,
                    symbolServerAllowlist,
                    principalAccessor,
                    processId,
                    durationSeconds,
                    topN,
                    resolveSourceLines,
                    symbolPath,
                    maxResolvedSources,
                    resolveMethodInstantiations,
                    maxResolvedMethodInstantiations,
                    nativeAotMapFile,
                    depth,
                    exportTrace,
                    deprecation,
                    requestContext,
                    cancellationToken).ConfigureAwait(false),
                KindCpu,
                (env, data) => env with { Cpu = data }),

            KindOffCpu => Project(
                await DiagnosticTools.CollectOffCpuSample(
                    offCpuSampler,
                    handles,
                    resolver,
                    symbolServerAllowlist,
                    principalAccessor,
                    processId,
                    durationSeconds,
                    topN,
                    symbolPath,
                    depth,
                    deprecation,
                    cancellationToken).ConfigureAwait(false),
                KindOffCpu,
                (env, data) => env with { OffCpu = data }),

            KindAllocation => Project(
                await DiagnosticTools.CollectAllocationSample(
                    allocationSampler,
                    handles,
                    resolver,
                    processId,
                    durationSeconds,
                    topN,
                    cancellationToken).ConfigureAwait(false),
                KindAllocation,
                (env, data) => env with { Allocation = data }),

            KindNativeAlloc => Project(
                await DiagnosticTools.CollectNativeAllocSample(
                    nativeAllocSampler,
                    handles,
                    resolver,
                    processId,
                    durationSeconds,
                    topN,
                    nativeAllocSamplePeriod,
                    cancellationToken).ConfigureAwait(false),
                KindNativeAlloc,
                (env, data) => env with { NativeAlloc = data }),

            KindMethodParams => await CollectMethodParametersAsync(
                methodParameterCollector,
                handles,
                resolver,
                securityOptions,
                principalAccessor,
                loggerFactory,
                processId,
                durationSeconds,
                maxEvents,
                previewCount,
                includeSensitiveValues,
                methods,
                requestContext,
                cancellationToken).ConfigureAwait(false),

            // Unreachable — TryValidate narrowed canonicalKind to the AllowedKinds set above.
            _ => DiagnosticResult.Fail<CollectSampleEnvelope>(
                $"Unhandled kind '{canonicalKind}'.",
                new DiagnosticError("InvalidArgument", $"Unhandled kind '{canonicalKind}'.", nameof(kind))),
        };
    }

    private static async Task<DiagnosticResult<CollectSampleEnvelope>> CollectMethodParametersAsync(
        IMethodParameterCaptureCollector methodParameterCollector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SecurityOptions securityOptions,
        IPrincipalAccessor principalAccessor,
        ILoggerFactory? loggerFactory,
        int? processId,
        int durationSeconds,
        int maxEvents,
        int previewCount,
        bool includeSensitiveValues,
        IReadOnlyList<MethodFilter>? methods,
        RequestContext<CallToolRequestParams>? requestContext,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory?.CreateLogger("DotnetDiagnostics.Mcp.Tools.CollectSampleTool");
        var principal = principalAccessor.Current;
        if (principal?.HasExplicitScope("sensitive-parameter-read") != true)
        {
            logger?.LogWarning(
                "{Tool} denied: literal sensitive-parameter-read scope required. tokenName={TokenName} processId={ProcessId} reason={Reason} methodFilters={MethodFilters} durationSeconds={DurationSeconds} maxEvents={MaxEvents}",
                ToolName,
                principal?.Name ?? "(none)",
                processId,
                "MissingSensitiveParameterScope",
                RenderMethodFilters(methods),
                durationSeconds,
                maxEvents);
            return DiagnosticResult.Fail<CollectSampleEnvelope>(
                "`collect_sample(kind=\"method-params\")` requires the literal scope `sensitive-parameter-read`. Root or wildcard tokens do not auto-grant this modifier scope.",
                new DiagnosticError(
                    "Forbidden",
                    "`collect_sample(kind=\"method-params\")` requires the literal scope `sensitive-parameter-read`. Root or wildcard tokens do not auto-grant this modifier scope.",
                    "sensitive-parameter-read"));
        }

        if (!securityOptions.AllowMethodParameterCapture)
        {
            logger?.LogWarning(
                "{Tool} denied: server policy disabled. tokenName={TokenName} processId={ProcessId} reason={Reason} methodFilters={MethodFilters} durationSeconds={DurationSeconds} maxEvents={MaxEvents}",
                ToolName,
                principal?.Name ?? "(none)",
                processId,
                "ServerPolicyDisabled",
                RenderMethodFilters(methods),
                durationSeconds,
                maxEvents);
            return DiagnosticResult.Fail<CollectSampleEnvelope>(
                "Method parameter capture is disabled by server policy. Set `Diagnostics:AllowMethodParameterCapture=true` (env `Diagnostics__AllowMethodParameterCapture=true`) to enable it.",
                new DiagnosticError(
                    "MethodParameterCaptureDisabled",
                    "Method parameter capture is disabled by server policy. Set `Diagnostics:AllowMethodParameterCapture=true` (env `Diagnostics__AllowMethodParameterCapture=true`) to enable it."));
        }

        if (!includeSensitiveValues)
        {
            return DiagnosticResult.Fail<CollectSampleEnvelope>(
                "`collect_sample(kind=\"method-params\")` requires `includeSensitiveValues=true` for an explicit sensitive-data acknowledgement.",
                new DiagnosticError(
                    "InvalidArgument",
                    "`collect_sample(kind=\"method-params\")` requires `includeSensitiveValues=true` for an explicit sensitive-data acknowledgement.",
                    nameof(includeSensitiveValues)));
        }

        if (TryFindInvalidMethodParamsKnob(requestContext, out var invalidKnob))
        {
            return DiagnosticResult.Fail<CollectSampleEnvelope>(
                $"Argument '{invalidKnob}' is not supported when kind='method-params'.",
                new DiagnosticError("InvalidArgument", $"Argument '{invalidKnob}' is not supported when kind='method-params'.", invalidKnob));
        }

        logger?.LogInformation(
            "{Tool} start. tokenName={TokenName} processId={ProcessId} runtimeVersion={RuntimeVersion} methodFilters={MethodFilters} durationSeconds={DurationSeconds} maxEvents={MaxEvents} previewCount={PreviewCount} includeSensitiveValues={IncludeSensitiveValues}",
            ToolName,
            principal?.Name ?? "(none)",
            processId,
            "(pending)",
            RenderMethodFilters(methods),
            durationSeconds,
            maxEvents,
            previewCount,
            true);

        var result = await MethodParameterCaptureUseCases.CollectAsync(
            methodParameterCollector,
            handles,
            resolver,
            processId,
            durationSeconds,
            maxEvents,
            previewCount,
            methods,
            cancellationToken).ConfigureAwait(false);

        if (result.Error is not null)
        {
            logger?.LogWarning(
                "{Tool} aborted. tokenName={TokenName} processId={ProcessId} reason={Reason} errorKind={ErrorKind} detail={Detail} methodFilters={MethodFilters}",
                ToolName,
                principal?.Name ?? "(none)",
                processId,
                "CaptureFailed",
                result.Error.Kind,
                result.Error.Detail ?? result.Error.Message,
                RenderMethodFilters(methods));
            return Project(result, KindMethodParams, (env, data) => env with { MethodParams = data });
        }

        var artifact = result.Handle is { Length: > 0 } handleId
            ? handles.TryGet<MethodParameterCaptureArtifact>(handleId)
            : null;
        logger?.LogInformation(
            "{Tool} complete. tokenName={TokenName} processId={ProcessId} runtimeVersion={RuntimeVersion} methodFilters={MethodFilters} elapsedMs={ElapsedMs} captureCount={CaptureCount} droppedCount={DroppedCount} truncatedValueCount={TruncatedValueCount} redactedValueCount={RedactedValueCount} handleId={HandleId} handleExpiresAt={HandleExpiresAt}",
            ToolName,
            principal?.Name ?? "(none)",
            processId,
            result.Data?.RuntimeVersion ?? "(unknown)",
            artifact is null ? RenderMethodFilters(methods) : string.Join(", ", artifact.ResolvedMethods.Select(FormatResolvedMethod)),
            durationSeconds * 1000,
            result.Data?.CaptureCount ?? 0,
            result.Data?.DroppedCount ?? 0,
            result.Data?.TruncatedValueCount ?? 0,
            result.Data?.RedactedValueCount ?? 0,
            result.Handle ?? "(none)",
            result.HandleExpiresAt);

        return Project(result, KindMethodParams, (env, data) => env with { MethodParams = data });
    }

    private static bool TryFindInvalidMethodParamsKnob(RequestContext<CallToolRequestParams>? requestContext, out string invalidKnob)
    {
        invalidKnob = string.Empty;
        var arguments = requestContext?.Params?.Arguments;
        if (arguments is null)
        {
            return false;
        }

        foreach (var key in new[]
                 {
                     "topN",
                     "depth",
                     "symbolPath",
                     "resolveSourceLines",
                     "maxResolvedSources",
                     "resolveMethodInstantiations",
                     "maxResolvedMethodInstantiations",
                     "nativeAotMapFile",
                     "exportTrace",
                     "nativeAllocSamplePeriod",
                 })
        {
            if (arguments.ContainsKey(key))
            {
                invalidKnob = key;
                return true;
            }
        }

        return false;
    }

    private static string RenderMethodFilters(IReadOnlyList<MethodFilter>? methods)
        => methods is null || methods.Count == 0
            ? "(none)"
            : string.Join(", ", methods.Select(filter => $"{filter.ModuleName}!{filter.TypeName}.{filter.MethodName}"));

    private static string FormatResolvedMethod(ResolvedMethodIdentity method)
        => $"{method.ModuleName}!{method.TypeName}.{method.MethodName}";

    /// <summary>
    /// Re-wraps a legacy sampler's <see cref="DiagnosticResult{T}"/> as a
    /// <see cref="CollectSampleEnvelope"/>-shaped result, preserving Summary, Hints, Handle,
    /// HandleExpiresAt, ResolvedProcess and Error so callers see the exact same envelope they
    /// got from the legacy tool — only the typed payload moves into the polymorphic shape.
    /// </summary>
    private static DiagnosticResult<CollectSampleEnvelope> Project<TInner>(
        DiagnosticResult<TInner> inner,
        string kind,
        Func<CollectSampleEnvelope, TInner, CollectSampleEnvelope> populate)
    {
        CollectSampleEnvelope? envelope = inner.Data is null
            ? null
            : populate(new CollectSampleEnvelope(kind), inner.Data);

        return new DiagnosticResult<CollectSampleEnvelope>(inner.Summary, inner.Hints, inner.Error)
        {
            Data = inner.IsError ? null : envelope,
            Signals = inner.Signals,
            Handle = inner.Handle,
            HandleExpiresAt = inner.HandleExpiresAt,
            ResolvedProcess = inner.ResolvedProcess,
            Cancelled = inner.Cancelled,
        };
    }
}

/// <summary>
/// Polymorphic payload returned by <see cref="CollectSampleTool.CollectSample"/>. Exactly one
/// of the kind-specific fields (<see cref="Cpu"/>, <see cref="OffCpu"/>,
/// <see cref="Allocation"/>) is populated, matched by <see cref="Kind"/>. Mirrors the
/// discriminator-envelope convention used by other consolidated tools
/// (<see cref="CollectEventsEnvelope"/>, <c>get_method_il</c>, …).
/// </summary>
public sealed record CollectSampleEnvelope(
    string Kind,
    CpuSample? Cpu = null,
    OffCpuSnapshot? OffCpu = null,
    AllocationSample? Allocation = null,
    NativeAllocSample? NativeAlloc = null,
    MethodParameterCaptureSample? MethodParams = null);
