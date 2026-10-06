namespace DotnetDiagnostics.Core.Counters;

/// <summary>Lifecycle and typed callback surface shared by Core-owned diagnostic sessions.</summary>
public interface IDiagnosticSession : IAsyncDisposable
{
    /// <summary>Process ID attached to the session.</summary>
    int ProcessId { get; }

    /// <summary>Completes after the session stops and its queued events have been dispatched.</summary>
    Task<DiagnosticSessionCompletion> Completion { get; }

    /// <summary>
    /// Attaches an asynchronous handler for one event type. Handlers must be attached before
    /// <see cref="StartAsync"/>. Matching handlers are invoked serially in attachment order.
    /// The callback token is canceled if handler delivery exceeds the shutdown wait budget.
    /// Disposing the registration detaches only that handler.
    /// </summary>
    IDisposable Attach<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : DiagnosticSessionEvent;

    /// <summary>Starts the session after at least one event handler has been attached.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the session and waits for shutdown and queued event delivery.</summary>
    Task<DiagnosticSessionCompletion> StopAsync();
}
