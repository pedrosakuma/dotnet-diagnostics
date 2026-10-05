namespace DotnetDiagnostics.Core.Counters;

/// <summary>
/// Combines multiple live sessions and finite captures behind one typed callback dispatcher.
/// </summary>
public sealed class ComposedDiagnosticSession : IDiagnosticSession
{
    private static readonly TimeSpan DispatchWaitBudget = TimeSpan.FromSeconds(5);

    private readonly DiagnosticSessionEventBuffer _events;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly CancellationTokenSource _handlerCancellationSource = new();
    private Task _stopCancellationTask = Task.CompletedTask;
    private readonly object _lifecycleLock = new();
    private readonly List<HandlerRegistration> _handlers = [];
    private readonly List<Func<CancellationToken, Action, Task<DiagnosticSessionCompletion?>>> _sources = [];
    private readonly List<DiagnosticSessionCompletion> _sourceCompletions = [];
    private readonly TaskCompletionSource<DiagnosticSessionCompletion> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _stopCancellationStarting;
    private Task? _startTask;
    private Task? _runTask;
    private Task? _dispatchTask;
    private DateTimeOffset _startedAt;
    private Exception? _sourceError;
    private Exception? _dispatchError;
    private bool _stopRequested;
    private bool _finished;

    /// <summary>Creates an empty session for captures targeting one process.</summary>
    public ComposedDiagnosticSession(int processId, int eventCapacity = 256)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process ID must be positive.");
        }

        if (eventCapacity is < 1 or > CounterSessionOptions.MaxAllowedObservationCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventCapacity),
                $"Event capacity must be between 1 and {CounterSessionOptions.MaxAllowedObservationCapacity}.");
        }

        ProcessId = processId;
        _events = new DiagnosticSessionEventBuffer(eventCapacity);
    }

    /// <inheritdoc />
    public int ProcessId { get; }

    /// <inheritdoc />
    public Task<DiagnosticSessionCompletion> Completion => _completion.Task;

    /// <summary>Total events dropped because the bounded dispatch queue was full.</summary>
    public long DroppedEvents => _events.DroppedObservations;

    /// <summary>
    /// Attaches an asynchronous handler for one event type. Handlers are invoked serially in
    /// attachment order and must be attached before the session starts.
    /// </summary>
    public IDisposable Attach<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : DiagnosticSessionEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lifecycleLock)
        {
            EnsureConfigurable();
            var registration = new HandlerRegistration(
                typeof(TEvent),
                (sessionEvent, cancellationToken) => handler((TEvent)sessionEvent, cancellationToken));
            _handlers.Add(registration);
            return new HandlerSubscription(this, registration);
        }
    }

    /// <summary>Adds a live session as one source in this composed session.</summary>
    public void AddSession(IDiagnosticSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.ProcessId != ProcessId)
        {
            throw new ArgumentException("All composed session sources must target the same process.", nameof(session));
        }

        lock (_lifecycleLock)
        {
            EnsureConfigurable();
            _sources.Add((cancellationToken, markStarted) =>
                RunChildSessionAsync(session, markStarted, cancellationToken));
        }
    }

    /// <summary>
    /// Adds a finite capture. Its statically typed result is delivered once as
    /// <see cref="DiagnosticSessionCaptureResult{TCapture}"/>.
    /// </summary>
    public void AddCapture<TCapture>(Func<CancellationToken, Task<TCapture>> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_lifecycleLock)
        {
            EnsureConfigurable();
            _sources.Add(async (cancellationToken, markStarted) =>
            {
                var captureTask = capture(cancellationToken);
                markStarted();
                var result = await captureTask.ConfigureAwait(false);
                _events.TryPublish(sequence => new DiagnosticSessionCaptureResult<TCapture>(
                    sequence,
                    DateTimeOffset.UtcNow,
                    result));
                return null;
            });
        }
    }

    /// <summary>
    /// Adds a finite capture that publishes typed observations while it runs and its typed
    /// terminal result when it completes.
    /// </summary>
    public void AddStreamingCapture<TObservation, TCapture>(
        Func<Action<TObservation>, CancellationToken, Task<TCapture>> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        AddEventCapture((publish, cancellationToken) => capture(
            observation => publish(new DiagnosticSessionObservation<TObservation>(
                0,
                DateTimeOffset.UtcNow,
                observation)),
            cancellationToken));
    }

    /// <summary>
    /// Adds a finite capture that publishes one or more session event types while it runs and its
    /// typed terminal result when it completes.
    /// </summary>
    public void AddEventCapture<TCapture>(
        Func<Action<DiagnosticSessionEvent>, CancellationToken, Task<TCapture>> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_lifecycleLock)
        {
            EnsureConfigurable();
            _sources.Add(async (cancellationToken, markStarted) =>
            {
                void Publish(DiagnosticSessionEvent observation)
                {
                    ArgumentNullException.ThrowIfNull(observation);
                    var unsequenced = observation.WithSequence(0);
                    _events.TryPublish(sequence => unsequenced.WithSequence(sequence));
                }

                var captureTask = capture(Publish, cancellationToken);
                markStarted();
                var result = await captureTask.ConfigureAwait(false);
                _events.TryPublish(sequence => new DiagnosticSessionCaptureResult<TCapture>(
                    sequence,
                    DateTimeOffset.UtcNow,
                    result));
                return null;
            });
        }
    }

    /// <inheritdoc />
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

            if (_sources.Count == 0)
            {
                throw new InvalidOperationException("Add at least one session or capture source before starting the session.");
            }

            if (_runTask is null)
            {
                var sourceStarted = _sources
                    .Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                    .ToArray();
                _startTask = Task.WhenAll(sourceStarted.Select(static source => source.Task));
                _runTask = Task.Run(
                    () => RunAsync(sourceStarted, cancellationToken),
                    CancellationToken.None);
            }

            return _startTask!;
        }
    }

    /// <inheritdoc />
    public async Task<DiagnosticSessionCompletion> StopAsync()
    {
        Task? runTask;
        var stoppedBeforeStart = false;
        var cancelSources = false;
        lock (_lifecycleLock)
        {
            if (_finished)
            {
                runTask = null;
            }
            else
            {
                runTask = _runTask;
                if (runTask is null)
                {
                    _stopRequested = true;
                    _finished = true;
                    _events.Complete();
                    stoppedBeforeStart = true;
                    var now = DateTimeOffset.UtcNow;
                    _completion.TrySetResult(new DiagnosticSessionCompletion(
                        DiagnosticSessionStatus.Stopped,
                        now,
                        now,
                        null,
                        _events.DroppedObservations,
                        null));
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

        if (runTask is not null)
        {
            await runTask.ConfigureAwait(false);
        }
        else if (stoppedBeforeStart)
        {
            try
            {
                await _stopCancellationTask.ConfigureAwait(false);
            }
            finally
            {
                _stopSource.Dispose();
                _handlerCancellationSource.Dispose();
            }
        }

        return await _completion.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(
        TaskCompletionSource[] sourceStarted,
        CancellationToken ownerCancellationToken)
    {
        _startedAt = DateTimeOffset.UtcNow;
        using var ownerRegistration = ownerCancellationToken.Register(
            static state => ((ComposedDiagnosticSession)state!).RequestStop(),
            this);
        _dispatchTask = DispatchEventsAsync();

        var sourceTasks = _sources
            .Select((source, index) => RunSourceAsync(source, sourceStarted[index], _stopSource.Token))
            .ToArray();
        await Task.WhenAll(sourceTasks).ConfigureAwait(false);
        _events.Complete();

        try
        {
            await _dispatchTask.WaitAsync(DispatchWaitBudget, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _dispatchError ??= new TimeoutException(
                $"Diagnostic session handlers did not finish within {DispatchWaitBudget.TotalSeconds:0.#} seconds.",
                ex);
            _handlerCancellationSource.Cancel();
            ObserveLater(_dispatchTask);
        }

        var sourceCompletions = _sourceCompletions.ToArray();
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
            _sourceError ??= ex;
        }

        var childFailure = sourceCompletions.FirstOrDefault(
            completion => completion.Status == DiagnosticSessionStatus.Failed);
        var error = _dispatchError ?? _sourceError ?? childFailure?.Error;
        var status = error is not null
            ? DiagnosticSessionStatus.Failed
            : stopRequested
                ? DiagnosticSessionStatus.Stopped
                : sourceCompletions.Any(completion => completion.Status == DiagnosticSessionStatus.TargetExited)
                    ? DiagnosticSessionStatus.TargetExited
                    : DiagnosticSessionStatus.Completed;
        var knownEventLosses = sourceCompletions
            .Where(completion => completion.EventPipeEventsLost.HasValue)
            .Sum(completion => completion.EventPipeEventsLost!.Value);

        _completion.TrySetResult(new DiagnosticSessionCompletion(
            status,
            _startedAt,
            DateTimeOffset.UtcNow,
            sourceCompletions.Any(completion => completion.EventPipeEventsLost.HasValue)
                ? knownEventLosses
                : null,
            _events.DroppedObservations,
            error));

        _stopSource.Dispose();
        _handlerCancellationSource.Dispose();
    }

    private async Task RunSourceAsync(
        Func<CancellationToken, Action, Task<DiagnosticSessionCompletion?>> source,
        TaskCompletionSource sourceStarted,
        CancellationToken cancellationToken)
    {
        void MarkStarted() => sourceStarted.TrySetResult();
        try
        {
            var completion = await source(cancellationToken, MarkStarted).ConfigureAwait(false);
            MarkStarted();
            if (completion is not null)
            {
                lock (_lifecycleLock)
                {
                    _sourceCompletions.Add(completion);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sourceStarted.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            sourceStarted.TrySetException(ex);
            var cancelSources = false;
            lock (_lifecycleLock)
            {
                _sourceError ??= ex;
                cancelSources = RequestStopLocked();
            }

            if (cancelSources)
            {
                StartStopCancellation();
            }
        }
    }

    private async Task<DiagnosticSessionCompletion?> RunChildSessionAsync(
        IDiagnosticSession session,
        Action markStarted,
        CancellationToken cancellationToken)
    {
        using var subscription = session.Attach<DiagnosticSessionEvent>((sessionEvent, _) =>
        {
            _events.TryPublish(sequence => sessionEvent.WithSequence(sequence));
            return ValueTask.CompletedTask;
        });

        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            markStarted();
            var completion = await session.Completion.ConfigureAwait(false);
            if (completion.Status == DiagnosticSessionStatus.Failed)
            {
                throw completion.Error ?? new InvalidOperationException(
                    $"A composed diagnostic session source for process {ProcessId} failed.");
            }

            return completion;
        }
        catch
        {
            await session.StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task DispatchEventsAsync()
    {
        await foreach (var sessionEvent in _events.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            HandlerRegistration[] handlers;
            lock (_lifecycleLock)
            {
                handlers = _handlers
                    .Where(handler => handler.EventType.IsInstanceOfType(sessionEvent))
                    .ToArray();
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

    private void EnsureConfigurable()
    {
        if (_startTask is not null || _finished || _stopRequested)
        {
            throw new InvalidOperationException("Session sources and handlers must be configured before the session starts.");
        }
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

    private sealed class HandlerSubscription(
        ComposedDiagnosticSession session,
        HandlerRegistration registration) : IDisposable
    {
        private ComposedDiagnosticSession? _session = session;

        public void Dispose() => Interlocked.Exchange(ref _session, null)?.Detach(registration);
    }
}
