namespace DotnetDiagnostics.Core.Kestrel;

/// <summary>
/// Aggregated view of the <c>Microsoft-AspNetCore-Server-Kestrel</c> EventSource over a fixed
/// EventPipe window: connection / request / TLS event counts, request and connection latency
/// percentiles, the connection- and request-queue-length counter timeline (head-of-line blocking),
/// and the live <c>KestrelServerOptions</c> JSON emitted by the Configuration event at session
/// enable. Each latency aggregate stays exact for the first few thousand samples, then switches
/// to a bounded reservoir sample so p50/p95 become approximate while max remains exact.
/// </summary>
public sealed record KestrelSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long ConnectionsStarted,
    long ConnectionsStopped,
    long ConnectionsRejected,
    long RequestsStarted,
    long RequestsStopped,
    long TlsHandshakesStarted,
    long TlsHandshakesStopped,
    long TlsHandshakesFailed,
    long PeakConnectionQueueLength,
    long PeakRequestQueueLength,
    TimeSpan RequestP50,
    TimeSpan RequestP95,
    TimeSpan RequestMax,
    TimeSpan TlsHandshakeP50,
    TimeSpan TlsHandshakeP95,
    TimeSpan TlsHandshakeMax,
    TimeSpan ConnectionDurationP50,
    TimeSpan ConnectionDurationP95,
    TimeSpan ConnectionDurationMax,
    IReadOnlyList<KestrelCounterSample> Counters,
    IReadOnlyList<KestrelQueuePoint> QueuePoints,
    IReadOnlyList<KestrelRequestGroup> ByOperation,
    IReadOnlyList<string> TlsProtocols,
    string? ConfigurationJson,
    IReadOnlyList<string> Notes);

/// <summary>Latest value of a single Kestrel EventCounter captured in the window.</summary>
public sealed record KestrelCounterSample(
    string Name,
    string DisplayName,
    double Value,
    string? Unit);

/// <summary>One sampled value of a queue-length counter, used to plot head-of-line blocking over time.</summary>
public sealed record KestrelQueuePoint(
    DateTimeOffset Timestamp,
    string Counter,
    double Value);

/// <summary>Request latency aggregated by HTTP method + path (+ protocol version).</summary>
public sealed record KestrelRequestGroup(
    string Method,
    string Path,
    string HttpVersion,
    int Count,
    TimeSpan TotalDuration,
    TimeSpan P95Duration,
    TimeSpan MaxDuration);
