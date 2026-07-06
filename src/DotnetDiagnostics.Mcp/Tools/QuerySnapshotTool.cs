using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Symbols;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// #207 — single drilldown surface that subsumes the five
/// handle-based query tools (<c>query_heap_snapshot</c>, <c>query_thread_snapshot</c>,
/// <c>query_off_cpu_snapshot</c>, <c>query_collection</c>, <c>get_call_tree</c>) behind
/// one <c>(handle, view)</c> contract. The dispatcher reads the artifact kind recorded
/// against the supplied handle in <see cref="IDiagnosticHandleStore"/> and forwards
/// to the matching legacy implementation so the response envelopes stay byte-identical
/// (asserted by <c>QuerySnapshotCompatibilityTests</c>). The legacy tools remain
/// registered through the deprecation window.
/// </summary>
/// <remarks>
/// <para><b>Authorization.</b> The static gate accepts any drilldown-capable
/// bearer (<c>RequireAnyScope</c> over the union of legacy scopes). After resolving the
/// handle kind we re-apply the exact legacy scope at runtime so the
/// <c>(handle family, origin, view)</c> boundary is preserved verbatim:</para>
/// <list type="bullet">
///   <item><description>heap-snapshot → <c>heap-read</c></description></item>
///   <item><description>thread-snapshot → <c>ptrace</c></description></item>
///   <item><description>off-cpu-snapshot → <c>eventpipe</c></description></item>
///   <item><description>cpu-sample / allocation-sample / native-alloc-sample (call-tree view) → <c>investigation-export</c></description></item>
///   <item><description>counters / exception-snapshot / crash-guard-snapshot / gc-events / event-source / activities / log-snapshot / jit-snapshot / threadpool-snapshot / contention-snapshot / db-snapshot / kestrel-snapshot / networking-snapshot / in-flight-requests / startup-snapshot → any of <c>read-counters</c> or <c>eventpipe</c> (matches <c>query_collection</c>)</description></item>
/// </list>
/// <para>Unknown handle kinds, unknown views and parameter shape violations all return
/// the structured <c>InvalidArgument</c> / <c>UnsupportedHandleKind</c> envelopes the
/// legacy tools emit — never a 500.</para>
/// </remarks>
[McpServerToolType]
public sealed class QuerySnapshotTool
{
    internal const string ToolName = "query_snapshot";

    // View constants accepted for the cpu-sample / allocation-sample handle kinds.
    // The legacy `get_call_tree` tool exposed no view discriminator (it had exactly one
    // projection); the unified tool exposes that projection as the canonical
    // `call-tree` view so the (handle, view) contract is uniform across kinds.
    internal const string CallTreeView = "call-tree";
    internal const string DiffView = "diff";

    // Heap-snapshot view that diffs two LIVE heap snapshots N seconds apart and ranks the types
    // that grew by retained bytes / instances, with retention-path drill-down on the top growers
    // (issue #463 — leak hunting). Like `diff`, it needs a second handle (baselineHandle).
    internal const string GrowthView = "growth";

    // Every view the cpu-sample / allocation-sample / native-alloc-sample kinds accept (analytics
    // views from #313 plus the original call-tree and the server-only diff).
    private static readonly string[] CpuViewNames =
    {
        CallTreeView,
        CpuSampleQueryDispatcher.TopMethodsView,
        CpuSampleQueryDispatcher.ByModuleView,
        CpuSampleQueryDispatcher.ByNamespaceView,
        CpuSampleQueryDispatcher.HotPathView,
        CpuSampleQueryDispatcher.CallerCalleeView,
        DiffView,
    };

    // Thread-snapshot view that re-opens the origin and classifies arbitrary addresses into
    // (module, rva, build-id) or an unmapped verdict (issue #275).
    internal const string ResolveAddressView = "resolve-address";

    // Thread-snapshot view that re-opens the origin and walks one thread's stack roots, surfacing
    // object-typed locals/parameters per frame — the ClrMD `!clrstack -a` equivalent (issue #449).
    internal const string FrameVarsView = "frame-vars";

    // Legacy default views, mirrored so unified callers can omit `view` and still get
    // the same projection the kind's legacy tool returned by default.
    internal const string DefaultHeapView = "top-types";
    internal const string DefaultThreadView = "top-blocked";
    internal const string DefaultOffCpuView = "topStacks";
    internal const string DefaultCollectionView = "summary";

    private static readonly IComparableProjector[] ComparableProjectors =
    [
        new GcDatasComparableProjector(),
        new CountersComparableProjector(),
        new GcEventsComparableProjector(),
        new HeapSnapshotComparableProjector(),
        new CpuSampleComparableProjector(),
        new NativeAllocSampleComparableProjector(),
        new AllocationSampleComparableProjector(),
        new ContentionComparableProjector(),
        new ThreadPoolComparableProjector(),
    ];

    // Scopes (mirrored from the legacy [RequireScope] attributes).
    private const string ScopeHeapRead = "heap-read";
    private const string ScopePtrace = "ptrace";
    private const string ScopeEventPipe = "eventpipe";
    private const string ScopeReadCounters = "read-counters";
    private const string ScopeInvestigationExport = "investigation-export";
    private const string ScopeSensitiveParameterRead = "sensitive-parameter-read";

