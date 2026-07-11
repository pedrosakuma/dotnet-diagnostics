using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.DistributedTrace;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.GatedCapture;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.ReplicaCounters;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.Triage;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Diagnostics;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

public sealed partial class CollectEventsTool
{
    private delegate Task<DiagnosticResult<CollectEventsEnvelope>> CollectEventsKindExecutor(
        CollectEventsDispatchContext context,
        int effectiveDuration,
        CancellationToken cancellationToken);

    private sealed class CollectEventsKindHandler
    {
        public required string RequiredScope { get; init; }
        public required Func<CollectEventsDispatchContext, int> DefaultDurationSeconds { get; init; }
        public required CollectEventsKindExecutor ExecuteAsync { get; init; }
    }

    private sealed class CollectEventsDispatchContext
    {
        public required ICounterCollector CounterCollector { get; init; }
        public required IExceptionCollector ExceptionCollector { get; init; }
        public required ICrashGuardCollector CrashGuardCollector { get; init; }
        public required IGcCollector GcCollector { get; init; }
        public required IGcDatasCollector GcDatasCollector { get; init; }
        public required IActivityCollector ActivityCollector { get; init; }
        public required IEventSourceCollector EventSourceCollector { get; init; }
        public required IEventCatalogCollector EventCatalogCollector { get; init; }
        public required ILogCollector LogCollector { get; init; }
        public required IJitCollector JitCollector { get; init; }
        public required IThreadPoolCollector ThreadPoolCollector { get; init; }
        public required IContentionCollector ContentionCollector { get; init; }
        public required IDbCollector DbCollector { get; init; }
        public required IKestrelCollector KestrelCollector { get; init; }
        public required INetworkingCollector NetworkingCollector { get; init; }
        public required IInFlightRequestCollector InFlightRequestCollector { get; init; }
        public required IStartupCollector StartupCollector { get; init; }
        public required IProcessResourcesCollector ProcessResourcesCollector { get; init; }
        public required IThresholdGatedCaptureCollector GatedCaptureCollector { get; init; }
        public required ICpuSampler CpuSampler { get; init; }
        public required IThreadSnapshotInspector ThreadSnapshotInspector { get; init; }
        public required IDumpInspector DumpInspector { get; init; }
        public required IProcessDumper ProcessDumper { get; init; }
        public required IProcessContextResolver Resolver { get; init; }
        public required IDiagnosticHandleStore Handles { get; init; }
        public required EventSourceAllowlist Allowlist { get; init; }
        public required SensitiveValueGate SensitiveGate { get; init; }
        public required IPrincipalAccessor PrincipalAccessor { get; init; }
        public required string CanonicalKind { get; init; }
        public required int? ProcessId { get; init; }
        public required int? DurationSeconds { get; init; }
        public required SamplingDepth Depth { get; init; }
        public required string[]? Providers { get; init; }
        public required string[]? Meters { get; init; }
        public required int IntervalSeconds { get; init; }
        public required int MaxInstrumentTimeSeries { get; init; }
        public required int MaxRecent { get; init; }
        public required int? MaxEvents { get; init; }
        public required string? ProviderName { get; init; }
        public required long Keywords { get; init; }
        public required int EventLevel { get; init; }
        public required bool UnsafeProvider { get; init; }
        public required IReadOnlyList<string>? Sources { get; init; }
        public required int MaxActivities { get; init; }
        public required double LongRunningThresholdMs { get; init; }
        public required int MaxRequests { get; init; }
        public required string? TraceId { get; init; }
        public required string? InvestigationHandleId { get; init; }
        public required IReadOnlyList<string>? InvestigationHandleIds { get; init; }
        public required IReadOnlyList<string>? Categories { get; init; }
        public required string MinLevel { get; init; }
        public required int MaxMessageBytes { get; init; }
        public required string? TriggerWhen { get; init; }
        public required string? CaptureKind { get; init; }
        public required int WindowSeconds { get; init; }
        public required int MaxCaptures { get; init; }
        public required int SampleIntervalSeconds { get; init; }
        public required bool ConfirmDump { get; init; }
        public required LegacyDiagnosticsFlagDeprecation? Deprecation { get; init; }
        public required RequestContext<CallToolRequestParams>? RequestContext { get; init; }
        public required BearerPrincipal? Principal { get; init; }

        public bool GatingRequested => !string.IsNullOrWhiteSpace(TriggerWhen) || !string.IsNullOrWhiteSpace(CaptureKind);
    }

