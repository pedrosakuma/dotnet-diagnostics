using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.DistributedTrace;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.GatedCapture;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.Tools.Dispatch;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Diagnostics;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// Consolidation: single MCP entry-point for the EventPipe collector family
/// (counters, exceptions, GC, EventSource, ActivitySource). Delegates to the legacy
/// <see cref="DiagnosticTools"/> methods for true behavioral parity — this tool exists
/// only to flatten the discriminator dispatch so the LLM picks one tool instead of five.
/// </summary>
/// <remarks>
/// <para>The tool inherits the union of the per-kind authorization scopes at dispatch time
/// (<c>read-counters</c> ∪ <c>eventpipe</c>) via <see cref="RequireAnyScopeAttribute"/>, then
/// re-checks the kind-specific scope inside the body so a caller holding only
/// <c>read-counters</c> cannot exfiltrate GC/exception/EventSource data through the new entry
/// point. This preserves docs/authorization.md#scopes boundaries verbatim.</para>
/// <para>#213 — the legacy collectors have been deleted in the alias
/// removal wave; this is now the sole entry point for the EventPipe collector family.</para>
/// </remarks>
[McpServerToolType]
public sealed class CollectEventsTool
{
    /// <summary>Allowed values for the <c>kind</c> discriminator. Order is preserved when
    /// rendered by <see cref="DiscriminatorDispatch"/> in failure envelopes so the LLM sees a
    /// stable hint list.</summary>
    internal static readonly IReadOnlyList<string> AllowedKinds = new[]
    {
        "counters",
        "exceptions",
        "crash-guard",
        "gc",
        "datas",
        "catalog",
        "event_source",
        "activities",
        "logs",
        "jit",
        "threadpool",
        "contention",
        "db",
        "kestrel",
        "networking",
        "startup",
        "distributed_trace",
    };

