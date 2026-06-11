using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.ThreadPool;

namespace DotnetDiagnostics.Core.Collection;

/// <summary>
/// Envelope returned by <c>query_collection</c>. Carries the artifact kind, the selected view,
/// the original collection metadata (processId, started-at, duration) and the typed
/// <see cref="Payload"/> produced by the view dispatcher.
/// </summary>
/// <param name="Kind">One of <see cref="CollectionHandleKinds"/>. Distinguishes which view shape <see cref="Payload"/> uses.</param>
/// <param name="View">The view name that was rendered (e.g. <c>summary</c>, <c>byType</c>, <c>events</c>, <c>byProvider</c>).</param>
/// <param name="ProcessId">PID of the target process at the moment of collection. Survives target restarts (handles do not).</param>
/// <param name="StartedAt">UTC timestamp the original collector started its session.</param>
/// <param name="Duration">Wall-clock duration of the original collection window.</param>
/// <param name="Payload">The view-specific data. Serialized polymorphically through <see cref="System.Text.Json"/> reflection.</param>
public sealed record CollectionQueryResult(
    string Kind,
    string View,
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    object Payload);

// --- Counters views ---------------------------------------------------------------------------

/// <summary>Default counters view: every EventCounter plus every Meter time series captured in the window.</summary>
public sealed record CountersSummaryView(
    int Count,
    IReadOnlyList<Counters.CounterValue> Counters,
    int MeterCount,
    IReadOnlyList<Counters.MeterInstrumentValue> Meters,
    IReadOnlyList<string> Notes);

/// <summary>Counters grouped by EventSource provider name.</summary>
public sealed record CountersByProviderView(
    IReadOnlyList<CountersProviderGroup> Providers);

/// <summary>One provider entry in <see cref="CountersByProviderView"/>.</summary>
public sealed record CountersProviderGroup(string Provider, IReadOnlyList<Counters.CounterValue> Counters);

// --- Exception snapshot views -----------------------------------------------------------------

/// <summary>Aggregated by-type counts (always exact, no truncation).</summary>
public sealed record ExceptionByTypeView(
    int TotalExceptions,
    IReadOnlyList<Exceptions.ExceptionCount> ByType);

/// <summary>Individual exception events (truncated to <see cref="RecentCap"/>).</summary>
public sealed record ExceptionRecentView(
    int TotalExceptions,
    int RecentCap,
    int Returned,
    IReadOnlyList<Exceptions.ManagedExceptionEvent> Recent);

// --- GC events views --------------------------------------------------------------------------

/// <summary>Aggregated GC pause headline.</summary>
public sealed record GcSummaryView(
    int TotalCollections,
    TimeSpan TotalPauseTime,
    TimeSpan MaxPauseTime,
    IReadOnlyList<Gc.GenerationStats> Generations);

/// <summary>Raw GC events (capped by <c>topN</c>).</summary>
public sealed record GcEventsView(
    int TotalCollections,
    int Returned,
    IReadOnlyList<Gc.GcEvent> Events);

/// <summary>GC pause histogram — buckets by pause duration so the LLM can describe the tail.</summary>
public sealed record GcPauseHistogramView(
    int TotalCollections,
    TimeSpan MaxPauseTime,
    IReadOnlyList<GcPauseBucket> Buckets);

/// <summary>One bucket in <see cref="GcPauseHistogramView"/>.</summary>
/// <param name="Label">Human-readable label (e.g. <c>"&lt;1ms"</c>, <c>"1-10ms"</c>).</param>
/// <param name="UpperBoundMs">Inclusive upper bound of this bucket in milliseconds. <see cref="int.MaxValue"/> = no upper bound.</param>
/// <param name="Count">Number of GC events whose pause falls in this bucket.</param>
public sealed record GcPauseBucket(string Label, int UpperBoundMs, int Count);

/// <summary>Per-GC timeline ordered by start time, capped to the earliest <c>topN</c> collections.</summary>
/// <param name="TotalCollections">Total GCs retained on the artifact (may be capped by the collector's maxEvents).</param>
/// <param name="Returned">Number of timeline rows actually returned (earliest by start time).</param>
/// <param name="Entries">The chronological GC rows.</param>
public sealed record GcTimelineView(
    int TotalCollections,
    int Returned,
    IReadOnlyList<GcTimelineEntry> Entries);