    private static readonly Dictionary<string, CollectEventsKindHandler> KindHandlers =
        new Dictionary<string, CollectEventsKindHandler>(StringComparer.Ordinal)
        {
            ["counters"] = CreateHandler("read-counters", _ => 5, RunCountersAsync),
            ["exceptions"] = CreateHandler("eventpipe", _ => 10, RunExceptionsAsync),
            ["crash-guard"] = CreateHandler("eventpipe", _ => 10, RunCrashGuardAsync),
            ["gc"] = CreateHandler("eventpipe", _ => 10, RunGcAsync),
            ["datas"] = CreateHandler("eventpipe", _ => 15, RunGcDatasAsync),
            ["catalog"] = CreateHandler("eventpipe", _ => 10, RunEventCatalogAsync),
            ["event_source"] = CreateHandler("eventpipe", _ => 10, RunEventSourceAsync),
            ["activities"] = CreateHandler("eventpipe", _ => 10, RunActivitiesAsync),
            ["logs"] = CreateHandler("eventpipe", _ => 10, RunLogsAsync),
            ["jit"] = CreateHandler("eventpipe", _ => 10, RunJitAsync),
            ["threadpool"] = CreateHandler("eventpipe", _ => 10, RunThreadPoolAsync),
            ["contention"] = CreateHandler("eventpipe", _ => 10, RunContentionAsync),
            ["db"] = CreateHandler("eventpipe", _ => 10, RunDbAsync),
            ["kestrel"] = CreateHandler("eventpipe", _ => 10, RunKestrelAsync),
            ["networking"] = CreateHandler("eventpipe", _ => 10, RunNetworkingAsync),
            ["requests"] = CreateHandler("eventpipe", _ => 10, RunRequestsAsync),
            ["startup"] = CreateHandler("eventpipe", _ => 10, RunStartupAsync),
            ["sweep"] = CreateHandler("eventpipe", _ => SweepUseCase.MinimumDurationSeconds, RunSweepAsync),
            ["distributed_trace"] = CreateHandler("eventpipe", _ => 10, RunDistributedTraceKindAsync),
            ["replica_counters"] = CreateHandler("read-counters", _ => 5, RunReplicaCountersKindAsync),
        };

