using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Text;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.ThreadPool;

public sealed class EventPipeThreadPoolCollector : IThreadPoolCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long ThreadingKeyword = 0x10000;

    private static readonly Dictionary<int, string> AdjustmentReasons = new()
    {
        [0] = "Warmup",
        [1] = "Initializing",
        [2] = "RandomMove",
        [3] = "ClimbingMove",
        [4] = "ChangePoint",
        [5] = "Stabilizing",
        [6] = "Starvation",
        [7] = "ThreadTimedOut",
    };

    private static readonly Dictionary<int, string> RuntimeEventIds = new()
    {
        [44] = "IOThreadCreationStart",
        [45] = "IOThreadCreationStop",
        [46] = "IOThreadRetirementStart",
        [47] = "IOThreadRetirementStop",
        [50] = "ThreadPoolWorkerThreadStart",
        [51] = "ThreadPoolWorkerThreadStop",
        [54] = "ThreadPoolWorkerThreadAdjustmentSample",
        [55] = "ThreadPoolWorkerThreadAdjustmentAdjustment",
        [56] = "ThreadPoolWorkerThreadAdjustmentStats",
        [57] = "ThreadPoolWorkerThreadWait",
        [59] = "ThreadPoolMinMaxThreadsChanged",
        [60] = "ThreadPoolWorkingThreadCount",
        [61] = "ThreadPoolEnqueue",
        [62] = "ThreadPoolDequeue",
        [85] = "ThreadPoolMinMaxThreadsChanged",
    };
    internal const int MaxTimelineSamples = 4096;

    private readonly ILogger<EventPipeThreadPoolCollector> _logger;

    public EventPipeThreadPoolCollector(ILogger<EventPipeThreadPoolCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeThreadPoolCollector>.Instance;
    }

    public async Task<ThreadPoolEventSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, ThreadingKeyword),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var notes = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var observedEventNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var workerSamples = new FixedCapacityQueue<CountSample>(MaxTimelineSamples);
        var iocpSamples = new FixedCapacityQueue<CountSample>(MaxTimelineSamples);
        var hillClimbing = new FixedCapacityQueue<ThreadPoolHillClimbingSample>(MaxTimelineSamples);
        var workItemOrigins = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        ThreadPoolEffectiveSettings? effectiveSettings = null;
        double? latestThroughput = null;
        int? lastWorkerCount = null;
        int? lastIocpCount = null;
        var totalEnqueueEvents = 0;
        var totalDequeueEvents = 0;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.ProviderName, RuntimeProvider, StringComparison.Ordinal))
                    {
                        return;
                    }

                    var timestamp = new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero);
                    var eventName = GetCanonicalEventName(traceEvent);
                    observedEventNames.TryAdd(eventName, 0);
                    switch (eventName)
                    {
                        case "IOThreadCreateV1":
                        case "IOThreadCreate":
                        case "IOThreadCreationStop":
                        {
                            if (TryReadIocpCount(traceEvent, lastIocpCount, out var iocpCount))
                            {
                                lastIocpCount = iocpCount;
                                iocpSamples.Enqueue(new CountSample(timestamp, iocpCount));
                            }

                            break;
                        }
                        case "IOThreadRetire":
                        case "IOThreadRetirementStop":
                        {
                            if (lastIocpCount is int currentIocpCount)
                            {
                                lastIocpCount = Math.Max(0, currentIocpCount - 1);
                                iocpSamples.Enqueue(new CountSample(timestamp, lastIocpCount.Value));
                            }

                            break;
                        }
                        case "ThreadPoolWorkerThreadStart":
                        case "ThreadPoolWorkerThreadStop":
                        case "ThreadPoolWorkerThreadWait":
                        {
                            if (TryReadWorkerCount(traceEvent, out var workerCount))
                            {
                                lastWorkerCount = workerCount;
                                workerSamples.Enqueue(new CountSample(timestamp, workerCount));
                            }

                            break;
                        }
                        case "ThreadPoolWorkerThreadAdjustmentSample":
                        {
                            var sampledWorkerCount = TryReadInt(traceEvent, out var workerCount, "NewWorkerThreadCount", "WorkerThreadCount", "ThreadCount", "NumThreads")
                                || TryReadIntByIndex(traceEvent, 2, out workerCount)
                                ? workerCount
                                : lastWorkerCount;
                            if (sampledWorkerCount is int concreteWorkerCount)
                            {
                                lastWorkerCount = concreteWorkerCount;
                                workerSamples.Enqueue(new CountSample(timestamp, concreteWorkerCount));
                            }

                            if (TryReadDouble(traceEvent, out var throughput, "Throughput", "AverageThroughput")
                                || TryReadDoubleByIndex(traceEvent, 0, out throughput)
                                || TryReadDoubleByIndex(traceEvent, 3, out throughput))
                            {
                                latestThroughput = throughput;
                            }

                            break;
                        }
                        case "ThreadPoolWorkingThreadCount":
                        {
                            if (TryReadInt(traceEvent, out var workingCount, "Count")
                                || TryReadIntByIndex(traceEvent, 0, out workingCount))
                            {
                                lastWorkerCount = workingCount;
                                workerSamples.Enqueue(new CountSample(timestamp, workingCount));
                            }

                            break;
                        }
                        case "ThreadPoolWorkerThreadAdjustmentAdjustment":
                        {
                            var reason = ResolveAdjustmentReason(traceEvent);
                            var oldCount = TryReadInt(traceEvent, out var explicitOld, "OldWorkerThreadCount", "OldThreadCount", "OldControlSetting")
                                ? explicitOld
                                : lastWorkerCount;
                            int? newCount = TryReadInt(traceEvent, out var explicitNew, "NewWorkerThreadCount", "NewThreadCount", "NewControlSetting")
                                ? explicitNew
                                : null;

                            if (newCount is null && (TryReadInt(traceEvent, out var delta, "NumberOfNewThreads") || TryReadIntByIndex(traceEvent, 1, out delta)))
                            {
                                if (oldCount is int inferredOld)
                                {
                                    newCount = inferredOld + delta;
                                }
                                else
                                {
                                    newCount = delta;
                                }
                            }

                            if (TryReadDouble(traceEvent, out var throughput, "Throughput", "AverageThroughput")
                                || TryReadDoubleByIndex(traceEvent, 2, out throughput)
                                || TryReadDoubleByIndex(traceEvent, 3, out throughput))
                            {
                                latestThroughput = throughput;
                            }

                            if (newCount is int concreteNewCount)
                            {
                                lastWorkerCount = concreteNewCount;
                                workerSamples.Enqueue(new CountSample(timestamp, concreteNewCount));
                            }

                            hillClimbing.Enqueue(new ThreadPoolHillClimbingSample(
                                timestamp,
                                reason,
                                oldCount,
                                newCount,
                                latestThroughput));
                            break;
                        }
                        case "ThreadPoolEnqueueWork":
                        case "ThreadPoolEnqueue":
                        {
                            Interlocked.Increment(ref totalEnqueueEvents);
                            var origin = ExtractWorkItemOrigin(traceEvent, notes);
                            if (!string.IsNullOrWhiteSpace(origin))
                            {
                                workItemOrigins.AddOrUpdate(origin, 1, static (_, count) => count + 1);
                            }

                            break;
                        }
                        case "ThreadPoolMinMaxThreadsChanged":
                        {
                            if (TryReadEffectiveSettings(traceEvent, out var updatedSettings))
                            {
                                effectiveSettings = updatedSettings;
                            }

                            break;
                        }
                        case "ThreadPoolDequeueWork":
                        case "ThreadPoolDequeue":
                        {
                            Interlocked.Increment(ref totalDequeueEvents);
                            break;
                        }
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe threadpool source ended for pid {Pid}.", processId);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await EventPipeSessionShutdown.StopAndDrainAsync(
                session,
                processingTask,
                ex => _logger.LogDebug(ex, "Stopping EventPipe threadpool session for pid {Pid} failed.", processId))
                .ConfigureAwait(false);
        }

        if (workerSamples.DroppedCount > 0)
        {
            notes.TryAdd($"Dropped {workerSamples.DroppedCount} worker-thread timeline sample(s) after reaching the in-memory cap of {MaxTimelineSamples}.", 0);
        }

        if (iocpSamples.DroppedCount > 0)
        {
            notes.TryAdd($"Dropped {iocpSamples.DroppedCount} IOCP timeline sample(s) after reaching the in-memory cap of {MaxTimelineSamples}.", 0);
        }

        if (hillClimbing.DroppedCount > 0)
        {
            notes.TryAdd($"Dropped {hillClimbing.DroppedCount} hill-climbing sample(s) after reaching the in-memory cap of {MaxTimelineSamples}.", 0);
        }

        var orderedWorkerSamples = workerSamples.Items.OrderBy(static sample => sample.Timestamp).ToList();
        var normalizedHillClimbing = NormalizeHillClimbing(hillClimbing.Items.OrderBy(static sample => sample.Timestamp).ToList(), orderedWorkerSamples, startedAt, notes);
        var orderedIocpSamples = iocpSamples.Items.OrderBy(static sample => sample.Timestamp).ToList();
        if (orderedWorkerSamples.Count == 0 && normalizedHillClimbing.Count > 0)
        {
            orderedWorkerSamples = new List<CountSample>(normalizedHillClimbing.Count);
            var syntheticWorkerCount = 0;
            foreach (var sample in normalizedHillClimbing)
            {
                syntheticWorkerCount = sample.NewCount
                    ?? (string.Equals(sample.Reason, "ThreadTimedOut", StringComparison.OrdinalIgnoreCase)
                        ? Math.Max(0, syntheticWorkerCount - 1)
                        : syntheticWorkerCount + 1);
                orderedWorkerSamples.Add(new CountSample(sample.Timestamp, syntheticWorkerCount));
            }

            notes.TryAdd("Worker thread timeline was inferred from hill-climbing transitions because per-event worker counts were unavailable.", 0);
        }
        if (effectiveSettings is null)
        {
            notes.TryAdd("Effective MinThreads/MaxThreads unavailable from the EventPipe-only ThreadPool collector. Use collect_thread_snapshot(view=\"threadpool\") when a ptrace-backed snapshot is acceptable.", 0);
        }

        var orderedNotes = notes.Keys.OrderBy(static note => note, StringComparer.Ordinal).ToList();

        if (normalizedHillClimbing.Count == 0)
        {
            orderedNotes.Add("No ThreadPool hill-climbing events were observed during the window. Increase duration or schedule the starvation workload after the collection starts.");
            if (!observedEventNames.IsEmpty)
            {
                orderedNotes.Add($"Observed ThreadingKeyword events: {string.Join(", ", observedEventNames.Keys.OrderBy(static name => name, StringComparer.Ordinal))}.");
            }
        }

        if (workItemOrigins.IsEmpty && Volatile.Read(ref totalEnqueueEvents) > 0)
        {
            orderedNotes.Add("ThreadPool enqueue events were observed, but no managed call stacks were available for origin attribution.");
        }

        return new ThreadPoolEventSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            WorkerThreadTimeline: Bucketize(orderedWorkerSamples, startedAt, duration),
            IocpThreadTimeline: Bucketize(orderedIocpSamples, startedAt, duration),
            HillClimbing: normalizedHillClimbing,
            WorkItemOrigins: workItemOrigins
                .Select(static kvp => new ThreadPoolWorkItemOrigin(kvp.Key, kvp.Value))
                .OrderByDescending(static origin => origin.Count)
                .ThenBy(static origin => origin.Method, StringComparer.Ordinal)
                .ToList(),
            EffectiveSettings: effectiveSettings,
            TotalEnqueueEvents: Volatile.Read(ref totalEnqueueEvents),
            TotalDequeueEvents: Volatile.Read(ref totalDequeueEvents),
            Notes: orderedNotes);
    }

    private static string GetCanonicalEventName(TraceEvent traceEvent)
        => GetCanonicalEventName((int)traceEvent.ID, traceEvent.EventName);

    internal static string GetCanonicalEventName(int eventId, string? eventName)
    {
        if (RuntimeEventIds.TryGetValue(eventId, out var mapped))
        {
            return mapped;
        }

        if (!string.IsNullOrWhiteSpace(eventName)
            && !eventName.StartsWith("EventID(", StringComparison.Ordinal)
            && !string.Equals(eventName, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeEventName(eventName);
        }

        return NormalizeEventName(eventName);
    }

    private static string NormalizeEventName(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(eventName.Length);
        foreach (var ch in eventName)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static List<ThreadPoolHillClimbingSample> NormalizeHillClimbing(
        List<ThreadPoolHillClimbingSample> samples,
        List<CountSample> workerSamples,
        DateTimeOffset startedAt,
        ConcurrentDictionary<string, byte> notes)
    {
        if (samples.Count == 0)
        {
            return samples;
        }

        var normalized = new List<ThreadPoolHillClimbingSample>(samples.Count);
        var inferredAny = false;
        var syntheticWorkerCount = workerSamples.Count > 0 ? workerSamples[0].Count : 0;
        var beforeIndex = -1;
        var afterIndex = 0;
        foreach (var sample in samples)
        {
            while (beforeIndex + 1 < workerSamples.Count && workerSamples[beforeIndex + 1].Timestamp <= sample.Timestamp)
            {
                beforeIndex++;
            }

            if (afterIndex < beforeIndex)
            {
                afterIndex = beforeIndex;
            }

            while (afterIndex < workerSamples.Count && workerSamples[afterIndex].Timestamp < sample.Timestamp)
            {
                afterIndex++;
            }

            var oldCount = sample.OldCount ?? ResolvePreviousWorkerCount(workerSamples, beforeIndex);
            var newCount = sample.NewCount ?? ResolveNextWorkerCount(workerSamples, afterIndex);
            var reason = sample.Reason;
            if (string.Equals(reason, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                if (oldCount is int oldWorkerCount && newCount is int newWorkerCount)
                {
                    if (newWorkerCount > oldWorkerCount)
                    {
                        reason = sample.Timestamp - startedAt <= TimeSpan.FromSeconds(1)
                            ? "Warmup"
                            : "Starvation";
                        inferredAny = true;
                    }
                    else if (newWorkerCount < oldWorkerCount)
                    {
                        reason = "ThreadTimedOut";
                        inferredAny = true;
                    }
                }
                else
                {
                    oldCount = syntheticWorkerCount;
                    newCount = syntheticWorkerCount + 1;
                    syntheticWorkerCount = newCount.Value;
                    reason = sample.Timestamp - startedAt <= TimeSpan.FromSeconds(1)
                        ? "Warmup"
                        : "Starvation";
                    inferredAny = true;
                }
            }
            else if (newCount is int concreteNewCount)
            {
                syntheticWorkerCount = concreteNewCount;
            }

            normalized.Add(sample with
            {
                Reason = reason,
                OldCount = oldCount,
                NewCount = newCount,
            });
        }

        if (inferredAny)
        {
            notes.TryAdd("ThreadPool hill-climbing reasons were inferred from worker-count transitions because the runtime manifest did not expose named adjustment reasons on this platform.", 0);
        }

        return normalized;
    }

    private static int? ResolvePreviousWorkerCount(IReadOnlyList<CountSample> workerSamples, int beforeIndex)
    {
        if (workerSamples.Count == 0)
        {
            return null;
        }

        return beforeIndex >= 0
            ? workerSamples[beforeIndex].Count
            : workerSamples[0].Count;
    }

    private static int? ResolveNextWorkerCount(IReadOnlyList<CountSample> workerSamples, int afterIndex)
    {
        if (workerSamples.Count == 0)
        {
            return null;
        }

        return afterIndex < workerSamples.Count
            ? workerSamples[afterIndex].Count
            : workerSamples[^1].Count;
    }

    private static bool TryReadWorkerCount(TraceEvent traceEvent, out int workerCount)
    {
        workerCount = 0;
        if (TryReadInt(traceEvent, out var total, "NewWorkerThreadCount", "WorkerThreadCount", "ThreadCount", "NumThreads"))
        {
            workerCount = total;
            return true;
        }

        if ((TryReadInt(traceEvent, out var active, "ActiveWorkerThreadCount", "ActiveThreads") || TryReadIntByIndex(traceEvent, 0, out active))
            && (TryReadInt(traceEvent, out var retired, "RetiredWorkerThreadCount", "RetiredThreads") || TryReadIntByIndex(traceEvent, 1, out retired)))
        {
            workerCount = Math.Max(0, active + retired);
            return true;
        }

        return false;
    }

    private static bool TryReadIocpCount(TraceEvent traceEvent, int? lastIocpCount, out int iocpCount)
    {
        iocpCount = 0;
        if (TryReadInt(traceEvent, out var total, "IOThreadCount", "NumIOThreads", "ThreadCount", "NumThreads"))
        {
            iocpCount = total;
            return true;
        }

        var hasActive = TryReadInt(traceEvent, out var active, "ActiveThreads") || TryReadIntByIndex(traceEvent, 0, out active);
        var hasRetired = TryReadInt(traceEvent, out var retired, "RetiredThreads") || TryReadIntByIndex(traceEvent, 1, out retired);
        var hasWorking = TryReadInt(traceEvent, out var working, "WorkingThreads") || TryReadIntByIndex(traceEvent, 2, out working);
        if (hasActive || hasRetired || hasWorking)
        {
            iocpCount = Math.Max(active + retired, working);
            if (lastIocpCount is int previous)
            {
                iocpCount = Math.Max(previous + 1, iocpCount);
            }

            return true;
        }

        if (lastIocpCount is int fallback)
        {
            iocpCount = fallback + 1;
            return true;
        }

        return false;
    }

    private static string ResolveAdjustmentReason(TraceEvent traceEvent)
    {
        if (TryReadString(traceEvent, out var reasonText, "Reason") && !string.IsNullOrWhiteSpace(reasonText))
        {
            return NormalizeAdjustmentReason(reasonText);
        }

        if ((TryReadInt(traceEvent, out var reason, "AdjustmentReason", "Reason")
                || TryReadIntByIndex(traceEvent, 0, out reason))
            && AdjustmentReasons.TryGetValue(reason, out var mapped))
        {
            return mapped;
        }

        return "Unknown";
    }

    internal static string NormalizeAdjustmentReason(string reason)
        => int.TryParse(reason, out var numericReason)
            && AdjustmentReasons.TryGetValue(numericReason, out var mapped)
                ? mapped
                : reason;

    private static string? ExtractWorkItemOrigin(TraceEvent traceEvent, ConcurrentDictionary<string, byte> notes)
    {
        TraceCallStack? stack;
        try
        {
            stack = traceEvent.CallStack();
        }
        catch (InvalidOperationException)
        {
            // A live EventPipeEventSource is not TraceLog-backed, so CallStack() throws. Swallowing
            // here keeps ThreadPool event processing alive — otherwise the exception propagates out
            // of the Dynamic.All callback and tears down source.Process() on the first enqueue event.
            notes.TryAdd("ThreadPool work-item origins require a TraceLog-backed session; origins are unavailable.", 0);
            return null;
        }

        if (stack is null)
        {
            notes.TryAdd("ThreadPoolEnqueueWork did not carry call stacks in this session; work-item origins are unavailable.", 0);
            return null;
        }

        string? fallback = null;
        var frame = stack;
        while (frame is not null)
        {
            var candidate = FormatFrame(frame);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                fallback ??= candidate;
                if (!IsInfrastructureFrame(candidate))
                {
                    return candidate;
                }
            }

            frame = frame.Caller;
        }

        return fallback;
    }

    private static string? FormatFrame(Microsoft.Diagnostics.Tracing.Etlx.TraceCallStack frame)
    {
        if (!string.IsNullOrWhiteSpace(frame.CodeAddress?.FullMethodName))
        {
            return frame.CodeAddress.FullMethodName;
        }

        if (!string.IsNullOrWhiteSpace(frame.CodeAddress?.Method?.FullMethodName))
        {
            return frame.CodeAddress.Method.FullMethodName;
        }

        var asText = frame.ToString();
        return string.IsNullOrWhiteSpace(asText) ? null : asText.Trim();
    }

    private static bool IsInfrastructureFrame(string frame)
        => frame.StartsWith("System.Threading.ThreadPool", StringComparison.Ordinal)
            || frame.Contains("ThreadPoolWorkQueue", StringComparison.Ordinal)
            || frame.Contains("PortableThreadPool", StringComparison.Ordinal)
            || frame.Contains("ExecutionContext.Run", StringComparison.Ordinal)
            || frame.Contains("QueueUserWorkItem", StringComparison.Ordinal)
            || frame.Contains("Task.Execute", StringComparison.Ordinal)
            || frame.Contains("Task.ScheduleAndStart", StringComparison.Ordinal)
            || frame.Contains("AwaitTaskContinuation", StringComparison.Ordinal);

    private static bool TryReadEffectiveSettings(TraceEvent traceEvent, out ThreadPoolEffectiveSettings settings)
    {
        if ((TryReadInt(traceEvent, out var workerMin, "MinWorkerThreads") || TryReadIntByIndex(traceEvent, 0, out workerMin))
            && (TryReadInt(traceEvent, out var workerMax, "MaxWorkerThreads") || TryReadIntByIndex(traceEvent, 1, out workerMax))
            && (TryReadInt(traceEvent, out var iocpMin, "MinIoCompletionThreads", "MinIOCompletionThreads") || TryReadIntByIndex(traceEvent, 2, out iocpMin))
            && (TryReadInt(traceEvent, out var iocpMax, "MaxIoCompletionThreads", "MaxIOCompletionThreads") || TryReadIntByIndex(traceEvent, 3, out iocpMax)))
        {
            settings = new ThreadPoolEffectiveSettings(workerMin, workerMax, iocpMin, iocpMax);
            return true;
        }

        settings = default!;
        return false;
    }

    private static IReadOnlyList<ThreadPoolCountBucket> Bucketize(
        IReadOnlyList<CountSample> ordered,
        DateTimeOffset startedAt,
        TimeSpan duration)
    {
        if (ordered.Count == 0)
        {
            return Array.Empty<ThreadPoolCountBucket>();
        }

        var bucketCount = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        var buckets = new List<ThreadPoolCountBucket>(bucketCount);
        var sampleIndex = 0;
        var lastSeen = 0;

        for (var i = 0; i < bucketCount; i++)
        {
            var bucketEnd = startedAt.AddSeconds(i + 1);
            while (sampleIndex < ordered.Count && ordered[sampleIndex].Timestamp <= bucketEnd)
            {
                lastSeen = ordered[sampleIndex].Count;
                sampleIndex++;
            }

            buckets.Add(new ThreadPoolCountBucket(startedAt.AddSeconds(i), lastSeen));
        }

        return buckets;
    }

    private static bool TryReadInt(TraceEvent traceEvent, out int value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryReadPayload(traceEvent, name, out var raw) || raw is null)
            {
                continue;
            }

            if (raw is int intValue)
            {
                value = intValue;
                return true;
            }

            if (raw is long longValue && longValue is >= int.MinValue and <= int.MaxValue)
            {
                value = (int)longValue;
                return true;
            }

            if (raw is uint uintValue && uintValue <= int.MaxValue)
            {
                value = (int)uintValue;
                return true;
            }

            if (raw is short shortValue)
            {
                value = shortValue;
                return true;
            }

            if (raw is ushort ushortValue)
            {
                value = ushortValue;
                return true;
            }

            if (raw is byte byteValue)
            {
                value = byteValue;
                return true;
            }

            if (raw is string stringValue && int.TryParse(stringValue, out var parsed))
            {
                value = parsed;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryReadIntByIndex(TraceEvent traceEvent, int index, out int value)
    {
        try
        {
            var raw = traceEvent.PayloadValue(index);
            if (raw is int intValue)
            {
                value = intValue;
                return true;
            }

            if (raw is long longValue && longValue is >= int.MinValue and <= int.MaxValue)
            {
                value = (int)longValue;
                return true;
            }

            if (raw is uint uintValue && uintValue <= int.MaxValue)
            {
                value = (int)uintValue;
                return true;
            }

            if (raw is short shortValue)
            {
                value = shortValue;
                return true;
            }

            if (raw is ushort ushortValue)
            {
                value = ushortValue;
                return true;
            }

            if (raw is byte byteValue)
            {
                value = byteValue;
                return true;
            }

            if (raw is string stringValue && int.TryParse(stringValue, out var parsed))
            {
                value = parsed;
                return true;
            }
        }
        catch (Exception)
        {
        }

        value = 0;
        return false;
    }

    private static bool TryReadDoubleByIndex(TraceEvent traceEvent, int index, out double value)
    {
        try
        {
            var raw = traceEvent.PayloadValue(index);
            if (raw is double doubleValue)
            {
                value = doubleValue;
                return true;
            }

            if (raw is float floatValue)
            {
                value = floatValue;
                return true;
            }

            if (raw is string stringValue && double.TryParse(stringValue, out var parsed))
            {
                value = parsed;
                return true;
            }
        }
        catch (Exception)
        {
        }

        value = 0;
        return false;
    }

    private static bool TryReadDouble(TraceEvent traceEvent, out double value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryReadPayload(traceEvent, name, out var raw) || raw is null)
            {
                continue;
            }

            if (raw is double doubleValue)
            {
                value = doubleValue;
                return true;
            }

            if (raw is float floatValue)
            {
                value = floatValue;
                return true;
            }

            if (raw is string stringValue && double.TryParse(stringValue, out var parsed))
            {
                value = parsed;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryReadString(TraceEvent traceEvent, out string? value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryReadPayload(traceEvent, name, out var raw) || raw is null)
            {
                continue;
            }

            value = raw.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    private static bool TryReadPayload(TraceEvent traceEvent, string name, out object? value)
    {
        value = null;
        try
        {
            value = traceEvent.PayloadByName(name);
            if (value is not null)
            {
                return true;
            }
        }
        catch (Exception)
        {
        }

        var payloadNames = traceEvent.PayloadNames;
        if (payloadNames is null)
        {
            return false;
        }

        var alternate = payloadNames.FirstOrDefault(payloadName =>
            string.Equals(payloadName, name, StringComparison.OrdinalIgnoreCase));
        if (alternate is null)
        {
            return false;
        }

        try
        {
            value = traceEvent.PayloadByName(alternate);
            return value is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal sealed class FixedCapacityQueue<T>
    {
        private readonly int _capacity;
        private readonly Queue<T> _items;

        public FixedCapacityQueue(int capacity)
        {
            _capacity = capacity;
            _items = new Queue<T>(capacity);
        }

        public int DroppedCount { get; private set; }

        public IReadOnlyCollection<T> Items => _items;

        public void Enqueue(T item)
        {
            if (_items.Count >= _capacity)
            {
                _items.Dequeue();
                DroppedCount++;
            }

            _items.Enqueue(item);
        }
    }

    private readonly record struct CountSample(DateTimeOffset Timestamp, int Count);
}
