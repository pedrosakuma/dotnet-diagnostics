namespace DotnetDiagnostics.Core.Kestrel;

/// <summary>
/// Collects a curated Kestrel request-pipeline view (connections, requests, TLS handshakes,
/// queue lengths and the live server configuration) from a target process over a fixed EventPipe
/// window.
/// </summary>
public interface IKestrelCollector
{
    Task<KestrelSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int intervalSeconds = 1,
        CancellationToken cancellationToken = default);
}