    [RequireAnyScope(
        ScopeReadCounters,
        ScopeEventPipe,
        ScopeHeapRead,
        ScopePtrace,
        ScopeInvestigationExport)]
    [McpServerTool(
        Name = ToolName,
        Title = "Drill into any drilldown snapshot (heap / thread / off-CPU / collection / call-tree)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    public static async Task<DiagnosticResult<object>> QuerySnapshot(
        IDiagnosticHandleStore handles,
        IDumpInspector inspector,
        SensitiveDataRedactor redactor,
        SensitiveValueGate sensitiveGate,
        SecurityOptions securityOptions,
        IPrincipalAccessor principalAccessor,
        INativeAddressResolver addressResolver,
        IFrameVariableResolver frameVariableResolver,
        [Description("Drilldown handle returned by a prior collector (inspect_heap, collect_thread_snapshot, collect_off_cpu_sample, collect_cpu_sample, collect_allocation_sample, collect_events(kind=\"counters\"), collect_events(kind=\"exceptions\"), collect_events(kind=\"crash-guard\"), collect_events(kind=\"gc\"), collect_events(kind=\"datas\"), collect_events(kind=\"catalog\"), collect_events(kind=\"event_source\"), collect_events(kind=\"activities\"), collect_events(kind=\"logs\"), collect_events(kind=\"jit\"), collect_events(kind=\"threadpool\"), collect_events(kind=\"contention\"), collect_events(kind=\"db\"), collect_events(kind=\"kestrel\"), collect_events(kind=\"networking\"), collect_events(kind=\"requests\"), collect_events(kind=\"startup\")).")] string handle,
        [Description("Kind-specific view. Heap: top-types|retention-paths|roots-by-kind|finalizer-queue|fragmentation|static-fields|delegate-targets|duplicate-strings|gchandles|timers|alc|object|gcroot|objsize|async|diff|growth. Thread: threads-summary|stack|lock-graph|deadlocks|top-blocked|unique-stacks|async-stalls|wait-chains|threadpool|resolve-address|frame-vars. Off-CPU: topStacks|byThread|stack. Collection: summary|byProvider|byType|recent|exceptions|stack|events|catalog|pauseHistogram|longestPauses|byGeneration|heap-stats|byEventName|bySource|byOperation|activities|byCategory|byLevel|errors|timeline|hillClimbing|workItemOrigins|byCallSite|byOwner|byCommand|n+1|connectionPool|queues|queue|tls|config|dns|requests|longRunning. cpu-sample/allocation-sample/native-alloc-sample: call-tree|top-methods|by-module|by-namespace|hot-path|caller-callee|diff. Omit to use the kind's default view.")] string? view = null,
        [Description("Maximum entries returned by any ranked-list view. Omit to use the per-kind legacy default: 50 for heap / thread / collection, 25 for off-CPU. For view=diff, defaults to 25 rows per bucket.")] int? topN = null,
        [Description("Ranking for ranked views. Heap view='top-types'/'growth': 'bytes' (default) or 'instances'. CPU-sample view='top-methods': 'exclusive' (self-time, default) or 'inclusive'.")] string rankBy = "bytes",
        [Description("Heap view='retention-paths' only: case-insensitive substring matched against TypeFullName.")] string? typeFullName = null,
        [Description("Heap view='object'/'gcroot'/'objsize': managed object address. Thread view='resolve-address': one or more native/instruction addresses (comma-separated) to classify into (module, rva, build-id) or an unmapped verdict. Decimal or 0x-prefixed hex.")] string? address = null,
        [Description("Heap views 'duplicate-strings' / 'object' only: opt-in to raw string content / field-value previews (gated by `Diagnostics:AllowSensitiveHeapValues` AND `sensitive-heap-read` scope per docs/authorization.md#modifier-scopes).")] bool includeSensitiveValues = false,
        [Description("Thread view='stack' only: thread id (ManagedThreadId for CoreCLR snapshots, OS TID for linux-native-stack snapshots). Required for view='frame-vars' (ManagedThreadId).")] int? threadId = null,
        [Description("Thread view='unique-stacks' only: number of top frames folded into the signature hash. Defaults to 20.")] int framesToHash = ThreadSnapshotUniqueStackGrouper.DefaultFramesToHash,
        [Description("Thread view='unique-stacks' only: drop groups with fewer than this many threads. Defaults to 1.")] int minCount = 1,
        [Description("Off-CPU view='stack' only: 1-based rank of the stack in the top-stacks list.")] int? stackRank = null,
        [Description("Call-tree (cpu-sample / allocation-sample) only: optional case-insensitive substring; the tree is re-rooted at the highest-ranked frame whose method name contains this text. Event-catalog views reuse this as an event-name substring filter.")] string? rootMethodFilter = null,
        [Description("Event-catalog views only: optional case-insensitive provider-name substring filter.")] string? providerFilter = null,
        [Description("DATAS 'tuning' view only: when true, emit only the rows where the heap-count decision changed versus the previous GC (plus the first row as a baseline).")] bool changesOnly = false,
        [Description("Call-tree only: maximum tree depth from the root. Must be >= 1. Defaults to 8.")] int maxDepth = 8,
        [Description("Call-tree only: approximate cap on the number of nodes returned (top children at each level). Must be >= 1. Defaults to 200.")] int maxNodes = 200,
        [Description("Diff view: baseline handle to compare against the current `handle`. Required for legacy pairwise diff unless `comparisonHandles` is supplied. Heap view='growth': required — the EARLIER live heap snapshot handle to diff the current (later) one against.")] string? baselineHandle = null,
        [Description("Diff view only: ordered handles to compare before the current `handle` for N-way journey diffs. Do not combine with `baselineHandle`; the current handle is appended as the final capture.")] string[]? comparisonHandles = null,
        [Description("Diff/growth views: minimum absolute delta percentage required for a row to surface. Defaults to 5.0.")] double minDeltaPct = 5.0,
        [Description("Diff view only: inline verbosity for comparable journey diffs. `full` returns the full matrix when it is below the inline threshold; `compact` returns verdict/headline/counts/notes plus top-N metric and key deltas. Large full diffs always return compact inline data plus a journey://diff/{handle} Resource link. Defaults to `full`.")] string depth = "full",
        [Description("Diff view only: journey interpretation mode. `trend` (default) compares ordered captures over time; `dispersion` compares unordered replicas for outliers and requires N-way comparable captures via comparisonHandles.")] string? mode = null,
        [Description("cpu-sample/allocation-sample 'hot-path' view only: a child must carry at least this percent of its parent's inclusive samples to extend the chain. Defaults to 50.")] double hotPathThresholdPercent = CpuSampleQueryDispatcher.DefaultHotPathThresholdPercent,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return InvalidArgument(nameof(handle), "is required");
        }

        var lookup = handles.TryGetWithKind(handle);
        if (lookup is null)
        {
            return HandleExpiredError(IsDiffView(view) ? "Current" : null, handle);
        }

        var kind = lookup.Value.Kind;
        var principal = principalAccessor.Current;
        var isDiffView = IsDiffView(view);
        var journeyMode = JourneyMode.Trend;
        if (isDiffView && !JourneyModeParser.TryParse(mode, out journeyMode))
        {
            return InvalidArgument(nameof(mode), "must be either 'trend' or 'dispersion' when view='diff'");
        }

        switch (kind)
        {
            case DiagnosticTools.HeapSnapshotKind:
                {
                    if (!RequireScope(principal, ScopeHeapRead, out var forbidden))
                    {
                        return forbidden!;
                    }
                    if (isDiffView)
                    {
                        return TryBuildDiff(handles, handle, lookup.Value, baselineHandle, comparisonHandles, minDeltaPct, topN, depth, journeyMode);
                    }
                    if (IsGrowthView(view))
                    {
                        return TryBuildHeapGrowth(handles, handle, lookup.Value, baselineHandle, rankBy, minDeltaPct, topN);
                    }
                    var resolvedView = string.IsNullOrWhiteSpace(view) ? DefaultHeapView : view!;
                    var heap = await DiagnosticTools.QueryHeapSnapshot(
                        handles,
                        inspector,
                        redactor,
                        sensitiveGate,
                        principalAccessor,
                        handle,
                        resolvedView,
                        topN ?? 50,
                        rankBy,
                        typeFullName,
                        address,
                        includeSensitiveValues,
                        deprecation,
                        cancellationToken).ConfigureAwait(false);
                    return AsObjectEnvelope(heap);
                }


            case DiagnosticTools.ThreadSnapshotKind:
                {
                    if (!RequireScope(principal, ScopePtrace, out var forbidden))
                    {
                        return forbidden!;
                    }

                    if (!string.IsNullOrWhiteSpace(view) && view!.Trim().Equals(ResolveAddressView, StringComparison.OrdinalIgnoreCase))
                    {
                        return await ResolveThreadAddressesAsync(
                            handles, addressResolver, handle, address, cancellationToken).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(view) && view!.Trim().Equals(FrameVarsView, StringComparison.OrdinalIgnoreCase))
                    {
                        // frame-vars re-opens the origin via ClrMD (same ptrace/dump-read footprint as
                        // inspect_heap live/dump) and may surface sensitive string values; gate it on
                        // heap-read in addition to the kind-wide ptrace scope.
                        if (!RequireScope(principal, ScopeHeapRead, out var heapForbidden))
                        {
                            return heapForbidden!;
                        }
                        return await ResolveFrameVariablesAsync(
                            handles, frameVariableResolver, sensitiveGate, principalAccessor, handle, threadId, includeSensitiveValues, cancellationToken).ConfigureAwait(false);
                    }

                    var resolvedView = string.IsNullOrWhiteSpace(view) ? DefaultThreadView : view!;
                    var thread = DiagnosticTools.QueryThreadSnapshot(
                        handles,
                        handle,
                        resolvedView,
                        threadId,
                        topN ?? 50,
                        framesToHash,
                        minCount);
                    return AsObjectEnvelope(thread);
                }

            case DiagnosticTools.OffCpuHandleKind:
                {
                    if (!RequireScope(principal, ScopeEventPipe, out var forbidden))
                    {
                        return forbidden!;
                    }
                    var resolvedView = string.IsNullOrWhiteSpace(view) ? DefaultOffCpuView : view!;
                    var offCpu = DiagnosticTools.QueryOffCpuSnapshot(
                        handles,
                        handle,
                        resolvedView,
                        topN ?? 25,
                        stackRank);
                    return AsObjectEnvelope(offCpu);
                }

            case "cpu-sample":
            case "allocation-sample":
            case DiagnosticTools.NativeAllocHandleKind:
                {
                    if (!RequireScope(principal, ScopeInvestigationExport, out var forbidden))
                    {
                        return forbidden!;
                    }
                    if (isDiffView)
                    {
                        return TryBuildDiff(handles, handle, lookup.Value, baselineHandle, comparisonHandles, minDeltaPct, topN, depth, journeyMode);
                    }

                    // Analytics views (top-methods / by-module / by-namespace / hot-path / caller-callee,
                    // issue #313) render from the same merged trace as call-tree via the host-neutral
                    // CpuSampleQueryDispatcher (shared with the CLI `session` REPL).
                    var cpuView = string.IsNullOrWhiteSpace(view) ? CpuSampleQueryDispatcher.CallTreeView : view!;
                    if (!string.Equals(cpuView, CpuSampleQueryDispatcher.CallTreeView, StringComparison.Ordinal)
                        && CpuSampleQueryDispatcher.IsKnownView(cpuView))
                    {
                        var trace = CpuSampleQueryDispatcher.ResolveTrace(lookup.Value.Artifact);
                        if (trace is null)
                        {
                            return HandleExpiredError(null, handle);
                        }

                        var cpuTopN = topN ?? CpuSampleQueryDispatcher.DefaultTopN;
                        return cpuView switch
                        {
                            CpuSampleQueryDispatcher.TopMethodsView => AsObjectEnvelope(
                                CpuSampleQueryDispatcher.RenderTopMethods(trace, handle,
                                    string.Equals(rankBy, "inclusive", StringComparison.OrdinalIgnoreCase) ? "inclusive" : "exclusive", cpuTopN)),
                            CpuSampleQueryDispatcher.ByModuleView => AsObjectEnvelope(
                                CpuSampleQueryDispatcher.RenderByModule(trace, handle, cpuTopN)),
                            CpuSampleQueryDispatcher.ByNamespaceView => AsObjectEnvelope(
                                CpuSampleQueryDispatcher.RenderByNamespace(trace, handle, cpuTopN)),
                            CpuSampleQueryDispatcher.HotPathView => AsObjectEnvelope(
                                CpuSampleQueryDispatcher.RenderHotPath(trace, handle, hotPathThresholdPercent)),
                            CpuSampleQueryDispatcher.CallerCalleeView => AsObjectEnvelope(
                                CpuSampleQueryDispatcher.RenderCallerCallee(trace, handle, rootMethodFilter, cpuTopN)),
                            _ => UnknownView(cpuView, kind, CpuViewNames),
                        };
                    }

                    // get_call_tree exposes a single projection; require either the canonical
                    // `call-tree` view or an omitted value, and reject anything else with a
                    // structured InvalidArgument envelope so a confused caller sees the same
                    // shape it would see from any other kind/view mismatch.
                    if (!string.IsNullOrWhiteSpace(view)
                        && !string.Equals(view, CallTreeView, StringComparison.Ordinal))
                    {
                        return UnknownView(view!, kind, CpuViewNames);
                    }
                    var callTree = DiagnosticTools.GetCallTree(
                        handles,
                        handle,
                        rootMethodFilter,
                        maxDepth,
                        maxNodes);
                    return AsObjectEnvelope(callTree);
                }


            case CollectionHandleKinds.Counters:
                {
                    if (!RequireAnyOfScope(principal, ScopeReadCounters, ScopeEventPipe, out var forbidden))
                    {
                        return forbidden!;
                    }
                    if (isDiffView)
                    {
                        return TryBuildDiff(handles, handle, lookup.Value, baselineHandle, comparisonHandles, minDeltaPct, topN, depth, journeyMode);
                    }
                    var resolvedView = string.IsNullOrWhiteSpace(view) ? null : view;
                    var collection = DiagnosticTools.QueryCollection(
                        handles,
                        principalAccessor,
                        handle,
                        resolvedView,
                        topN ?? 50);
                    return AsObjectEnvelope(collection);
                }

                case CollectionHandleKinds.EventCatalog:
                {
                    if (!RequireScope(principal, ScopeEventPipe, out var forbidden))
                    {
                        return forbidden!;
                    }

                    var snapshot = handles.TryGet<EventCatalogSnapshot>(handle);
                    if (snapshot is null)
                    {
                        return HandleExpiredError(null, handle);
                    }

                    var result = EventCatalogQueryDispatcher.Render(
                        snapshot,
                        handle,
                        view,
                        topN ?? EventCatalogQueryDispatcher.DefaultTopN,
                        providerFilter,
                        rootMethodFilter);
                    return AsObjectEnvelope(result);
                }

                case CollectionHandleKinds.GcDatas:
                {
                    if (!RequireScope(principal, ScopeEventPipe, out var forbidden))
                    {
                        return forbidden!;
                    }
                    if (isDiffView)
                    {
                        return TryBuildDiff(handles, handle, lookup.Value, baselineHandle, comparisonHandles, minDeltaPct, topN, depth, journeyMode);
                    }

                    var snapshot = handles.TryGet<GcDatasSnapshot>(handle);
                    if (snapshot is null)
                    {
                        return HandleExpiredError(null, handle);
                    }

                    var result = GcDatasQueryDispatcher.Render(
                        snapshot,
                        handle,
                        view,
                        topN ?? GcDatasQueryDispatcher.DefaultTopN,
                        changesOnly);
                    return AsObjectEnvelope(result);
                }

                case CollectionHandleKinds.ExceptionSnapshot:
                case CollectionHandleKinds.CrashGuardSnapshot:
                case CollectionHandleKinds.GcEvents:
                case CollectionHandleKinds.EventSource:
                case CollectionHandleKinds.Activities:
                case CollectionHandleKinds.LogSnapshot:
                case CollectionHandleKinds.JitSnapshot:
                case CollectionHandleKinds.ThreadPoolSnapshot:
                case CollectionHandleKinds.ContentionSnapshot:
                case CollectionHandleKinds.DbSnapshot:
                case CollectionHandleKinds.KestrelSnapshot:
                case CollectionHandleKinds.NetworkingSnapshot:
                case CollectionHandleKinds.StartupSnapshot:
                {
                    if (!RequireScope(principal, ScopeEventPipe, out var forbidden))
                    {
                        return forbidden!;
                    }
                    if (isDiffView && IsComparableDiffKind(kind))
                    {
                        return TryBuildDiff(handles, handle, lookup.Value, baselineHandle, comparisonHandles, minDeltaPct, topN, depth, journeyMode);
                    }
                    // Forward null/empty unchanged so query_collection's own default
                    // (`summary`) kicks in — guarantees byte-equal envelopes with the legacy
                    // call when the caller omits view.
                    var resolvedView = string.IsNullOrWhiteSpace(view) ? null : view;
                    var collection = DiagnosticTools.QueryCollection(
                        handles,
                        principalAccessor,
                        handle,
                        resolvedView,
                        topN ?? 50);
                    return AsObjectEnvelope(collection);
                }

            case MethodParameterCaptureUseCases.HandleKind:
                {
                    if (!RequireScope(principal, ScopeEventPipe, out var eventPipeForbidden))
                    {
                        return eventPipeForbidden!;
                    }

                    var artifact = handles.TryGet<MethodParameterCaptureArtifact>(handle);
                    if (artifact is null)
                    {
                        return HandleExpiredError(null, handle);
                    }

                    var resolvedView = string.IsNullOrWhiteSpace(view) ? MethodParameterCaptureQueryDispatcher.SummaryView : view!;
                    if (string.Equals(resolvedView, MethodParameterCaptureQueryDispatcher.EventsView, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!RequireExplicitScope(principal, ScopeSensitiveParameterRead, out var modifierForbidden))
                        {
                            return modifierForbidden!;
                        }

                        if (!securityOptions.AllowMethodParameterCapture)
                        {
                            return DiagnosticResult.Fail<object>(
                                "Method parameter capture is disabled by server policy. Set `Diagnostics:AllowMethodParameterCapture=true` (env `Diagnostics__AllowMethodParameterCapture=true`) to enable it.",
                                new DiagnosticError(
                                    "MethodParameterCaptureDisabled",
                                    "Method parameter capture is disabled by server policy. Set `Diagnostics:AllowMethodParameterCapture=true` (env `Diagnostics__AllowMethodParameterCapture=true`) to enable it."));
                        }

                        if (!includeSensitiveValues)
                        {
                            return InvalidArgument(nameof(includeSensitiveValues), "must be true when view='events' for a method-parameter capture handle");
                        }
                    }

                    var effectiveTopN = topN ?? Math.Max(artifact.CaptureCount, 1);
                    var methodParams = MethodParameterCaptureQueryDispatcher.Render(artifact, handle, resolvedView, effectiveTopN);
                    return AsObjectEnvelope(methodParams);
                }

            default:
                return DiagnosticResult.Fail<object>(
                    $"Handle '{handle}' is of kind '{kind}' which query_snapshot does not support.",
                    new DiagnosticError(
                        "UnsupportedHandleKind",
                        $"query_snapshot dispatches over kinds: {string.Join(", ", SupportedKinds)}.",
                        kind),
                    new NextActionHint(ToolName,
                        "Use a handle issued by inspect_heap, collect_thread_snapshot, collect_off_cpu_sample, collect_cpu_sample, collect_allocation_sample, or any of the EventPipe collectors.",
                        null));
        }
    }


