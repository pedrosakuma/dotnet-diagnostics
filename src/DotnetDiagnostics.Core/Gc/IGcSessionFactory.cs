namespace DotnetDiagnostics.Core.Gc;

/// <summary>Creates Core-owned live GC pause sessions.</summary>
public interface IGcSessionFactory
{
    /// <summary>Creates a session. Attach event handlers before calling <see cref="DotnetDiagnostics.Core.Counters.IDiagnosticSession.StartAsync"/>.</summary>
    GcSession CreateSession(
        int processId,
        GcSessionOptions? options = null);
}

/// <summary>Options for a live GC pause session.</summary>
public sealed record GcSessionOptions
{
    /// <summary>Maximum queue capacity allowed for one live session.</summary>
    public const int MaxAllowedObservationCapacity = 16_384;

    /// <summary>Maximum number of pending observations retained for the single consumer.</summary>
    public int ObservationCapacity { get; init; } = 256;
}
