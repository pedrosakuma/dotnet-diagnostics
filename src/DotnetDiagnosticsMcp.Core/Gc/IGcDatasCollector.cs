namespace DotnetDiagnosticsMcp.Core.Gc;

/// <summary>
/// Collects DATAS (Dynamic Adaptation To Application Sizes) GC tuning events from a running
/// .NET process over a fixed window.
/// </summary>
public interface IGcDatasCollector
{
    /// <summary>
    /// Captures DATAS tuning events for <paramref name="duration"/>. Returns an empty snapshot
    /// (no events) when the target runs Workstation GC or DATAS is otherwise disabled.
    /// </summary>
    Task<GcDatasSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxEvents = 1000,
        CancellationToken cancellationToken = default);
}
