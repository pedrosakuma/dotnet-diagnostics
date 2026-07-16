using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.Internal;

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

        var shutdownBudget = budget ?? DefaultBudget;
        var sw = Stopwatch.StartNew();
        try
        {
            await StopSessionAsync(session, onError, shutdownBudget).ConfigureAwait(false);

            var remaining = shutdownBudget - sw.Elapsed;
            if (processingTask.IsCompleted)
            {
                await ObserveProcessingTaskAsync(processingTask, onError, propagateProcessingErrors).ConfigureAwait(false);
                return;
            }

            if (remaining <= TimeSpan.Zero)
            {
                ObserveLater(processingTask);
                throw new TimeoutException($"EventPipe processing did not drain within {shutdownBudget.TotalSeconds:0.#} seconds.");
            }

            try
            {
                await processingTask.WaitAsync(remaining, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ObserveLater(processingTask);
                throw;
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
        finally
        {
            session.Dispose();
        }
    }

    public static async Task StopSessionAsync(
        EventPipeSession session,
        Action<Exception> onError,
        TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(onError);

        var shutdownBudget = budget ?? DefaultBudget;
        if (shutdownBudget <= TimeSpan.Zero)
        {
            session.Dispose();
            return;
        }

        using var shutdownCts = new CancellationTokenSource(shutdownBudget);
        try
        {
            await EventPipeSessionControl.Gate.WaitAsync(shutdownCts.Token).ConfigureAwait(false);
            try
            {
                await session.StopAsync(shutdownCts.Token).ConfigureAwait(false);
            }
            finally
            {
                EventPipeSessionControl.Gate.Release();
            }
        }
        catch (Exception ex)
        {
            onError(ex);
            session.Dispose();
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
}
