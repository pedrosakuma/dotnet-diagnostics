using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.ThreadPool;

namespace DotnetDiagnostics.Core.Collection;

/// <summary>
/// Renders a previously-collected artifact under a named view. Pure (no I/O, no DI) so the
/// <c>query_collection</c> tool can stay a thin reflection-over-the-store wrapper.
/// </summary>
/// <remarks>
/// Adding a new view = adding one branch to the per-kind switch. Adding a new collector kind =
/// new constant in <see cref="CollectionHandleKinds"/>, new render overload, and new entry in the
/// dispatcher's kind switch.
/// </remarks>
public static class CollectionQueryDispatcher
{
    /// <summary>
    /// Allowed view names per artifact kind, surfaced verbatim in error messages so the LLM can
    /// retry with a valid view without re-reading the tool description.
    /// </summary>
    public static IReadOnlyList<string> ViewsFor(string kind) => kind switch
    {
        CollectionHandleKinds.Counters => new[] { "summary", "byProvider" },
        CollectionHandleKinds.ExceptionSnapshot => new[] { "summary", "byType", "recent" },
        CollectionHandleKinds.GcEvents => new[] { "summary", "events", "pauseHistogram", "timeline", "longestPauses", "byGeneration", "heap-stats" },
        CollectionHandleKinds.EventSource => new[] { "summary", "byEventName", "events" },
        CollectionHandleKinds.Activities => new[] { "summary", "bySource", "byOperation", "activities", "gc-overlay" },
        CollectionHandleKinds.LogSnapshot => new[] { "summary", "byCategory", "byLevel", "recent", "errors" },
        CollectionHandleKinds.JitSnapshot => new[] { "summary", "topMethods", "tierDistribution", "reJIT" },
        CollectionHandleKinds.ThreadPoolSnapshot => new[] { "summary", "timeline", "hillClimbing", "workItemOrigins" },
        CollectionHandleKinds.ContentionSnapshot => new[] { "summary", "byCallSite", "byOwner" },
        CollectionHandleKinds.DbSnapshot => new[] { "summary", "byCommand", "n+1", "connectionPool" },
        CollectionHandleKinds.KestrelSnapshot => new[] { "summary", "byOperation", "queues", "tls", "config" },
        CollectionHandleKinds.NetworkingSnapshot => new[] { "summary", "byOperation", "queue", "tls", "dns" },
        CollectionHandleKinds.StartupSnapshot => new[] { "summary", "assemblies", "modules", "di", "timeline" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Default view for a given kind when the caller doesn't pass one.</summary>
    public static string DefaultViewFor(string kind) => "summary";

    /// <summary>Dispatch outcome union — success carries a <see cref="CollectionQueryResult"/>; the four failure flavors are observable strings.</summary>
    public readonly record struct DispatchOutcome(
        CollectionQueryResult? Result,
        string? UnknownKind,
        string? UnknownView,
        string? InvalidArgument,
        IReadOnlyList<string>? AllowedViews);

    /// <summary>
    /// Renders <paramref name="artifact"/> under <paramref name="view"/>. <paramref name="kind"/>
    /// is the value the artifact was registered with — used to validate the artifact's runtime
    /// type matches what the dispatcher expects.
    /// </summary>
    public static DispatchOutcome Dispatch(string kind, string? view, object artifact, int topN)
        => Dispatch(kind, view, artifact, topN, correlateArtifact: null);

    /// <summary>
    /// Renders <paramref name="artifact"/> under <paramref name="view"/> with optional correlation artifact.
    /// The <paramref name="correlateArtifact"/> is used for views that require a second data source
    /// (e.g., "gc-overlay" requires a <see cref="GcSummary"/> to correlate with activities).
    /// </summary>
    public static DispatchOutcome Dispatch(string kind, string? view, object artifact, int topN, object? correlateArtifact)
    {
        if (topN < 1)
        {
            return new DispatchOutcome(null, null, null, "topN must be >= 1", null);
        }

        var effectiveView = string.IsNullOrWhiteSpace(view) ? DefaultViewFor(kind) : view!;
        var allowed = ViewsFor(kind);
        if (allowed.Count == 0)
        {
            return new DispatchOutcome(null, kind, null, null, null);
        }
        if (!allowed.Contains(effectiveView, StringComparer.OrdinalIgnoreCase))
        {
            return new DispatchOutcome(null, null, effectiveView, null, allowed);
        }

        return kind switch
        {
            CollectionHandleKinds.Counters when artifact is CounterSnapshot c
                => Ok(Render(c, effectiveView)),
            CollectionHandleKinds.ExceptionSnapshot when artifact is ExceptionSnapshot e
                => Ok(Render(e, effectiveView, topN)),
            CollectionHandleKinds.GcEvents when artifact is GcSummary g
                => Ok(Render(g, effectiveView, topN)),
            CollectionHandleKinds.EventSource when artifact is EventSourceCapture es
                => Ok(Render(es, effectiveView, topN)),
            CollectionHandleKinds.Activities when artifact is ActivityCapture a
                => RenderActivities(a, effectiveView, topN, correlateArtifact),
            CollectionHandleKinds.LogSnapshot when artifact is LogSnapshot logs
                => Ok(Render(logs, effectiveView, topN)),
            CollectionHandleKinds.JitSnapshot when artifact is JitSnapshot jit
                => Ok(Render(jit, effectiveView, topN)),
            CollectionHandleKinds.ThreadPoolSnapshot when artifact is ThreadPoolEventSnapshot threadPool
                => Ok(Render(threadPool, effectiveView, topN)),
            CollectionHandleKinds.ContentionSnapshot when artifact is ContentionSnapshot contention
                => Ok(Render(contention, effectiveView, topN)),
            CollectionHandleKinds.DbSnapshot when artifact is DbSnapshot db
                => Ok(Render(db, effectiveView, topN)),
            CollectionHandleKinds.KestrelSnapshot when artifact is KestrelSnapshot kestrel
                => Ok(Render(kestrel, effectiveView, topN)),
            CollectionHandleKinds.NetworkingSnapshot when artifact is NetworkingSnapshot networking
                => Ok(Render(networking, effectiveView, topN)),
            CollectionHandleKinds.StartupSnapshot when artifact is StartupSnapshot startup
                => Ok(Render(startup, effectiveView, topN)),
            _ => new DispatchOutcome(null, kind, null, null, null),
        };
    }

    private static DispatchOutcome Ok(CollectionQueryResult result) =>
        new(result, null, null, null, null);

    // --- Per-kind render -------------------------------------------------------------

    private static CollectionQueryResult Render(CounterSnapshot c, string view)
    {
        object payload = view.Equals("byProvider", StringComparison.OrdinalIgnoreCase)
            ? new CountersByProviderView(c.Counters
                .GroupBy(v => v.Provider)
                .Select(g => new CountersProviderGroup(g.Key, g.ToList()))
                .ToList())
            : new CountersSummaryView(c.Counters.Count, c.Counters, c.Meters.Count, c.Meters, c.Notes);

        return new CollectionQueryResult(
            CollectionHandleKinds.Counters, view, c.ProcessId, c.StartedAt, c.Duration, payload);
    }

    private static CollectionQueryResult Render(ExceptionSnapshot e, string view, int topN)
    {
        object payload = view.ToLowerInvariant() switch
        {
            "bytype" => new ExceptionByTypeView(e.TotalExceptions, e.ByType),
            "recent" => RecentView(e, topN),
            _ /* summary */ => new ExceptionByTypeView(
                e.TotalExceptions,
                e.ByType.Take(topN).ToList()),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.ExceptionSnapshot, view, e.ProcessId, e.StartedAt, e.Duration, payload);
    }

    private static ExceptionRecentView RecentView(ExceptionSnapshot e, int topN)
    {
        var sliced = e.Recent.Take(topN).ToList();
        return new ExceptionRecentView(e.TotalExceptions, e.RecentCap, sliced.Count, sliced);
    }

    private static CollectionQueryResult Render(GcSummary g, string view, int topN)
    {
        object payload = view.ToLowerInvariant() switch
        {
            "events" => new GcEventsView(g.TotalCollections, Math.Min(topN, g.Events.Count), g.Events.Take(topN).ToList()),
            "pausehistogram" => BuildHistogram(g),
            "timeline" => BuildTimeline(g, topN),
            "longestpauses" => BuildLongestPauses(g, topN),
            "bygeneration" => BuildByGeneration(g),
            "heap-stats" or "heapstats" => BuildHeapStats(g, topN),
            _ /* summary */ => new GcSummaryView(g.TotalCollections, g.TotalPauseTime, g.MaxPauseTime, g.Generations),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.GcEvents, view, g.ProcessId, g.StartedAt, g.Duration, payload);
    }

    private static GcPauseHistogramView BuildHistogram(GcSummary g)
    {
        // Buckets aligned with the rules of thumb the playbook uses (<1ms negligible,
        // 1-10ms typical gen0, 10-100ms gen1/gen2, >100ms problematic, >1s catastrophic).
        var bounds = new (string Label, int UpperBoundMs)[]
        {
            ("<1ms", 1),
            ("1-10ms", 10),
            ("10-100ms", 100),
            ("100-1000ms", 1000),
            (">=1s", int.MaxValue),
        };

        var counts = new int[bounds.Length];
        foreach (var ev in g.Events)
        {
            var ms = ev.PauseDuration.TotalMilliseconds;
            for (var i = 0; i < bounds.Length; i++)
            {
                if (ms < bounds[i].UpperBoundMs)
                {
                    counts[i]++;
                    break;
                }
            }
        }

        var buckets = bounds.Select((b, i) => new GcPauseBucket(b.Label, b.UpperBoundMs, counts[i])).ToList();
        return new GcPauseHistogramView(g.TotalCollections, g.MaxPauseTime, buckets);
    }

    // Orders retained GC events by start time (stable on original ordinal to break 1ms-resolution
    // ties) and assigns each a 0-based timeline Index plus the start-to-start gap from its predecessor.
    private static List<GcTimelineEntry> BuildTimelineEntries(GcSummary g)
    {
        var ordered = g.Events
            .Select((ev, ordinal) => (ev, ordinal))
            .OrderBy(x => x.ev.Timestamp)
            .ThenBy(x => x.ordinal)
            .ToList();

        var entries = new List<GcTimelineEntry>(ordered.Count);
        DateTimeOffset? previousStart = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var ev = ordered[i].ev;
            var gap = previousStart is { } prev && ev.Timestamp > prev
                ? ev.Timestamp - prev
                : TimeSpan.Zero;
            entries.Add(new GcTimelineEntry(i, ev.Timestamp, ev.Generation, ev.Reason, ev.Type, ev.PauseDuration, gap));
            previousStart = ev.Timestamp;
        }

        return entries;
    }

    private static GcTimelineView BuildTimeline(GcSummary g, int topN)
    {
        var entries = BuildTimelineEntries(g);
        var slice = entries.Take(topN).ToList();
        return new GcTimelineView(g.TotalCollections, slice.Count, slice);
    }

    private static GcLongestPausesView BuildLongestPauses(GcSummary g, int topN)
    {
        var ranked = BuildTimelineEntries(g)
            .OrderByDescending(e => e.PauseDuration)
            .ThenBy(e => e.Index)
            .Take(topN)
            .ToList();
        return new GcLongestPausesView(g.TotalCollections, ranked.Count, ranked);
    }

    private static GcByGenerationView BuildByGeneration(GcSummary g)
    {
        // Background GCs are gen2 by depth but get their own mutually-exclusive bucket: gen2 here
        // means non-background gen2 only. Buckets with no events are omitted.
        static string BucketOf(GcEvent ev) =>
            string.Equals(ev.Type, "BackgroundGC", StringComparison.Ordinal)
                ? "background"
                : $"gen{ev.Generation}";

        static int OrderOf(string bucket) => bucket switch
        {
            "gen0" => 0,
            "gen1" => 1,
            "gen2" => 2,
            "background" => 3,
            _ => 4,
        };

        var stats = g.Events
            .GroupBy(BucketOf)
            .Select(grp =>
            {
                var total = grp.Aggregate(TimeSpan.Zero, (acc, e) => acc + e.PauseDuration);
                var count = grp.Count();
                var max = grp.Max(e => e.PauseDuration);
                var mean = TimeSpan.FromTicks(total.Ticks / count);
                return new GcGenerationPauseStats(grp.Key, count, total, mean, max);
            })
            .OrderBy(s => OrderOf(s.Bucket))
            .ToList();

        return new GcByGenerationView(g.TotalCollections, stats);
    }

    private static GcHeapStatsView BuildHeapStats(GcSummary g, int topN)
    {
        var ordered = (g.HeapStats ?? Array.Empty<GcHeapStatsSample>())
            .OrderBy(s => s.Timestamp)
            .ToList();

        GcHeapStatsTrend? trend = null;
        if (ordered.Count >= 2)
        {
            var first = ordered[0];
            var last = ordered[^1];
            trend = new GcHeapStatsTrend(
                FirstAt: first.Timestamp,
                LastAt: last.Timestamp,
                Gen2DeltaBytes: last.Gen2SizeBytes - first.Gen2SizeBytes,
                LohDeltaBytes: last.LohSizeBytes - first.LohSizeBytes,
                PohDeltaBytes: last.PohSizeBytes - first.PohSizeBytes,
                TotalHeapDeltaBytes: last.TotalHeapSizeBytes - first.TotalHeapSizeBytes,
                PinnedObjectCountDelta: last.PinnedObjectCount - first.PinnedObjectCount,
                GcHandleCountDelta: last.GcHandleCount - first.GcHandleCount);
        }

        var slice = ordered.Take(topN).ToList();
        return new GcHeapStatsView(ordered.Count, slice.Count, trend, slice);
    }

    private static CollectionQueryResult Render(EventSourceCapture es, string view, int topN)
    {
        var capturedCount = es.Events.Count;
        var truncated = es.TotalEvents > capturedCount;

        object payload = view.ToLowerInvariant() switch
        {
            "byeventname" => new EventSourceByEventNameView(
                es.Provider,
                es.TotalEvents,
                capturedCount,
                truncated,
                es.Events.GroupBy(e => e.EventName)
                    .Select(g => new EventSourceEventNameGroup(g.Key, g.Count()))
                    .OrderByDescending(g => g.Count)
                    .ToList()),
            "events" => new EventSourceEventsView(
                es.Provider,
                es.TotalEvents,
                Math.Min(topN, es.Events.Count),
                es.Events.Take(topN).ToList()),
            _ /* summary */ => new EventSourceByEventNameView(
                es.Provider,
                es.TotalEvents,
                capturedCount,
                truncated,
                es.Events.GroupBy(e => e.EventName)
                    .Select(g => new EventSourceEventNameGroup(g.Key, g.Count()))
                    .OrderByDescending(g => g.Count)
                    .Take(topN)
                    .ToList()),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.EventSource, view, es.ProcessId, es.StartedAt, es.Duration, payload);
    }

    private static CollectionQueryResult Render(ActivityCapture capture, string view, int topN)
    {
        var capturedCount = capture.Activities.Count;
        var truncated = capture.TotalActivities > capturedCount;

        object payload = view.ToLowerInvariant() switch
        {
            "bysource" => new ActivitiesBySourceView(
                capture.SourceFilters,
                capture.TotalActivities,
                capturedCount,
                truncated,
                capture.BySource.Take(topN).ToList()),
            "byoperation" => new ActivitiesByOperationView(
                capture.SourceFilters,
                capture.TotalActivities,
                capturedCount,
                truncated,
                capture.ByOperation.Take(topN).ToList()),
            "activities" => new ActivitiesListView(
                capture.SourceFilters,
                capture.TotalActivities,
                Math.Min(topN, capture.Activities.Count),
                capture.Activities.Take(topN).ToList()),
            _ /* summary */ => new ActivitiesSummaryView(
                capture.SourceFilters,
                capture.TotalActivities,
                capture.CompletedActivities,
                capturedCount,
                truncated,
                capture.BySource.Take(topN).ToList(),
                capture.ByOperation.Take(topN).ToList()),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.Activities, view, capture.ProcessId, capture.StartedAt, capture.Duration, payload);
    }

    /// <summary>
    /// Handles Activities rendering with optional GC correlation for the "gc-overlay" view.
    /// </summary>
    private static DispatchOutcome RenderActivities(ActivityCapture capture, string view, int topN, object? correlateArtifact)
    {
        if (view.Equals("gc-overlay", StringComparison.OrdinalIgnoreCase))
        {
            if (correlateArtifact is not GcSummary gcSummary)
            {
                return new DispatchOutcome(
                    null, null, null,
                    "gc-overlay view requires gcHandle parameter pointing to a gc-events artifact",
                    null);
            }

            var overlay = GcActivityCorrelator.Correlate(capture, gcSummary, topN);
            return Ok(new CollectionQueryResult(
                CollectionHandleKinds.Activities,
                view,
                capture.ProcessId,
                capture.StartedAt,
                capture.Duration,
                overlay));
        }

        // Delegate to the standard Activities renderer for all other views
        return Ok(Render(capture, view, topN));
    }

    private static CollectionQueryResult Render(LogSnapshot snapshot, string view, int topN)
    {
        var capturedCount = snapshot.Recent.Count;
        var counts = new LogLevelCounts(
            snapshot.EventsByLevelTrace,
            snapshot.EventsByLevelDebug,
            snapshot.EventsByLevelInformation,
            snapshot.EventsByLevelWarning,
            snapshot.EventsByLevelError,
            snapshot.EventsByLevelCritical);

        object payload = view.ToLowerInvariant() switch
        {
            "bycategory" => new LogByCategoryView(
                snapshot.CategoryFilters,
                snapshot.MinimumLevel,
                snapshot.TotalEvents,
                Math.Min(topN, snapshot.ByCategory.Count),
                snapshot.ByCategory.Take(topN).ToList()),
            "bylevel" => new LogByLevelView(
                snapshot.TotalEvents,
                new[]
                {
                    CreateLogLevelGroup("Trace", snapshot.EventsByLevelTrace, snapshot.Recent, topN),
                    CreateLogLevelGroup("Debug", snapshot.EventsByLevelDebug, snapshot.Recent, topN),
                    CreateLogLevelGroup("Information", snapshot.EventsByLevelInformation, snapshot.Recent, topN),
                    CreateLogLevelGroup("Warning", snapshot.EventsByLevelWarning, snapshot.Recent, topN),
                    CreateLogLevelGroup("Error", snapshot.EventsByLevelError, snapshot.Recent, topN),
                    CreateLogLevelGroup("Critical", snapshot.EventsByLevelCritical, snapshot.Recent, topN),
                }),
            "recent" => new LogRecentView(
                snapshot.TotalEvents,
                capturedCount,
                snapshot.Truncated,
                Math.Min(topN, snapshot.Recent.Count),
                snapshot.Recent.TakeLast(topN).ToList()),
            "errors" => new LogErrorsView(
                snapshot.TotalEvents,
                Math.Min(topN, snapshot.Recent.Count(static entry => IsWarningOrHigher(entry.Level))),
                snapshot.Recent.Where(static entry => IsWarningOrHigher(entry.Level)).TakeLast(topN).ToList()),
            _ /* summary */ => new LogSummaryView(
                snapshot.CategoryFilters,
                snapshot.MinimumLevel,
                snapshot.TotalEvents,
                counts,
                capturedCount,
                snapshot.Truncated,
                snapshot.ByCategory.Take(topN).ToList(),
                snapshot.Notes),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.LogSnapshot, view, snapshot.ProcessId, snapshot.StartedAt, snapshot.Duration, payload);
    }

    private static LogLevelGroup CreateLogLevelGroup(string level, long count, IReadOnlyList<LogEntry> recent, int topN) =>
        new(
            level,
            count,
            recent.Where(entry => string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase))
                .TakeLast(topN)
                .ToList());

