using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Internal;

internal static class EventPipeCollectionRunner
{
    internal sealed record Completion(string Status, long? EventsLost, TimeSpan StreamReadDuration);

    public static async Task<Completion> RunAsync(
        EventPipeSession session,
        TimeSpan duration,
        Action<EventPipeEventSource> configure,
        Action<Exception> onProcessingError,
        CancellationToken cancellationToken,
        Action<long, bool, DateTimeOffset>? onDrained = null,
        bool stopOnProcessingEnd = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(onProcessingError);

        var stopRequested = 0;
        var shutdownFailed = false;
        var completion = new Completion("unknown", null, TimeSpan.Zero);
        var processingTask = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                configure(source);
                source.Process();
                completion = new Completion(Volatile.Read(ref stopRequested) == 0 ? "early" : "normal",
                    source.EventsLost, watch.Elapsed);
                onDrained?.Invoke(source.EventsLost, Volatile.Read(ref stopRequested) == 0,
                    new DateTimeOffset(source.SessionStartTime.ToUniversalTime()));
            }
            catch (Exception ex)
            {
                completion = new Completion("source-failure", null, watch.Elapsed);
                onProcessingError(ex);
            }
        }, cancellationToken);

        try
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(duration, delayCts.Token);
            if (stopOnProcessingEnd)
            {
                await Task.WhenAny(delay, processingTask).ConfigureAwait(false);
                await delayCts.CancelAsync().ConfigureAwait(false);
                try { await delay.ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                cancellationToken.ThrowIfCancellationRequested();
            }
            else
            {
                await delay.ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref stopRequested, 1);
            try
            {
                await EventPipeSessionShutdown
                    .StopAndDrainAsync(session, processingTask, ex =>
                    {
                        shutdownFailed = true;
                        onProcessingError(ex);
                    })
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex) when (cancellationToken.IsCancellationRequested)
            {
                onProcessingError(ex);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (shutdownFailed && completion.Status == "normal")
            completion = completion with { Status = "unknown", EventsLost = null };
        return completion;
    }
}
