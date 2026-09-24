using DotnetDiagnostics.Core.Drilldown;

namespace DotnetDiagnostics.Core.CaptureRecording;

/// <summary>
/// Flows through one collection invocation, not singleton collector state. Collectors capture
/// Current in a local before wiring native/event callbacks; those callbacks may not flow context.
/// </summary>
internal static class CaptureRecordingContext
{
    private static readonly AsyncLocal<Scope?> Active = new();

    internal static ICaptureObservationSink? Current => Active.Value?.Sink;

    internal static IDisposable Enter(ICaptureObservationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var scope = new Scope(sink, Active.Value);
        Active.Value = scope;
        return scope;
    }

    internal static void ArtifactRegistered(DiagnosticHandle handle, object artifact)
        => Current?.ArtifactRegistered(handle, artifact);

    private sealed class Scope(ICaptureObservationSink sink, Scope? previous) : IDisposable
    {
        internal ICaptureObservationSink Sink { get; } = sink;

        public void Dispose()
        {
            if (ReferenceEquals(Active.Value, previous)) return;
            if (!ReferenceEquals(Active.Value, this))
                throw new InvalidOperationException("Capture recording scopes must be disposed in nesting order.");
            Active.Value = previous;
        }
    }
}
