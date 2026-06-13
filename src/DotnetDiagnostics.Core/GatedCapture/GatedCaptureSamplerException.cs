namespace DotnetDiagnostics.Core.GatedCapture;

/// <summary>
/// Thrown when the bounded threshold-gated watch could not observe its metric at all because the
/// underlying metric source (the EventPipe EventCounters session) failed to start or faulted before
/// emitting a single sample — and the failure is not explained by the target exiting or the caller
/// cancelling. Surfacing this distinguishes a genuine attach/permission failure from a benign
/// "predicate never tripped" result.
/// </summary>
public sealed class GatedCaptureSamplerException : Exception
{
    public GatedCaptureSamplerException(int processId, GatedCaptureMetric metric, Exception innerException)
        : base(BuildMessage(processId, metric, innerException), innerException)
    {
        ProcessId = processId;
        Metric = metric;
    }

    /// <summary>Target PID whose metric session failed.</summary>
    public int ProcessId { get; }

    /// <summary>The metric the watch was attempting to sample.</summary>
    public GatedCaptureMetric Metric { get; }

    private static string BuildMessage(int processId, GatedCaptureMetric metric, Exception innerException)
    {
        var (provider, counter) = GatedCaptureMetrics.Counter(metric);
        return $"Threshold-gated capture could not sample '{provider}/{counter}' on pid {processId}: " +
            $"the EventPipe metric session failed before any sample arrived ({innerException.GetType().Name}: {innerException.Message}).";
    }
}
