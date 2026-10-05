using System.ComponentModel;
using System.Diagnostics;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Counters;

/// <summary>
/// Owns a live EventPipe counter session, its bounded observation queue, and deterministic
/// cancellation and shutdown.
/// </summary>
public sealed class CounterSession : IAsyncDisposable
{
    private readonly EventPipeSession _eventPipeSession;
    private readonly CounterObservationBuffer _observations;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly CancellationTokenRegistration _ownerCancellationRegistration;
    private readonly DateTimeOffset _startedAt;
    private readonly Action<Exception> _onShutdownError;
    private readonly Task<ProcessResult> _processingTask;
    private readonly Task<CounterSessionCompletion> _completionTask;
    private readonly object _lifecycleLock = new();
    private bool _stopRequested;
    private bool _finished;

    internal CounterSession(
        int processId,
        EventPipeSession eventPipeSession,
        int observationCapacity,
        Action<Exception> onShutdownError,
        CancellationToken ownerCancellationToken)
    {
        ProcessId = processId;
        _eventPipeSession = eventPipeSession;
        _observations = new CounterObservationBuffer(observationCapacity);
        _startedAt = DateTimeOffset.UtcNow;
        _onShutdownError = onShutdownError;
        _processingTask = Task.Run(ProcessEvents);
        _ownerCancellationRegistration = ownerCancellationToken.Register(
            static state => ((CounterSession)state!).RequestStop(),
            this);
        _completionTask = RunToCompletionAsync();
    }

    /// <summary>Process ID attached to the session.</summary>
    public int ProcessId { get; }

    /// <summary>Reads sequenced observations until the session terminates.</summary>
    public IAsyncEnumerable<CounterObservation> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _observations.ReadAllAsync(cancellationToken);

    /// <summary>Completes when the stream terminates and EventPipe shutdown/drain has finished.</summary>
    public Task<CounterSessionCompletion> Completion => _completionTask;

    /// <summary>Total observations dropped because the bounded consumer queue was full.</summary>
    public long DroppedObservations => _observations.DroppedObservations;

    /// <summary>Stops the session once and waits for EventPipe shutdown and drain to finish.</summary>
    public async Task<CounterSessionCompletion> StopAsync()
    {
        RequestStop();
        return await _completionTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void RequestStop()
    {
        lock (_lifecycleLock)
        {
            if (_finished || _stopRequested)
            {
                return;
            }

            _stopRequested = true;
            _stopSource.Cancel();
        }
    }

    private async Task<CounterSessionCompletion> RunToCompletionAsync()
    {
        Exception? shutdownError = null;
        try
        {
            var stopSignal = Task.Delay(Timeout.InfiniteTimeSpan, _stopSource.Token);
            await Task.WhenAny(_processingTask, stopSignal).ConfigureAwait(false);

            try
            {
                await EventPipeSessionShutdown.StopAndDrainAsync(
                    _eventPipeSession,
                    _processingTask,
                    ex =>
                    {
                        shutdownError ??= ex;
                        _onShutdownError(ex);
                    },
                    propagateProcessingErrors: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                shutdownError ??= ex;
            }

            var processing = await _processingTask.ConfigureAwait(false);
            bool stopRequested;
            lock (_lifecycleLock)
            {
                stopRequested = _stopRequested;
            }

            var error = processing.Error ?? shutdownError;
            var targetAlive = true;
            if (!stopRequested)
            {
                targetAlive = IsTargetAlive(out var targetInspectionError);
                error ??= targetInspectionError;
            }

            var status = DetermineStatus(stopRequested, targetAlive, processing.Error, shutdownError);
            if (status == CounterSessionStatus.Failed && error is null)
            {
                error = new InvalidOperationException(
                    $"The EventPipe counter stream for process {ProcessId} ended while the target process was still running.");
            }
            else if (status == CounterSessionStatus.Stopped && processing.Error is IOException)
            {
                error = null;
            }

            return new CounterSessionCompletion(
                status,
                _startedAt,
                DateTimeOffset.UtcNow,
                processing.EventPipeEventsLost,
                _observations.DroppedObservations,
                error);
        }
        finally
        {
            _ownerCancellationRegistration.Dispose();
            lock (_lifecycleLock)
            {
                _finished = true;
                _stopSource.Dispose();
            }
            _observations.Complete();
        }
    }

    private ProcessResult ProcessEvents()
    {
        long? eventsLost = null;
        Exception? error = null;
        try
        {
            using var source = new EventPipeEventSource(_eventPipeSession.EventStream);
            source.Dynamic.All += traceEvent =>
            {
                if (!string.Equals(traceEvent.EventName, "EventCounters", StringComparison.Ordinal))
                {
                    return;
                }

                var value = EventPipeCounterCollector.ExtractCounterPayload(traceEvent, out _);
                if (value is not null)
                {
                    _observations.TryPublish(
                        new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime()),
                        value with { Provider = traceEvent.ProviderName });
                }
            };
            source.Process();
            eventsLost = source.EventsLost;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return new ProcessResult(eventsLost, error);
    }

    private bool IsTargetAlive(out Exception? inspectionError)
    {
        inspectionError = null;
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            inspectionError = ex;
            return true;
        }
    }

    internal static CounterSessionStatus DetermineStatus(
        bool stopRequested,
        bool targetAlive,
        Exception? processingError,
        Exception? shutdownError)
    {
        if (stopRequested)
        {
            return shutdownError is null && (processingError is null or IOException)
                ? CounterSessionStatus.Stopped
                : CounterSessionStatus.Failed;
        }

        return targetAlive
            ? CounterSessionStatus.Failed
            : CounterSessionStatus.TargetExited;
    }

    private sealed record ProcessResult(long? EventPipeEventsLost, Exception? Error);
}
