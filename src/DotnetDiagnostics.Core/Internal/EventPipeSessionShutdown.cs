using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.Internal;

internal enum EventPipeForcedCloseReason
{
    StopBudgetExpired,
    StopFailed,
    DrainIncomplete,
}

internal static class EventPipeSessionShutdown
{
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    public static async Task StopAndDrainAsync(
        EventPipeSession session,
        Task processingTask,
        Action<Exception> onError,
        TimeSpan? budget = null,
        bool propagateProcessingErrors = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(processingTask);
        ArgumentNullException.ThrowIfNull(onError);

        await StopAndDrainAsync(
            session.StopAsync, session.Dispose, processingTask, onError,
            budget ?? DefaultBudget, propagateProcessingErrors).ConfigureAwait(false);
    }

    internal static async Task StopAndDrainAsync(
        Func<CancellationToken, Task> stopAsync,
        Action dispose,
        Task processingTask,
        Action<Exception> onError,
        TimeSpan shutdownBudget,
        bool propagateProcessingErrors = false,
        Func<EventPipeForcedCloseReason, Task>? beforeForcedClose = null)
    {
        var closePrepared = false;
        async Task PrepareCloseAsync(EventPipeForcedCloseReason reason)
        {
            if (!closePrepared && beforeForcedClose is not null)
            {
                closePrepared = true;
                await beforeForcedClose(reason).ConfigureAwait(false);
            }
        }

        try
        {
            // Session-control calls are serialized and may spend this whole budget waiting for a
            // sibling stop. The stream still needs its own bounded window to consume the stop marker.
            await StopThenDrainAsync(
                () => StopSessionAsync(stopAsync, dispose, onError, shutdownBudget, PrepareCloseAsync),
                processingTask,
                onError,
                shutdownBudget,
                propagateProcessingErrors).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (!processingTask.IsCompleted)
                {
                    await PrepareCloseAsync(EventPipeForcedCloseReason.DrainIncomplete).ConfigureAwait(false);
                }
            }
            finally
            {
                ObserveLater(processingTask);
                dispose();
            }
        }
    }

    internal static async Task StopThenDrainAsync(
        Func<Task> stopSessionAsync,
        Task processingTask,
        Action<Exception> onError,
        TimeSpan drainBudget,
        bool propagateProcessingErrors = false)
    {
        ArgumentNullException.ThrowIfNull(stopSessionAsync);
        ArgumentNullException.ThrowIfNull(processingTask);
        ArgumentNullException.ThrowIfNull(onError);

        await stopSessionAsync().ConfigureAwait(false);

        if (processingTask.IsCompleted)
        {
            await ObserveProcessingTaskAsync(processingTask, onError, propagateProcessingErrors).ConfigureAwait(false);
            return;
        }

        if (drainBudget <= TimeSpan.Zero)
        {
            ObserveLater(processingTask);
            throw CreateDrainTimeout(drainBudget);
        }

        try
        {
            await processingTask.WaitAsync(drainBudget, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            ObserveLater(processingTask);
            throw CreateDrainTimeout(drainBudget, ex);
        }
        catch (Exception ex)
        {
            onError(ex);
            if (propagateProcessingErrors)
            {
                throw;
            }
        }
    }

    public static async Task StopSessionAsync(
        EventPipeSession session,
        Action<Exception> onError,
        TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(onError);

        await StopSessionAsync(session.StopAsync, session.Dispose, onError, budget ?? DefaultBudget)
            .ConfigureAwait(false);
    }

    private static async Task StopSessionAsync(
        Func<CancellationToken, Task> stopAsync,
        Action dispose,
        Action<Exception> onError,
        TimeSpan shutdownBudget,
        Func<EventPipeForcedCloseReason, Task>? beforeForcedClose = null)
    {
        if (shutdownBudget <= TimeSpan.Zero)
        {
            if (beforeForcedClose is not null)
            {
                await beforeForcedClose(EventPipeForcedCloseReason.StopBudgetExpired).ConfigureAwait(false);
            }
            dispose();
            return;
        }

        using var shutdownCts = new CancellationTokenSource(shutdownBudget);
        try
        {
            await EventPipeSessionControl.Gate.WaitAsync(shutdownCts.Token).ConfigureAwait(false);
            try
            {
                await stopAsync(shutdownCts.Token).ConfigureAwait(false);
            }
            finally
            {
                EventPipeSessionControl.Gate.Release();
            }
        }
        catch (Exception ex)
        {
            onError(ex);
            if (beforeForcedClose is not null)
            {
                await beforeForcedClose(shutdownCts.IsCancellationRequested
                    ? EventPipeForcedCloseReason.StopBudgetExpired
                    : EventPipeForcedCloseReason.StopFailed).ConfigureAwait(false);
            }
            dispose();
        }
    }

    private static async Task ObserveProcessingTaskAsync(
        Task processingTask,
        Action<Exception> onError,
        bool propagateProcessingErrors)
    {
        try
        {
            await processingTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onError(ex);
            if (propagateProcessingErrors)
            {
                throw;
            }
        }
    }

    private static void ObserveLater(Task processingTask)
    {
        _ = processingTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static TimeoutException CreateDrainTimeout(TimeSpan drainBudget, Exception? innerException = null) =>
        new(
            FormattableString.Invariant(
                $"EventPipe processing did not drain within {drainBudget.TotalSeconds:0.#} seconds after session stop completed."),
            innerException);
}