    private static bool IsDiffView(string? view)
        => string.Equals(view, DiffView, StringComparison.Ordinal);

    private static bool IsGrowthView(string? view)
        => string.Equals(view?.Trim(), GrowthView, StringComparison.OrdinalIgnoreCase);

    private static DiagnosticResult<object> TryBuildHeapGrowth(
        IDiagnosticHandleStore handles,
        string handle,
        HandleLookup currentLookup,
        string? baselineHandle,
        string rankBy,
        double minDeltaPct,
        int? topN)
    {
        if (string.IsNullOrWhiteSpace(baselineHandle))
        {
            return InvalidArgument(nameof(baselineHandle), "is required when view='growth' (pass the EARLIER live heap snapshot handle)");
        }

        var normalizedRank = (rankBy ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedRank.Length == 0)
        {
            normalizedRank = HeapGrowthDiff.RankByBytes;
        }
        if (normalizedRank is not (HeapGrowthDiff.RankByBytes or HeapGrowthDiff.RankByInstances))
        {
            return InvalidArgument(nameof(rankBy), $"must be 'bytes' or 'instances' (got '{rankBy}')");
        }

        if (minDeltaPct < 0)
        {
            return InvalidArgument(nameof(minDeltaPct), "must be >= 0");
        }

        var effectiveTopN = topN ?? 25;
        if (effectiveTopN < 1)
        {
            return InvalidArgument(nameof(topN), "must be >= 1 when view='growth'");
        }

        var baselineLookup = handles.TryGetWithKind(baselineHandle!);
        if (baselineLookup is null)
        {
            return HandleExpiredError("Baseline", baselineHandle!);
        }

        if (!string.Equals(currentLookup.Kind, baselineLookup.Value.Kind, StringComparison.Ordinal))
        {
            return InvalidKindPair(currentLookup.Kind, baselineLookup.Value.Kind);
        }

        if (currentLookup.Artifact is not HeapSnapshotArtifact current || baselineLookup.Value.Artifact is not HeapSnapshotArtifact baseline)
        {
            return UnsupportedDiffKind(currentLookup.Kind);
        }

        if (baseline.Origin != HeapSnapshotOrigin.Live || current.Origin != HeapSnapshotOrigin.Live)
        {
            return InvalidArgument(
                nameof(baselineHandle),
                $"view='growth' requires two LIVE heap snapshots (got baseline origin '{baseline.Origin}', current origin '{current.Origin}'). Capture both with inspect_heap(source=\"live\") on the same running process and pass the EARLIER handle as baselineHandle.");
        }

        var growth = HeapGrowthDiff.Build(baseline, baselineHandle!, current, handle, normalizedRank, minDeltaPct, effectiveTopN);

        var topGrower = growth.Growers.Count > 0 ? growth.Growers[0] : null;
        var summary = topGrower is null
            ? $"No types grew (>= {minDeltaPct}% by {normalizedRank}) between baseline '{baselineHandle}' and current '{handle}' over {growth.Elapsed.TotalSeconds:F1}s — verdict {growth.Verdict}."
            : $"Heap grew {growth.TotalHeapGrowthBytes:N0} bytes over {growth.Elapsed.TotalSeconds:F1}s (pid {growth.ProcessId}); {growth.TotalGrowers} type(s) grew, top {growth.Growers.Count} returned ranked by {normalizedRank}. Top grower `{topGrower.TypeFullName}` +{topGrower.BytesDelta:N0} bytes / +{topGrower.InstancesDelta:N0} instances. Verdict {growth.Verdict}.";

        var hint = topGrower is { RetentionPaths: null or { Count: 0 } }
            ? new NextActionHint("inspect_heap",
                "Re-capture both snapshots with includeRetentionPaths=true to see what's holding the top growers.",
                new Dictionary<string, object?> { ["processId"] = growth.ProcessId, ["source"] = "live", ["includeRetentionPaths"] = true })
            : new NextActionHint(ToolName,
                "Drill into a specific grower with view='retention-paths' on the current handle.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = "retention-paths", ["typeFullName"] = topGrower?.TypeFullName });

        return AsObjectEnvelope(DiagnosticResult.Ok<object>(growth, summary, hint));
    }

    // Legacy typed pairwise (N=2 baselineHandle) kinds handled by ComparablePairwiseSampleDiff in their own
    // case blocks. They also gain N-ary comparable projectors over time; this list backs the
    // registry-driven diffable-kind reporting so the InvalidKindPair message stays accurate as
    // projectors are added.
    private static readonly string[] LegacyTypedDiffKinds =
    {
        DiagnosticTools.HeapSnapshotKind,
        "cpu-sample",
        "allocation-sample",
        DiagnosticTools.NativeAllocHandleKind,
    };

    // A grouped-collection handle kind is diffable via view='diff' iff a comparable projector is
    // registered for it. Replacing the former gc-events special-case keeps this registry-driven:
    // registering a projector auto-enables its diff gating (issue #338).
    private static bool IsComparableDiffKind(string kind)
        => ComparableProjectors.Any(p => string.Equals(p.Kind, kind, StringComparison.Ordinal));

    private static string DiffableKindsList()
        => string.Join(
            ", ",
            LegacyTypedDiffKinds
                .Concat(ComparableProjectors.Select(p => p.Kind))
                .Distinct(StringComparer.Ordinal));

    private static async Task<DiagnosticResult<object>> ResolveThreadAddressesAsync(
        IDiagnosticHandleStore handles,
        INativeAddressResolver addressResolver,
        string handle,
        string? address,
        CancellationToken cancellationToken)
    {
        var snapshot = handles.TryGet<ThreadSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return HandleExpiredError(null, handle);
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return InvalidArgument(nameof(address), "is required for view='resolve-address' (decimal or 0x-prefixed hex; comma-separated for several)");
        }

        var parsed = new List<ulong>();
        foreach (var token in address.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!NativeAddressClassifier.TryParseAddress(token, out var value))
            {
                return InvalidArgument(nameof(address), $"'{token}' is not a valid decimal or 0x-prefixed hex address");
            }

            parsed.Add(value);
        }