    private static CollectEventsKindHandler CreateHandler(
        string requiredScope,
        Func<CollectEventsDispatchContext, int> defaultDuration,
        CollectEventsKindExecutor executeAsync)
        => new()
        {
            RequiredScope = requiredScope,
            DefaultDurationSeconds = defaultDuration,
            ExecuteAsync = executeAsync,
        };

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunCountersAsync(
        CollectEventsDispatchContext context,
        int effectiveDuration,
        CancellationToken cancellationToken)
        => context.GatingRequested
            ? RunGatedCaptureAsync(
                context.GatedCaptureCollector,
                context.Resolver,
                context.Handles,
                context.CpuSampler,
                context.ThreadSnapshotInspector,
                context.DumpInspector,
                context.ProcessDumper,
                context.Principal,
                context.CanonicalKind,
                context.TriggerWhen,
                context.CaptureKind,
                context.WindowSeconds,
                context.MaxCaptures,
                context.SampleIntervalSeconds,
                context.ConfirmDump,
                context.ProcessId,
                cancellationToken)
            : RunTimedCollectionAsync(
                context,
                effectiveDuration,
                ct => DiagnosticTools.SnapshotCounters(
                    context.CounterCollector,
                    context.Resolver,
                    context.Handles,
                    context.ProcessId,
                    effectiveDuration,
                    context.Providers,
                    context.Meters,
                    context.IntervalSeconds,
                    context.MaxInstrumentTimeSeries,
                    context.Depth,
                    ct),
                (env, data) => env with { Counters = data },
                cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunExceptionsAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectExceptions(
                context.ExceptionCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.MaxRecent,
                context.Depth,
                ct),
            (env, data) => env with { Exceptions = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunCrashGuardAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectCrashGuard(
                context.CrashGuardCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.MaxRecent,
                context.Depth,
                ct),
            (env, data) => env with { CrashGuard = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunGcAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectGcEvents(
                context.GcCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.MaxEvents ?? 200,
                context.Depth,
                ct),
            (env, data) => env with { Gc = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunGcDatasAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectGcDatas(
                context.GcDatasCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.MaxEvents ?? 1000,
                ct),
            (env, data) => env with { Datas = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunEventCatalogAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectEventCatalog(
                context.EventCatalogCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.Providers,
                context.MaxEvents ?? 200,
                context.Depth,
                ct),
            (env, data) => env with { Catalog = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunEventSourceAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectEventSource(
                context.EventSourceCollector,
                context.Resolver,
                context.Handles,
                context.Allowlist,
                context.SensitiveGate,
                context.PrincipalAccessor,
                context.ProviderName ?? string.Empty,
                context.ProcessId,
                effectiveDuration,
                context.Keywords,
                context.EventLevel,
                context.MaxEvents ?? 200,
                context.Depth,
                context.UnsafeProvider,
                context.Deprecation,
                ct),
            (env, data) => env with { EventSource = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunActivitiesAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectActivities(
                context.ActivityCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                context.Sources,
                effectiveDuration,
                context.MaxActivities,
                ct),
            (env, data) => env with { Activities = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunLogsAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectLogs(
                context.LogCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.Categories,
                context.MinLevel,
                context.MaxEvents ?? 500,
                context.MaxMessageBytes,
                context.Depth,
                ct),
            (env, data) => env with { Logs = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunJitAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectJit(
                context.JitCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.Depth,
                ct),
            (env, data) => env with { Jit = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunThreadPoolAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectThreadPool(
                context.ThreadPoolCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.Depth,
                ct),
            (env, data) => env with { ThreadPool = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunContentionAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectContention(
                context.ContentionCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.Depth,
                ct),
            (env, data) => env with { Contention = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunDbAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectDb(
                context.DbCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.IntervalSeconds,
                context.Depth,
                ct),
            (env, data) => env with { Db = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunKestrelAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectKestrel(
                context.KestrelCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.IntervalSeconds,
                context.Depth,
                ct),
            (env, data) => env with { Kestrel = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunNetworkingAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectNetworking(
                context.NetworkingCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.IntervalSeconds,
                context.Depth,
                ct),
            (env, data) => env with { Networking = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunRequestsAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectInFlightRequests(
                context.InFlightRequestCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.LongRunningThresholdMs,
                context.MaxRequests,
                context.Depth,
                ct),
            (env, data) => env with { Requests = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunStartupAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => DiagnosticTools.CollectStartup(
                context.StartupCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.Depth,
                ct),
            (env, data) => env with { Startup = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunSweepAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunTimedCollectionAsync(
            context,
            effectiveDuration,
            ct => SweepUseCase.RunSweep(
                context.CounterCollector,
                context.GcCollector,
                context.ExceptionCollector,
                context.ThreadPoolCollector,
                context.ProcessResourcesCollector,
                context.Resolver,
                context.Handles,
                context.ProcessId,
                effectiveDuration,
                context.MaxRecent,
                context.MaxEvents ?? 200,
                context.Depth,
                ct),
            (env, data) => env with { Sweep = data },
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunDistributedTraceKindAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunDistributedTraceAsync(
            context.RequestContext,
            context.Principal,
            context.TraceId,
            context.InvestigationHandleIds,
            effectiveDuration,
            context.MaxActivities,
            context.Sources,
            cancellationToken);

    private static Task<DiagnosticResult<CollectEventsEnvelope>> RunReplicaCountersKindAsync(CollectEventsDispatchContext context, int effectiveDuration, CancellationToken cancellationToken)
        => RunReplicaCountersAsync(
            context.RequestContext,
            context.Principal,
            context.InvestigationHandleIds,
            effectiveDuration,
            context.IntervalSeconds,
            cancellationToken);

    private static async Task<DiagnosticResult<CollectEventsEnvelope>> RunTimedCollectionAsync<TInner>(
        CollectEventsDispatchContext context,
        int effectiveDuration,
        Func<CancellationToken, Task<DiagnosticResult<TInner>>> collectAsync,
        Func<CollectEventsEnvelope, TInner, CollectEventsEnvelope> populate,
        CancellationToken cancellationToken)
        where TInner : class
    {
        try
        {
            return await CollectionProgressTicker.RunAsync(
                context.RequestContext,
                $"collect_events:{context.CanonicalKind}",
                TimeSpan.FromSeconds(effectiveDuration),
                TimeSpan.FromSeconds(1),
                async ct => Project(await collectAsync(ct).ConfigureAwait(false), context.CanonicalKind, populate),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DiagnosticResult<CollectEventsEnvelope>(
                $"collect_events(kind='{context.CanonicalKind}') cancelled by the client before the {effectiveDuration}s window elapsed. " +
                "No payload was retained — restart the collection to capture data.",
                Array.Empty<NextActionHint>())
            {
                Data = new CollectEventsEnvelope(context.CanonicalKind),
                Cancelled = true,
            };
        }
    }
}
