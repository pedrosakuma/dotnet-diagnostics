namespace DotnetDiagnosticsMcp.Core.Capabilities;

/// <summary>
/// Detects which diagnostic tools are usable on a target process.
/// </summary>
public interface ICapabilityDetector
{
    Task<DiagnosticCapabilities> DetectAsync(int processId, CancellationToken cancellationToken = default);
}
