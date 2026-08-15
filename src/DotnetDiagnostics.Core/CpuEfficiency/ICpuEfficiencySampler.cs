namespace DotnetDiagnostics.Core.CpuEfficiency;

/// <summary>
/// Platform-agnostic aggregate CPU microarchitecture-efficiency sampler. Implementations wrap
/// <c>perf stat</c> (Linux) or an ETW kernel PMC session (Windows); see
/// <see cref="RoutingCpuEfficiencySampler"/> for the platform dispatch used by the MCP tool layer.
/// </summary>
public interface ICpuEfficiencySampler
{
    /// <summary>
    /// True when this backend's prerequisites (binary/tooling present, sufficient capability or
    /// elevation) are satisfied on the current host. Does NOT guarantee the host's CPU actually
    /// exposes a vPMU — that failure mode is reported per-metric in <see cref="CpuEfficiencySample.Notes"/>
    /// after a real attempt, per the graceful-degradation requirement in issue #828.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Collects an aggregate CPU efficiency snapshot for <paramref name="processId"/> over
    /// <paramref name="duration"/>.
    /// </summary>
    Task<CpuEfficiencySample> SampleAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}