/// <summary>One GC in the <see cref="GcTimelineView"/> (also reused by <see cref="GcLongestPausesView"/>).</summary>
/// <param name="Index">0-based position in the start-time-ordered timeline (stable cross-reference key).</param>
/// <param name="Timestamp">GCStart time of this collection.</param>
/// <param name="Generation">Collected generation (0/1/2).</param>
/// <param name="Reason">Runtime-reported GC reason.</param>
/// <param name="Type">GC type — one of <c>NonConcurrentGC</c>, <c>BackgroundGC</c>, <c>ForegroundGC</c>.</param>
/// <param name="PauseDuration">GCStart→GCStop elapsed (for background GCs this is collected elapsed, not pure stop-the-world pause).</param>
/// <param name="GapSincePreviousStart">Start-to-start gap from the previous timeline entry (0 for the first; clamped to non-negative).</param>
public sealed record GcTimelineEntry(
    int Index,
    DateTimeOffset Timestamp,
    int Generation,
    string Reason,
    string Type,
    TimeSpan PauseDuration,
    TimeSpan GapSincePreviousStart);

/// <summary>The N longest GC pauses, ranked by pause descending. Each entry keeps its timeline <see cref="GcTimelineEntry.Index"/>.</summary>
public sealed record GcLongestPausesView(
    int TotalCollections,
    int Returned,
    IReadOnlyList<GcTimelineEntry> Pauses);

/// <summary>Pause statistics split per generation, with background GCs called out as their own mutually-exclusive bucket.</summary>
/// <param name="TotalCollections">Total GCs retained on the artifact.</param>
/// <param name="Generations">One row per non-empty bucket (gen0/gen1/gen2/background), in that order.</param>
public sealed record GcByGenerationView(
    int TotalCollections,
    IReadOnlyList<GcGenerationPauseStats> Generations);

/// <summary>Count + total/mean/max pause for one generation bucket.</summary>
/// <param name="Bucket">One of <c>gen0</c>, <c>gen1</c>, <c>gen2</c>, <c>background</c>. <c>gen2</c> excludes background GCs.</param>
/// <param name="Count">Number of GCs in this bucket.</param>
/// <param name="TotalPause">Sum of pause durations in this bucket.</param>
/// <param name="MeanPause">Mean pause (<see cref="TotalPause"/> / <see cref="Count"/>).</param>
/// <param name="MaxPause">Longest single pause in this bucket.</param>
public sealed record GcGenerationPauseStats(
    string Bucket,
    int Count,
    TimeSpan TotalPause,
    TimeSpan MeanPause,
    TimeSpan MaxPause);

/// <summary>
/// Per-generation heap-size and pinned-object trend over the collection window, derived from the
/// <c>GCHeapStats</c> samples retained behind a <c>gc-events</c> handle (issue #384). The classic
/// signal for a slow managed leak or pinning pressure that pause data alone misses.
/// </summary>
/// <param name="SampleCount">Total <c>GCHeapStats</c> samples retained on the artifact.</param>
/// <param name="Returned">Number of samples actually returned (earliest by timestamp, capped by <c>topN</c>).</param>
/// <param name="Trend">First→last deltas across the window (null when fewer than two samples exist).</param>
/// <param name="Samples">The chronological per-GC heap-size samples.</param>
public sealed record GcHeapStatsView(
    int SampleCount,
    int Returned,
    GcHeapStatsTrend? Trend,
    IReadOnlyList<Gc.GcHeapStatsSample> Samples);

/// <summary>First→last deltas of the key heap-size and leak-pressure signals across the window.</summary>
/// <param name="FirstAt">Timestamp of the first retained sample.</param>
/// <param name="LastAt">Timestamp of the last retained sample.</param>
/// <param name="Gen2DeltaBytes">Change in gen2 size (the classic managed-leak signal).</param>
/// <param name="LohDeltaBytes">Change in large-object-heap size.</param>
/// <param name="PohDeltaBytes">Change in pinned-object-heap size (V2 only; 0 on V1 runtimes).</param>
/// <param name="TotalHeapDeltaBytes">Change in total managed heap size.</param>
/// <param name="PinnedObjectCountDelta">Change in pinned-object count (pinning-pressure signal).</param>
/// <param name="GcHandleCountDelta">Change in GC-handle count (handle-leak signal).</param>
public sealed record GcHeapStatsTrend(
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    long Gen2DeltaBytes,
    long LohDeltaBytes,
    long PohDeltaBytes,
    long TotalHeapDeltaBytes,
    long PinnedObjectCountDelta,
    long GcHandleCountDelta);