        if (parsed.Count == 0)
        {
            return InvalidArgument(nameof(address), "contained no parseable addresses");
        }

        IReadOnlyList<NativeAddressLocation> locations;
        try
        {
            locations = await addressResolver.ResolveAsync(snapshot, parsed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            return AsObjectEnvelope(DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"Could not resolve addresses against snapshot '{handle}': {ex.Message}",
                new DiagnosticError("AddressResolutionUnavailable", ex.Message, handle),
                new NextActionHint("collect_thread_snapshot", "Re-capture the snapshot if the origin process or dump is no longer reachable.", null)));
        }

        var entries = locations.Select(static l => new ResolvedAddressEntry(
            Address: $"0x{l.Address:x}",
            Kind: l.Kind switch
            {
                NativeAddressKind.Module => "module",
                NativeAddressKind.Managed => "managed",
                NativeAddressKind.MappedNonModule => "mapped-non-module",
                _ => "unmapped-or-not-captured",
            },
            Module: l.Module,
            ModulePath: l.ModulePath,
            Rva: l.Rva is { } r ? $"0x{r:x}" : null,
            BuildId: l.BuildId,
            Readable: l.Readable,
            Display: l.Display)
        {
            ManagedMethod = l.ManagedMethod,
            LoadBase = l.LoadBase is { } b ? $"0x{b:x}" : null,
        }).ToArray();

