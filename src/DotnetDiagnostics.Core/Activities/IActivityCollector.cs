namespace DotnetDiagnostics.Core.Activities;

/// <summary>
/// Collects completed ActivitySource stop events via the DiagnosticSource EventPipe provider,
/// carrying ids, parent linkage, trace/span ids, tags, and duration. Open spans are not tracked.
/// </summary>
public interface IActivityCollector
{
    Task<ActivityCapture> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? sources = null,
        int maxActivities = 200,
        CancellationToken cancellationToken = default);

    /// <summary>Targeted capture has a separate match budget; implementations must not silently ignore the filter.</summary>
    Task<ActivityCapture> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? sources,
        int maxActivities,
        string? traceId,
        int maxMatchedActivities,
        CancellationToken cancellationToken = default)
        => traceId is null
            ? CollectAsync(processId, duration, sources, maxActivities, cancellationToken)
            : throw new NotSupportedException("This collector does not support targeted activity retention.");

    /// <summary>Authority capture is explicit; older implementations must not silently ignore opt-in.</summary>
    Task<ActivityCapture> CollectAsync(
        int processId, TimeSpan duration, IReadOnlyList<string>? sources, int maxActivities,
        string? traceId, int maxMatchedActivities, bool includeHttpDestination,
        CancellationToken cancellationToken = default)
        => !includeHttpDestination
            ? CollectAsync(processId, duration, sources, maxActivities, traceId, maxMatchedActivities, cancellationToken)
            : throw new NotSupportedException("This collector does not support HTTP destination capture.");
}
