using System.Diagnostics.Tracing;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Gc;

/// <summary>
/// Default <see cref="IGcCollector"/> backed by an EventPipe session subscribed to the
/// runtime GC keyword (0x1) on <c>Microsoft-Windows-DotNETRuntime</c>. Pairs
/// GCStart/GCStop for collection elapsed; separate suspend/restart events measure runtime suspension.
/// </summary>
public sealed class EventPipeGcCollector : IGcCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long GcKeyword = 0x1;

    private readonly ILogger<EventPipeGcCollector> _logger;
    internal Action<string>? CollectionStarted { get; init; }
    internal EventPipeProvider? ReadinessProvider { get; init; }
    internal Action<EventPipeEventSource>? ConfigureReadiness { get; init; }

    public EventPipeGcCollector(ILogger<EventPipeGcCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeGcCollector>.Instance;
    }

    public async Task<GcSummary> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxEvents = 200,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (maxEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "maxEvents must be >= 1.");
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxEvents, GcCaptureState.MaxRetainedIntervals);

        var providers = new List<EventPipeProvider>
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Informational, GcKeyword),
        };
        if (ReadinessProvider is not null) providers.Add(ReadinessProvider);

        var processStartedAt = ProcessLifetime.TryReadStart(processId);
        var startedAt = DateTimeOffset.UtcNow;
        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        // EventPipeEventSource invokes these callbacks on the single source.Process() thread, so
        // plain collections are sufficient and avoid unnecessary synchronization on the hot path.
        var state = new GcCaptureState(maxEvents);
        var aggregation = state.Collections;
        var heapStats = new List<GcHeapStatsSample>(Math.Min(maxEvents, 128));
        var droppedHeapStats = 0;
        long eventsLost = 0;
        var completion = "normal-stop";
        var observationEnd = startedAt;

        await EventPipeCollectionRunner.RunAsync(
            session,
            duration,
            source =>
            {
                ConfigureReadiness?.Invoke(source);
                source.Clr.GCStart += traceEvent =>
                {
                    state.CollectionBegin(traceEvent.ClrInstanceID, unchecked((uint)traceEvent.Count), traceEvent.Version,
                        new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime()), traceEvent.Depth,
                        traceEvent.Reason.ToString(), traceEvent.Type.ToString());
                    CollectionStarted?.Invoke(traceEvent.Type.ToString());
                };

                source.Clr.GCStop += traceEvent =>
                {
                    state.CollectionEnd(traceEvent.ClrInstanceID, unchecked((uint)traceEvent.Count), traceEvent.Version,
                        new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime()));
                };
                source.Clr.GCSuspendEEStart += e => state.SuspendBegin(e.ClrInstanceID, e.ThreadID, e.Version,
                    new DateTimeOffset(e.TimeStamp.ToUniversalTime()), (int)e.Reason, unchecked((uint)e.Count));
                source.Clr.GCSuspendEEStop += e => state.Boundary(e.ClrInstanceID, e.ThreadID, e.Version,
                    new DateTimeOffset(e.TimeStamp.ToUniversalTime()), 0);
                source.Clr.GCRestartEEStart += e => state.Boundary(e.ClrInstanceID, e.ThreadID, e.Version,
                    new DateTimeOffset(e.TimeStamp.ToUniversalTime()), 1);
                source.Clr.GCRestartEEStop += e => state.Boundary(e.ClrInstanceID, e.ThreadID, e.Version,
                    new DateTimeOffset(e.TimeStamp.ToUniversalTime()), 2);

                source.Clr.GCHeapStats += traceEvent =>
                {
                    if (heapStats.Count >= maxEvents)
                    {
                        if (droppedHeapStats < int.MaxValue) droppedHeapStats++;
                        return;
                    }

                    heapStats.Add(new GcHeapStatsSample(
                        Timestamp: new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero),
                        Gen0SizeBytes: traceEvent.GenerationSize0,
                        Gen1SizeBytes: traceEvent.GenerationSize1,
                        Gen2SizeBytes: traceEvent.GenerationSize2,
                        LohSizeBytes: traceEvent.GenerationSize3,
                        PohSizeBytes: traceEvent.GenerationSize4,
                        TotalHeapSizeBytes: traceEvent.TotalHeapSize,
                        TotalPromotedBytes: traceEvent.TotalPromoted,
                        Gen2PromotedBytes: traceEvent.TotalPromotedSize2,
                        PohPromotedBytes: traceEvent.TotalPromotedSize4,
                        FinalizationPromotedBytes: traceEvent.FinalizationPromotedSize,
                        FinalizationPromotedCount: (long)traceEvent.FinalizationPromotedCount,
                        PinnedObjectCount: traceEvent.PinnedObjectCount,
                        GcHandleCount: traceEvent.GCHandleCount));
                };
            },
            ex =>
            {
                completion = "processing-failure";
                _logger.LogDebug(ex, "EventPipe GC source ended for pid {Pid}.", processId);
            },
            cancellationToken,
            (lost, early, streamStart) =>
            {
                eventsLost = lost;
                observationEnd = DateTimeOffset.UtcNow;
                if (early) completion = "early-exit";
                if (streamStart > DateTimeOffset.UnixEpoch && streamStart <= observationEnd)
                    startedAt = streamStart;
                else
                    completion = "missing-session-header";
            }).ConfigureAwait(false);

        return new GcSummary(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: observationEnd > startedAt ? observationEnd - startedAt : TimeSpan.Zero,
            TotalCollections: aggregation.TotalCollections,
            TotalPauseTime: aggregation.TotalPauseTime,
            MaxPauseTime: aggregation.MaxPauseTime,
            Generations: aggregation.Generations,
            Events: aggregation.Events,
            HeapStats: heapStats.OrderBy(s => s.Timestamp).ToList(),
            DroppedEvents: aggregation.DroppedEvents,
            DroppedHeapStats: droppedHeapStats,
            Suspension: state.Finish(startedAt, observationEnd, processStartedAt, eventsLost, completion))
        {
            RequestedDuration = duration,
        };
    }
}

