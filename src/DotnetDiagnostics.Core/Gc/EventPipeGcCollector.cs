using System.Diagnostics.Tracing;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Gc;

/// <summary>
/// Default <see cref="IGcCollector"/> backed by an EventPipe session subscribed to the
/// runtime GC keyword (0x1) on <c>Microsoft-Windows-DotNETRuntime</c>. Pairs
/// GCStart/GCStop events to compute pause durations per collection.
/// </summary>
public sealed class EventPipeGcCollector : IGcCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long GcKeyword = 0x1;

    private readonly ILogger<EventPipeGcCollector> _logger;

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

        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Informational, GcKeyword),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        // EventPipeEventSource invokes these callbacks on the single source.Process() thread, so
        // plain collections are sufficient and avoid unnecessary synchronization on the hot path.
        var aggregation = new GcEventAggregation(maxEvents);
        var heapStats = new List<GcHeapStatsSample>(Math.Min(maxEvents, 128));
        var droppedHeapStats = 0;
        var pending = new Dictionary<long, GCStartTraceData>();

        await EventPipeCollectionRunner.RunAsync(
            session,
            duration,
            source =>
            {
                source.Clr.GCStart += traceEvent =>
                {
                    var data = (GCStartTraceData)traceEvent.Clone();
                    pending[data.Count] = data;
                };

                source.Clr.GCStop += traceEvent =>
                {
                    if (!pending.Remove(traceEvent.Count, out var start))
                    {
                        return;
                    }

                    var pause = traceEvent.TimeStamp - start.TimeStamp;
                    aggregation.Add(new GcEvent(
                        Timestamp: new DateTimeOffset(start.TimeStamp.ToUniversalTime(), TimeSpan.Zero),
                        Generation: start.Depth,
                        Reason: start.Reason.ToString(),
                        Type: start.Type.ToString(),
                        PauseDuration: pause < TimeSpan.Zero ? TimeSpan.Zero : pause));
                };

                source.Clr.GCHeapStats += traceEvent =>
                {
                    if (heapStats.Count >= maxEvents)
                    {
                        droppedHeapStats++;
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
            ex => _logger.LogDebug(ex, "EventPipe GC source ended for pid {Pid}.", processId),
            cancellationToken).ConfigureAwait(false);

        return new GcSummary(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalCollections: aggregation.TotalCollections,
            TotalPauseTime: aggregation.TotalPauseTime,
            MaxPauseTime: aggregation.MaxPauseTime,
            Generations: aggregation.Generations,
            Events: aggregation.Events,
            HeapStats: heapStats.OrderBy(s => s.Timestamp).ToList(),
            DroppedEvents: aggregation.DroppedEvents,
            DroppedHeapStats: droppedHeapStats);
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

    public int TotalCollections { get; private set; }

    public int DroppedEvents => TotalCollections - _events.Count;

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
        TotalCollections++;
        _totalPauseTicks += gcEvent.PauseDuration.Ticks;
        _maxPauseTicks = Math.Max(_maxPauseTicks, gcEvent.PauseDuration.Ticks);
        if ((uint)gcEvent.Generation < (uint)_generationCounts.Length)
        {
            _generationCounts[gcEvent.Generation]++;
        }

        if (_events.Count < _maxEvents)
        {
            _events.Add(gcEvent);
        }
    }
}
