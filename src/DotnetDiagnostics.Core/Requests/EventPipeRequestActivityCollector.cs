using System.Diagnostics.Tracing;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.Requests;

public interface IRequestActivityCollector
{
    Task<RequestActivityCapture> CollectAsync(int processId, RequestActivityOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Owns one union subscription; never composes differently filtered DiagnosticSource sessions.</summary>
public sealed class EventPipeRequestActivityCollector : IRequestActivityCollector
{
    internal Action<string>? Observed { get; init; }
    internal Action<CapturedActivity>? CompletedActivityObserved { get; init; }

    public async Task<RequestActivityCapture> CollectAsync(int processId, RequestActivityOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        // Copy caller-owned filters before the asynchronous attach.
        options = options with { Sources = options.Sources.ToArray() };
        var filters = options.Sources.ToList();
        var arguments = EventPipeActivityCollector.BuildProviderArguments(filters, includeTags: false);
        arguments["FilterAndPayloadSpecs"] = EventPipeInFlightRequestCollector.BuildFilterSpec(includeLegacyIdentity: false) +
            "\n" + arguments["FilterAndPayloadSpecs"];
        var requestedStart = DateTimeOffset.UtcNow;
        var lifetime = ProcessLifetime.TryReadStart(processId);
        var state = new RequestActivityState();
        var id = Guid.NewGuid().ToString("N");
        var session = await new DiagnosticsClient(processId).StartEventPipeSessionWithTimeoutAsync(
            [new EventPipeProvider("Microsoft-Diagnostics-DiagnosticSource", EventLevel.Verbose, 3, arguments)],
            requestRundown: false, circularBufferMB: 64, cancellationToken: cancellationToken).ConfigureAwait(false);
        DateTimeOffset? streamStart = null;
        var completion = "unknown";
        long? lost = null;
        var closed = false;
        try
        {
            var result = await EventPipeCollectionRunner.RunAsync(session, TimeSpan.FromSeconds(options.DurationSeconds),
                source => source.Dynamic.All += e =>
                {
                    if (e.ProviderName != "Microsoft-Diagnostics-DiagnosticSource") return;
                    lock (state)
                    {
                        if (closed) return;
                        try
                        {
                            if (e.EventName is "Activity1/Start" or "Activity1/Stop")
                            {
                                if (!EventPipeInFlightRequestCollector.TryCreateEvent(e, out var request, strictIdentity: true))
                                {
                                    state.ParseErrors++;
                                    return;
                                }
                                state.ObserveRequest(request);
                                Observed?.Invoke("request");
                            }
                            else if (e.EventName.EndsWith("Stop", StringComparison.Ordinal) &&
                                EventPipeActivityCollector.TryCreateActivity(e, filters, out var activity, out var hasStart, includeTags: false))
                            {
                                if (!hasStart || activity.Duration is null)
                                {
                                    state.ParseErrors++;
                                    return;
                                }
                                state.ObserveActivity(activity);
                                Observed?.Invoke("activity");
                                CompletedActivityObserved?.Invoke(activity);
                            }
                        }
                        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException)
                        {
                            state.ParseErrors++;
                        }
                    }
                },
                _ => { lock (state) { if (!closed) state.ParseErrors++; } }, cancellationToken,
                (eventsLost, _, start) =>
                {
                    lock (state)
                    {
                        if (closed) return;
                        lost = eventsLost;
                        if (start > DateTimeOffset.UnixEpoch && start <= DateTimeOffset.UtcNow) streamStart = start;
                    }
                }, stopOnProcessingEnd: true).ConfigureAwait(false);
            completion = result.Status;
            lost = result.EventsLost;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion = "cancelled";
        }
        var end = DateTimeOffset.UtcNow;
        var finalLifetime = ProcessLifetime.TryReadStart(processId);
        var lifetimeStatus = lifetime is null || finalLifetime is null ? "unknown"
            : lifetime == finalLifetime ? "known" : "changed";
        lock (state)
        {
            closed = true;
            return state.Close(id, processId, lifetime, requestedStart, options, streamStart, end,
                completion, lost, lifetimeStatus);
        }
    }
}
