using System.ComponentModel;
using System.Diagnostics;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Counters;

/// <summary>
/// Shared lifecycle machinery for a Core-owned live EventPipe session: handler attachment,
/// start/stop/cancellation, bounded sequencing and dispatch, and terminal status classification.
/// Subclasses supply only the EventPipe provider parsing via <see cref="ConfigureSource"/>.
/// </summary>
public abstract class EventPipeDiagnosticSessionBase : IDiagnosticSession
{
    private static readonly TimeSpan ShutdownWaitBudget = TimeSpan.FromSeconds(5);

    private readonly Func<CancellationToken, Task<EventPipeSession>> _startEventPipeSession;
    private readonly Action<Exception> _onShutdownError;
    private readonly DiagnosticSessionEventBuffer _observations;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly CancellationTokenSource _handlerCancellationSource = new();
    private Task _stopCancellationTask = Task.CompletedTask;
    private readonly object _lifecycleLock = new();
    private readonly List<HandlerRegistration> _handlers = [];
    private readonly TaskCompletionSource<DiagnosticSessionCompletion> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _stopCancellationStarting;
    private EventPipeSession? _eventPipeSession;
    private CancellationTokenRegistration _ownerCancellationRegistration;
    private Task? _startTask;
    private Task<ProcessResult>? _processingTask;
    private Task? _dispatchTask;
    private DateTimeOffset _startedAt;
    private Exception? _dispatchError;
    private bool _stopRequested;
    private bool _started;
    private bool _finished;

    private protected EventPipeDiagnosticSessionBase(
        int processId,
        int observationCapacity,
        Func<CancellationToken, Task<EventPipeSession>> startEventPipeSession,
        Action<Exception> onShutdownError)
    {
        ProcessId = processId;
        _startEventPipeSession = startEventPipeSession;
        _onShutdownError = onShutdownError;
        _observations = new DiagnosticSessionEventBuffer(observationCapacity);
    }

    /// <summary>Process ID attached to the session.</summary>
    public int ProcessId { get; }

    /// <summary>Completes when the session has stopped and all queued events have been dispatched.</summary>
    public Task<DiagnosticSessionCompletion> Completion => _completion.Task;

    /// <summary>Total events dropped because the bounded dispatch queue was full.</summary>
    public long DroppedObservations => _observations.DroppedObservations;