    [RequireAnyScope("read-counters", "eventpipe")]
    [McpServerTool(
        Name = "collect_events",
        Title = "Collect EventPipe events (counters | exceptions | crash-guard | gc | datas | catalog | event_source | activities | logs | jit | threadpool | contention | db | kestrel | networking | startup)",
        Destructive = false,
        // Not read-only: the threshold-gated capture path (triggerWhen + captureKind="dump") can write
        // a process dump to disk. That path is doubly gated (confirmDump=true + the dump-write scope),
        // but the static hint must still reflect that the tool can modify its environment.
        ReadOnly = false,
        Idempotent = false,
        UseStructuredContent = true,
        // Issue #425 — long EventPipe windows can be promoted to an MCP Task so the client owns
        // progress + cancellation uniformly with collect_sample. Optional: synchronous collection
        // (with notifications/progress via CollectionProgressTicker) still works for older clients.
        TaskSupport = ToolTaskSupport.Optional)]
    [Description(
        "Unified EventPipe collector. Choose what to capture via the 'kind' parameter " +
        "(counters, gc, exceptions, logs, …). Returns a drilldown handle.")]
    public static async Task<DiagnosticResult<CollectEventsEnvelope>> CollectEvents(
        // DI services (union of every kind's dependencies). The MCP SDK injects these per call;
        // tools that don't need a given collector simply ignore the unused parameter.
        ICounterCollector counterCollector,
        IExceptionCollector exceptionCollector,
        ICrashGuardCollector crashGuardCollector,
        IGcCollector gcCollector,
        IGcDatasCollector gcDatasCollector,
        IActivityCollector activityCollector,
        IEventSourceCollector eventSourceCollector,
        IEventCatalogCollector eventCatalogCollector,
        ILogCollector logCollector,
        IJitCollector jitCollector,
        IThreadPoolCollector threadPoolCollector,
        IContentionCollector contentionCollector,
        IDbCollector dbCollector,
        IKestrelCollector kestrelCollector,
        INetworkingCollector networkingCollector,
        IStartupCollector startupCollector,
        IThresholdGatedCaptureCollector gatedCaptureCollector,
        ICpuSampler cpuSampler,
        IThreadSnapshotInspector threadSnapshotInspector,
        IDumpInspector dumpInspector,
        IProcessDumper processDumper,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        EventSourceAllowlist allowlist,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        [Description(
            "Which EventPipe family to collect (default 'counters'): " +
            "'counters' (EventCounter snapshot — cheap first signal; uses the 'read-counters' scope), " +
            "'exceptions' (managed exception stream), 'crash-guard' (fatal/unhandled-exception postmortem guard), 'gc' (GC start/stop pairs + pause durations), " +
            "'datas' (DATAS Server-GC heap-count tuning), 'catalog' (metadata-only provider/event-name catalog), " +
            "'event_source' (generic provider passthrough — requires providerName), 'activities' (ActivitySource spans), " +
            "'logs' (curated ILogger view), 'jit' (tiered compilation / ReadyToRun activity), " +
            "'threadpool' (ThreadPool starvation: worker/IOCP timelines, hill-climbing, work-item origins), " +
            "'contention' (lock contention by call site + owner thread), 'db' (curated EF Core / SqlClient view), " +
            "'kestrel' (Kestrel HTTP server: connection/request/TLS latency, queue lengths, live KestrelServerOptions config), " +
            "'networking' (curated outbound HTTP / DNS / TLS / socket view: latency percentiles + HttpClient time-in-queue), " +
            "'startup' (loader + DependencyInjection events emitted during the window; pre-attach cold-start events are missed). " +
            "All kinds except 'counters' use the 'eventpipe' scope. " +
            "IMPORTANT: for 'exceptions', 'crash-guard', and 'gc', start collection BEFORE the workload — EventPipe sessions " +
            "take ~500 ms–1 s to fully start and earlier events are missed. For 'startup', attaching to an already-running process misses the initial cold-start; true cold-start capture requires enabling EventPipe before/at process launch (reverse-connect or CLI --launch/DOTNET_ startup session).")]
        string kind = "counters",
        // Shared options.
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")]
        int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults differ per kind (counters: 5; all other kinds: 10).")]
        int? durationSeconds = null,
        [Description("Verbosity (summary|detail|raw). Applies to all kinds; semantics match the legacy collectors — 'summary' trims the bulky inline list (Counters, Recent, Events) but keeps it behind the issued handle.")]
        SamplingDepth depth = SamplingDepth.Summary,
        // kind=counters
        [Description("kind=counters or kind=catalog. For counters: optional EventCounter provider names; null uses runtime/ASP.NET defaults and empty skips legacy EventCounters. For catalog: optional EventPipe provider names; null/empty uses a broad curated default set, and custom EventSources must be named explicitly because EventPipe has no wildcard.")]
        string[]? providers = null,
        [Description("kind=counters only. Optional list of Meter names to subscribe to through System.Diagnostics.Metrics. Null/empty disables Meter collection.")]
        string[]? meters = null,
        [Description("kind=counters only. Refresh interval (in seconds) requested from each provider. Defaults to 1.")]
        int intervalSeconds = 1,
        [Description("kind=counters only. Maximum Meter time series (and histograms) retained before the collector caps results. Defaults to 1000.")]
        int maxInstrumentTimeSeries = 1000,
        // kind=exceptions / kind=crash-guard
        [Description("kind=exceptions or kind=crash-guard only. Maximum number of individual exception details to return. Must be >= 1. Defaults to 100.")]
        int maxRecent = 100,
        // kind=gc / kind=catalog / kind=event_source / kind=logs
        [Description("kind=gc, kind=catalog, kind=event_source, or kind=logs. Maximum number of events to return. Must be >= 1. Defaults to 200 for gc/catalog/event_source and 500 for logs when omitted through the kind-specific path. Catalog samples are metadata-only; payload values are never captured.")]
        int? maxEvents = null,
        // kind=event_source
        [Description("kind=event_source only. EventSource provider name (e.g. 'System.Net.Http' or 'Microsoft.AspNetCore.Hosting'). Required when kind='event_source'.")]
        string? providerName = null,
        [Description("kind=event_source only. EventSource keyword mask. -1 (default) means all keywords. Clamped to a safer default for non-allowlisted providers (unsafeProvider path).")]
        long keywords = -1,
        [Description("kind=event_source only. Event verbosity level (0=LogAlways..5=Verbose). Defaults to 5.")]
        int eventLevel = 5,
        [Description("kind=event_source only. Opt-in switch for non-allowlisted EventSource providers (issue #165 / M2). Only honoured when the server has 'Diagnostics:AllowSensitiveHeapValues=true' or the principal holds the 'eventsource-any' scope.")]
        bool unsafeProvider = false,
        // kind=activities
        [Description("kind=activities only. Optional ActivitySource name filters. Supports '*' and '?' wildcards. Null/empty captures all sources.")]
        IReadOnlyList<string>? sources = null,
        [Description("kind=activities only. Maximum number of captured activities to retain. Must be >= 1. Defaults to 200.")]
        int maxActivities = 200,
        // kind=distributed_trace
        [Description("kind=distributed_trace only (REQUIRED). The W3C trace-id (32-hex, e.g. the 'trace-id' field of a 'traceparent' header) to correlate across every attached Pod. Orchestrator mode must be enabled and you must have attached to the replicas first (attach_to_pod). Fans out a bounded collect_events(kind=activities) to each attached Pod, then stitches the per-Pod spans into one timeline ordered by parent/child span links with the slowest hop flagged.")]
        string? traceId = null,
        // kind=logs
        [Description("kind=logs only. Optional case-insensitive glob filters for ILogger categories. Null/empty captures all categories.")]
        IReadOnlyList<string>? categories = null,
        [Description("kind=logs only. Minimum log level to retain (Trace|Debug|Information|Warning|Error|Critical). Defaults to Information.")]
        string minLevel = "Information",
        [Description("kind=logs only. Maximum UTF-8 bytes retained per message/scope/exception string before truncation. Defaults to 4096.")]
        int maxMessageBytes = 4096,
        // Bounded threshold-gated capture (issue #419). Requires kind=counters.
        [Description("Threshold-gated capture (kind=counters). Single metric predicate that arms a BOUNDED watch: <metric><op><value>, e.g. 'cpu>85', 'gcHeapMb>=1500', 'rssMb>2000', 'threadCount>400', 'activeTimerCount>1000'. Operators: > >= < <=. When set together with captureKind, collect_events polls the metric for at most windowSeconds and fires captureKind the moment the predicate trips (NOT a daemon — one synchronous call). Metrics map to System.Runtime EventCounters (rssMb=working-set, threadCount=threadpool-thread-count).")]
        string? triggerWhen = null,
        [Description("Threshold-gated capture (kind=counters). What to capture when triggerWhen trips: 'dump' | 'cpu-sample' | 'heap' | 'thread-snapshot'. The artifact registers under a drilldown handle (cpu-sample/heap/thread-snapshot) or writes to disk (dump). Required scopes by kind: cpu-sample=eventpipe; heap=heap-read+ptrace; thread-snapshot=ptrace; dump=dump-write+ptrace.")]
        string? captureKind = null,
        [Description("Threshold-gated capture only. Hard upper bound (seconds) on how long the watch is armed. Required when triggerWhen/captureKind are set. Must be 1..300 (the watch is bounded — no indefinite arming).")]
        int windowSeconds = 0,
        [Description("Threshold-gated capture only. Hard cap on how many captures fire before the call returns. Must be 1..10. Defaults to 1.")]
        int maxCaptures = 1,
        [Description("Threshold-gated capture only. How often (seconds) the metric is polled within the window. Must be 1..windowSeconds. Defaults to 2.")]
        int sampleIntervalSeconds = 2,
        [Description("Threshold-gated capture only. captureKind='dump' confirmation gate — writing a heap dump to disk requires confirmDump=true (defense-in-depth, mirrors collect_process_dump). Ignored for other capture kinds.")]
        bool confirmDump = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (!DiscriminatorDispatch.TryValidate<CollectEventsEnvelope>(
                kind, AllowedKinds, nameof(kind), out var canonicalKind, out var dispatchFailure))
        {
            return dispatchFailure!;
        }

        // Per-kind authorization re-check. The dispatch-time gate only proved the caller holds
        // one of {read-counters, eventpipe}; we now enforce the precise legacy scope so this
        // unified entry-point cannot widen a narrower bearer's reach. Skipped when no principal
        // is materialized (stdio root accessor returns null — treated as root by the filter).
        var principal = principalAccessor.Current;
        if (principal is not null)
        {
            var requiredScope = canonicalKind == "counters" ? "read-counters" : "eventpipe";
            if (!principal.HasScope(requiredScope))
            {
                var message =
                    $"kind='{canonicalKind}' requires the '{requiredScope}' scope. " +
                    "collect_events preserves the per-kind authorization boundary of its legacy collectors.";
                return DiagnosticResult.Fail<CollectEventsEnvelope>(
                    message,
                    new DiagnosticError("InsufficientScope", message, requiredScope));
            }
        }

        // Distributed trace correlation (#437): orchestrator fan-out across attached Pods. Handled
        // before the gated-capture + single-process EventPipe paths because it neither targets a
        // single pid nor opens a local EventPipe session — it proxies collect_events(kind=activities)
        // to each attached Pod and stitches the results. Requires orchestrator services (only present
        // when Orchestrator:Enabled=true) which are resolved optionally from the request scope.
        if (canonicalKind == "distributed_trace")
        {
            return await RunDistributedTraceAsync(
                requestContext, principal, traceId, durationSeconds, maxActivities, sources, cancellationToken)
                .ConfigureAwait(false);
        }

        // Bounded threshold-gated capture (#419): when triggerWhen + captureKind are supplied, the
        // call arms a bounded watch instead of a fixed-window collection. Requires kind=counters
        // (the metric source) and re-checks the per-captureKind scope on top of read-counters.
        var gatingRequested = !string.IsNullOrWhiteSpace(triggerWhen) || !string.IsNullOrWhiteSpace(captureKind);
        if (gatingRequested)
        {
            return await RunGatedCaptureAsync(
                gatedCaptureCollector, resolver, handles, cpuSampler, threadSnapshotInspector, dumpInspector, processDumper,
                principal, canonicalKind, triggerWhen, captureKind, windowSeconds, maxCaptures, sampleIntervalSeconds,
                confirmDump, processId, cancellationToken).ConfigureAwait(false);
        }

        // Default durationSeconds per kind matches the legacy tool defaults so callers omitting
        // the parameter see no behavioral change.
        var effectiveDuration = durationSeconds ?? (canonicalKind == "counters" ? 5 : canonicalKind == "datas" ? 15 : 10);

        // Stage A of issue #211: emit MCP notifications/progress while the
        // EventPipe session is open, and translate MCP notifications/cancelled into a partial
        // envelope so spec-compliant clients no longer need the legacy job-polling bridge.
        try
        {
            return await CollectionProgressTicker.RunAsync(
                requestContext,
                $"collect_events:{canonicalKind}",
                TimeSpan.FromSeconds(effectiveDuration),
                TimeSpan.FromSeconds(1),
                async ct => canonicalKind switch
                {
                    "counters" => Project(
                        await DiagnosticTools.SnapshotCounters(
                            counterCollector, resolver, handles,
                            processId, effectiveDuration, providers, meters, intervalSeconds, maxInstrumentTimeSeries, depth,
                            ct).ConfigureAwait(false),
                        "counters",
                        (env, data) => env with { Counters = data }),

                    "exceptions" => Project(
                        await DiagnosticTools.CollectExceptions(
                            exceptionCollector, resolver, handles,
                            processId, effectiveDuration, maxRecent, depth,
                            ct).ConfigureAwait(false),
                        "exceptions",
                        (env, data) => env with { Exceptions = data }),

                    "crash-guard" => Project(
                        await DiagnosticTools.CollectCrashGuard(
                            crashGuardCollector, resolver, handles,
                            processId, effectiveDuration, maxRecent, depth,
                            ct).ConfigureAwait(false),
                        "crash-guard",
                        (env, data) => env with { CrashGuard = data }),

                    "gc" => Project(
                        await DiagnosticTools.CollectGcEvents(
                            gcCollector, resolver, handles,
                            processId, effectiveDuration, maxEvents ?? 200, depth,
                            ct).ConfigureAwait(false),
                        "gc",
                        (env, data) => env with { Gc = data }),

                    "datas" => Project(
                        await DiagnosticTools.CollectGcDatas(
                            gcDatasCollector, resolver, handles,
                            processId, effectiveDuration, maxEvents ?? 1000,
                            ct).ConfigureAwait(false),
                        "datas",
                        (env, data) => env with { Datas = data }),

                    "catalog" => Project(
                        await DiagnosticTools.CollectEventCatalog(
                            eventCatalogCollector, resolver, handles,
                            processId, effectiveDuration, providers, maxEvents ?? 200, depth,
                            ct).ConfigureAwait(false),
                        "catalog",
                        (env, data) => env with { Catalog = data }),

                    "event_source" => Project(
                        await DiagnosticTools.CollectEventSource(
                            eventSourceCollector, resolver, handles,
                            allowlist, sensitiveGate, principalAccessor,
                            providerName ?? string.Empty,
                            processId, effectiveDuration, keywords, eventLevel, maxEvents ?? 200, depth,
                            unsafeProvider, deprecation,
                            ct).ConfigureAwait(false),
                        "event_source",
                        (env, data) => env with { EventSource = data }),

                    "activities" => Project(
                        await DiagnosticTools.CollectActivities(
                            activityCollector, resolver, handles,
                            processId, sources, effectiveDuration, maxActivities,
                            ct).ConfigureAwait(false),
                        "activities",
                        (env, data) => env with { Activities = data }),

                    "logs" => Project(
                        await DiagnosticTools.CollectLogs(
                            logCollector, resolver, handles,
                            processId, effectiveDuration, categories, minLevel,
                            maxEvents ?? 500, maxMessageBytes, depth,
                            ct).ConfigureAwait(false),
                        "logs",
                        (env, data) => env with { Logs = data }),

                    "jit" => Project(
                        await DiagnosticTools.CollectJit(
                            jitCollector, resolver, handles,
                            processId, effectiveDuration, depth,
                            ct).ConfigureAwait(false),
                        "jit",
                        (env, data) => env with { Jit = data }),

                    "threadpool" => Project(
                        await DiagnosticTools.CollectThreadPool(
                            threadPoolCollector, resolver, handles,
                            processId, effectiveDuration, depth,
                            ct).ConfigureAwait(false),
                        "threadpool",
                        (env, data) => env with { ThreadPool = data }),

                    "contention" => Project(
                        await DiagnosticTools.CollectContention(
                            contentionCollector, resolver, handles,
                            processId, effectiveDuration, depth,
                            ct).ConfigureAwait(false),
                        "contention",
                        (env, data) => env with { Contention = data }),
 
                    "db" => Project(
                        await DiagnosticTools.CollectDb(
                            dbCollector, resolver, handles,
                            processId, effectiveDuration, intervalSeconds, depth,
                            ct).ConfigureAwait(false),
                        "db",
                        (env, data) => env with { Db = data }),

                    "kestrel" => Project(
                        await DiagnosticTools.CollectKestrel(
                            kestrelCollector, resolver, handles,
                            processId, effectiveDuration, intervalSeconds, depth,
                            ct).ConfigureAwait(false),
                        "kestrel",
                        (env, data) => env with { Kestrel = data }),

                    "networking" => Project(
                        await DiagnosticTools.CollectNetworking(
                            networkingCollector, resolver, handles,
                            processId, effectiveDuration, intervalSeconds, depth,
                            ct).ConfigureAwait(false),
                        "networking",
                        (env, data) => env with { Networking = data }),

                    "startup" => Project(
                        await DiagnosticTools.CollectStartup(
                            startupCollector, resolver, handles,
                            processId, effectiveDuration, depth,
                            ct).ConfigureAwait(false),
                        "startup",
                        (env, data) => env with { Startup = data }),
 
                    // Unreachable — TryValidate narrowed canonicalKind to the AllowedKinds set above.
                    _ => DiagnosticResult.Fail<CollectEventsEnvelope>(
                        $"Unhandled kind '{canonicalKind}'.",
                        new DiagnosticError("InvalidArgument", $"Unhandled kind '{canonicalKind}'.", nameof(kind))),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DiagnosticResult<CollectEventsEnvelope>(
                $"collect_events(kind='{canonicalKind}') cancelled by the client before the {effectiveDuration}s window elapsed. " +
                "No payload was retained — restart the collection to capture data.",
                Array.Empty<NextActionHint>())
            {
                Data = new CollectEventsEnvelope(canonicalKind),
                Cancelled = true,
            };
        }
    }

    /// <summary>
    /// Re-wraps a legacy collector's <see cref="DiagnosticResult{T}"/> as a
    /// <see cref="CollectEventsEnvelope"/>-shaped result, preserving Summary, Hints, Handle,
    /// HandleExpiresAt, ResolvedProcess and Error so callers see the exact same envelope they
    /// got from the legacy tool — only the typed payload moves into the polymorphic shape.
    /// </summary>
    private static DiagnosticResult<CollectEventsEnvelope> Project<TInner>(
        DiagnosticResult<TInner> inner,
        string kind,
        Func<CollectEventsEnvelope, TInner, CollectEventsEnvelope> populate)
    {
        var envelope = new CollectEventsEnvelope(kind);
        if (inner.Data is not null)
        {
            envelope = populate(envelope, inner.Data);
        }

        return new DiagnosticResult<CollectEventsEnvelope>(inner.Summary, inner.Hints, inner.Error)
        {
            Data = inner.IsError ? null : envelope,
            Handle = inner.Handle,
            HandleExpiresAt = inner.HandleExpiresAt,
            ResolvedProcess = inner.ResolvedProcess,
        };
    }

    /// <summary>
    /// Bounded threshold-gated capture path (#419). Re-checks the captureKind-specific scope on top
    /// of the read-counters gate, then arms a single bounded watch via <see cref="GatedCaptureUseCases"/>.
    /// </summary>
    private static async Task<DiagnosticResult<CollectEventsEnvelope>> RunGatedCaptureAsync(
        IThresholdGatedCaptureCollector gatedCaptureCollector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        ICpuSampler cpuSampler,
        IThreadSnapshotInspector threadSnapshotInspector,
        IDumpInspector dumpInspector,
        IProcessDumper processDumper,
        BearerPrincipal? principal,
        string canonicalKind,
        string? triggerWhen,
        string? captureKind,
        int windowSeconds,
        int maxCaptures,
        int sampleIntervalSeconds,
        bool confirmDump,
        int? processId,
        CancellationToken cancellationToken)
    {
        if (canonicalKind != "counters")
        {
            var message = $"Threshold-gated capture (triggerWhen/captureKind) is only supported with kind='counters' (the metric source). Got kind='{canonicalKind}'.";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message, new DiagnosticError("InvalidArgument", message, "kind"));
        }

        // captureKind-specific scope re-check (the dispatch gate only proved read-counters/eventpipe).
        if (principal is not null && GatedCaptureKinds.TryParse(captureKind, out var parsedKind))
        {
            foreach (var scope in RequiredCaptureScopes(parsedKind.Value))
            {
                if (!principal.HasScope(scope))
                {
                    var message = $"captureKind='{GatedCaptureKinds.Token(parsedKind.Value)}' requires the '{scope}' scope.";
                    return DiagnosticResult.Fail<CollectEventsEnvelope>(
                        message, new DiagnosticError("InsufficientScope", message, scope));
                }
            }
        }

        var result = await GatedCaptureUseCases.WatchAndCapture(
            gatedCaptureCollector, resolver, handles, cpuSampler, threadSnapshotInspector, dumpInspector, processDumper,
            triggerWhen, captureKind, windowSeconds, maxCaptures, sampleIntervalSeconds, confirmDump, processId,
            dumpOutputDirectory: null, cancellationToken).ConfigureAwait(false);

        return Project(result, "counters", (env, data) => env with { GatedCapture = data });
    }

    private static string[] RequiredCaptureScopes(GatedCaptureKind kind) => kind switch
    {
        GatedCaptureKind.CpuSample => new[] { "eventpipe" },
        GatedCaptureKind.Heap => new[] { "heap-read", "ptrace" },
        GatedCaptureKind.ThreadSnapshot => new[] { "ptrace" },
        GatedCaptureKind.Dump => new[] { "dump-write", "ptrace" },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// Distributed trace correlation fan-out (#437). Resolves the orchestrator services from the
    /// request scope (present only when Orchestrator:Enabled=true), enumerates the caller's active
    /// investigations, fans out a bounded activities capture to each, and stitches one timeline.
    /// </summary>
    private static async Task<DiagnosticResult<CollectEventsEnvelope>> RunDistributedTraceAsync(
        RequestContext<CallToolRequestParams>? requestContext,
        BearerPrincipal? principal,
        string? traceId,
        int? durationSeconds,
        int maxActivities,
        IReadOnlyList<string>? sources,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            const string message = "kind='distributed_trace' requires a 'traceId' (the 32-hex W3C trace-id to correlate across attached Pods).";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message,
                new DiagnosticError("InvalidArgument", message, "traceId"),
                new NextActionHint("collect_events", "Pass the trace-id from the slow request's 'traceparent' header.", null));
        }

        if (maxActivities < 1)
        {
            const string message = "maxActivities must be >= 1.";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message, new DiagnosticError("InvalidArgument", message, "maxActivities"));
        }

