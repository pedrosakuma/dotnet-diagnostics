namespace DotnetDiagnostics.Core.Contention;

/// <summary>
/// Collects CLR monitor-lock contention activity from a target process over a fixed EventPipe window.
/// </summary>
public interface IContentionCollector
{
    Task<ContentionSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}

/// <summary>Collects monitor contention while publishing completed contention events incrementally.</summary>
public interface IStreamingContentionCollector
{
    Task<ContentionSnapshot> CollectStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<ContentionEventSample> onObservation,
        CancellationToken cancellationToken = default);
}
