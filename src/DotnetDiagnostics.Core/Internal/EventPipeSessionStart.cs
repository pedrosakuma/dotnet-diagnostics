using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.Internal;

internal static class EventPipeSessionStart
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static Task<EventPipeSession> StartEventPipeSessionWithTimeoutAsync(
        this DiagnosticsClient client,
        IEnumerable<EventPipeProvider> providers,
        bool requestRundown,
        int circularBufferMB,
        CancellationToken cancellationToken) =>
        client.StartEventPipeSessionWithTimeoutAsync(
            providers,
            requestRundown,
            circularBufferMB,
            DefaultTimeout,
            cancellationToken);

    public static async Task<EventPipeSession> StartEventPipeSessionWithTimeoutAsync(
        this DiagnosticsClient client,
        IEnumerable<EventPipeProvider> providers,
        bool requestRundown,
        int circularBufferMB,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(providers);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        try
        {
            await EventPipeSessionControl.Gate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            try
            {
                return await client
                    .StartEventPipeSessionAsync(providers, requestRundown, circularBufferMB, linkedCts.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                EventPipeSessionControl.Gate.Release();
            }
        }
        catch (OperationCanceledException ex) when (linkedCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Starting an EventPipe session did not complete within {timeout.TotalSeconds:0}s; the target's diagnostic IPC may be wedged or the process is unresponsive.",
                ex);
        }
    }
}
