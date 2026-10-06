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

/// <summary>Collects Kestrel events while publishing typed lifecycle observations incrementally.</summary>
public interface IStreamingKestrelCollector
{
    Task<KestrelSnapshot> CollectStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<KestrelObservation> onObservation,
        int intervalSeconds = 1,
        CancellationToken cancellationToken = default);
}

/// <summary>One sanitized Kestrel lifecycle, request, or counter observation.</summary>
public sealed record KestrelObservation(
    DateTimeOffset Timestamp,
    string EventName,
    string? Method = null,
    string? Path = null,
    TimeSpan? Duration = null,
    string? CounterName = null,
    double? CounterValue = null,
    string? Protocols = null);