        var effectiveDuration = durationSeconds ?? 10;
        if (effectiveDuration < 1)
        {
            const string message = "durationSeconds must be >= 1.";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message, new DiagnosticError("InvalidArgument", message, "durationSeconds"));
        }

        var services = requestContext?.Services;
        var store = services?.GetService(typeof(IInvestigationStore)) as IInvestigationStore;
        var proxy = services?.GetService(typeof(IInvestigationProxyClient)) as IInvestigationProxyClient;
        var options = services?.GetService(typeof(OrchestratorOptions)) as OrchestratorOptions;

        if (store is null || proxy is null || options is null || !options.Enabled)
        {
            const string message = "kind='distributed_trace' requires orchestrator mode (Orchestrator:Enabled=true). " +
                "It correlates a trace across Pods you have attached to via attach_to_pod.";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message,
                new DiagnosticError(OrchestratorErrorKinds.OrchestratorDisabled, message),
                new NextActionHint("list_orchestrator", "Enable orchestrator mode and attach to the replicas first.",
                    new Dictionary<string, object?> { ["kind"] = "pods" }));
        }

        // Distributed correlation reads investigation handles + drives the proxy — gate it on the
        // same scope attach_to_pod / list investigations use, on top of the eventpipe collection scope.
        if (principal is not null && !principal.HasScope("orchestrator-attach"))
        {
            const string message = "kind='distributed_trace' requires the 'orchestrator-attach' scope (it reads your investigation handles and proxies to the attached Pods).";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message, new DiagnosticError(OrchestratorErrorKinds.PermissionDenied, message, "orchestrator-attach"));
        }

        var callerSessionId = requestContext?.Server is { } server
            ? OrchestratorTools.TryGetServerSessionId(server)
            : null;

        var fanout = await DistributedTraceCorrelator.CorrelateAsync(
            store, proxy, callerSessionId, traceId.Trim(), effectiveDuration, maxActivities, sources, cancellationToken)
            .ConfigureAwait(false);

        if (fanout.AttachedActivePods == 0)
        {
            var message = "kind='distributed_trace': no Active investigations are attached. Call attach_to_pod for each replica first, then re-run with the same traceId.";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message,
                new DiagnosticError("NoActiveInvestigation", message),
                new NextActionHint("list_orchestrator", "List candidate Pods, then attach_to_pod to the replicas serving this trace.",
                    new Dictionary<string, object?> { ["kind"] = "pods" }));
        }

        var timeline = fanout.Timeline;
        var hints = new List<NextActionHint>();
        string summary;

        // Timeline is null only when zero per-Pod collections succeeded. With at least one attached Pod
        // that means every reachable collection failed — surface that as a fan-out error, not as an
        // empty (in-flight) trace, so the LLM does not conclude "the trace simply wasn't live".
        if (timeline is null)
        {
            var message = $"distributed_trace {traceId}: every one of the {fanout.AttachedActivePods} attached Pod(s) " +
                "failed to collect activities — no spans could be correlated. See the per-Pod errors.";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message,
                new DiagnosticError("DistributedTraceFanoutFailed", message),
                new NextActionHint("list_orchestrator", "Verify the attached Pods are still reachable, then re-run with the same traceId.",
                    new Dictionary<string, object?> { ["kind"] = "investigations" }))
                with
            { Data = new CollectEventsEnvelope("distributed_trace", DistributedTrace: null, PodErrors: fanout.PodErrors) };
        }

        if (timeline.SpanCount == 0)
        {
            summary = $"distributed_trace {traceId}: fanned out to {fanout.AttachedActivePods} attached Pod(s) " +
                $"but no matching spans were captured in {effectiveDuration}s. Trace correlation targets in-flight traces — re-run while the trace is live.";
            hints.Add(new NextActionHint("collect_events",
                "Re-issue with the trace live, or widen durationSeconds; confirm the replicas emit ActivitySource instrumentation.",
                new Dictionary<string, object?> { ["kind"] = "distributed_trace", ["traceId"] = traceId, ["durationSeconds"] = effectiveDuration + 5 }));
        }
        else
        {
            var slow = timeline.SlowestHop;
            summary = $"distributed_trace {timeline.TraceId}: stitched {timeline.SpanCount} span(s) across {timeline.Coverage.Count(c => c.MatchedSpans > 0)}/{fanout.AttachedActivePods} attached Pod(s)." +
                (slow is not null
                    ? $" Slowest hop: {slow.PodName} {slow.SourceName}/{slow.OperationName} (self {slow.SelfDurationMs:F1} ms)."
                    : string.Empty);
            if (slow is not null)
            {
                hints.Add(new NextActionHint("collect_sample",
                    $"Drill into the slowest hop on Pod '{slow.PodName}' to see what its CPU is doing.",
                    new Dictionary<string, object?> { ["kind"] = "cpu", ["durationSeconds"] = 10 }));
            }
        }

        if (fanout.PodErrors.Count > 0)
        {
            summary += $" {fanout.PodErrors.Count} Pod(s) could not be collected (see data.podErrors).";
        }

        var envelope = new CollectEventsEnvelope("distributed_trace", DistributedTrace: timeline, PodErrors: fanout.PodErrors);
        return DiagnosticResult.Ok(envelope, summary, hints.ToArray());
    }
}

