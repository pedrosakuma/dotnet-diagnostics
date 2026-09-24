using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Security;
using System.Globalization;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Exceptions;

/// <summary>
/// EventPipe crash guard that records ExceptionThrown_V1 plus unhandled/fail-fast runtime events
/// and returns early when the target process exits during the guard window.
/// </summary>
public sealed class EventPipeCrashGuardCollector : ICrashGuardCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long ExceptionKeyword = 0x8000;
    private const long StackKeyword = 0x40000000;
    private static readonly char[] StackLineSeparators = ['\r', '\n'];
    internal const int RecordedStackFrameLimit = 128;

    private readonly ILogger<EventPipeCrashGuardCollector> _logger;
    internal EventPipeProvider? ReadinessProvider { get; init; }
    internal Action<EventPipeEventSource>? ConfigureReadiness { get; init; }
    internal Action<CrashGuardExceptionEvent>? ExceptionObserved { get; init; }
    internal Task? ObservationWindowEnded { get; init; }

    public EventPipeCrashGuardCollector(ILogger<EventPipeCrashGuardCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeCrashGuardCollector>.Instance;
    }

    public async Task<CrashGuardSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxRecent = 100,
        CancellationToken cancellationToken = default)
    {
        var observationSink = CaptureRecordingContext.Current;
        var observationRedactor = observationSink is null ? null : new SensitiveDataRedactor();
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (maxRecent < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRecent), "maxRecent must be >= 1.");
        }

        using var process = TryGetProcess(processId);
        var providers = new List<EventPipeProvider>
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, ExceptionKeyword | StackKeyword),
        };
        if (ReadinessProvider is not null) providers.Add(ReadinessProvider);

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var exceptions = new List<CrashGuardExceptionEvent>(Math.Min(maxRecent, 128));
        var counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var notes = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var unhandledObserved = 0;
        var total = 0;
        var gate = new object();
        CrashGuardExceptionEvent? lastObservedException = null;
        CrashGuardExceptionEvent? explicitUnhandledException = null;
        long? eventsLost = null;
        string? processingError = null;
        var streamCompleted = false;
        var drainCompleted = false;
        string? shutdownError = null;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                ConfigureReadiness?.Invoke(source);
                source.Clr.ExceptionStart += traceEvent =>
                {
                    var captured = CaptureException(traceEvent, "ExceptionThrown_V1", isUnhandled: false, notes);
                    if (observationSink is not null)
                        RecordCrashObservation(observationSink, captured.Timestamp, captured.ThreadId, captured.EventName,
                            false, traceEvent.ExceptionType, traceEvent.ExceptionMessage, captured.ExceptionHResult,
                            captured.ManagedStack, observationRedactor!);
                    RecordException(captured, exceptions, counts, gate, maxRecent, ref total, ref lastObservedException, ref explicitUnhandledException);
                    ExceptionObserved?.Invoke(captured);
                };

                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.ProviderName, RuntimeProvider, StringComparison.Ordinal))
                    {
                        return;
                    }

                    var eventName = EventName(traceEvent);
                    if (!IsUnhandledRuntimeEvent(eventName))
                    {
                        return;
                    }

                    Interlocked.Exchange(ref unhandledObserved, 1);
                    var captured = CaptureDynamicException(traceEvent, eventName, notes);
                    if (observationSink is not null)
                        RecordCrashObservation(observationSink, ToUtc(traceEvent.TimeStamp), traceEvent.ThreadID, eventName,
                            true, TryReadPayloadString(traceEvent, "ExceptionType", "ExceptionTypeName", "TypeName", "Exception"),
                            TryReadPayloadString(traceEvent, "ExceptionMessage", "Message", "ExceptionMessageText"),
                            TryReadPayloadString(traceEvent, "ExceptionHRESULT", "HResult"), captured?.ManagedStack, observationRedactor!);
                    if (captured is not null)
                    {
                        RecordException(captured, exceptions, counts, gate, maxRecent, ref total, ref lastObservedException, ref explicitUnhandledException);
                    }
                    else
                    {
                        notes.TryAdd($"Observed runtime crash event '{eventName}' without exception payload.", 0);
                    }
                };

                source.Process();
                lock (gate)
                {
                    eventsLost = source.EventsLost;
                    streamCompleted = true;
                }
            }
            catch (Exception ex)
            {
                lock (gate) processingError = ex.GetType().Name;
                _logger.LogDebug(ex, "EventPipe crash-guard source ended for pid {Pid}.", processId);
            }
        }, cancellationToken);

        var targetEndedDuringWindow = false;
        using var exitWaitCts = new CancellationTokenSource();
        Task? exitTask = null;
        try
        {
            // Tests can control this boundary without assuming a runtime's crash/dump latency.
            var delayTask = ObservationWindowEnded?.WaitAsync(cancellationToken) ?? Task.Delay(duration, cancellationToken);
            exitTask = process is null
                ? Task.Delay(Timeout.InfiniteTimeSpan, exitWaitCts.Token)
                : process.WaitForExitAsync(exitWaitCts.Token);
            var completed = await Task.WhenAny(delayTask, exitTask).ConfigureAwait(false);
            targetEndedDuringWindow = completed == exitTask;
            if (completed == delayTask)
            {
                await delayTask.ConfigureAwait(false);
            }
        }
        finally
        {
            if (!targetEndedDuringWindow)
            {
                await exitWaitCts.CancelAsync().ConfigureAwait(false);
                if (exitTask is not null)
                {
                    try { await exitTask.ConfigureAwait(false); } catch (Exception) { }
                }
                await EventPipeSessionShutdown.StopSessionAsync(
                    session,
                    ex =>
                    {
                        shutdownError = ex.GetType().Name;
                        _logger.LogDebug(ex, "Stopping crash-guard EventPipe session for pid {Pid} failed.", processId);
                    })
                    .ConfigureAwait(false);
            }
            try
            {
                var drainTask = Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                await (await Task.WhenAny(processingTask, drainTask).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception) { }
            drainCompleted = processingTask.IsCompleted;
            if (!drainCompleted)
                notes.TryAdd("Crash-guard reader did not finish within the bounded drain; stream coverage is unknown.", 0);
            session.Dispose();
        }

        var processExited = HasExited(process) || (!IsProcessAlive(processId) && processingTask.IsCompleted);
        var exitCode = TryReadExitCode(process);
        List<CrashGuardExceptionEvent> capturedExceptions;
        CrashGuardExceptionEvent? lastObserved;
        CrashGuardExceptionEvent? explicitUnhandled;
        CrashGuardObservation observation;
        lock (gate)
        {
            capturedExceptions = exceptions.ToList();
            lastObserved = lastObservedException;
            explicitUnhandled = explicitUnhandledException;
            observation = new(streamCompleted, eventsLost, processingError,
                Interlocked.CompareExchange(ref unhandledObserved, 0, 0) == 1,
                targetEndedDuringWindow, lastObserved)
            {
                DrainCompleted = drainCompleted,
                ShutdownError = shutdownError,
            };
        }

        var finalEvidence = CrashGuardFinalEvidence.Resolve(processExited, exitCode,
            observation.ExplicitCrashEventObserved, explicitUnhandled, lastObserved);
        var finalException = finalEvidence.FinalException;
        if (finalEvidence.InferredFromExit)
        {
            notes.TryAdd("Target process exited non-zero during the guard window; the last observed exception is treated as the final unhandled exception.", 0);
        }

        if (finalException is not null)
        {
            EnsureRetainedFinalException(capturedExceptions, finalException, maxRecent);
        }

        var byType = counts
            .Select(kvp => new ExceptionCount(kvp.Key, kvp.Value))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.ExceptionType, StringComparer.Ordinal)
            .ToList();

        var snapshot = new CrashGuardSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: DateTimeOffset.UtcNow - startedAt,
            ProcessExited: processExited,
            ExitCode: exitCode,
            UnhandledExceptionObserved: finalEvidence.UnhandledObserved,
            TotalExceptions: Volatile.Read(ref total),
            ByType: byType,
            Exceptions: capturedExceptions,
            FinalException: finalException,
            Notes: notes.Keys.OrderBy(static note => note, StringComparer.Ordinal).ToList())
        { RecentCap = maxRecent, Observation = observation };
        RecordRetainedEvidence(observationSink, snapshot, finalEvidence.InferredFromExit);
        return snapshot;
    }

    internal static void RecordRetainedEvidence(ICaptureObservationSink? sink, CrashGuardSnapshot snapshot, bool inferredFromExit)
    {
        if (sink is null) return;
        var redactor = new SensitiveDataRedactor();
        sink.ReportSourceLoss(RuntimeProvider, snapshot.Observation?.EventsLost);
        sink.TryAppend(ProviderObservationProjection.Create(
            "crashguard.retention.aggregate", null, null, "crashguard",
            ("provider", RuntimeProvider), ("processExited", snapshot.ProcessExited), ("exitCode", snapshot.ExitCode),
            ("totalExceptions", snapshot.TotalExceptions), ("retainedExceptions", snapshot.Exceptions.Count),
            ("recentCap", snapshot.RecentCap), ("finalInferredFromExit", inferredFromExit),
            ("unhandledExceptionObserved", snapshot.UnhandledExceptionObserved),
            ("streamCompleted", snapshot.Observation?.StreamCompleted), ("drainCompleted", snapshot.Observation?.DrainCompleted),
            ("eventsLost", snapshot.Observation?.EventsLost), ("processingError", snapshot.Observation?.ProcessingError),
            ("shutdownError", snapshot.Observation?.ShutdownError),
            ("explicitCrashEventObserved", snapshot.Observation?.ExplicitCrashEventObserved),
            ("exitObservedDuringWindow", snapshot.Observation?.ExitObservedDuringWindow)));
        for (var index = 0; index < snapshot.Exceptions.Count; index++)
            RecordRetainedException(sink, snapshot.Exceptions[index], index, "retained", redactor);
        if (snapshot.FinalException is { } final)
            RecordRetainedException(sink, final, -1, inferredFromExit ? "final-inferred-from-exit" : "final-evidence", redactor);
        if (snapshot.Observation?.LastObservedException is { } last)
            RecordRetainedException(sink, last, -2, "last-observed-not-proof-of-termination", redactor);
    }

    internal static void RecordCrashObservation(ICaptureObservationSink sink, DateTimeOffset timestamp, int threadId,
        string eventName, bool isUnhandled, string? type, string? message, string? hresult,
        IReadOnlyList<string>? stack, SensitiveDataRedactor redactor)
    {
        var retained = Math.Min(stack?.Count ?? 0, RecordedStackFrameLimit);
        var fields = new List<CaptureObservationField>(retained + 9)
        {
            CaptureObservationField.String("provider", RuntimeProvider),
            CaptureObservationField.String("exceptionType", redactor.Redact(type)),
            CaptureObservationField.String("message", redactor.Redact(message)),
            CaptureObservationField.String("hResult", redactor.Redact(hresult)),
            CaptureObservationField.Bool("explicitUnhandledEvent", isUnhandled),
            CaptureObservationField.String("interpretation", "First-chance exceptions do not establish process termination."),
            CaptureObservationField.Int64("omittedStackFrames", (stack?.Count ?? 0) - retained),
            CaptureObservationField.Int64("stackFrameLimit", RecordedStackFrameLimit),
            CaptureObservationField.Bool("stackAvailable", stack is not null),
        };
        for (var i = 0; i < retained; i++)
            fields.Add(CaptureObservationField.String("stack." + i.ToString(CultureInfo.InvariantCulture), redactor.Redact(stack![i])));
        sink.TryAppend(new CaptureObservation("crashguard.exception.observed", timestamp, threadId, eventName, fields));
    }

    private static void RecordRetainedException(ICaptureObservationSink sink, CrashGuardExceptionEvent exception, int index,
        string role, SensitiveDataRedactor redactor)
    {
        var retained = Math.Min(exception.ManagedStack.Count, RecordedStackFrameLimit);
        sink.TryAppend(ProviderObservationProjection.Create(
            "crashguard.retained-evidence", exception.Timestamp, exception.ThreadId, exception.EventName,
            ("provider", RuntimeProvider), ("exceptionType", redactor.Redact(exception.ExceptionType)),
            ("exceptionMessage", redactor.Redact(exception.ExceptionMessage)), ("hResult", redactor.Redact(exception.ExceptionHResult)),
            ("isUnhandled", exception.IsUnhandled), ("role", role), ("evidenceIndex", index),
            ("managedFrameCount", exception.ManagedStack.Count), ("omittedStackFrames", exception.ManagedStack.Count - retained),
            ("stackFrameLimit", RecordedStackFrameLimit)));
        for (var frame = 0; frame < retained; frame++)
            sink.TryAppend(ProviderObservationProjection.Create(
                "crashguard.retained-frame", exception.Timestamp, exception.ThreadId, redactor.Redact(exception.ManagedStack[frame]),
                ("provider", RuntimeProvider), ("evidenceIndex", index), ("role", role), ("frameIndex", frame)));
    }

    private static void RecordException(
        CrashGuardExceptionEvent captured,
        List<CrashGuardExceptionEvent> exceptions,
        ConcurrentDictionary<string, int> counts,
        object gate,
        int maxRecent,
        ref int total,
        ref CrashGuardExceptionEvent? lastObservedException,
        ref CrashGuardExceptionEvent? explicitUnhandledException)
    {
        Interlocked.Increment(ref total);
        counts.AddOrUpdate(captured.ExceptionType, 1, static (_, value) => value + 1);
        lock (gate)
        {
            lastObservedException = captured;
            if (captured.IsUnhandled)
            {
                explicitUnhandledException = captured;
            }

            if (exceptions.Count < maxRecent)
            {
                exceptions.Add(captured);
            }
            else if (captured.IsUnhandled && exceptions.Count > 0)
            {
                exceptions[^1] = captured;
            }
        }
    }

    private static void EnsureRetainedFinalException(
        List<CrashGuardExceptionEvent> capturedExceptions,
        CrashGuardExceptionEvent finalException,
        int maxRecent)
    {
        for (var i = 0; i < capturedExceptions.Count; i++)
        {
            if (SameExceptionEvent(capturedExceptions[i], finalException))
            {
                capturedExceptions[i] = finalException;
                return;
            }
        }

        if (capturedExceptions.Count < maxRecent)
        {
            capturedExceptions.Add(finalException);
        }
        else if (capturedExceptions.Count > 0)
        {
            capturedExceptions[^1] = finalException;
        }
    }

    private static bool SameExceptionEvent(CrashGuardExceptionEvent left, CrashGuardExceptionEvent right)
        => left.Timestamp == right.Timestamp
            && left.ThreadId == right.ThreadId
            && string.Equals(left.EventName, right.EventName, StringComparison.Ordinal)
            && string.Equals(left.ExceptionType, right.ExceptionType, StringComparison.Ordinal)
            && string.Equals(left.ExceptionMessage, right.ExceptionMessage, StringComparison.Ordinal);

    private static CrashGuardExceptionEvent CaptureException(
        ExceptionTraceData traceEvent,
        string eventName,
        bool isUnhandled,
        ConcurrentDictionary<string, byte> notes)
    {
        var message = traceEvent.ExceptionMessage ?? string.Empty;
        var stack = ExtractManagedStack(traceEvent, notes);
        if (stack.Count == 0)
        {
            stack = SplitStack(message);
        }

        return new CrashGuardExceptionEvent(
            Timestamp: ToUtc(traceEvent.TimeStamp),
            ExceptionType: string.IsNullOrWhiteSpace(traceEvent.ExceptionType) ? "(unknown)" : traceEvent.ExceptionType,
            ExceptionMessage: message,
            ExceptionHResult: "0x" + traceEvent.ExceptionHRESULT.ToString("X", CultureInfo.InvariantCulture),
            ThreadId: traceEvent.ThreadID,
            EventName: eventName,
            IsUnhandled: isUnhandled,
            ManagedStack: stack);
    }

    private static CrashGuardExceptionEvent? CaptureDynamicException(
        TraceEvent traceEvent,
        string eventName,
        ConcurrentDictionary<string, byte> notes)
    {
        var type = TryReadPayloadString(traceEvent, "ExceptionType", "ExceptionTypeName", "TypeName", "Exception")
            ?? "(unknown)";
        var message = TryReadPayloadString(traceEvent, "ExceptionMessage", "Message", "ExceptionMessageText")
            ?? string.Empty;
        var hresult = TryReadPayloadString(traceEvent, "ExceptionHRESULT", "HResult")
            ?? string.Empty;
        var stack = TryReadPayloadString(traceEvent, "StackTrace", "ExceptionStackTrace", "ExceptionToString");
        var managedStack = SplitStack(stack);
        if (managedStack.Count == 0)
        {
            managedStack = ExtractManagedStack(traceEvent, notes);
        }

        if (type == "(unknown)" && string.IsNullOrWhiteSpace(message) && managedStack.Count == 0)
        {
            return null;
        }

        return new CrashGuardExceptionEvent(
            Timestamp: ToUtc(traceEvent.TimeStamp),
            ExceptionType: type,
            ExceptionMessage: message,
            ExceptionHResult: string.IsNullOrWhiteSpace(hresult) ? "0x0" : hresult,
            ThreadId: traceEvent.ThreadID,
            EventName: eventName,
            IsUnhandled: true,
            ManagedStack: managedStack);
    }

    private static IReadOnlyList<string> ExtractManagedStack(
        TraceEvent traceEvent,
        ConcurrentDictionary<string, byte> notes)
    {
        TraceCallStack? stack;
        try
        {
            stack = traceEvent.CallStack();
        }
        catch (InvalidOperationException)
        {
            notes.TryAdd("Runtime exception events did not expose TraceLog-backed call stacks in this live EventPipe session.", 0);
            return Array.Empty<string>();
        }

        if (stack is null)
        {
            notes.TryAdd("Runtime exception events did not carry managed call stacks in this session.", 0);
            return Array.Empty<string>();
        }

        var frames = new List<string>();
        var frame = stack;
        while (frame is not null)
        {
            var formatted = FormatFrame(frame);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                frames.Add(formatted);
            }

            frame = frame.Caller;
        }

        return frames;
    }

    private static string? FormatFrame(TraceCallStack frame)
    {
        if (!string.IsNullOrWhiteSpace(frame.CodeAddress?.FullMethodName))
        {
            return frame.CodeAddress.FullMethodName;
        }

        if (!string.IsNullOrWhiteSpace(frame.CodeAddress?.Method?.FullMethodName))
        {
            return frame.CodeAddress.Method.FullMethodName;
        }

        var text = frame.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? TryReadPayloadString(TraceEvent traceEvent, params string[] names)
    {
        foreach (var name in names)
        {
            var index = Array.FindIndex(traceEvent.PayloadNames, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            var value = traceEvent.PayloadValue(index);
            if (value is not null)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static IReadOnlyList<string> SplitStack(string? stack)
        => string.IsNullOrWhiteSpace(stack)
            ? Array.Empty<string>()
            : stack.Split(StackLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => line.StartsWith("at ", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                .ToList();

    private static bool IsUnhandledRuntimeEvent(string eventName)
        => eventName.Contains("Unhandled", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("FailFast", StringComparison.OrdinalIgnoreCase);

    private static string EventName(TraceEvent traceEvent)
        => !string.IsNullOrWhiteSpace(traceEvent.EventName)
            ? traceEvent.EventName
            : (!string.IsNullOrWhiteSpace(traceEvent.TaskName) ? traceEvent.TaskName : traceEvent.ID.ToString());

    private static DateTimeOffset ToUtc(DateTime timestamp)
        => new(timestamp.ToUniversalTime(), TimeSpan.Zero);

    private static Process? TryGetProcess(int processId)
    {
        try { return Process.GetProcessById(processId); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    private static bool HasExited(Process? process)
    {
        if (process is null)
        {
            return false;
        }

        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static int? TryReadExitCode(Process? process)
    {
        if (!HasExited(process))
        {
            return null;
        }

        try { return process!.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool IsProcessAlive(int processId)
    {
        using var process = TryGetProcess(processId);
        if (process is null)
        {
            return false;
        }

        return !HasExited(process);
    }
}