    private static bool IsWarningOrHigher(string level) =>
        string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(level, "Critical", StringComparison.OrdinalIgnoreCase);

    private static CollectionQueryResult Render(JitSnapshot snapshot, string view, int topN)
    {
        var topMethods = snapshot.Methods.Take(topN).ToList();
        var rejitMethods = snapshot.Methods
            .Where(static method => method.ReJitCount > 0 || method.OsrCount > 0)
            .Take(topN)
            .ToList();

        object payload = view.ToLowerInvariant() switch
        {
            "topmethods" => new JitTopMethodsView(snapshot.UniqueMethods, topMethods.Count, topMethods),
            "tierdistribution" => new JitTierDistributionView(snapshot.Distribution, snapshot.Tier1Percent, snapshot.R2RHitRatePercent, snapshot.HealthCheck),
            "rejit" => new JitReJitView(snapshot.ReJitCount, snapshot.OsrCount, rejitMethods.Count, rejitMethods),
            _ => new JitSummaryView(
                snapshot.JitStartCount,
                snapshot.CompletedCompilations,
                snapshot.UniqueMethods,
                snapshot.Distribution,
                snapshot.R2RLookupCount,
                snapshot.ReJitCount,
                snapshot.OsrCount,
                snapshot.IlMapCount,
                snapshot.Tier1Percent,
                snapshot.R2RHitRatePercent,
                snapshot.HealthCheck,
                topMethods,
                snapshot.Notes),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.JitSnapshot, view, snapshot.ProcessId, snapshot.StartedAt, snapshot.Duration, payload);
    }

