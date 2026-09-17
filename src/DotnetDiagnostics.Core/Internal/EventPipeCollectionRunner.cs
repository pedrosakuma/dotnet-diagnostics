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
        CancellationToken cancellationToken,
        Action<long, bool, DateTimeOffset>? onDrained = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(onProcessingError);

        var stopRequested = 0;
        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                configure(source);
                source.Process();
                onDrained?.Invoke(source.EventsLost, Volatile.Read(ref stopRequested) == 0,
                    new DateTimeOffset(source.SessionStartTime.ToUniversalTime()));
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
            Volatile.Write(ref stopRequested, 1);
            await EventPipeSessionShutdown
                .StopAndDrainAsync(session, processingTask, onProcessingError)
                .ConfigureAwait(false);
        }
    }
}
