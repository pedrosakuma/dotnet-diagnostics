using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Internal;

internal static class EventPipeCollectionRunner
{
    public static async Task RunAsync(
        EventPipeSession session,
        TimeSpan duration,
        Action<EventPipeEventSource> configure,
        Action<Exception> onProcessingError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(onProcessingError);

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                configure(source);
                source.Process();
            }
            catch (Exception ex)
            {
                onProcessingError(ex);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await EventPipeSessionShutdown
                .StopAndDrainAsync(session, processingTask, onProcessingError)
                .ConfigureAwait(false);
        }
    }
}
