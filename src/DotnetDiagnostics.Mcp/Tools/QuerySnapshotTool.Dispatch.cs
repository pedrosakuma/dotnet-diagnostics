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

namespace DotnetDiagnostics.Mcp.Tools;

public sealed partial class QuerySnapshotTool
{
    private delegate Task<DiagnosticResult<object>> QuerySnapshotHandler(QuerySnapshotDispatchContext context);

    private static readonly Dictionary<string, QuerySnapshotHandler> KindHandlers =
        new Dictionary<string, QuerySnapshotHandler>(StringComparer.Ordinal)
        {
            [DiagnosticTools.HeapSnapshotKind] = HandleHeapSnapshotAsync,
            [DiagnosticTools.ThreadSnapshotKind] = HandleThreadSnapshotAsync,
            [DiagnosticTools.OffCpuHandleKind] = HandleOffCpuSnapshotAsync,
            ["cpu-sample"] = HandleCpuSampleAsync,
            ["allocation-sample"] = HandleCpuSampleAsync,
            [DiagnosticTools.NativeAllocHandleKind] = HandleCpuSampleAsync,
            [CollectionHandleKinds.Counters] = HandleCountersCollectionAsync,
            [CollectionHandleKinds.EventCatalog] = HandleEventCatalogAsync,
            [CollectionHandleKinds.GcDatas] = HandleGcDatasAsync,
            [CollectionHandleKinds.ExceptionSnapshot] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.CrashGuardSnapshot] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.GcEvents] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.EventSource] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.Activities] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.LogSnapshot] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.JitSnapshot] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.ThreadPoolSnapshot] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.ContentionSnapshot] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.DbSnapshot] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.KestrelSnapshot] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.NetworkingSnapshot] = HandleEventPipeCollectionAsync,
            [CollectionHandleKinds.StartupSnapshot] = HandleEventPipeCollectionAsync,
            [MethodParameterCaptureUseCases.HandleKind] = HandleMethodParameterCaptureAsync,
        };

    private sealed class QuerySnapshotDispatchContext
    {
        public required IDiagnosticHandleStore Handles { get; init; }
        public required IDumpInspector Inspector { get; init; }
        public required SensitiveDataRedactor Redactor { get; init; }
        public required SensitiveValueGate SensitiveGate { get; init; }
        public required SecurityOptions SecurityOptions { get; init; }
        public required IPrincipalAccessor PrincipalAccessor { get; init; }
        public required INativeAddressResolver AddressResolver { get; init; }
        public required IFrameVariableResolver FrameVariableResolver { get; init; }
        public required HandleLookup Lookup { get; init; }
        public required string Handle { get; init; }
        public required string? View { get; init; }
        public required int? TopN { get; init; }
        public required string RankBy { get; init; }
        public required string? TypeFullName { get; init; }
        public required string? Address { get; init; }
        public required bool IncludeSensitiveValues { get; init; }
        public required int? ThreadId { get; init; }
        public required int FramesToHash { get; init; }
        public required int MinCount { get; init; }
        public required int? StackRank { get; init; }
        public required string? RootMethodFilter { get; init; }
        public required string? ProviderFilter { get; init; }
        public required bool ChangesOnly { get; init; }
        public required int MaxDepth { get; init; }
        public required int MaxNodes { get; init; }
        public required string? BaselineHandle { get; init; }
        public required string[]? ComparisonHandles { get; init; }
        public required double MinDeltaPct { get; init; }
        public required string Depth { get; init; }
        public required JourneyMode JourneyMode { get; init; }
        public required double HotPathThresholdPercent { get; init; }
        public required LegacyDiagnosticsFlagDeprecation? Deprecation { get; init; }
        public required BearerPrincipal? Principal { get; init; }
        public required CancellationToken CancellationToken { get; init; }

        public string Kind => Lookup.Kind;

        public string ResolveView(string defaultView)
            => string.IsNullOrWhiteSpace(View) ? defaultView : View!;

        public bool MatchesView(string expected)
            => !string.IsNullOrWhiteSpace(View)
                && View!.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<DiagnosticResult<object>> HandleHeapSnapshotAsync(QuerySnapshotDispatchContext context)
    {
        if (!RequireScope(context.Principal, ScopeHeapRead, out var forbidden))
        {
            return forbidden!;
        }

        if (IsDiffView(context))
        {
            return TryBuildDiff(
                context.Handles,
                context.Handle,
                context.Lookup,
                context.BaselineHandle,
                context.ComparisonHandles,
                context.MinDeltaPct,
                context.TopN,
                context.Depth,
                context.JourneyMode);
        }

        if (IsGrowthView(context.View))
        {
            return TryBuildHeapGrowth(
                context.Handles,
                context.Handle,
                context.Lookup,
                context.BaselineHandle,
                context.RankBy,
                context.MinDeltaPct,
                context.TopN);
        }

        var heap = await DiagnosticTools.QueryHeapSnapshot(
            context.Handles,
            context.Inspector,
            context.Redactor,
            context.SensitiveGate,
            context.PrincipalAccessor,
            context.Handle,
            context.ResolveView(DefaultHeapView),
            context.TopN ?? 50,
            context.RankBy,
            context.TypeFullName,
            context.Address,
            context.IncludeSensitiveValues,
            context.Deprecation,
            context.CancellationToken).ConfigureAwait(false);
        return AsObjectEnvelope(heap);
    }

    private static Task<DiagnosticResult<object>> HandleThreadSnapshotAsync(QuerySnapshotDispatchContext context)
    {
        if (!RequireScope(context.Principal, ScopePtrace, out var forbidden))
        {
            return Task.FromResult(forbidden!);
        }

        if (context.MatchesView(ResolveAddressView))
        {
            return ResolveThreadAddressesAsync(
                context.Handles,
                context.AddressResolver,
                context.Handle,
                context.Address,
                context.CancellationToken);
        }

        if (context.MatchesView(FrameVarsView))
        {
            if (!RequireScope(context.Principal, ScopeHeapRead, out var heapForbidden))
            {
                return Task.FromResult(heapForbidden!);
            }

            return ResolveFrameVariablesAsync(
                context.Handles,
                context.FrameVariableResolver,
                context.SensitiveGate,
                context.PrincipalAccessor,
                context.Handle,
                context.ThreadId,
                context.IncludeSensitiveValues,
                context.CancellationToken);
        }

        var thread = DiagnosticTools.QueryThreadSnapshot(
            context.Handles,
            context.Handle,
            context.ResolveView(DefaultThreadView),
            context.ThreadId,
            context.TopN ?? 50,
            context.FramesToHash,
            context.MinCount);
        return Task.FromResult(AsObjectEnvelope(thread));
    }

    private static Task<DiagnosticResult<object>> HandleOffCpuSnapshotAsync(QuerySnapshotDispatchContext context)
    {
        if (!RequireScope(context.Principal, ScopeEventPipe, out var forbidden))
        {
            return Task.FromResult(forbidden!);
        }

        var offCpu = DiagnosticTools.QueryOffCpuSnapshot(
            context.Handles,
            context.Handle,
            context.ResolveView(DefaultOffCpuView),
            context.TopN ?? 25,
            context.StackRank);
        return Task.FromResult(AsObjectEnvelope(offCpu));
    }

    private static Task<DiagnosticResult<object>> HandleCpuSampleAsync(QuerySnapshotDispatchContext context)
    {
        if (!RequireScope(context.Principal, ScopeInvestigationExport, out var forbidden))
        {
            return Task.FromResult(forbidden!);
        }

        if (IsDiffView(context))
        {
            return Task.FromResult(TryBuildDiff(
                context.Handles,
                context.Handle,
                context.Lookup,
                context.BaselineHandle,
                context.ComparisonHandles,
                context.MinDeltaPct,
                context.TopN,
                context.Depth,
                context.JourneyMode));
        }

        var cpuView = string.IsNullOrWhiteSpace(context.View) ? CpuSampleQueryDispatcher.CallTreeView : context.View!;
        if (!string.Equals(cpuView, CpuSampleQueryDispatcher.CallTreeView, StringComparison.Ordinal)
            && CpuSampleQueryDispatcher.IsKnownView(cpuView))
        {
            var trace = CpuSampleQueryDispatcher.ResolveTrace(context.Lookup.Artifact);
            if (trace is null)
            {
                return Task.FromResult(HandleExpiredError(null, context.Handle));
            }

            var cpuTopN = context.TopN ?? CpuSampleQueryDispatcher.DefaultTopN;
            DiagnosticResult<object> result = cpuView switch
            {
                CpuSampleQueryDispatcher.TopMethodsView => AsObjectEnvelope(
                    CpuSampleQueryDispatcher.RenderTopMethods(
                        trace,
                        context.Handle,
                        string.Equals(context.RankBy, "inclusive", StringComparison.OrdinalIgnoreCase) ? "inclusive" : "exclusive",
                        cpuTopN)),
                CpuSampleQueryDispatcher.ByModuleView => AsObjectEnvelope(
                    CpuSampleQueryDispatcher.RenderByModule(trace, context.Handle, cpuTopN)),
                CpuSampleQueryDispatcher.ByNamespaceView => AsObjectEnvelope(
                    CpuSampleQueryDispatcher.RenderByNamespace(trace, context.Handle, cpuTopN)),
                CpuSampleQueryDispatcher.HotPathView => AsObjectEnvelope(
                    CpuSampleQueryDispatcher.RenderHotPath(trace, context.Handle, context.HotPathThresholdPercent)),
                CpuSampleQueryDispatcher.CallerCalleeView => AsObjectEnvelope(
                    CpuSampleQueryDispatcher.RenderCallerCallee(trace, context.Handle, context.RootMethodFilter, cpuTopN)),
                _ => UnknownView(cpuView, context.Kind, CpuViewNames),
            };
            return Task.FromResult(result);
        }

        if (!string.IsNullOrWhiteSpace(context.View)
            && !string.Equals(context.View, CallTreeView, StringComparison.Ordinal))
        {
            return Task.FromResult(UnknownView(context.View!, context.Kind, CpuViewNames));
        }

        var callTree = DiagnosticTools.GetCallTree(
            context.Handles,
            context.Handle,
            context.RootMethodFilter,
            context.MaxDepth,
            context.MaxNodes);
        return Task.FromResult(AsObjectEnvelope(callTree));
    }

    private static Task<DiagnosticResult<object>> HandleCountersCollectionAsync(QuerySnapshotDispatchContext context)
    {
        if (!RequireAnyOfScope(context.Principal, ScopeReadCounters, ScopeEventPipe, out var forbidden))
        {
            return Task.FromResult(forbidden!);
        }

        if (IsDiffView(context))
        {
            return Task.FromResult(TryBuildDiff(
                context.Handles,
                context.Handle,
                context.Lookup,
                context.BaselineHandle,
                context.ComparisonHandles,
                context.MinDeltaPct,
                context.TopN,
                context.Depth,
                context.JourneyMode));
        }

        var collection = DiagnosticTools.QueryCollection(
            context.Handles,
            context.PrincipalAccessor,
            context.Handle,
            string.IsNullOrWhiteSpace(context.View) ? null : context.View,
            context.TopN ?? 50);
        return Task.FromResult(AsObjectEnvelope(collection));
    }

    private static Task<DiagnosticResult<object>> HandleEventCatalogAsync(QuerySnapshotDispatchContext context)
    {
        if (!RequireScope(context.Principal, ScopeEventPipe, out var forbidden))
        {
            return Task.FromResult(forbidden!);
        }

        var snapshot = context.Handles.TryGet<EventCatalogSnapshot>(context.Handle);
        if (snapshot is null)
        {
            return Task.FromResult(HandleExpiredError(null, context.Handle));
        }

        var result = EventCatalogQueryDispatcher.Render(
            snapshot,
            context.Handle,
            context.View,
            context.TopN ?? EventCatalogQueryDispatcher.DefaultTopN,
            context.ProviderFilter,
            context.RootMethodFilter);
        return Task.FromResult(AsObjectEnvelope(result));
    }

    private static Task<DiagnosticResult<object>> HandleGcDatasAsync(QuerySnapshotDispatchContext context)
    {
        if (!RequireScope(context.Principal, ScopeEventPipe, out var forbidden))
        {
            return Task.FromResult(forbidden!);
        }

        if (IsDiffView(context))
        {
            return Task.FromResult(TryBuildDiff(
                context.Handles,
                context.Handle,
                context.Lookup,
                context.BaselineHandle,
                context.ComparisonHandles,
                context.MinDeltaPct,
                context.TopN,
                context.Depth,
                context.JourneyMode));
        }

        var snapshot = context.Handles.TryGet<GcDatasSnapshot>(context.Handle);
        if (snapshot is null)
        {
            return Task.FromResult(HandleExpiredError(null, context.Handle));
        }

        var result = GcDatasQueryDispatcher.Render(
            snapshot,
            context.Handle,
            context.View,
            context.TopN ?? GcDatasQueryDispatcher.DefaultTopN,
            context.ChangesOnly);
        return Task.FromResult(AsObjectEnvelope(result));
    }

    private static Task<DiagnosticResult<object>> HandleEventPipeCollectionAsync(QuerySnapshotDispatchContext context)
    {
        if (!RequireScope(context.Principal, ScopeEventPipe, out var forbidden))
        {
            return Task.FromResult(forbidden!);
        }

        if (IsDiffView(context) && IsComparableDiffKind(context.Kind))
        {
            return Task.FromResult(TryBuildDiff(
                context.Handles,
                context.Handle,
                context.Lookup,
                context.BaselineHandle,
                context.ComparisonHandles,
                context.MinDeltaPct,
                context.TopN,
                context.Depth,
                context.JourneyMode));
        }

        var collection = DiagnosticTools.QueryCollection(
            context.Handles,
            context.PrincipalAccessor,
            context.Handle,
            string.IsNullOrWhiteSpace(context.View) ? null : context.View,
            context.TopN ?? 50);
        return Task.FromResult(AsObjectEnvelope(collection));
    }

    private static Task<DiagnosticResult<object>> HandleMethodParameterCaptureAsync(QuerySnapshotDispatchContext context)
    {
        if (!RequireScope(context.Principal, ScopeEventPipe, out var eventPipeForbidden))
        {
            return Task.FromResult(eventPipeForbidden!);
        }

        var artifact = context.Handles.TryGet<MethodParameterCaptureArtifact>(context.Handle);
        if (artifact is null)
        {
            return Task.FromResult(HandleExpiredError(null, context.Handle));
        }

        var resolvedView = string.IsNullOrWhiteSpace(context.View)
            ? MethodParameterCaptureQueryDispatcher.SummaryView
            : context.View!;
        if (string.Equals(resolvedView, MethodParameterCaptureQueryDispatcher.EventsView, StringComparison.OrdinalIgnoreCase))
        {
            if (!RequireExplicitScope(context.Principal, ScopeSensitiveParameterRead, out var modifierForbidden))
            {
                return Task.FromResult(modifierForbidden!);
            }

            if (!context.SecurityOptions.AllowMethodParameterCapture)
            {
                return Task.FromResult(DiagnosticResult.Fail<object>(
                    "Method parameter capture is disabled by server policy. Set `Diagnostics:AllowMethodParameterCapture=true` (env `Diagnostics__AllowMethodParameterCapture=true`) to enable it.",
                    new DiagnosticError(
                        "MethodParameterCaptureDisabled",
                        "Method parameter capture is disabled by server policy. Set `Diagnostics:AllowMethodParameterCapture=true` (env `Diagnostics__AllowMethodParameterCapture=true`) to enable it.")));
            }

            if (!context.IncludeSensitiveValues)
            {
                return Task.FromResult(InvalidArgument(nameof(context.IncludeSensitiveValues), "must be true when view='events' for a method-parameter capture handle"));
            }
        }

        var effectiveTopN = context.TopN ?? Math.Max(artifact.CaptureCount, 1);
        var methodParams = MethodParameterCaptureQueryDispatcher.Render(artifact, context.Handle, resolvedView, effectiveTopN);
        return Task.FromResult(AsObjectEnvelope(methodParams));
    }

    private static bool IsDiffView(QuerySnapshotDispatchContext context)
        => IsDiffView(context.View);

    private static DiagnosticResult<object> UnsupportedHandleKind(string handle, string kind)
        => DiagnosticResult.Fail<object>(
            $"Handle '{handle}' is of kind '{kind}' which query_snapshot does not support.",
            new DiagnosticError(
                "UnsupportedHandleKind",
                $"query_snapshot dispatches over kinds: {string.Join(", ", SupportedKinds)}.",
                kind),
            new NextActionHint(ToolName,
                "Use a handle issued by inspect_heap, collect_thread_snapshot, collect_off_cpu_sample, collect_cpu_sample, collect_allocation_sample, or any of the EventPipe collectors.",
                null));
}