// --- EventSource views ------------------------------------------------------------------------

/// <summary>Counts grouped by EventName.</summary>
/// <param name="Provider">EventSource provider name (echoed from the capture).</param>
/// <param name="TotalEvents">Total events observed during the collection window — may exceed
/// <paramref name="CapturedCount"/> because the original collector caps stored events at
/// <c>maxEvents</c>; only captured events contribute to the per-name counts below.</param>
/// <param name="CapturedCount">Events that were actually stored and thus grouped here.</param>
/// <param name="Truncated">True when the collector dropped tail events (<c>TotalEvents &gt; CapturedCount</c>).
/// When true, <paramref name="ByEventName"/> reflects only the captured prefix — re-run
/// <c>collect_event_source</c> with a larger <c>maxEvents</c> to get exact totals.</param>
/// <param name="ByEventName">Per-event-name aggregates over the captured subset only.</param>
public sealed record EventSourceByEventNameView(
    string Provider,
    int TotalEvents,
    int CapturedCount,
    bool Truncated,
    IReadOnlyList<EventSourceEventNameGroup> ByEventName);

/// <summary>One group in <see cref="EventSourceByEventNameView"/>.</summary>
public sealed record EventSourceEventNameGroup(string EventName, int Count);

/// <summary>Raw captured events (capped by <c>topN</c>).</summary>
public sealed record EventSourceEventsView(
    string Provider,
    int TotalEvents,
    int Returned,
    IReadOnlyList<EventSources.CapturedEvent> Events);

// --- Activity views ---------------------------------------------------------------------------

/// <summary>Headline view for captured activities; grouping aggregates reflect only the stored subset when truncated.</summary>
public sealed record ActivitiesSummaryView(
    IReadOnlyList<string>? SourceFilters,
    int TotalActivities,
    int CompletedActivities,
    int CapturedCount,
    bool Truncated,
    IReadOnlyList<Activities.ActivitySourceSummary> BySource,
    IReadOnlyList<Activities.ActivityOperationSummary> ByOperation);

/// <summary>Activities grouped by source name.</summary>
public sealed record ActivitiesBySourceView(
    IReadOnlyList<string>? SourceFilters,
    int TotalActivities,
    int CapturedCount,
    bool Truncated,
    IReadOnlyList<Activities.ActivitySourceSummary> Sources);

/// <summary>Activities grouped by source+operation.</summary>
public sealed record ActivitiesByOperationView(
    IReadOnlyList<string>? SourceFilters,
    int TotalActivities,
    int CapturedCount,
    bool Truncated,
    IReadOnlyList<Activities.ActivityOperationSummary> Operations);

/// <summary>Raw captured activities (capped by <c>topN</c>).</summary>
public sealed record ActivitiesListView(
    IReadOnlyList<string>? SourceFilters,
    int TotalActivities,
    int Returned,
    IReadOnlyList<Activities.CapturedActivity> Activities);


// --- Log snapshot views ------------------------------------------------------------------------

public sealed record LogSummaryView(
    IReadOnlyList<string>? CategoryFilters,
    string MinimumLevel,
    long TotalEvents,
    LogLevelCounts Counts,
    int CapturedCount,
    bool Truncated,
    IReadOnlyList<Logs.LogCategoryGroup> ByCategory,
    IReadOnlyList<string> Notes);

public sealed record LogLevelCounts(
    long Trace,
    long Debug,
    long Information,
    long Warning,
    long Error,
    long Critical);

public sealed record LogByCategoryView(
    IReadOnlyList<string>? CategoryFilters,
    string MinimumLevel,
    long TotalEvents,
    int Returned,
    IReadOnlyList<Logs.LogCategoryGroup> Categories);

public sealed record LogByLevelView(
    long TotalEvents,
    IReadOnlyList<LogLevelGroup> Levels);

public sealed record LogLevelGroup(
    string Level,
    long Count,
    IReadOnlyList<Logs.LogEntry> Samples);

public sealed record LogRecentView(
    long TotalEvents,
    int CapturedCount,
    bool Truncated,
    int Returned,
    IReadOnlyList<Logs.LogEntry> Recent);

