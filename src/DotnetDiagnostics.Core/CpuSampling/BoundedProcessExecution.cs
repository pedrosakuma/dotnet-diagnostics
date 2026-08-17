using System.Diagnostics;

namespace DotnetDiagnostics.Core.CpuSampling;

internal static class BoundedProcessExecution
{
    public static async Task<T> RunAsync<T>(
        Process process,
        TimeSpan timeout,
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(action);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Process timeout must be positive.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await action(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync(process).ConfigureAwait(false);
            throw new TimeoutException(
                $"{operation} did not complete within {timeout.TotalSeconds:0.#} seconds.",
                ex);
        }
        catch
        {
            await StopAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            return;
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort process cleanup; preserve the original timeout/failure.
        }
    }
}