        var origin = snapshot.Origin.ToString().ToLowerInvariant();
        var result = new ThreadSnapshotQueryResult(
            handle, ResolveAddressView, origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
        {
            ResolvedAddresses = entries,
        };

        var unresolved = entries.Count(e => e.Kind == "unmapped-or-not-captured");
        var summary = $"Resolved {entries.Length} address(es) against snapshot '{handle}' ({origin}, pid {snapshot.ProcessId})" +
            (unresolved > 0 ? $"; {unresolved} unmapped-or-not-captured." : ".");
        return AsObjectEnvelope(DiagnosticResult.Ok(result, summary));
    }

    private static async Task<DiagnosticResult<object>> ResolveFrameVariablesAsync(
        IDiagnosticHandleStore handles,
        IFrameVariableResolver resolver,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        string handle,
        int? threadId,
        bool includeSensitiveValues,
        CancellationToken cancellationToken)
    {
        var snapshot = handles.TryGet<ThreadSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return HandleExpiredError(null, handle);
        }

        if (threadId is null)
        {
            return InvalidArgument(nameof(threadId), "is required for view='frame-vars' (ManagedThreadId from view='threads-summary')");
        }

        // Guard against PID reuse / drift: the requested thread must have been present in the
        // captured snapshot, otherwise we'd resolve frames from whatever now owns that PID.
        if (!snapshot.Threads.Any(t => t.ManagedThreadId == threadId.Value))
        {
            return AsObjectEnvelope(DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"Managed thread {threadId.Value} was not present in snapshot '{handle}'; re-capture before inspecting frame variables.",
                new DiagnosticError("ThreadNotInSnapshot", $"thread {threadId.Value} absent from snapshot", handle),
                new NextActionHint("query_snapshot", "Use view='threads-summary' to list current ManagedThreadIds.", null)));
        }

        var principalUnlocksSensitive = principalAccessor.Current?.HasExplicitScope("sensitive-heap-read") == true;
        var emitSensitive = sensitiveGate.ShouldEmit(includeSensitiveValues, principalUnlocksSensitive);

        FrameVariablesResult frameVars;
        try
        {
            frameVars = await resolver.ResolveAsync(snapshot, threadId.Value, emitSensitive, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            return AsObjectEnvelope(DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"Could not inspect frame locals against snapshot '{handle}': {ex.Message}",
                new DiagnosticError("FrameVariablesUnavailable", ex.Message, handle),
                new NextActionHint("collect_thread_snapshot", "Re-capture the snapshot if the origin process or dump is no longer reachable.", null)));
        }

        var origin = snapshot.Origin.ToString().ToLowerInvariant();
        var result = new ThreadSnapshotQueryResult(
            handle, FrameVarsView, origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
        {
            FrameVariables = frameVars,
            ThreadId = threadId.Value,
        };

        var varCount = frameVars.Frames.Sum(fr => fr.Variables.Count);
        var summary = $"Recovered {varCount} object-typed local(s)/parameter(s) across {frameVars.Frames.Count} frame(s) on managed thread {frameVars.ManagedThreadId} from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId})" +
            (frameVars.CurrentExceptionType is { } exType ? $"; current exception {exType}." : ".");
        return AsObjectEnvelope(DiagnosticResult.Ok(result, summary));
    }

    private static DiagnosticResult<object> TryBuildDiff(
        IDiagnosticHandleStore handles,
        string handle,
        HandleLookup currentLookup,
        string? baselineHandle,
        string[]? comparisonHandles,
        double minDeltaPct,
        int? topN,
        string depth,
        JourneyMode mode)
    {
        var hasBaseline = !string.IsNullOrWhiteSpace(baselineHandle);
        var hasComparisonHandles = comparisonHandles is { Length: > 0 };
        if (hasBaseline && hasComparisonHandles)
        {
            return InvalidArgument(nameof(comparisonHandles), "cannot be combined with baselineHandle; pass either baselineHandle for legacy pairwise diff or comparisonHandles for an ordered N-way journey diff");
        }

        if (!hasBaseline && comparisonHandles is { Length: 0 })
        {
            return InvalidArgument(nameof(comparisonHandles), "must contain at least one handle when supplied for view='diff'");
        }

        if (!hasBaseline && !hasComparisonHandles)
        {
            return InvalidArgument(nameof(baselineHandle), "is required when view='diff' unless comparisonHandles is supplied");
        }

        if (minDeltaPct < 0)
        {
            return InvalidArgument(nameof(minDeltaPct), "must be >= 0");
        }

        var effectiveTopN = topN ?? 25;
        if (effectiveTopN < 1)
        {
            return InvalidArgument(nameof(topN), "must be >= 1 when view='diff'");
        }

        if (!JourneyDiffPresentation.TryParseDepth(depth, out var journeyDepth))
        {
            return InvalidArgument(nameof(depth), "must be either 'compact' or 'full' when view='diff'");
        }

        if (hasComparisonHandles)
        {
            return TryBuildComparableJourneyDiff(handles, handle, currentLookup, comparisonHandles!, minDeltaPct, effectiveTopN, journeyDepth, mode);
        }

        var baselineLookup = handles.TryGetWithKind(baselineHandle!);
        if (baselineLookup is null)
        {
            return HandleExpiredError("Baseline", baselineHandle!);
        }

        if (!string.Equals(currentLookup.Kind, baselineLookup.Value.Kind, StringComparison.Ordinal))
        {
            return InvalidKindPair(currentLookup.Kind, baselineLookup.Value.Kind);
        }

        if (mode == JourneyMode.Dispersion && LegacyTypedDiffKinds.Contains(currentLookup.Kind, StringComparer.Ordinal))
        {
            return InvalidArgument(nameof(mode), $"mode='dispersion' requires N-ary comparable captures via comparisonHandles; it is not available for the legacy pairwise baselineHandle diff of {currentLookup.Kind}");
        }

        return currentLookup.Kind switch
        {
            DiagnosticTools.HeapSnapshotKind when currentLookup.Artifact is HeapSnapshotArtifact current && baselineLookup.Value.Artifact is HeapSnapshotArtifact baseline
                => WrapDiff(currentLookup.Kind, baselineHandle!, handle, ComparablePairwiseSampleDiff.Compare(baseline, baselineHandle!, current, handle, minDeltaPct, effectiveTopN)),

            "cpu-sample" when currentLookup.Artifact is CpuSampleTraceArtifact current && baselineLookup.Value.Artifact is CpuSampleTraceArtifact baseline
                => WrapDiff(currentLookup.Kind, baselineHandle!, handle, ComparablePairwiseSampleDiff.Compare(baseline, baselineHandle!, current, handle, minDeltaPct, effectiveTopN)),

            DiagnosticTools.NativeAllocHandleKind when currentLookup.Artifact is CpuSampleTraceArtifact current && baselineLookup.Value.Artifact is CpuSampleTraceArtifact baseline
                => WrapDiff(currentLookup.Kind, baselineHandle!, handle, ComparablePairwiseSampleDiff.Compare(baseline, baselineHandle!, current, handle, minDeltaPct, effectiveTopN)),

            "allocation-sample" when currentLookup.Artifact is AllocationSampleArtifact current && baselineLookup.Value.Artifact is AllocationSampleArtifact baseline
                => WrapDiff(currentLookup.Kind, baselineHandle!, handle, ComparablePairwiseSampleDiff.Compare(baseline.Summary, baselineHandle!, current.Summary, handle, minDeltaPct, effectiveTopN)),

            _ => TryBuildComparableJourneyDiff(handles, handle, currentLookup, new[] { baselineHandle! }, minDeltaPct, effectiveTopN, journeyDepth, mode)
        };
    }

    private static DiagnosticResult<object> TryBuildComparableJourneyDiff(
        IDiagnosticHandleStore handles,
        string currentHandle,
        HandleLookup currentLookup,
        string[] comparisonHandles,
        double minDeltaPct,
        int topN,
        JourneyDiffDepth depth,
        JourneyMode mode)
    {
        var projector = ComparableProjectors.FirstOrDefault(p => string.Equals(p.Kind, currentLookup.Kind, StringComparison.Ordinal));
        if (projector is null || !projector.CanProject(currentLookup.Artifact))
        {
            return UnsupportedDiffKind(currentLookup.Kind);
        }

        var snapshots = new List<ComparableSnapshot>(comparisonHandles.Length + 1);
        var seenHandles = new HashSet<string>(StringComparer.Ordinal) { currentHandle };
        for (var i = 0; i < comparisonHandles.Length; i++)
        {
            var comparisonHandle = comparisonHandles[i];
            if (string.IsNullOrWhiteSpace(comparisonHandle))
            {
                return InvalidArgument(nameof(comparisonHandles), $"entry {i} is empty");
            }
            if (!seenHandles.Add(comparisonHandle))
            {
                return InvalidArgument(nameof(comparisonHandles), $"entry {i} duplicates another comparison handle or the current handle");
            }

            var lookup = handles.TryGetWithKind(comparisonHandle);
            if (lookup is null)
            {
                return HandleExpiredError($"Comparison[{i}]", comparisonHandle);
            }
            if (!string.Equals(currentLookup.Kind, lookup.Value.Kind, StringComparison.Ordinal))
            {
                return InvalidKindPair(currentLookup.Kind, lookup.Value.Kind);
            }
            if (!projector.CanProject(lookup.Value.Artifact))
            {
                return UnsupportedDiffKind(lookup.Value.Kind);
            }

            snapshots.Add(projector.Project(lookup.Value.Artifact, comparisonHandles.Length == 1 ? "baseline" : $"comparison-{i + 1}"));
        }

        snapshots.Add(projector.Project(currentLookup.Artifact, "current"));
        var diff = SnapshotDiffer.Compare(snapshots, mode, minDeltaPct, topN);
        return JourneyDiffPresentation.BuildResult(
            diff,
            handles,
            currentLookup.Handle.ProcessId,
            topN,
            depth,
            BuildJourneyDiffSummary(diff, currentHandle, comparisonHandles),
            currentLookup.Handle.Origin == HandleOrigin.Live,
            currentLookup.Handle.Origin);
    }

    private static DiagnosticResult<object> UnsupportedDiffKind(string kind)
        => DiagnosticResult.Fail<object>(
            $"Handle kind '{kind}' cannot be diffed via view='diff'.",
            new DiagnosticError("InvalidArgument", $"Handle kind '{kind}' cannot be diffed via view='diff'.", "view"));

    private static DiagnosticResult<object> WrapDiff<TKey, TMetric>(string kind, string baselineHandle, string currentHandle, SampleDiff<TKey, TMetric> diff)
        => AsObjectEnvelope(DiagnosticResult.Ok(diff, BuildDiffSummary(kind, baselineHandle, currentHandle, diff)));

    private static string BuildDiffSummary<TKey, TMetric>(string kind, string baselineHandle, string currentHandle, SampleDiff<TKey, TMetric> diff)
        => $"Compared {kind} handle '{currentHandle}' against baseline '{baselineHandle}': {diff.TotalAdded} added, {diff.TotalRemoved} removed, {diff.TotalChanged} changed — verdict {diff.Verdict}.";

    private static string BuildJourneyDiffSummary(SnapshotJourneyDiff diff, string currentHandle, string[] comparisonHandles)
        => $"Compared {diff.Kind} handle '{currentHandle}' across {comparisonHandles.Length + 1} captures: {diff.MetricSeries.Count} metric series, {diff.KeyMatrix.Count} key rows — verdict {diff.Verdict}.";

    private static DiagnosticResult<object> HandleExpiredError(string? side, string handle)
    {
        var prefix = string.IsNullOrWhiteSpace(side) ? "Handle" : $"{side} handle";
        return DiagnosticResult.Fail<object>(
            $"{prefix} '{handle}' is unknown or expired.",
            new DiagnosticError(
                "HandleExpired",
                "Drill-down handles live ~10min and are invalidated when the target process exits.",
                handle),
            new NextActionHint(ToolName,
                "Re-run the original collector on the same pid to issue a fresh handle.",
                null));
    }

    private static DiagnosticResult<object> InvalidKindPair(string currentKind, string baselineKind)
    {
        var message = $"query_snapshot(view='diff') requires handles of the same supported kind. Accepted pairs/kinds: {DiffableKindsList()}. Received baseline/comparison={baselineKind}, current={currentKind}.";
        return DiagnosticResult.Fail<object>(
            message,
            new DiagnosticError("InvalidArgument", message, "baselineHandle"),
            new NextActionHint(ToolName, "Retry with two handles issued by the same collector family.", null));
    }

    public static Task<DiagnosticResult<object>> QuerySnapshot(
        IDiagnosticHandleStore handles,
        IDumpInspector inspector,
        SensitiveDataRedactor redactor,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        INativeAddressResolver addressResolver,
        IFrameVariableResolver frameVariableResolver,
        string handle,
        string? view = null,
        int? topN = null,
        string rankBy = "bytes",
        string? typeFullName = null,
        string? address = null,
        bool includeSensitiveValues = false,
        int? threadId = null,
        int framesToHash = ThreadSnapshotUniqueStackGrouper.DefaultFramesToHash,
        int minCount = 1,
        int? stackRank = null,
        string? rootMethodFilter = null,
        string? providerFilter = null,
        bool changesOnly = false,
        int maxDepth = 8,
        int maxNodes = 200,
        string? baselineHandle = null,
        string[]? comparisonHandles = null,
        double minDeltaPct = 5.0,
        string depth = "full",
        string? mode = null,
        double hotPathThresholdPercent = CpuSampleQueryDispatcher.DefaultHotPathThresholdPercent,
        string? investigationHandleId = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
        => QuerySnapshot(
            handles,
            inspector,
            redactor,
            sensitiveGate,
            new SecurityOptions(),
            principalAccessor,
            addressResolver,
            frameVariableResolver,
            handle,
            view,
            topN,
            rankBy,
            typeFullName,
            address,
            includeSensitiveValues,
            threadId,
            framesToHash,
            minCount,
            stackRank,
            rootMethodFilter,
            providerFilter,
            changesOnly,
            maxDepth,
            maxNodes,
            baselineHandle,
            comparisonHandles,
            minDeltaPct,
            depth,
            mode,
            hotPathThresholdPercent,
            investigationHandleId,
            deprecation,
            cancellationToken);

    private static readonly string[] SupportedKinds =
    {
        DiagnosticTools.HeapSnapshotKind,
        DiagnosticTools.ThreadSnapshotKind,
        DiagnosticTools.OffCpuHandleKind,
        "cpu-sample",
        "allocation-sample",
        DiagnosticTools.NativeAllocHandleKind,
        CollectionHandleKinds.Counters,
        CollectionHandleKinds.ExceptionSnapshot,
        CollectionHandleKinds.CrashGuardSnapshot,
        CollectionHandleKinds.GcEvents,
        CollectionHandleKinds.GcDatas,
        CollectionHandleKinds.EventCatalog,
        CollectionHandleKinds.EventSource,
        CollectionHandleKinds.Activities,
        CollectionHandleKinds.LogSnapshot,
        CollectionHandleKinds.JitSnapshot,
        CollectionHandleKinds.ThreadPoolSnapshot,
        CollectionHandleKinds.ContentionSnapshot,
        CollectionHandleKinds.DbSnapshot,
        CollectionHandleKinds.KestrelSnapshot,
        CollectionHandleKinds.NetworkingSnapshot,
        CollectionHandleKinds.StartupSnapshot,
        MethodParameterCaptureUseCases.HandleKind,
    };

    /// <summary>
    /// Projects a typed <see cref="DiagnosticResult{T}"/> into the polymorphic
    /// <c>DiagnosticResult&lt;object&gt;</c> shape <c>query_snapshot</c> exposes, preserving
    /// every envelope field. <c>System.Text.Json</c> serializes polymorphically on
    /// <c>object</c> properties (default since .NET 6), so the resulting JSON is byte-equal
    /// to the legacy envelope — what makes <c>QuerySnapshotCompatibilityTests</c> pass.
    /// </summary>
    private static DiagnosticResult<object> AsObjectEnvelope<T>(DiagnosticResult<T> source) where T : class
        => new(source.Summary, source.Hints, source.Error)
        {
            Data = source.Data,
            Handle = source.Handle,
            HandleExpiresAt = source.HandleExpiresAt,
            ResolvedProcess = source.ResolvedProcess,
        };

    private static bool RequireScope(BearerPrincipal? principal, string scope, out DiagnosticResult<object>? failure)
    {
        if (principal is null || principal.HasScope(scope))
        {
            failure = null;
            return true;
        }

        failure = Forbidden(scope, $"requires scope '{scope}'");
        return false;
    }

    private static bool RequireAnyOfScope(BearerPrincipal? principal, string a, string b, out DiagnosticResult<object>? failure)
    {
        if (principal is null || principal.HasScope(a) || principal.HasScope(b))
        {
            failure = null;
            return true;
        }

        failure = Forbidden($"{a}|{b}", $"requires one of scope '{a}' or '{b}'");
        return false;
    }

    private static bool RequireExplicitScope(BearerPrincipal? principal, string scope, out DiagnosticResult<object>? failure)
    {
        if (principal is null || principal.HasExplicitScope(scope))
        {
            failure = null;
            return true;
        }

        failure = DiagnosticResult.Fail<object>(
            $"`{ToolName}` requires the literal scope `{scope}` for method-parameter capture handles. Root or wildcard tokens do not auto-grant this modifier scope.",
            new DiagnosticError("Forbidden", $"`{ToolName}` requires the literal scope `{scope}` for method-parameter capture handles. Root or wildcard tokens do not auto-grant this modifier scope.", scope));
        return false;
    }

    private static DiagnosticResult<object> Forbidden(string requiredScope, string requirement)
    {
        var message = $"forbidden: tool '{ToolName}' {requirement}.";
        return DiagnosticResult.Fail<object>(
            message,
            new DiagnosticError("Forbidden", message, requiredScope));
    }

    private static DiagnosticResult<object> InvalidArgument(string parameterName, string requirement)
    {
        var message = $"Argument '{parameterName}' {requirement}.";
        return DiagnosticResult.Fail<object>(
            message,
            new DiagnosticError("InvalidArgument", message, parameterName),
            new NextActionHint(ToolName,
                "Re-issue query_snapshot with valid arguments — handle is required.",
                null));
    }

    private static DiagnosticResult<object> UnknownView(string view, string kind, string[] allowed)
    {
        var allowedRendered = allowed.Length == 0 ? "(none)" : string.Join(", ", allowed);
        var message = $"View '{view}' is not defined for kind '{kind}'. Allowed: {allowedRendered}.";
        return DiagnosticResult.Fail<object>(
            message,
            new DiagnosticError("InvalidArgument", message, "view"),
            new NextActionHint(ToolName,
                "Retry with one of the allowed views for this handle kind.",
                new Dictionary<string, object?>
                {
                    ["view"] = allowed.Length > 0 ? allowed[0] : null,
                }));
    }
}