public sealed record LogErrorsView(
    long TotalEvents,
    int Returned,
    IReadOnlyList<Logs.LogEntry> Errors);


// --- JIT snapshot views ------------------------------------------------------------------------

public sealed record JitSummaryView(
    int JitStartCount,
    int CompletedCompilations,
    int UniqueMethods,
    JitTierDistribution Distribution,
    int R2RLookupCount,
    int ReJitCount,
    int OsrCount,
    int IlMapCount,
    double Tier1Percent,
    double? R2RHitRatePercent,
    string HealthCheck,
    IReadOnlyList<JitMethodSummary> TopMethods,
    IReadOnlyList<string> Notes);

public sealed record JitTopMethodsView(
    int UniqueMethods,
    int Returned,
    IReadOnlyList<JitMethodSummary> Methods);

public sealed record JitTierDistributionView(
    JitTierDistribution Distribution,
    double Tier1Percent,
    double? R2RHitRatePercent,
    string HealthCheck);

public sealed record JitReJitView(
    int ReJitCount,
    int OsrCount,
    int Returned,
    IReadOnlyList<JitMethodSummary> Methods);
// --- ThreadPool snapshot views -----------------------------------------------------------------

public sealed record ThreadPoolSummaryView(
    int LatestWorkerThreadCount,
    int PeakWorkerThreadCount,
    int LatestIocpThreadCount,
    int PeakIocpThreadCount,
    int HillClimbingEvents,
    int StarvationAdjustments,
    int TotalEnqueueEvents,
    int TotalDequeueEvents,
    ThreadPoolEffectiveSettings? EffectiveSettings,
    IReadOnlyList<ThreadPoolWorkItemOrigin> TopWorkItemOrigins,
    IReadOnlyList<string> Notes);

public sealed record ThreadPoolTimelineView(
    IReadOnlyList<ThreadPoolCountBucket> WorkerThreads,
    IReadOnlyList<ThreadPoolCountBucket> IocpThreads);

public sealed record ThreadPoolHillClimbingView(
    int Returned,
    IReadOnlyList<ThreadPoolHillClimbingSample> Samples);

public sealed record ThreadPoolWorkItemOriginsView(
    int TotalEnqueueEvents,
    int Returned,
    IReadOnlyList<ThreadPoolWorkItemOrigin> Origins);

// --- Contention snapshot views -----------------------------------------------------------------

public sealed record ContentionSummaryView(
    int TotalEvents,
    int ContendedMonitorCount,
    TimeSpan TotalContentionDuration,
    TimeSpan P50ContentionDuration,
    TimeSpan P95ContentionDuration,
    TimeSpan MaxContentionDuration,
    IReadOnlyList<string> Notes);

public sealed record ContentionByCallSiteView(
    int TotalEvents,
    int Returned,
    IReadOnlyList<ContentionCallSiteGroup> CallSites);

public sealed record ContentionCallSiteGroup(
    string CallSiteMethod,
    string CallSiteModule,
    int EventCount,
    int DistinctMonitors,
    int DistinctOwnerThreads,
    TimeSpan TotalContentionDuration,
    TimeSpan MaxContentionDuration);

public sealed record ContentionByOwnerView(
    int TotalEvents,
    int Returned,
    IReadOnlyList<ContentionOwnerGroup> Owners);

public sealed record ContentionOwnerGroup(
    int? OwnerManagedThreadId,
    int EventCount,
    int DistinctMonitors,
    TimeSpan TotalContentionDuration,
    TimeSpan MaxContentionDuration);

// --- DB snapshot views -------------------------------------------------------------------------

public sealed record DbSummaryView(
    long TotalCommands,
    int DistinctCommands,
    int NPlusOneCount,
    IReadOnlyList<Db.DbCommandAggregate> TopCommands,
    IReadOnlyList<Db.DbConnectionPoolStats> ConnectionPool,
    IReadOnlyList<string> Notes);

public sealed record DbByCommandView(
    long TotalCommands,
    int Returned,
    IReadOnlyList<Db.DbCommandAggregate> Commands);

public sealed record DbNPlusOneView(
    int TotalIncidents,
    int Returned,
    IReadOnlyList<Db.DbNPlusOneIncident> Incidents);

public sealed record DbConnectionPoolView(
    int Providers,
    int PoolExhaustedCount,
    IReadOnlyList<Db.DbConnectionPoolStats> ConnectionPool,
    IReadOnlyList<string> Notes);
