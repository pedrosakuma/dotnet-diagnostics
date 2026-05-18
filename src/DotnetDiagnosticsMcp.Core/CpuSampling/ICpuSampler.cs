namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Collects CPU samples from a target process via EventPipe and returns the top-N hotspots aggregated by frame.
/// </summary>
public interface ICpuSampler
{
    Task<CpuSample> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        CancellationToken cancellationToken = default);
}