/// <summary>
/// Keeps exact constant-size GC aggregates while retaining only the first configured number of raw
/// events. EventPipe callbacks are single-threaded, so no synchronization is required.
/// </summary>
internal sealed class GcEventAggregation
{
    private readonly int _maxEvents;
    private readonly List<GcEvent> _events;
    private readonly int[] _generationCounts = new int[3];
    private long _totalPauseTicks;
    private long _maxPauseTicks;

    public GcEventAggregation(int maxEvents)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEvents, 1);
        _maxEvents = maxEvents;
        _events = new List<GcEvent>(Math.Min(maxEvents, 128));
    }

    public long ObservedCollections { get; private set; }
    public int TotalCollections => (int)Math.Min(int.MaxValue, ObservedCollections);

    public int DroppedEvents => (int)Math.Min(int.MaxValue, ObservedCollections - _events.Count);

    public TimeSpan TotalPauseTime => TimeSpan.FromTicks(_totalPauseTicks);

    public TimeSpan MaxPauseTime => TimeSpan.FromTicks(_maxPauseTicks);

    public IReadOnlyList<GcEvent> Events => _events;

    public IReadOnlyList<GenerationStats> Generations =>
        Enumerable.Range(0, _generationCounts.Length)
            .Where(generation => _generationCounts[generation] > 0)
            .Select(generation => new GenerationStats(generation, _generationCounts[generation]))
            .ToList();

    public void Add(GcEvent gcEvent)
    {
        if (ObservedCollections < long.MaxValue) ObservedCollections++;
        _totalPauseTicks = gcEvent.PauseDuration.Ticks > long.MaxValue - _totalPauseTicks
            ? long.MaxValue : _totalPauseTicks + gcEvent.PauseDuration.Ticks;
        _maxPauseTicks = Math.Max(_maxPauseTicks, gcEvent.PauseDuration.Ticks);
        if ((uint)gcEvent.Generation < (uint)_generationCounts.Length)
        {
            if (_generationCounts[gcEvent.Generation] < int.MaxValue) _generationCounts[gcEvent.Generation]++;
        }

        if (_events.Count < _maxEvents)
        {
            _events.Add(gcEvent);
        }
    }
}
