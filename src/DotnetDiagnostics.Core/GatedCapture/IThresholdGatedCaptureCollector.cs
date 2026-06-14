namespace DotnetDiagnostics.Core.GatedCapture;

/// <summary>
/// Polls one runtime metric over a <b>bounded</b> window and fires a heavier capture the moment a
/// <see cref="TriggerPredicate"/> trips (issue #419). The threshold-gated, LLM/human-driven
/// equivalent of DebugDiag <c>collect</c> — explicitly <b>not</b> an always-on daemon. The whole
/// operation is one synchronous invocation: it samples the metric for at most <c>window</c>, fires
/// up to <c>maxCaptures</c>, and returns. Nothing persists after the call.
/// </summary>
public interface IThresholdGatedCaptureCollector
{
    /// <summary>
    /// Arms the bounded watch. Returns as soon as <paramref name="maxCaptures"/> captures have fired,
    /// when <paramref name="window"/> elapses, or when the target process exits.
    /// </summary>
    /// <param name="processId">Resolved target PID.</param>
    /// <param name="predicate">The single metric comparison that gates a capture.</param>
    /// <param name="captureKind">What to capture when the predicate trips (carried in the result;
    /// the actual capture is performed by <paramref name="captureCallback"/>).</param>
    /// <param name="window">Hard upper bound on how long the watch is armed.</param>
    /// <param name="maxCaptures">Hard cap on how many captures fire.</param>
    /// <param name="sampleInterval">How often the metric is polled within the window.</param>
    /// <param name="captureCallback">Invoked once per trip to perform + register the capture. It is
    /// passed the outer cancellation token (not the window timer) so an in-flight capture is allowed
    /// to finish even if the window elapses mid-capture.</param>
    /// <param name="cancellationToken">Caller cancellation (e.g. MCP <c>notifications/cancelled</c>).</param>
    Task<GatedCaptureResult> WatchAndCaptureAsync(
        int processId,
        TriggerPredicate predicate,
        GatedCaptureKind captureKind,
        TimeSpan window,
        int maxCaptures,
        TimeSpan sampleInterval,
        Func<GatedCaptureTrigger, CancellationToken, Task<GatedCaptureOutcome>> captureCallback,
        CancellationToken cancellationToken = default);
}