/// <summary>
/// Polymorphic payload returned by <see cref="CollectEventsTool.CollectEvents"/>. Exactly one
/// of the kind-specific fields (<see cref="Counters"/>, <see cref="Exceptions"/>, <see cref="CrashGuard"/>,
/// <see cref="Gc"/>, <see cref="Datas"/>, <see cref="Catalog"/>, <see cref="EventSource"/>, <see cref="Activities"/>, <see cref="Logs"/>, <see cref="Jit"/>, <see cref="ThreadPool"/>, <see cref="Contention"/>, <see cref="Db"/>, <see cref="Kestrel"/>, <see cref="Networking"/>, <see cref="Startup"/>) is populated, matched
/// by <see cref="Kind"/>. Mirrors the discriminator-envelope convention used by other
/// consolidated tools (e.g. <c>get_method_il</c>).
/// </summary>
public sealed record CollectEventsEnvelope(
    string Kind,
    CounterSnapshot? Counters = null,
    ExceptionSnapshot? Exceptions = null,
    CrashGuardSnapshot? CrashGuard = null,
    GcSummary? Gc = null,
    GcDatasSnapshot? Datas = null,
    EventCatalogSnapshot? Catalog = null,
    EventSourceCapture? EventSource = null,
    ActivityCapture? Activities = null,
    LogSnapshot? Logs = null,
    JitSnapshot? Jit = null,
    ThreadPoolEventSnapshot? ThreadPool = null,
    ContentionSnapshot? Contention = null,
    DbSnapshot? Db = null,
    KestrelSnapshot? Kestrel = null,
    NetworkingSnapshot? Networking = null,
    StartupSnapshot? Startup = null,
    GatedCaptureResult? GatedCapture = null,
    DistributedTraceTimeline? DistributedTrace = null,
    IReadOnlyList<string>? PodErrors = null);