    private static CollectionQueryResult Render(ThreadPoolEventSnapshot snapshot, string view, int topN)
    {
        var latestWorker = snapshot.WorkerThreadTimeline.Count > 0 ? snapshot.WorkerThreadTimeline[^1].Count : 0;
        var peakWorker = snapshot.WorkerThreadTimeline.Count > 0 ? snapshot.WorkerThreadTimeline.Max(static bucket => bucket.Count) : 0;
        var latestIocp = snapshot.IocpThreadTimeline.Count > 0 ? snapshot.IocpThreadTimeline[^1].Count : 0;
        var peakIocp = snapshot.IocpThreadTimeline.Count > 0 ? snapshot.IocpThreadTimeline.Max(static bucket => bucket.Count) : 0;
        var starvationAdjustments = snapshot.HillClimbing.Count(static sample => string.Equals(sample.Reason, "Starvation", StringComparison.OrdinalIgnoreCase));

        object payload = view.ToLowerInvariant() switch
        {
            "timeline" => new ThreadPoolTimelineView(snapshot.WorkerThreadTimeline, snapshot.IocpThreadTimeline),
            "hillclimbing" => new ThreadPoolHillClimbingView(
                Math.Min(topN, snapshot.HillClimbing.Count),
                snapshot.HillClimbing.Take(topN).ToList()),
            "workitemorigins" => new ThreadPoolWorkItemOriginsView(
                snapshot.TotalEnqueueEvents,
                Math.Min(topN, snapshot.WorkItemOrigins.Count),
                snapshot.WorkItemOrigins.Take(topN).ToList()),
            _ => new ThreadPoolSummaryView(
                latestWorker,
                peakWorker,
                latestIocp,
                peakIocp,
                snapshot.HillClimbing.Count,
                starvationAdjustments,
                snapshot.TotalEnqueueEvents,
                snapshot.TotalDequeueEvents,
                snapshot.EffectiveSettings,
                snapshot.WorkItemOrigins.Take(topN).ToList(),
                snapshot.Notes),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.ThreadPoolSnapshot, view, snapshot.ProcessId, snapshot.StartedAt, snapshot.Duration, payload);
    }

