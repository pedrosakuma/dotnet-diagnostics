namespace DotnetDiagnostics.Core.Jit;

/// <summary>
/// Collects CLR JIT / tiered-compilation activity from the runtime EventPipe stream.
/// </summary>
public interface IJitCollector
{
    Task<JitSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}

/// <summary>Collects JIT activity while publishing each completed compilation incrementally.</summary>
public interface IStreamingJitCollector
{
    Task<JitSnapshot> CollectStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<JitCompilationObservation> onObservation,
        CancellationToken cancellationToken = default);
}
