using System.Diagnostics.Tracing;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Contention;

public sealed class EventPipeContentionCollector : IContentionCollector, IStreamingContentionCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long ContentionKeyword = 0x4000;
    private const string UnknownCallSite = "(unknown)";
    private const string UnknownModule = "(unknown)";
    internal const int MaxTrackedEvents = 200;
    private const int MaxTrackedMonitorIds = 4096;
    private const long MaxTraceBytes = 64L * 1024 * 1024;

    private readonly ILogger<EventPipeContentionCollector> _logger;

    public EventPipeContentionCollector(ILogger<EventPipeContentionCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeContentionCollector>.Instance;
    }

    public Task<ContentionSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
        => CollectCoreAsync(processId, duration, null, cancellationToken);

    public Task<ContentionSnapshot> CollectStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<ContentionEventSample> onObservation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onObservation);
        return CollectCoreAsync(processId, duration, onObservation, cancellationToken);
    }

    private async Task<ContentionSnapshot> CollectCoreAsync(
        int processId,
        TimeSpan duration,
        Action<ContentionEventSample>? onObservation,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var tracePath = Path.Combine(
            Path.GetTempPath(),
            $"diagnosticsmcp-contention-{processId}-{Guid.NewGuid():N}.nettrace");
        string? etlxPath = null;

        try
        {
            await CollectTraceAsync(processId, tracePath, duration, cancellationToken).ConfigureAwait(false);
            etlxPath = TraceLog.CreateFromEventPipeDataFile(tracePath);
            return ParseTrace(
                processId,
                startedAt,
                duration,
                etlxPath,
                onObservation,
                cancellationToken);
        }
        finally
        {
            TryDelete(tracePath);
            if (etlxPath is not null)
            {
                TryDelete(etlxPath);
            }
        }
    }

    private async Task CollectTraceAsync(
        int processId,
        string tracePath,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, ContentionKeyword),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(
                providers,
                requestRundown: true,
                circularBufferMB: 64,
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
        var copyTask = Task.Run(async () =>
        {
            await using var output = File.Create(tracePath);
            await CopyBoundedTraceAsync(session.EventStream, output, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

        try
        {
            var delay = Task.Delay(duration, cancellationToken);
            var completed = await Task.WhenAny(delay, copyTask).ConfigureAwait(false);
            if (completed == copyTask)
            {
                await copyTask.ConfigureAwait(false);
                if (!delay.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "The contention EventPipe stream ended before the requested capture window completed.");
                }
            }
            else
            {
                await delay.ConfigureAwait(false);
            }
        }
        finally
        {
            await EventPipeSessionShutdown.StopAndDrainAsync(
                session,
                copyTask,
                ex => _logger.LogDebug(ex, "Stopping contention EventPipe session for pid {Pid} failed.", processId),
                propagateProcessingErrors: true)
                .ConfigureAwait(false);
        }
    }

    private static ContentionSnapshot ParseTrace(
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        string etlxPath,
        Action<ContentionEventSample>? onObservation,
        CancellationToken cancellationToken)
    {
        var notes = new HashSet<string>(StringComparer.Ordinal);
        var recording = CaptureRecordingContext.Current;
        var topEvents = new TopContentionEvents(MaxTrackedEvents, recording, onObservation);
        var pendingByThread = new Dictionary<int, Stack<PendingContention>>();
        var durations = new BoundedDurationSampler();
        var distinctMonitorIds = new HashSet<ulong>();
        var distinctMonitorOverflow = 0;
        var totalDuration = TimeSpan.Zero;
        var totalEvents = 0;

        using (var traceLog = new TraceLog(etlxPath))
        {
            EventPipeCollectionRunner.ReportSourceLoss(recording, traceLog.EventsLost);
            if (traceLog.EventsLost > 0)
            {
                notes.Add($"The contention trace reports {traceLog.EventsLost} lost event(s); counts and start/stop correlation may be incomplete.");
            }

            var source = traceLog.Events.GetSource();
            source.Clr.ContentionStart += data =>
            {
                if (data.ProcessID != processId)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var pending = new PendingContention(
                    StartedAt: ToUtcOffset(data.TimeStamp),
                    LockId: data.LockID,
                    AssociatedObjectId: data.AssociatedObjectID,
                    OwnerManagedThreadId: data.LockOwnerThreadID > 0 && data.LockOwnerThreadID <= int.MaxValue
                        ? (int)data.LockOwnerThreadID
                        : null,
                    CallSite: ExtractCallSite(data, notes));

                if (!pendingByThread.TryGetValue(data.ThreadID, out var stack))
                {
                    stack = new Stack<PendingContention>();
                    pendingByThread[data.ThreadID] = stack;
                }

                stack.Push(pending);
            };

            source.Clr.ContentionStop += data =>
            {
                if (data.ProcessID != processId)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!pendingByThread.TryGetValue(data.ThreadID, out var stack) || stack.Count == 0)
                {
                    notes.Add("Observed ContentionStop without a matching ContentionStart; the event was skipped.");
                    return;
                }

                var pending = stack.Pop();
                var stoppedAt = ToUtcOffset(data.TimeStamp);
                var durationFromStop = data.DurationNs > 0
                    ? TimeSpan.FromMilliseconds(data.DurationNs / 1_000_000d)
                    : stoppedAt - pending.StartedAt;
                var waitDuration = durationFromStop >= TimeSpan.Zero ? durationFromStop : TimeSpan.Zero;

                totalEvents++;
                totalDuration += waitDuration;
                durations.Add(waitDuration);

                if (pending.LockId != 0)
                {
                    if (distinctMonitorIds.Count < MaxTrackedMonitorIds || distinctMonitorIds.Contains(pending.LockId))
                    {
                        distinctMonitorIds.Add(pending.LockId);
                    }
                    else
                    {
                        distinctMonitorOverflow++;
                    }
                }

                var sample = new ContentionEventSample(
                    StartedAt: pending.StartedAt,
                    StoppedAt: stoppedAt,
                    Duration: waitDuration,
                    ContendingThreadId: data.ThreadID,
                    OwnerManagedThreadId: pending.OwnerManagedThreadId,
                    LockId: pending.LockId,
                    AssociatedObjectId: pending.AssociatedObjectId,
                    CallSiteMethod: pending.CallSite.Method,
                    CallSiteModule: pending.CallSite.Module);
                topEvents.Add(sample, durationFromStop >= TimeSpan.Zero,
                    data.DurationNs > 0 ? "runtime-stop-duration" : "matched-wall-clock-boundaries");
            };

            source.Process();
        }

        var unfinishedStarts = pendingByThread.Sum(static entry => entry.Value.Count);
        if (unfinishedStarts > 0)
        {
            notes.Add($"{unfinishedStarts} ContentionStart event(s) did not observe a matching stop before the window ended.");
        }

        var orderedEvents = topEvents.GetOrdered();

        if (totalEvents == 0)
        {
            if (OperatingSystem.IsLinux())
            {
                notes.Add("No lock contention events were observed in the collection window. Current Linux runtimes may not emit ContentionStart/Stop over EventPipe even when monitor-lock-contention counters rise.");
            }
            else
            {
                notes.Add("No lock contention events were observed in the collection window.");
            }
        }

        if (topEvents.DroppedCount > 0)
        {
            notes.Add($"Retained the {orderedEvents.Count} longest contention event(s) after reaching the in-memory cap of {MaxTrackedEvents}; {topEvents.DroppedCount} shorter event(s) were dropped.");
        }

        if (durations.IsApproximate)
        {
            notes.Add($"Contention duration percentiles are exact up to {BoundedPercentileSampler.ExactSampleCapacity} samples and become reservoir-sampled approximations above that cap; max remains exact.");
        }

        if (distinctMonitorOverflow > 0)
        {
            notes.Add($"Tracked up to {MaxTrackedMonitorIds} distinct monitor ids; {distinctMonitorOverflow} additional contention event(s) used untracked monitor ids, so DistinctMonitors is a lower bound.");
        }

        return new ContentionSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalEvents: totalEvents,
            DistinctMonitors: distinctMonitorIds.Count,
            TotalContentionDuration: totalDuration,
            P50ContentionDuration: durations.GetPercentile(0.50),
            P95ContentionDuration: durations.GetPercentile(0.95),
            MaxContentionDuration: durations.Max,
            Events: orderedEvents,
            Notes: notes.OrderBy(static note => note, StringComparer.Ordinal).ToList());
    }

    private static async Task CopyBoundedTraceAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (read > MaxTraceBytes - written)
            {
                throw new InvalidOperationException(
                    $"Contention capture reached MaxTraceBytes={MaxTraceBytes}; shorten the capture. No partial result is returned.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of temporary trace files.
        }
    }

    private static DateTimeOffset ToUtcOffset(DateTime timestamp) =>
        new(timestamp.ToUniversalTime(), TimeSpan.Zero);

    private static CallSite ExtractCallSite(ContentionStartTraceData data, HashSet<string> notes)
    {
        TraceCallStack? stack;
        try
        {
            stack = data.CallStack();
        }
        catch (InvalidOperationException)
        {
            notes.Add("ContentionStart call-stack metadata could not be read; byCallSite falls back to '(unknown)'.");
            return new CallSite(UnknownCallSite, UnknownModule);
        }

        if (stack is null)
        {
            notes.Add("ContentionStart did not carry call stacks in this session; byCallSite falls back to '(unknown)'.");
            return new CallSite(UnknownCallSite, UnknownModule);
        }

        CallSite? fallback = null;
        var frame = stack;
        while (frame is not null)
        {
            var candidate = FormatCallSite(frame);
            if (candidate is not null)
            {
                fallback ??= candidate;
                if (!IsInfrastructureFrame(candidate.Method))
                {
                    return ReportUnknownComponents(candidate, notes);
                }
            }

            frame = frame.Caller;
        }

        if (fallback is null)
        {
            notes.Add("ContentionStart call stacks contained no resolvable method frames; byCallSite falls back to '(unknown)'.");
            return new CallSite(UnknownCallSite, UnknownModule);
        }

        return ReportUnknownComponents(fallback, notes);
    }

    private static CallSite ReportUnknownComponents(CallSite callSite, HashSet<string> notes)
    {
        if (string.Equals(callSite.Module, UnknownModule, StringComparison.Ordinal))
        {
            notes.Add("ContentionStart method metadata was resolved, but module metadata was unavailable; CallSiteModule falls back to '(unknown)'.");
        }

        return callSite;
    }

    private static CallSite? FormatCallSite(TraceCallStack frame)
    {
        var method = frame.CodeAddress?.FullMethodName;
        if (string.IsNullOrWhiteSpace(method))
        {
            method = frame.CodeAddress?.Method?.FullMethodName;
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            method = frame.ToString();
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            return null;
        }

        var module = frame.CodeAddress?.ModuleName;
        if (string.IsNullOrWhiteSpace(module))
        {
            module = frame.CodeAddress?.Method?.MethodModuleFile?.Name;
        }

        if (string.IsNullOrWhiteSpace(module))
        {
            module = frame.CodeAddress?.ModuleFile?.Name;
        }

        return new CallSite(method.Trim(), string.IsNullOrWhiteSpace(module) ? UnknownModule : Path.GetFileName(module.Trim()));
    }

    private static bool IsInfrastructureFrame(string frame)
        => frame.StartsWith("System.Threading.Monitor", StringComparison.Ordinal)
            || frame.StartsWith("System.Threading.Lock", StringComparison.Ordinal)
            || frame.Contains("System.Private.CoreLib!System.Threading.Monitor", StringComparison.Ordinal)
            || frame.Contains("AwareLock", StringComparison.Ordinal)
            || frame.Contains("ExecutionContext.Run", StringComparison.Ordinal)
            || frame.Contains("ThreadPoolWorkQueue", StringComparison.Ordinal)
            || frame.Contains("PortableThreadPool", StringComparison.Ordinal)
            || frame.Contains("Task.Execute", StringComparison.Ordinal)
            || frame.Contains("AwaitTaskContinuation", StringComparison.Ordinal);

    private sealed record PendingContention(
        DateTimeOffset StartedAt,
        ulong LockId,
        ulong AssociatedObjectId,
        int? OwnerManagedThreadId,
        CallSite CallSite);

    private sealed record CallSite(string Method, string Module);

    internal sealed class TopContentionEvents
    {
        private readonly int _capacity;
        private readonly List<ContentionEventSample> _events;
        private readonly ICaptureObservationSink? _sink;
        private readonly Action<ContentionEventSample>? _onObservation;

        public TopContentionEvents(int capacity, ICaptureObservationSink? sink = null,
            Action<ContentionEventSample>? onObservation = null)
        {
            _capacity = capacity;
            _events = new List<ContentionEventSample>(capacity);
            _sink = sink;
            _onObservation = onObservation;
        }

        public int DroppedCount { get; private set; }

        public void Add(ContentionEventSample sample, bool validDuration = true, string durationSource = "matched-wall-clock-boundaries")
        {
            _onObservation?.Invoke(sample);
            if (_sink is not null) RuntimeObservationProjection.Contention(_sink, sample, validDuration, durationSource);
            if (_events.Count < _capacity)
            {
                _events.Add(sample);
                return;
            }

            var shortestIndex = 0;
            for (var i = 1; i < _events.Count; i++)
            {
                if (_events[i].Duration < _events[shortestIndex].Duration)
                {
                    shortestIndex = i;
                }
            }

            if (_events[shortestIndex].Duration >= sample.Duration)
            {
                DroppedCount++;
                return;
            }

            _events[shortestIndex] = sample;
            DroppedCount++;
        }

        public IReadOnlyList<ContentionEventSample> GetOrdered() => _events
            .OrderByDescending(static item => item.Duration)
            .ThenBy(static item => item.CallSiteMethod, StringComparer.Ordinal)
            .ToList();
    }
}
