namespace DotnetDiagnostics.Core.CaptureRecording;

/// <summary>
/// Optional bounded admission for sequential replay after the live collection has stopped
/// and drained. Never call from a live EventPipe/native callback.
/// </summary>
internal interface IReplayCaptureObservationSink : ICaptureObservationSink
{
    /// <summary>
    /// Awaits queue capacity for one copied observation without retrying TryAppend.
    /// True means admitted, not persisted. False is a terminal, accounted rejection;
    /// cancellation throws and must not be translated to successful admission.
    /// Implementations bound pending observations as well as the writer queue.
    /// </summary>
    ValueTask<bool> AppendReplayAsync(CaptureObservation observation, CancellationToken cancellationToken = default);
}
