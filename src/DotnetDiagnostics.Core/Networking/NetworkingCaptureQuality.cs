namespace DotnetDiagnostics.Core.Networking;

/// <summary>Acquisition facts, independent of activity correlation. Missing metadata means unknown.</summary>
/// <param name="Completion">normal, early, source-failure, or unknown; normal requires a drained source after requested stop.</param>
/// <param name="EventsLost">Transport-reported loss after drain; null when unavailable, never an assumed zero.</param>
/// <param name="StreamReadDuration">Local elapsed time reading the stream, including buffering/drain; not target coverage or last-event time.</param>
/// <param name="ParseErrors">Events whose collector payload parsing failed.</param>
public sealed record NetworkingCaptureQuality(
    string Completion,
    long? EventsLost,
    TimeSpan? StreamReadDuration,
    long ParseErrors)
{
    /// <summary>True unless normal drain, known zero loss and no parsing errors were recorded.</summary>
    public bool HasLimitations => Completion != "normal" || EventsLost != 0 || ParseErrors != 0;

    internal string Describe() =>
        $"Capture completion={Completion}; transport events lost={EventsLost?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; parse errors={ParseErrors}. " +
        (HasLimitations ? "Observation is incomplete or uncertain; retained data is partial. " : "") +
        "Duration is requested time; stream-read elapsed is not proof of target coverage.";
}