    private static CollectionQueryResult Render(ContentionSnapshot snapshot, string view, int topN)
    {
        var byCallSite = snapshot.Events
            .GroupBy(static item => new { item.CallSiteMethod, item.CallSiteModule })
            .Select(static group => new ContentionCallSiteGroup(
                group.Key.CallSiteMethod,
                group.Key.CallSiteModule,
                group.Count(),
                group.Select(static item => item.LockId).Where(static lockId => lockId != 0).Distinct().Count(),
                group.Select(static item => item.OwnerManagedThreadId).Where(static owner => owner.HasValue).Select(static owner => owner!.Value).Distinct().Count(),
                group.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration),
                group.Max(static item => item.Duration)))
            .OrderByDescending(static group => group.TotalContentionDuration)
            .ThenByDescending(static group => group.EventCount)
            .ThenBy(static group => group.CallSiteMethod, StringComparer.Ordinal)
            .Take(topN)
            .ToList();
        var byOwner = snapshot.Events
            .GroupBy(static item => item.OwnerManagedThreadId)
            .Select(static group => new ContentionOwnerGroup(
                group.Key,
                group.Count(),
                group.Select(static item => item.LockId).Where(static lockId => lockId != 0).Distinct().Count(),
                group.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration),
                group.Max(static item => item.Duration)))
            .OrderByDescending(static group => group.TotalContentionDuration)
            .ThenBy(static group => group.OwnerManagedThreadId)
            .Take(topN)
            .ToList();

        object payload = view.ToLowerInvariant() switch
        {
            "bycallsite" => new ContentionByCallSiteView(snapshot.TotalEvents, byCallSite.Count, byCallSite),
            "byowner" => new ContentionByOwnerView(snapshot.TotalEvents, byOwner.Count, byOwner),
            _ => new ContentionSummaryView(
                snapshot.TotalEvents,
                snapshot.DistinctMonitors,
                snapshot.TotalContentionDuration,
                snapshot.P50ContentionDuration,
                snapshot.P95ContentionDuration,
                snapshot.MaxContentionDuration,
                snapshot.Notes),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.ContentionSnapshot, view, snapshot.ProcessId, snapshot.StartedAt, snapshot.Duration, payload);
    }

    private static CollectionQueryResult Render(DbSnapshot snapshot, string view, int topN)
    {
        var poolExhaustedCount = snapshot.ConnectionPool.Sum(static stats => stats.PoolExhaustedCount);
        object payload = view.ToLowerInvariant() switch
        {
            "bycommand" => new DbByCommandView(
                snapshot.TotalCommands,
                Math.Min(topN, snapshot.ByCommand.Count),
                snapshot.ByCommand.Take(topN).ToList()),
            "n+1" => new DbNPlusOneView(
                snapshot.NPlusOne.Count,
                Math.Min(topN, snapshot.NPlusOne.Count),
                snapshot.NPlusOne.Take(topN).ToList()),
            "connectionpool" => new DbConnectionPoolView(
                snapshot.ConnectionPool.Count,
                poolExhaustedCount,
                snapshot.ConnectionPool.Take(topN).ToList(),
                snapshot.Notes),
            _ => new DbSummaryView(
                snapshot.TotalCommands,
                snapshot.ByCommand.Count,
                snapshot.NPlusOne.Count,
                snapshot.ByCommand.Take(topN).ToList(),
                snapshot.ConnectionPool.Take(topN).ToList(),
                snapshot.Notes),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.DbSnapshot, view, snapshot.ProcessId, snapshot.StartedAt, snapshot.Duration, payload);
    }

    private static CollectionQueryResult Render(KestrelSnapshot snapshot, string view, int topN)
    {
        object payload = view.ToLowerInvariant() switch
        {
            "byoperation" => new KestrelByOperationView(
                snapshot.ByOperation.Count,
                Math.Min(topN, snapshot.ByOperation.Count),
                snapshot.ByOperation.Take(topN).ToList()),
            "queues" => new KestrelQueuesView(
                snapshot.PeakConnectionQueueLength,
                snapshot.PeakRequestQueueLength,
                Math.Min(topN, snapshot.QueuePoints.Count),
                snapshot.QueuePoints.Take(topN).ToList(),
                snapshot.Notes),
            "tls" => new KestrelTlsView(
                snapshot.TlsHandshakesStarted,
                snapshot.TlsHandshakesStopped,
                snapshot.TlsHandshakesFailed,
                snapshot.TlsHandshakeP50,
                snapshot.TlsHandshakeP95,
                snapshot.TlsHandshakeMax,
                snapshot.TlsProtocols),
            "config" => new KestrelConfigurationView(
                snapshot.ConfigurationJson,
                snapshot.Notes),
            _ => new KestrelSummaryView(
                snapshot.ConnectionsStarted,
                snapshot.ConnectionsStopped,
                snapshot.ConnectionsRejected,
                snapshot.RequestsStarted,
                snapshot.RequestsStopped,
                snapshot.TlsHandshakesStarted,
                snapshot.TlsHandshakesFailed,
                snapshot.PeakConnectionQueueLength,
                snapshot.PeakRequestQueueLength,
                snapshot.RequestP95,
                snapshot.RequestMax,
                snapshot.Counters,
                snapshot.ConfigurationJson is not null,
                snapshot.Notes),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.KestrelSnapshot, view, snapshot.ProcessId, snapshot.StartedAt, snapshot.Duration, payload);
    }

    private static CollectionQueryResult Render(NetworkingSnapshot snapshot, string view, int topN)
    {
        object payload = view.ToLowerInvariant() switch
        {
            "byoperation" => new NetworkingByOperationView(
                snapshot.ByOperation.Count,
                Math.Min(topN, snapshot.ByOperation.Count),
                snapshot.ByOperation.Take(topN).ToList()),
            "queue" => new NetworkingQueueView(
                snapshot.HttpRequestsLeftQueue,
                snapshot.TimeInQueueP50,
                snapshot.TimeInQueueP95,
                snapshot.TimeInQueueMax,
                snapshot.HttpConnectionsEstablished,
                snapshot.HttpConnectionsClosed,
                snapshot.Counters.Where(c => c.Provider == "System.Net.Http").ToList(),
                snapshot.Notes),
            "tls" => new NetworkingTlsView(
                snapshot.TlsHandshakesStarted,
                snapshot.TlsHandshakesStopped,
                snapshot.TlsHandshakesFailed,
                snapshot.TlsP50,
                snapshot.TlsP95,
                snapshot.TlsMax,
                snapshot.TlsProtocols),
            "dns" => new NetworkingDnsView(
                snapshot.DnsLookupsStarted,
                snapshot.DnsLookupsStopped,
                snapshot.DnsLookupsFailed,
                snapshot.DnsP50,
                snapshot.DnsP95,
                snapshot.DnsMax),
            _ => new NetworkingSummaryView(
                snapshot.HttpRequestsStarted,
                snapshot.HttpRequestsStopped,
                snapshot.HttpRequestsFailed,
                snapshot.HttpConnectionsEstablished,
                snapshot.HttpConnectionsClosed,
                snapshot.HttpRequestP95,
                snapshot.HttpRequestMax,
                snapshot.TimeInQueueP95,
                snapshot.DnsLookupsStarted,
                snapshot.DnsLookupsFailed,
                snapshot.TlsHandshakesStarted,
                snapshot.TlsHandshakesFailed,
                snapshot.SocketConnectsStarted,
                snapshot.SocketConnectsFailed,
                snapshot.Counters,
                snapshot.Notes),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.NetworkingSnapshot, view, snapshot.ProcessId, snapshot.StartedAt, snapshot.Duration, payload);
    }

    private static CollectionQueryResult Render(StartupSnapshot snapshot, string view, int topN)
    {
        var topAssemblies = BuildLoadAggregates(
            snapshot.AssemblyLoads,
            static item => item.AssemblyName,
            static item => item.Timestamp,
            topN);
        var topModules = BuildLoadAggregates(
            snapshot.ModuleLoads,
            static item => item.ModuleName,
            static item => item.Timestamp,
            topN);
        var diAggregate = new StartupDiAggregate(
            snapshot.TotalDiEvents,
            snapshot.DiServiceProviderBuiltCount,
            snapshot.DiServiceProviderDescriptorsCount,
            snapshot.DiCallSiteBuiltCount,
            snapshot.DiServiceResolvedCount,
            snapshot.DiExpressionTreeGeneratedCount,
            snapshot.DiDynamicMethodBuiltCount,
            snapshot.DiServiceRealizationFailedCount,
            snapshot.ObservedDiActivityDuration);

        object payload = view.ToLowerInvariant() switch
        {
            "assemblies" => new StartupAssembliesView(
                snapshot.TotalAssemblyLoads,
                Math.Min(topN, snapshot.AssemblyLoads.Count),
                snapshot.AssemblyLoads.Take(topN).ToList(),
                topAssemblies),
            "modules" => new StartupModulesView(
                snapshot.TotalModuleLoads,
                Math.Min(topN, snapshot.ModuleLoads.Count),
                snapshot.ModuleLoads.Take(topN).ToList(),
                topModules),
            "di" => new StartupDiView(
                diAggregate,
                Math.Min(topN, snapshot.DiEvents.Count),
                snapshot.DiEvents.Take(topN).ToList(),
                snapshot.Notes),
            "timeline" => new StartupTimelineView(
                snapshot.Timeline.Count,
                Math.Min(topN, snapshot.Timeline.Count),
                snapshot.Timeline.Take(topN).ToList()),
            _ => new StartupSummaryView(
                snapshot.TotalAssemblyLoads,
                snapshot.TotalModuleLoads,
                diAggregate,
                topAssemblies,
                topModules,
                snapshot.Notes),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.StartupSnapshot, view, snapshot.ProcessId, snapshot.StartedAt, snapshot.Duration, payload);
    }

    private static List<StartupLoadAggregate> BuildLoadAggregates<T>(
        IReadOnlyList<T> items,
        Func<T, string> nameSelector,
        Func<T, DateTimeOffset> timestampSelector,
        int topN)
    {
        return items
            .GroupBy(nameSelector)
            .Select(group => new StartupLoadAggregate(
                group.Key,
                group.Count(),
                group.Min(timestampSelector),
                group.Max(timestampSelector)))
            .OrderByDescending(static group => group.Count)
            .ThenBy(static group => group.Name, StringComparer.Ordinal)
            .Take(topN)
            .ToList();
    }
}
