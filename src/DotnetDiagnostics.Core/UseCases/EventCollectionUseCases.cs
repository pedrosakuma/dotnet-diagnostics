using System.Globalization;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.ThreadPool;
using Microsoft.Extensions.Logging;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral EventPipe collection use cases (issue #288, building on the #285 process-inspection
/// extraction). These own the full <see cref="DiagnosticResult{T}"/> orchestration — argument
/// validation, process resolution, collector invocation, depth-gated inlining, handle registration,
/// summary text and next-action hints — for the ten <c>collect</c> kinds (counters, exceptions, gc,
/// logs, jit, threadpool, contention, db, activities, event_source). They depend on Core
/// abstractions only and carry no MCP/transport knowledge, so both the MCP <c>collect_events</c>
/// tool and the standalone <c>dotnet-diagnostics collect</c> CLI share one behavior.
/// </summary>
/// <remarks>
/// The MCP Server keeps thin <c>DiagnosticTools.Collect*</c> wrappers (preserving their signatures
/// and the per-kind scope re-check + progress ticker that live in the tool layer) that forward here,
/// so the existing envelope byte-compat tests keep passing. The only kind with a transport seam is
/// <see cref="CollectEventSource"/>: instead of taking the Server <c>IPrincipalAccessor</c> /
/// <c>LegacyDiagnosticsFlagDeprecation</c> it takes a pre-computed
/// <c>principalAllowsEventSourceAny</c> bool and an <see cref="IEventSourceDeprecationSink"/>.
/// </remarks>
public static class EventCollectionUseCases
{
    private static readonly TimeSpan CollectionHandleTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Snapshots EventCounters / Meters and curates a headline summary with threshold-driven hints.
    /// </summary>
    public static async Task<DiagnosticResult<CounterSnapshot>> SnapshotCounters(
        ICounterCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 5,
        string[]? providers = null,
        string[]? meters = null,
        int intervalSeconds = 1,
        int maxInstrumentTimeSeries = 1000,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1)
        {
            return InvalidArg<CounterSnapshot>(nameof(durationSeconds), "must be >= 1");
        }
        if (intervalSeconds < 1)
        {
            return InvalidArg<CounterSnapshot>(nameof(intervalSeconds), "must be >= 1");
        }
        if (maxInstrumentTimeSeries < 1)
        {
            return InvalidArg<CounterSnapshot>(nameof(maxInstrumentTimeSeries), "must be >= 1");
        }

        var resolved = await ResolveContextAsync<CounterSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(durationSeconds),
            providers,
            meters,
            intervalSeconds,
            maxInstrumentTimeSeries,
            cancellationToken).ConfigureAwait(false);

        var cpu = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "cpu-usage");
        var heap = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "gc-heap-size");
        var timeInGc = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "time-in-gc");
        var queueLength = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "threadpool-queue-length");
        var allocRate = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "alloc-rate");
        var gen2Count = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "gen-2-gc-count");
        var contention = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "monitor-lock-contention-count");

        // Build hints based on counter thresholds (highest priority first).
        var hints = new List<NextActionHint>();

        // CPU > 70% → recommend CPU sampling (existing behavior, highest priority).
        if ((cpu?.Value ?? 0) >= 70)
        {
            hints.Add(new NextActionHint("collect_sample", $"cpu-usage={cpu!.Value:F1}% — investigate the hot path.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10, ["topN"] = 25 }));
        }

        // ThreadPool queue > 50 → potential starvation (Phase 12 Wave 1.1).
        if ((queueLength?.Value ?? 0) > 50)
        {
            hints.Add(new NextActionHint("collect_events", $"threadpool-queue-length={queueLength!.Value:F0} — possible ThreadPool starvation.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "threadpool", ["durationSeconds"] = 10 }));
        }

        // GC time > 15% → GC pressure (Phase 12 Wave 1.2).
        if ((timeInGc?.Value ?? 0) > 15)
        {
            hints.Add(new NextActionHint("collect_events", $"time-in-gc={timeInGc!.Value:F1}% — GC pressure detected.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "gc", ["durationSeconds"] = 10 }));
            hints.Add(new NextActionHint("inspect_heap", "GC pressure — inspect heap for allocation patterns.",
                new Dictionary<string, object?> { ["processId"] = pid, ["source"] = "live" }));
        }

        // High allocation rate with active Gen2 GCs → allocation pressure (Phase 12 Wave 1.3).
        // gen-2-gc-count is an EventCounter increment that shows the count in the last interval only,
        // not the cumulative total. Any Gen2 GC activity (> 0 in last interval) combined with high
        // alloc-rate (> 50 MB/s) signals an allocation hotspot worth investigating.
        if ((allocRate?.Value ?? 0) > 50_000_000 && (gen2Count?.Value ?? 0) > 0)
        {
            hints.Add(new NextActionHint("collect_sample", $"alloc-rate={allocRate!.Value / 1_000_000:F1} MB/s + gen-2 GCs active — allocation hotspot likely.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "allocation", ["durationSeconds"] = 10 }));
        }

        // High lock contention → investigate contention sources (Phase 12 Wave 1.4).
        // monitor-lock-contention-count > 10/interval suggests lock storms worth investigating.
        if ((contention?.Value ?? 0) > 10)
        {
            hints.Add(new NextActionHint("collect_events", $"monitor-lock-contention-count={contention!.Value:F0} — lock contention detected.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "contention", ["durationSeconds"] = 10 }));
        }

        // I/O bounded pattern: low CPU (< 30%) but queue building up → likely waiting on external I/O.
        // Suggest thread snapshot to see blocking stacks + activities to see what's in-flight.
        if ((cpu?.Value ?? 0) < 30 && (queueLength?.Value ?? 0) > 10)
        {
            hints.Add(new NextActionHint("collect_thread_snapshot", $"cpu-usage={cpu?.Value:F1}% but threadpool-queue-length={queueLength?.Value:F0} — I/O bound likely, inspect blocking stacks.",
                new Dictionary<string, object?> { ["processId"] = pid }));
            hints.Add(new NextActionHint("collect_events", "Low CPU + queue buildup — trace activities to see what's waiting.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "activities", ["durationSeconds"] = 10 }));
        }

        // Fallback: counters look quiet, suggest GC events to confirm.
        if (hints.Count == 0)
        {
            hints.Add(new NextActionHint("collect_events", "Counters look quiet — confirm there are no GC pauses before widening scope.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "gc", ["durationSeconds"] = 10 }));
        }

        // The handle always carries the FULL snapshot (query_collection drilldown stays cheap),
        // but the inline payload is depth-gated to keep first-look responses small.
        var handle = handles.Register(pid, CollectionHandleKinds.Counters, snapshot, CollectionHandleTtl);

        var inlinePayload = snapshot;
        var droppedCounters = 0;
        var droppedMeters = 0;
        if (depth == SamplingDepth.Summary)
        {
            var filteredCounters = HeadlineCounters.FilterCounters(snapshot.Counters);
            var filteredMeters = HeadlineCounters.FilterMeters(snapshot.Meters);
            droppedCounters = snapshot.Counters.Count - filteredCounters.Count;
            droppedMeters = snapshot.Meters.Count - filteredMeters.Count;
            inlinePayload = snapshot with { Counters = filteredCounters, Meters = filteredMeters };
        }

        var requestDuration = HeadlineCounters.FindRequestDuration(snapshot.Meters);
        var requestDurationText = requestDuration?.Histogram is { } histogram
            ? $", http.server.request.duration p95={histogram.P95.ToString("F3", CultureInfo.InvariantCulture)}{requestDuration.Unit}"
            : string.Empty;
        var noteText = snapshot.Notes.Count > 0
            ? $" Notes: {string.Join(" | ", snapshot.Notes)}"
            : string.Empty;

        var summaryText = depth == SamplingDepth.Summary
            ? $"Captured {snapshot.Counters.Count} counter(s) and {snapshot.Meters.Count} meter series over {durationSeconds}s — showing {inlinePayload.Counters.Count} headline counter(s) and {inlinePayload.Meters.Count} headline meter(s) (dropped {droppedCounters} counter(s), {droppedMeters} meter(s); handle has all). cpu-usage={cpu?.Value:F1}%, gc-heap-size={heap?.Value:F1}{requestDurationText}.{noteText}"
            : $"Captured {snapshot.Counters.Count} counter(s) and {snapshot.Meters.Count} meter series over {durationSeconds}s — cpu-usage={cpu?.Value:F1}%, gc-heap-size={heap?.Value:F1}{requestDurationText}.{noteText}";

        // Always add the drill-down hint at the end.
        hints.Add(new NextActionHint("query_snapshot",
            "Drill into this counter snapshot without re-collecting (views: summary, byProvider).",
            new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byProvider" }));

        var ok = DiagnosticResult.OkWithHandle(
            inlinePayload,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            [.. hints]);
        return WithContext(ok, resolved.Context);
    }

    /// <summary>Captures the managed exception stream for the window.</summary>
    public static async Task<DiagnosticResult<ExceptionSnapshot>> CollectExceptions(
        IExceptionCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        int maxRecent = 100,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<ExceptionSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (maxRecent < 1) return InvalidArg<ExceptionSnapshot>(nameof(maxRecent), "must be >= 1");

        var resolved = await ResolveContextAsync<ExceptionSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snap = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), maxRecent, cancellationToken).ConfigureAwait(false);
        var topType = snap.ByType.OrderByDescending(c => c.Count).FirstOrDefault();

        var inlineSnap = snap;
        var droppedRecent = 0;
        if (depth == SamplingDepth.Summary && snap.Recent.Count > 0)
        {
            droppedRecent = snap.Recent.Count;
            inlineSnap = snap with { Recent = Array.Empty<ManagedExceptionEvent>() };
        }

        var summary = snap.TotalExceptions == 0
            ? $"No managed exceptions thrown in {durationSeconds}s. If you expected some, ensure the collection started before the workload."
            : (depth == SamplingDepth.Summary && droppedRecent > 0
                ? $"{snap.TotalExceptions} exception(s) over {durationSeconds}s; most common: {topType?.ExceptionType} ({topType?.Count}). Dropped {droppedRecent} Recent entry(ies) from inline (handle has all)."
                : $"{snap.TotalExceptions} exception(s) over {durationSeconds}s; most common: {topType?.ExceptionType} ({topType?.Count}).");

        var primaryHint = snap.TotalExceptions > 0
            ? new NextActionHint("collect_events", "Subscribe to a domain-specific EventSource to correlate with the exception spikes.",
                new Dictionary<string, object?> { ["processId"] = pid, ["providerName"] = "System.Net.Http", ["durationSeconds"] = 10 })
            : new NextActionHint("collect_events", "No exception pressure — sweep GC events next.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 });

        var handle = handles.Register(pid, CollectionHandleKinds.ExceptionSnapshot, snap, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnap,
            summary,
            handle.Id,
            handle.ExpiresAt,
            primaryHint,
            new NextActionHint("query_snapshot",
                "Drill into this exception snapshot without re-collecting (views: summary, byType, recent).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byType", ["topN"] = 20 })),
            resolved.Context);
    }

    /// <summary>Captures exception/crash signals and returns early if the target exits.</summary>
    public static async Task<DiagnosticResult<CrashGuardSnapshot>> CollectCrashGuard(
        ICrashGuardCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        int maxRecent = 100,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<CrashGuardSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (maxRecent < 1) return InvalidArg<CrashGuardSnapshot>(nameof(maxRecent), "must be >= 1");

        var resolved = await ResolveContextAsync<CrashGuardSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snap = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), maxRecent, cancellationToken).ConfigureAwait(false);
        var inlineSnap = snap;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnap = snap with { Exceptions = Array.Empty<CrashGuardExceptionEvent>() };
        }

        var handle = handles.Register(pid, CollectionHandleKinds.CrashGuardSnapshot, snap, CollectionHandleTtl, evictWhenProcessExits: false, origin: HandleOrigin.Live);
        var hints = new List<NextActionHint>
        {
            new("query_snapshot",
                "Drill into this crash-guard snapshot without re-collecting (views: summary, exceptions, stack).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = snap.FinalException is not null ? "stack" : "exceptions", ["topN"] = 25 }),
        };

        if (snap.UnhandledExceptionObserved)
        {
            hints.Insert(0, new NextActionHint("collect_process_dump",
                "An unhandled exception was observed; collect or inspect the crash dump for heap/native context while correlating with this exception stream.",
                new Dictionary<string, object?> { ["processId"] = pid, ["dumpType"] = "Mini" }));
        }

        var summary = snap.UnhandledExceptionObserved && snap.FinalException is { } final
            ? $"Crash guard observed an unhandled {final.ExceptionType} after {snap.Duration.TotalSeconds:F1}s: {final.ExceptionMessage}"
            : snap.ProcessExited
                ? $"Crash guard observed process exit after {snap.Duration.TotalSeconds:F1}s (exitCode={snap.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}) and {snap.TotalExceptions} exception(s)."
                : $"Crash guard captured {snap.TotalExceptions} exception(s) over {durationSeconds}s; no unhandled exception or process exit observed.";

        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnap,
            summary,
            handle.Id,
            handle.ExpiresAt,
            hints.ToArray()),
            resolved.Context);
    }

    /// <summary>Pairs GCStart/GCStop events into pause durations and per-generation counts.</summary>
    public static async Task<DiagnosticResult<GcSummary>> CollectGcEvents(
        IGcCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        int maxEvents = 200,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<GcSummary>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<GcSummary>(nameof(maxEvents), "must be >= 1");

        var resolved = await ResolveContextAsync<GcSummary>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var gc = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), maxEvents, cancellationToken).ConfigureAwait(false);

        var inlineGc = gc;
        var droppedEvents = 0;
        if (depth == SamplingDepth.Summary && gc.Events.Count > 0)
        {
            droppedEvents = gc.Events.Count;
            inlineGc = gc with { Events = Array.Empty<GcEvent>() };
        }

        var summary = gc.TotalCollections == 0
            ? $"No GC activity in {durationSeconds}s — heap is quiet or the workload is idle."
            : (depth == SamplingDepth.Summary && droppedEvents > 0
                ? $"{gc.TotalCollections} collection(s), max pause {gc.MaxPauseTime.TotalMilliseconds:F1}ms, total pause {gc.TotalPauseTime.TotalMilliseconds:F1}ms. Dropped {droppedEvents} Event(s) from inline (handle has all)."
                : $"{gc.TotalCollections} collection(s), max pause {gc.MaxPauseTime.TotalMilliseconds:F1}ms, total pause {gc.TotalPauseTime.TotalMilliseconds:F1}ms.");

        var primaryHint = gc.MaxPauseTime.TotalMilliseconds > 100
            ? new NextActionHint("collect_process_dump",
                $"Max GC pause {gc.MaxPauseTime.TotalMilliseconds:F0}ms is high — capture a WithHeap dump for offline heap analysis.",
                new Dictionary<string, object?> { ["processId"] = pid, ["dumpType"] = "WithHeap" })
            : new NextActionHint("collect_events", "GC looks healthy — pivot to a domain EventSource (e.g. System.Net.Http) for application-level signal.",
                new Dictionary<string, object?> { ["processId"] = pid, ["providerName"] = "System.Net.Http", ["durationSeconds"] = 10 });

        var handle = handles.Register(pid, CollectionHandleKinds.GcEvents, gc, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineGc,
            summary,
            handle.Id,
            handle.ExpiresAt,
            primaryHint,
            new NextActionHint("query_snapshot",
                "Drill into these GC events without re-collecting (views: summary, events, pauseHistogram, timeline, longestPauses, byGeneration).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "pauseHistogram" })),
            resolved.Context);
    }

    /// <summary>
    /// Captures a broad metadata-only EventPipe catalog: provider, event name, level and timestamp.
    /// Payload values are intentionally omitted; use CollectEventSource for targeted payload capture.
    /// </summary>
    public static async Task<DiagnosticResult<EventCatalogSnapshot>> CollectEventCatalog(
        IEventCatalogCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        IReadOnlyList<string>? providers = null,
        int maxEvents = 200,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<EventCatalogSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<EventCatalogSnapshot>(nameof(maxEvents), "must be >= 1");

        var resolved = await ResolveContextAsync<EventCatalogSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CaptureAsync(
            pid, TimeSpan.FromSeconds(durationSeconds), providers, maxEvents, cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        var droppedSample = 0;
        if (depth == SamplingDepth.Summary && snapshot.Sample.Count > 0)
        {
            droppedSample = snapshot.Sample.Count;
            inlineSnapshot = snapshot with { Sample = Array.Empty<CatalogEventOccurrence>() };
        }

        var summary = snapshot.TotalEvents == 0
            ? $"No catalog events captured in {durationSeconds}s. EventPipe requires explicit provider names; pass providers to target custom EventSources."
            : (depth == SamplingDepth.Summary && droppedSample > 0
                ? $"Cataloged {snapshot.TotalEvents} metadata-only event(s) across {snapshot.DistinctEventTypes} event type(s) over {durationSeconds}s. Dropped {droppedSample} sampled occurrence(s) from inline (handle has all)."
                : $"Cataloged {snapshot.TotalEvents} metadata-only event(s) across {snapshot.DistinctEventTypes} event type(s) over {durationSeconds}s.");

        var handle = handles.Register(pid, CollectionHandleKinds.EventCatalog, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into this metadata-only event catalog (views: catalog, byProvider, events).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = EventCatalogQueryDispatcher.CatalogView }),
            new NextActionHint("collect_events",
                "Use kind=event_source for targeted payload capture when you know the provider and have the required allowlist/scope.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "event_source", ["providerName"] = "<provider>" })),
            resolved.Context);
    }

    /// <summary>
    /// Captures DATAS (Dynamic Adaptation To Application Sizes) GC tuning events. Populated only on
    /// Server GC with DATAS enabled (default-on in .NET 9+); Workstation GC emits no DATAS events,
    /// in which case this returns a graceful <c>NoDatasEvents</c> result.
    /// </summary>
    public static async Task<DiagnosticResult<GcDatasSnapshot>> CollectGcDatas(
        IGcDatasCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 15,
        int maxEvents = 1000,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<GcDatasSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<GcDatasSnapshot>(nameof(maxEvents), "must be >= 1");

        var resolved = await ResolveContextAsync<GcDatasSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid, TimeSpan.FromSeconds(durationSeconds), maxEvents, cancellationToken).ConfigureAwait(false);

        if (!snapshot.HasData)
        {
            var parseStats = snapshot.ParseStats;
            var sawUnparseable = parseStats.MalformedPayloads > 0 || parseStats.UnsupportedVersion > 0;
            var message = sawUnparseable
                ? $"DATAS events were present but none could be decoded ({parseStats.UnsupportedVersion} unsupported-version, {parseStats.MalformedPayloads} malformed). The target runtime may emit a newer DATAS event version than this build understands."
                : $"No DATAS events observed in {durationSeconds}s. DATAS tuning events require Server GC (default-on in .NET 9+; otherwise set DOTNET_GCDynamicAdaptationMode=1). Workstation GC, .NET < 9, or a quiet/short window all produce no events.";
            var code = sawUnparseable ? "UnsupportedDatasPayload" : "NoDatasEvents";
            return WithContext(
                DiagnosticResult.Fail<GcDatasSnapshot>(
                    message,
                    new DiagnosticError(code, message),
                    new NextActionHint("collect_events",
                        "Confirm the target uses Server GC, then re-run kind=datas during sustained allocation.",
                        new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "datas", ["durationSeconds"] = 20 })),
                resolved.Context);
        }

        var changes = 0;
        var ordered = snapshot.TuningEvents.OrderBy(t => t.Timestamp).ThenBy(t => t.GcIndex).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].NewHeapCount != ordered[i - 1].NewHeapCount) changes++;
        }

        var hcRange = ordered.Count == 0
            ? "n/a"
            : $"{ordered.Min(t => t.NewHeapCount)}–{ordered.Max(t => t.NewHeapCount)}";
        var summary =
            $"DATAS over {durationSeconds}s: {snapshot.Samples.Count} sample(s), {snapshot.TuningEvents.Count} tuning event(s) (heap count {hcRange}, {changes} change(s)), {snapshot.FullGcTuningEvents.Count} gen2 backstop event(s).";

        var handle = handles.Register(pid, CollectionHandleKinds.GcDatas, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            snapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into DATAS tuning (views: overview, tuning, samples, gen2; tuning supports changesOnly).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = GcDatasQueryDispatcher.OverviewView })),
            resolved.Context);
    }

    /// <summary>Curates the Microsoft-Extensions-Logging EventSource into an ILogger view.</summary>
    public static async Task<DiagnosticResult<LogSnapshot>> CollectLogs(
        ILogCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        IReadOnlyList<string>? categories = null,
        string minLevel = "Information",
        int maxEvents = 500,
        int maxMessageBytes = 4096,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<LogSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<LogSnapshot>(nameof(maxEvents), "must be >= 1");
        if (maxMessageBytes < 16) return InvalidArg<LogSnapshot>(nameof(maxMessageBytes), "must be >= 16");
        if (!Enum.TryParse<LogLevel>(minLevel, ignoreCase: true, out var parsedMinLevel)
            || parsedMinLevel is LogLevel.None or < LogLevel.Trace or > LogLevel.Critical)
        {
            return InvalidArg<LogSnapshot>(nameof(minLevel), "must be one of Trace|Debug|Information|Warning|Error|Critical");
        }

        var resolved = await ResolveContextAsync<LogSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector
            .CollectAsync(
                pid,
                TimeSpan.FromSeconds(durationSeconds),
                categories,
                parsedMinLevel,
                maxEvents,
                maxMessageBytes,
                includeJsonPayload: depth != SamplingDepth.Summary,
                cancellationToken)
            .ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        var droppedRecent = 0;
        if (depth == SamplingDepth.Summary && snapshot.Recent.Count > 0)
        {
            droppedRecent = snapshot.Recent.Count;
            inlineSnapshot = snapshot with { Recent = Array.Empty<LogEntry>() };
        }

        var topCategory = snapshot.ByCategory.Count > 0 ? snapshot.ByCategory[0] : null;
        var warningPlus = snapshot.EventsByLevelWarning + snapshot.EventsByLevelError + snapshot.EventsByLevelCritical;
        var summary = snapshot.TotalEvents == 0
            ? $"No ILogger events captured in {durationSeconds}s. Widen categories or lower minLevel if you expected logs."
            : (depth == SamplingDepth.Summary && droppedRecent > 0
                ? $"Captured {snapshot.TotalEvents} ILogger event(s) over {durationSeconds}s at minLevel={snapshot.MinimumLevel}; warning+={warningPlus}. Top category: {topCategory?.Category} ({topCategory?.Count}). Dropped {droppedRecent} Recent entry(ies) from inline (handle has all)."
                : $"Captured {snapshot.TotalEvents} ILogger event(s) over {durationSeconds}s at minLevel={snapshot.MinimumLevel}; warning+={warningPlus}. Top category: {topCategory?.Category} ({topCategory?.Count}).");

        var handle = handles.Register(pid, CollectionHandleKinds.LogSnapshot, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into this log snapshot without re-collecting (views: summary, byCategory, byLevel, recent, errors).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "errors", ["topN"] = 25 }),
            new NextActionHint("collect_sample",
                warningPlus > 0
                    ? "Correlate warning/error spikes with CPU hotspots from the same window."
                    : "If the app was slow without warning/error logs, pivot to CPU sampling for the same process.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 })),
            resolved.Context);
    }

    /// <summary>Reconstructs JIT / tiered-compilation activity from the runtime provider.</summary>
    public static async Task<DiagnosticResult<JitSnapshot>> CollectJit(
        IJitCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<JitSnapshot>(nameof(durationSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<JitSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector
            .CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), cancellationToken)
            .ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        var droppedMethods = 0;
        if (depth == SamplingDepth.Summary && snapshot.Methods.Count > 10)
        {
            droppedMethods = snapshot.Methods.Count - 10;
            var inlineNotes = snapshot.Notes
                .Append($"{droppedMethods} additional method(s) omitted from the inline payload (handle has all).")
                .ToList();
            inlineSnapshot = snapshot with
            {
                Methods = snapshot.Methods.Take(10).ToList(),
                Notes = inlineNotes,
            };
        }

        var summary = snapshot.CompletedCompilations == 0 && snapshot.R2RLookupCount == 0
            ? $"No JIT or ReadyToRun activity captured in {durationSeconds}s. Trigger a cold path during the window and retry."
            : (depth == SamplingDepth.Summary && droppedMethods > 0
                ? $"Observed {snapshot.CompletedCompilations} completed JIT compilation(s) across {snapshot.UniqueMethods} method(s) in {durationSeconds}s. {snapshot.HealthCheck} ReJIT={snapshot.ReJitCount}, OSR={snapshot.OsrCount}. Dropped {droppedMethods} method row(s) from inline (handle has all)."
                : $"Observed {snapshot.CompletedCompilations} completed JIT compilation(s) across {snapshot.UniqueMethods} method(s) in {durationSeconds}s. {snapshot.HealthCheck} ReJIT={snapshot.ReJitCount}, OSR={snapshot.OsrCount}.");

        var handle = handles.Register(pid, CollectionHandleKinds.JitSnapshot, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into this JIT snapshot without re-collecting (views: summary, topMethods, tierDistribution, reJIT).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "topMethods", ["topN"] = 25 }),
            new NextActionHint("collect_sample",
                "If cold-start JIT pressure lines up with latency, correlate it with CPU hotspots from the same process.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "cpu", ["durationSeconds"] = 10 })),
            resolved.Context);
    }

    /// <summary>Curates the runtime ThreadingKeyword into a ThreadPool starvation view.</summary>
    public static async Task<DiagnosticResult<ThreadPoolEventSnapshot>> CollectThreadPool(
        IThreadPoolCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<ThreadPoolEventSnapshot>(nameof(durationSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<ThreadPoolEventSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with
            {
                WorkerThreadTimeline = Array.Empty<ThreadPoolCountBucket>(),
                IocpThreadTimeline = Array.Empty<ThreadPoolCountBucket>(),
                HillClimbing = Array.Empty<ThreadPoolHillClimbingSample>(),
                WorkItemOrigins = snapshot.WorkItemOrigins.Take(5).ToList(),
            };
        }

        var peakWorkers = snapshot.WorkerThreadTimeline.Count > 0 ? snapshot.WorkerThreadTimeline.Max(static bucket => bucket.Count) : 0;
        var latestWorkers = snapshot.WorkerThreadTimeline.Count > 0 ? snapshot.WorkerThreadTimeline[^1].Count : 0;
        var starvationEvents = snapshot.HillClimbing.Count(static sample => string.Equals(sample.Reason, "Starvation", StringComparison.OrdinalIgnoreCase));
        var summary = snapshot.HillClimbing.Count == 0 && snapshot.TotalEnqueueEvents == 0
            ? $"No ThreadPool starvation signals were captured in {durationSeconds}s. Start the workload after collection begins if you expected queue growth."
            : $"Captured ThreadPool activity over {durationSeconds}s: workers latest/peak={latestWorkers}/{peakWorkers}, hill-climbing events={snapshot.HillClimbing.Count}, starvation reasons={starvationEvents}, enqueue/dequeue={snapshot.TotalEnqueueEvents}/{snapshot.TotalDequeueEvents}.";

        var handle = handles.Register(pid, CollectionHandleKinds.ThreadPoolSnapshot, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into the worker + IOCP timelines for this ThreadPool snapshot.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "timeline" }),
            new NextActionHint("query_snapshot",
                "Inspect hill-climbing transitions and starvation reasons without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "hillClimbing" }),
            new NextActionHint("query_snapshot",
                "Inspect top work-item origins without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "workItemOrigins", ["topN"] = 20 })),
            resolved.Context);
    }

    /// <summary>Aggregates the runtime Contention keyword by call site and owner thread.</summary>
    public static async Task<DiagnosticResult<ContentionSnapshot>> CollectContention(
        IContentionCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<ContentionSnapshot>(nameof(durationSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<ContentionSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with
            {
                Events = Array.Empty<ContentionEventSample>(),
            };
        }

        var handle = handles.Register(pid, CollectionHandleKinds.ContentionSnapshot, snapshot, CollectionHandleTtl);
        var hints = new List<NextActionHint>();

        string summary;
        if (snapshot.TotalEvents == 0)
        {
            if (OperatingSystem.IsLinux())
            {
                // Linux doesn't emit ContentionStart/Stop via EventPipe (nomac exclusion in runtime).
                // Guide the LLM toward alternative diagnostic paths that DO work.
                summary = $"No lock contention events were captured in {durationSeconds}s. " +
                          "Linux runtimes do not emit ContentionStart/Stop over EventPipe (known limitation). " +
                          "Use alternative signals: 'monitor-lock-contention-count' counter + thread snapshots to identify blocked threads.";

                hints.Add(new NextActionHint(
                    "collect_events",
                    "Collect counters to see if monitor-lock-contention-count is rising (confirms contention exists even without events).",
                    new Dictionary<string, object?> { ["kind"] = "counters", ["processId"] = pid, ["durationSeconds"] = 5, ["providers"] = (IReadOnlyList<string>)["System.Runtime"] }));

                hints.Add(new NextActionHint(
                    "collect_thread_snapshot",
                    "Take a thread snapshot then use query_snapshot(view='lock-graph') to see contended SyncBlocks with waiter counts.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
            }
            else
            {
                summary = $"No lock contention events were captured in {durationSeconds}s. Start the collection before the workload and retry if you expected waits.";
            }
        }
        else
        {
            summary = $"Captured {snapshot.TotalEvents} lock-contention event(s) over {durationSeconds}s across {snapshot.DistinctMonitors} contended monitor(s). " +
                      $"Total wait={snapshot.TotalContentionDuration.TotalMilliseconds:F1}ms, p95={snapshot.P95ContentionDuration.TotalMilliseconds:F1}ms, max={snapshot.MaxContentionDuration.TotalMilliseconds:F1}ms.";

            hints.Add(new NextActionHint("query_snapshot",
                "Group this contention window by call site without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byCallSite", ["topN"] = 20 }));
            hints.Add(new NextActionHint("query_snapshot",
                "Group this contention window by owner thread without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byOwner", ["topN"] = 20 }));
        }

        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            hints.ToArray()),
            resolved.Context);
    }

    /// <summary>Curates EF Core / SqlClient command + pool diagnostics into a DB view.</summary>
    public static async Task<DiagnosticResult<DbSnapshot>> CollectDb(
        IDbCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        int intervalSeconds = 1,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<DbSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (intervalSeconds < 1) return InvalidArg<DbSnapshot>(nameof(intervalSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<DbSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(durationSeconds),
            intervalSeconds,
            cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with
            {
                ByCommand = snapshot.ByCommand.Take(5).ToList(),
                NPlusOne = snapshot.NPlusOne.Take(5).ToList(),
            };
        }

        var topCommand = snapshot.ByCommand.Count > 0 ? snapshot.ByCommand[0] : null;
        var poolExhaustedCount = snapshot.ConnectionPool.Sum(static stats => stats.PoolExhaustedCount);
        var summary = snapshot.TotalCommands == 0
            ? $"No DB commands captured in {durationSeconds}s. Start the collection before the workload and ensure the target emits EF Core or SqlClient diagnostics."
            : $"Captured {snapshot.TotalCommands} DB command(s) over {durationSeconds}s across {snapshot.ByCommand.Count} distinct command shape(s). " +
              $"Top command: {topCommand?.CommandTextHash} ({topCommand?.Count} call(s), p95={topCommand?.P95Ms:F1}ms). " +
              $"N+1 incidents: {snapshot.NPlusOne.Count}. Pool exhaustion signals: {poolExhaustedCount}.";

        var handle = handles.Register(pid, CollectionHandleKinds.DbSnapshot, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into this DB snapshot without re-collecting (views: summary, byCommand, n+1, connectionPool).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "n+1", ["topN"] = 25 }),
            new NextActionHint("collect_sample",
                snapshot.NPlusOne.Count > 0 || snapshot.ByCommand.Any(static command => command.P95Ms > 50)
                    ? "Correlate slow-query hotspots with CPU stacks from the same process."
                    : "If DB latency looks healthy, pivot to CPU sampling or logs for the same process.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 })),
            resolved.Context);
    }

    /// <summary>Curates the Kestrel request pipeline (connections, requests, TLS, queue lengths, live config) into a snapshot.</summary>
    public static async Task<DiagnosticResult<KestrelSnapshot>> CollectKestrel(
        IKestrelCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        int intervalSeconds = 1,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<KestrelSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (intervalSeconds < 1) return InvalidArg<KestrelSnapshot>(nameof(intervalSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<KestrelSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(durationSeconds),
            intervalSeconds,
            cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with
            {
                ByOperation = snapshot.ByOperation.Take(5).ToList(),
                QueuePoints = Array.Empty<KestrelQueuePoint>(),
                ConfigurationJson = null,
            };
        }

        var handle = handles.Register(pid, CollectionHandleKinds.KestrelSnapshot, snapshot, CollectionHandleTtl);
        var hints = new List<NextActionHint>();

        string summary;
        if (snapshot.RequestsStarted == 0 && snapshot.ConnectionsStarted == 0 && snapshot.Counters.Count == 0)
        {
            summary = $"No Kestrel activity captured in {durationSeconds}s. Confirm the target hosts an ASP.NET Core app on Kestrel and that traffic flows during collection (start the session before the load).";
        }
        else
        {
            summary = $"Captured {snapshot.RequestsStarted} request(s) and {snapshot.ConnectionsStarted} connection(s) over {durationSeconds}s. " +
                      $"Peak request-queue-length={snapshot.PeakRequestQueueLength}, connection-queue-length={snapshot.PeakConnectionQueueLength}. " +
                      $"Request latency p95={snapshot.RequestP95.TotalMilliseconds:F1}ms, max={snapshot.RequestMax.TotalMilliseconds:F1}ms. " +
                      $"TLS handshakes: {snapshot.TlsHandshakesStarted} started, {snapshot.TlsHandshakesFailed} failed.";

            if (snapshot.PeakRequestQueueLength > 0 || snapshot.PeakConnectionQueueLength > 0)
            {
                hints.Add(new NextActionHint("query_snapshot",
                    "Inspect the connection/request queue-length timeline to localize head-of-line blocking.",
                    new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "queues", ["topN"] = 50 }));
            }

            hints.Add(new NextActionHint("query_snapshot",
                "Group request latency by method + path without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byOperation", ["topN"] = 25 }));
        }

        if (snapshot.ConfigurationJson is not null)
        {
            hints.Add(new NextActionHint("query_snapshot",
                "Read the live KestrelServerOptions JSON (TLS, limits, keep-alive, HTTP protocol versions) captured at session enable.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "config" }));
        }

        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            hints.ToArray()),
            resolved.Context);
    }

    /// <summary>Enumerates ASP.NET Core requests that are in-flight (started but not stopped) over a fixed EventPipe window — pure EventPipe, no ptrace.</summary>
    public static async Task<DiagnosticResult<InFlightRequestSnapshot>> CollectInFlightRequests(
        IInFlightRequestCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        double longRunningThresholdMs = 1000,
        int maxRequests = 100,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<InFlightRequestSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (longRunningThresholdMs < 0) return InvalidArg<InFlightRequestSnapshot>(nameof(longRunningThresholdMs), "must be >= 0");
        if (maxRequests < 1) return InvalidArg<InFlightRequestSnapshot>(nameof(maxRequests), "must be >= 1");

        var resolved = await ResolveContextAsync<InFlightRequestSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(durationSeconds),
            longRunningThresholdMs,
            maxRequests,
            cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with { Requests = snapshot.Requests.Take(10).ToList() };
        }

        var handle = handles.Register(pid, CollectionHandleKinds.InFlightRequests, snapshot, CollectionHandleTtl);
        var hints = new List<NextActionHint>();

        string summary;
        if (snapshot.RequestsStarted == 0)
        {
            summary = $"No ASP.NET Core requests started in {durationSeconds}s. Confirm the target hosts an ASP.NET Core app and that traffic flows during collection (start the session before the load).";
        }
        else if (snapshot.InFlightCount == 0)
        {
            summary = $"All {snapshot.RequestsStarted} request(s) that started in {durationSeconds}s also completed — nothing is in-flight.";
        }
        else
        {
            summary = $"{snapshot.InFlightCount} request(s) still in-flight after {durationSeconds}s " +
                      $"({snapshot.RequestsStarted} started, {snapshot.RequestsCompleted} completed). " +
                      $"Oldest has been running {snapshot.OldestElapsedMs:F0}ms. " +
                      $"{snapshot.LongRunningCount} exceed the {snapshot.LongRunningThresholdMs:F0}ms long-running threshold.";

            hints.Add(new NextActionHint("query_snapshot",
                "List every in-flight request (path, verb, elapsed, trace-id) oldest-first without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "requests", ["topN"] = 50 }));

            if (snapshot.LongRunningCount > 0)
            {
                hints.Add(new NextActionHint("query_snapshot",
                    "Focus on the requests that crossed the long-running threshold — the most likely stuck work.",
                    new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "longRunning", ["topN"] = 50 }));

                hints.Add(new NextActionHint("inspect_process",
                    "Capture the live thread stacks behind these in-flight requests (requires the ptrace scope).",
                    new Dictionary<string, object?> { ["view"] = "requests-now", ["processId"] = pid }));
            }
        }

        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            hints.ToArray()),
            resolved.Context);
    }

    /// <summary>Curates the stable .NET networking EventSources (HTTP / DNS / TLS / sockets) into a snapshot.</summary>
    public static async Task<DiagnosticResult<NetworkingSnapshot>> CollectNetworking(
        INetworkingCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        int intervalSeconds = 1,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<NetworkingSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (intervalSeconds < 1) return InvalidArg<NetworkingSnapshot>(nameof(intervalSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<NetworkingSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(durationSeconds),
            intervalSeconds,
            cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with
            {
                ByOperation = snapshot.ByOperation.Take(5).ToList(),
            };
        }

        var handle = handles.Register(pid, CollectionHandleKinds.NetworkingSnapshot, snapshot, CollectionHandleTtl);
        var hints = new List<NextActionHint>();

        string summary;
        if (snapshot.HttpRequestsStarted == 0 && snapshot.DnsLookupsStarted == 0
            && snapshot.SocketConnectsStarted == 0 && snapshot.Counters.Count == 0)
        {
            summary = $"No networking activity captured in {durationSeconds}s. Confirm the target makes outbound HTTP / DNS / socket calls during collection (start the session before the load).";
        }
        else
        {
            summary = $"Captured {snapshot.HttpRequestsStarted} HTTP request(s) ({snapshot.HttpRequestsFailed} failed) over {durationSeconds}s. " +
                      $"Request p95={snapshot.HttpRequestP95.TotalMilliseconds:F1}ms, time-in-queue p95={snapshot.TimeInQueueP95.TotalMilliseconds:F1}ms. " +
                      $"DNS: {snapshot.DnsLookupsStarted} lookup(s), {snapshot.DnsLookupsFailed} failed. " +
                      $"TLS: {snapshot.TlsHandshakesStarted} handshake(s), {snapshot.TlsHandshakesFailed} failed. " +
                      $"Sockets: {snapshot.SocketConnectsStarted} connect(s), {snapshot.SocketConnectsFailed} failed.";

            if (snapshot.TimeInQueueP95 > TimeSpan.Zero || snapshot.HttpRequestsLeftQueue > 0)
            {
                hints.Add(new NextActionHint("query_snapshot",
                    "Inspect HttpClient connection-pool time-in-queue — rising queue time is the #1 outbound-HTTP saturation signal.",
                    new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "queue" }));
            }

            hints.Add(new NextActionHint("query_snapshot",
                "Group outbound HTTP latency by host without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byOperation", ["topN"] = 25 }));
        }

        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            hints.ToArray()),
            resolved.Context);
    }

    /// <summary>Captures startup-related assembly/module loader and DependencyInjection EventPipe activity.</summary>
    public static async Task<DiagnosticResult<StartupSnapshot>> CollectStartup(
        IStartupCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = 10,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<StartupSnapshot>(nameof(durationSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<StartupSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with
            {
                AssemblyLoads = snapshot.AssemblyLoads.Take(5).ToList(),
                ModuleLoads = snapshot.ModuleLoads.Take(5).ToList(),
                DiEvents = snapshot.DiEvents.Take(5).ToList(),
                Timeline = snapshot.Timeline.Take(20).ToList(),
            };
        }

        var summary = snapshot.TotalAssemblyLoads == 0 && snapshot.TotalModuleLoads == 0 && snapshot.TotalDiEvents == 0
            ? $"No startup loader/DI events captured in {durationSeconds}s. Attaching to an already-running process misses events emitted before attach; true cold-start capture requires starting EventPipe before or at process launch."
            : $"Captured startup activity over {durationSeconds}s: assemblies={snapshot.TotalAssemblyLoads}, modules={snapshot.TotalModuleLoads}, DI events={snapshot.TotalDiEvents}, service providers built={snapshot.DiServiceProviderBuiltCount}, observed DI span={snapshot.ObservedDiActivityDuration.TotalMilliseconds:F1}ms.";

        var handle = handles.Register(pid, CollectionHandleKinds.StartupSnapshot, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into the startup timeline without re-collecting (views: summary, assemblies, modules, di, timeline).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "timeline", ["topN"] = 50 }),
            new NextActionHint("collect_events",
                "Use kind='jit' separately if JIT-at-startup is suspected; startup does not duplicate JIT events.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "jit", ["durationSeconds"] = durationSeconds })),
            resolved.Context);
    }

    /// <summary>
    /// Cold-start variant of <see cref="CollectStartup"/> (issue #446): arms the session on a suspended
    /// reverse-connected target and resumes inside the collector, so pre-attach events are recovered. No
    /// process-context resolution is performed up front because the runtime is suspended; the pid is the
    /// launched child's.
    /// </summary>
    public static async Task<DiagnosticResult<StartupSnapshot>> CollectStartupColdStart(
        IStartupCollector collector,
        IDiagnosticHandleStore handles,
        DotnetDiagnostics.Core.Launch.SuspendedTarget target,
        int durationSeconds = 10,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(target);
        if (durationSeconds < 1) return InvalidArg<StartupSnapshot>(nameof(durationSeconds), "must be >= 1");

        var pid = target.ProcessId;
        var snapshot = await collector.CollectColdStartAsync(target, TimeSpan.FromSeconds(durationSeconds), cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with
            {
                AssemblyLoads = snapshot.AssemblyLoads.Take(5).ToList(),
                ModuleLoads = snapshot.ModuleLoads.Take(5).ToList(),
                DiEvents = snapshot.DiEvents.Take(5).ToList(),
                Timeline = snapshot.Timeline.Take(20).ToList(),
            };
        }

        var summary = $"Cold-start capture over {durationSeconds}s (suspended reverse-connect; pre-attach events included): assemblies={snapshot.TotalAssemblyLoads}, modules={snapshot.TotalModuleLoads}, DI events={snapshot.TotalDiEvents}, service providers built={snapshot.DiServiceProviderBuiltCount}, observed DI span={snapshot.ObservedDiActivityDuration.TotalMilliseconds:F1}ms.";

        var handle = handles.Register(pid, CollectionHandleKinds.StartupSnapshot, snapshot, CollectionHandleTtl);
        return DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into the startup timeline without re-collecting (views: summary, assemblies, modules, di, timeline).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "timeline", ["topN"] = 50 }));
    }

    /// <summary>Captures completed ActivitySource spans via the DiagnosticSource EventPipe bridge.</summary>
    public static async Task<DiagnosticResult<ActivityCapture>> CollectActivities(
        IActivityCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        IReadOnlyList<string>? sources = null,
        int durationSeconds = 10,
        int maxActivities = 200,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<ActivityCapture>(nameof(durationSeconds), "must be >= 1");
        if (maxActivities < 1) return InvalidArg<ActivityCapture>(nameof(maxActivities), "must be >= 1");

        var resolved = await ResolveContextAsync<ActivityCapture>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var capture = await collector
            .CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), sources, maxActivities, cancellationToken)
            .ConfigureAwait(false);

        var truncated = capture.TotalActivities > capture.Activities.Count;
        var topSource = capture.BySource.Count > 0 ? capture.BySource[0] : null;
        var topOperation = capture.ByOperation.Count > 0 ? capture.ByOperation[0] : null;
        var summary = capture.TotalActivities == 0
            ? $"No ActivitySource spans in {durationSeconds}s. Verify the target emits ActivitySource instrumentation or widen the 'sources' filter."
            : $"Captured {capture.Activities.Count} activity record(s) out of {capture.TotalActivities} observed over {durationSeconds}s across {capture.BySource.Count} source(s). " +
              $"Top source: {topSource?.SourceName} ({topSource?.Count}). Top operation: {topOperation?.SourceName}/{topOperation?.OperationName} ({topOperation?.Count})." +
              (truncated ? $" Truncated by maxActivities={maxActivities}; summaries reflect the stored subset." : string.Empty);

        var primaryHint = topOperation is { MaxDurationMs: > 250 }
            ? new NextActionHint("collect_sample",
                $"Correlate the slowest captured operation ({topOperation.SourceName}/{topOperation.OperationName}, max {topOperation.MaxDurationMs:F1} ms) with CPU hotspots in the same process.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 })
            : new NextActionHint("collect_events",
                "Cross-check ActivitySource timing with runtime counters for the same process.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds });

        var handle = handles.Register(pid, CollectionHandleKinds.Activities, capture, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            capture,
            summary,
            handle.Id,
            handle.ExpiresAt,
            primaryHint,
            new NextActionHint("query_snapshot",
                "Drill into these activities without re-collecting (views: summary, bySource, byOperation, activities).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byOperation" })),
            resolved.Context);
    }

    /// <summary>
    /// Generic EventSource passthrough for a single provider. Mirrors the legacy collector exactly,
    /// but takes the pre-computed <paramref name="principalAllowsEventSourceAny"/> bool (the caller
    /// holds the <c>eventsource-any</c> scope) and an <see cref="IEventSourceDeprecationSink"/>
    /// instead of the Server principal/deprecation types, so it stays host-neutral.
    /// </summary>
    public static async Task<DiagnosticResult<EventSourceCapture>> CollectEventSource(
        IEventSourceCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        EventSourceAllowlist allowlist,
        SensitiveValueGate sensitiveGate,
        bool principalAllowsEventSourceAny,
        string providerName,
        int? processId = null,
        int durationSeconds = 10,
        long keywords = -1,
        int eventLevel = 5,
        int maxEvents = 200,
        SamplingDepth depth = SamplingDepth.Summary,
        bool unsafeProvider = false,
        IEventSourceDeprecationSink? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return InvalidArg<EventSourceCapture>(nameof(providerName), "is required");
        if (durationSeconds < 1) return InvalidArg<EventSourceCapture>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<EventSourceCapture>(nameof(maxEvents), "must be >= 1");

        // B4 / issue #165 / M2: gate non-curated providers behind the sensitive-value flag.
        // Custom EventSources frequently log user ids, auth-failure context, etc.
        var allowedByDefault = allowlist.IsAllowed(providerName);
        // docs/authorization.md#scopes: scope-first predicate is
        // 'principal.HasExplicitScope("eventsource-any") OR allowlist allows'.
        // The principal-side check lets us emit a once-per-process deprecation warning
        // when the allowlist (not the scope) was the bypass mechanism. The allowlist
        // policy itself is retained — only the implicit deployment-wide "every caller
        // can capture allowlisted providers" pattern is deprecated for caller-level
        // distinction.
        var principalAllowsAny = principalAllowsEventSourceAny;
        if (!allowedByDefault)
        {
            // Caller can use unsafeProvider when EITHER the server has the legacy
            // AllowSensitiveHeapValues flag set, OR their bearer principal holds the
            // 'eventsource-any' modifier scope. Either path bypasses the curated
            // allowlist for THIS call only; the warn-on-allow audit line is emitted
            // by the tool filter.
            if (!unsafeProvider || (!sensitiveGate.IsAllowedByServer && !principalAllowsAny))
            {
                var preview = string.Join(", ", allowlist.AllowedProviders.Take(8));
                return DiagnosticResult.Fail<EventSourceCapture>(
                    $"EventSource provider '{providerName}' is not on the allowlist.",
                    new DiagnosticError(
                        "EventSourceProviderNotAllowed",
                        "Add the provider to `Diagnostics:EventSourceAllowlist` (env: `Diagnostics__EventSourceAllowlist__0=<provider>`), grant the caller the 'eventsource-any' scope (docs/authorization.md#modifier-scopes), or — on legacy deployments — set `Diagnostics:AllowSensitiveHeapValues=true` on the server AND pass `unsafeProvider=true` per call. Curated allowlist includes: " + preview + (allowlist.AllowedProviders.Count > 8 ? ", …" : "") + ". Tracked by issue #165 (B4); subsumed into the 'eventsource-any' modifier scope by B5.4.",
                        providerName));
            }

            // unsafeProvider path was taken. If the principal lacks the scope, the
            // AllowSensitiveHeapValues flag is the bypass mechanism — surface the
            // sensitive-heap deprecation (that flag is the one truly going away).
            if (deprecation is not null && !principalAllowsAny && sensitiveGate.IsAllowedByServer)
            {
                deprecation.NotifySensitiveHeapValuesFlagBypass();
            }

            // Opt-in path: clamp the dangerous defaults (verbose + every-keyword) so the
            // capture doesn't accidentally pull every payload field at full verbosity.
            if (keywords == -1) keywords = 0;
            if (eventLevel > 4) eventLevel = 4;
        }
        else if (deprecation is not null && !principalAllowsAny)
        {
            // The curated allowlist (not the scope) authorised this call. Fire the
            // once-per-process deprecation telemetry so operators see they should be
            // distinguishing capable callers with the 'eventsource-any' scope rather
            // than relying on the deployment-wide allowlist alone.
            deprecation.NotifyEventSourceAllowlistBypass();
        }

        var resolved = await ResolveContextAsync<EventSourceCapture>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var capture = await collector.CaptureAsync(
            pid, providerName, TimeSpan.FromSeconds(durationSeconds), keywords, eventLevel, maxEvents, cancellationToken).ConfigureAwait(false);

        var inlineCapture = capture;
        var droppedCapEvents = 0;
        if (depth == SamplingDepth.Summary && capture.Events.Count > 0)
        {
            droppedCapEvents = capture.Events.Count;
            inlineCapture = capture with { Events = Array.Empty<CapturedEvent>() };
        }

        var summary = capture.Events.Count == 0
            ? $"No events from '{providerName}' in {durationSeconds}s. Verify the provider name and that it's actually instrumented in the target."
            : (depth == SamplingDepth.Summary && droppedCapEvents > 0
                ? $"Captured {capture.Events.Count} event(s) from '{providerName}' over {durationSeconds}s. Dropped {droppedCapEvents} Event(s) from inline (handle has all)."
                : $"Captured {capture.Events.Count} event(s) from '{providerName}' over {durationSeconds}s.");

        var handle = handles.Register(pid, CollectionHandleKinds.EventSource, capture, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineCapture,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("collect_events", "Cross-check captured events against runtime counters for the same window.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds }),
            new NextActionHint("query_snapshot",
                "Drill into this capture without re-collecting (views: summary, byEventName, events).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byEventName" })),
            resolved.Context);
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
