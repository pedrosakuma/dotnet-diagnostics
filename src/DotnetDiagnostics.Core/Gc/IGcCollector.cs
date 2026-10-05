namespace DotnetDiagnostics.Core.Gc;

/// <summary>
/// Collects GC events from the target process over a fixed time window and returns a summary
/// (counts per generation, total/max pause, and per-event details).
/// </summary>
public interface IGcCollector
{
    Task<GcSummary> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxEvents = 200,
        CancellationToken cancellationToken = default);
}

/// <summary>Collects GC events and heap samples while publishing each observation incrementally.</summary>
public interface IStreamingGcCollector
{
    Task<GcSummary> CollectStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<GcEvent> onCollection,
        Action<GcHeapStatsSample> onHeapSample,
        int maxEvents = 200,
        CancellationToken cancellationToken = default);
}