    /// <summary>
    /// Attaches an asynchronous handler for one event type. Attach handlers before starting the
    /// session. Disposing the returned registration detaches only that handler.
    /// </summary>
    public IDisposable Attach<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : DiagnosticSessionEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lifecycleLock)
        {
            if (_started || _startTask is not null || _finished)
            {
                throw new InvalidOperationException("Session handlers must be attached before the session starts.");
            }

            var registration = new HandlerRegistration(
                typeof(TEvent),
                (sessionEvent, cancellationToken) => handler((TEvent)sessionEvent, cancellationToken));
            _handlers.Add(registration);
            return new HandlerSubscription(this, registration);
        }
    }

    /// <summary>Starts EventPipe after handlers have been attached.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_finished || _stopRequested)
            {
                throw new InvalidOperationException("A stopped session cannot be started.");
            }

            if (_handlers.Count == 0)
            {
                throw new InvalidOperationException("Attach at least one event handler before starting the session.");
            }

            return _startTask ??= Task.Run(
                () => StartCoreAsync(cancellationToken),
                CancellationToken.None);
        }
    }

    /// <summary>Stops the session once and waits for EventPipe shutdown and queued handler delivery.</summary>
    public async Task<DiagnosticSessionCompletion> StopAsync()
    {
        Task? startTask;
        var completeWithoutStart = false;
        var cancelSources = false;
        lock (_lifecycleLock)
        {
            if (_finished)
            {
                startTask = null;
            }
            else
            {
                startTask = _startTask;
                if (startTask is null)
                {
                    _stopRequested = true;
                    _finished = true;
                    _observations.Complete();
                    _stopSource.Dispose();
                    _handlerCancellationSource.Dispose();
                    var now = DateTimeOffset.UtcNow;
                    _completion.TrySetResult(new DiagnosticSessionCompletion(
                        DiagnosticSessionStatus.Stopped,
                        now,
                        now,
                        null,
                        _observations.DroppedObservations,
                        null));
                    completeWithoutStart = true;
                }
                else
                {
                    cancelSources = RequestStopLocked();
                }
            }
        }

        if (cancelSources)
        {
            StartStopCancellation();
        }

        if (completeWithoutStart || startTask is null)
        {
            return await _completion.Task.ConfigureAwait(false);
        }

        try
        {
            await startTask.ConfigureAwait(false);
        }
        catch
        {
            // Startup failure is captured in Completion and remains observable to StartAsync.
        }

        return await _completion.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Subscribes the live <see cref="EventPipeEventSource"/> to the provider(s) this session
    /// cares about, publishing typed observations into <paramref name="observations"/> as events
    /// arrive. Invoked once on the single source-processing thread before <c>source.Process()</c>.
    /// </summary>
    private protected abstract void ConfigureSource(EventPipeEventSource source, DiagnosticSessionEventBuffer observations);

    private async Task StartCoreAsync(CancellationToken ownerCancellationToken)
    {
        _ownerCancellationRegistration = ownerCancellationToken.Register(
            static state => ((EventPipeDiagnosticSessionBase)state!).RequestStop(),
            this);

        try
        {
            ownerCancellationToken.ThrowIfCancellationRequested();
            _eventPipeSession = await _startEventPipeSession(_stopSource.Token).ConfigureAwait(false);
            _startedAt = DateTimeOffset.UtcNow;
            lock (_lifecycleLock)
            {
                _started = true;
            }

            _processingTask = Task.Run(ProcessEvents);
            _dispatchTask = DispatchEventsAsync();
            _ = RunToCompletionAsync();
        }
        catch (Exception ex)
        {
            _ownerCancellationRegistration.Dispose();
            _observations.Complete();
            var completionError = ex;
            lock (_lifecycleLock)
            {
                _finished = true;
            }

            try
            {
                await AwaitStopCancellationAsync().ConfigureAwait(false);
            }
            catch (Exception cancellationError)
            {
                completionError = new AggregateException(ex, cancellationError);
            }

            _stopSource.Dispose();
            _handlerCancellationSource.Dispose();

            var status = completionError == ex && ex is OperationCanceledException && _stopRequested
                ? DiagnosticSessionStatus.Stopped
                : DiagnosticSessionStatus.Failed;
            var now = DateTimeOffset.UtcNow;
            _completion.TrySetResult(new DiagnosticSessionCompletion(
                status,
                now,
                now,
                null,
                _observations.DroppedObservations,
                status == DiagnosticSessionStatus.Failed ? completionError : null));
            throw;
        }
    }

    private void RequestStop()
    {
        var cancelSources = false;
        lock (_lifecycleLock)
        {
            cancelSources = RequestStopLocked();
        }

        if (cancelSources)
        {
            StartStopCancellation();
        }
    }

    private bool RequestStopLocked()
    {
        if (_finished || _stopRequested)
        {
            return false;
        }

        _stopRequested = true;
        _stopCancellationStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }

    private void StartStopCancellation()
    {
        Task cancellationTask;
        try
        {
            _stopSource.Cancel();
            cancellationTask = Task.CompletedTask;
        }
        catch (Exception ex)
        {
            cancellationTask = Task.FromException(ex);
        }

        lock (_lifecycleLock)
        {
            _stopCancellationTask = cancellationTask;
            _stopCancellationStarting?.TrySetResult();
        }
    }

    private async Task AwaitStopCancellationAsync()
    {
        TaskCompletionSource? starting;
        lock (_lifecycleLock)
        {
            starting = _stopCancellationStarting;
        }

        if (starting is not null)
        {
            await starting.Task.ConfigureAwait(false);
        }

        Task cancellationTask;
        lock (_lifecycleLock)
        {
            cancellationTask = _stopCancellationTask;
        }

        await cancellationTask.ConfigureAwait(false);
    }

    private async Task RunToCompletionAsync()
    {
        Exception? shutdownError = null;
        long? eventsLost = null;
        var processingTask = _processingTask!;
        var dispatchTask = _dispatchTask!;

        try
        {
            var stopSignal = Task.Delay(Timeout.InfiniteTimeSpan, _stopSource.Token);
            await Task.WhenAny(processingTask, stopSignal).ConfigureAwait(false);

            try
            {
                await EventPipeSessionShutdown.StopAndDrainAsync(
                    _eventPipeSession!,
                    processingTask,
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

            ProcessResult? processing = null;
            try
            {
                processing = await processingTask.WaitAsync(ShutdownWaitBudget).ConfigureAwait(false);
                eventsLost = processing.EventPipeEventsLost;
            }
            catch (TimeoutException ex)
            {
                shutdownError ??= ex;
            }

            _observations.Complete();
            try
            {
                await dispatchTask.WaitAsync(ShutdownWaitBudget).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                shutdownError ??= new TimeoutException(
                    $"Diagnostic session handlers did not finish within {ShutdownWaitBudget.TotalSeconds:0.#} seconds.",
                    ex);
                _handlerCancellationSource.Cancel();
                ObserveLater(dispatchTask);
            }

            bool stopRequested;
            lock (_lifecycleLock)
            {
                stopRequested = _stopRequested;
                _finished = true;
            }

            try
            {
                await AwaitStopCancellationAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                shutdownError ??= ex;
            }

            var targetAlive = true;
            Exception? targetInspectionError = null;
            if (!stopRequested)
            {
                targetAlive = IsTargetAlive(out targetInspectionError);
            }

            var error = _dispatchError ?? processing?.Error ?? shutdownError ?? targetInspectionError;
            var status = DetermineStatus(
                stopRequested,
                targetAlive,
                processing?.Error,
                shutdownError,
                _dispatchError);
            if (status == DiagnosticSessionStatus.Failed)
            {
                error ??= new InvalidOperationException(
                    $"The EventPipe stream for process {ProcessId} ended while the target process was still running.");
            }
            else
            {
                error = null;
            }

            _completion.TrySetResult(new DiagnosticSessionCompletion(
                status,
                _startedAt,
                DateTimeOffset.UtcNow,
                eventsLost,
                _observations.DroppedObservations,
                error));
        }
        finally
        {
            _ownerCancellationRegistration.Dispose();
            lock (_lifecycleLock)
            {
                _stopSource.Dispose();
                _handlerCancellationSource.Dispose();
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
            using var source = new EventPipeEventSource(_eventPipeSession!.EventStream);
            ConfigureSource(source, _observations);
            source.Process();
            eventsLost = source.EventsLost;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return new ProcessResult(eventsLost, error);
    }

    private async Task DispatchEventsAsync()
    {
        await foreach (var sessionEvent in _observations.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            HandlerRegistration[] handlers;
            lock (_lifecycleLock)
            {
                handlers = _handlers.Where(handler => handler.EventType.IsInstanceOfType(sessionEvent)).ToArray();
            }

            foreach (var handler in handlers)
            {
                if (_dispatchError is not null)
                {
                    break;
                }

                try
                {
                    await handler.InvokeAsync(sessionEvent, _handlerCancellationSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_handlerCancellationSource.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _dispatchError = ex;
                    RequestStop();
                    break;
                }
            }
        }
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

    internal static DiagnosticSessionStatus DetermineStatus(
        bool stopRequested,
        bool targetAlive,
        Exception? processingError,
        Exception? shutdownError,
        Exception? dispatchError = null)
    {
        if (dispatchError is not null)
        {
            return DiagnosticSessionStatus.Failed;
        }

        if (stopRequested)
        {
            return shutdownError is null && (processingError is null or IOException)
                ? DiagnosticSessionStatus.Stopped
                : DiagnosticSessionStatus.Failed;
        }

        return targetAlive ? DiagnosticSessionStatus.Failed : DiagnosticSessionStatus.TargetExited;
    }

    private void Detach(HandlerRegistration registration)
    {
        lock (_lifecycleLock)
        {
            _handlers.Remove(registration);
        }
    }

    private static void ObserveLater(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record HandlerRegistration(
        Type EventType,
        Func<DiagnosticSessionEvent, CancellationToken, ValueTask> InvokeAsync);

    private sealed class HandlerSubscription(EventPipeDiagnosticSessionBase session, HandlerRegistration registration) : IDisposable
    {
        private EventPipeDiagnosticSessionBase? _session = session;

        public void Dispose() => Interlocked.Exchange(ref _session, null)?.Detach(registration);
    }

    private sealed record ProcessResult(long? EventPipeEventsLost, Exception? Error);
}
