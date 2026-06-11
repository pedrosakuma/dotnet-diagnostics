namespace DotnetDiagnostics.Core.Networking;

/// <summary>
/// Aggregated view of the stable .NET networking EventSources over a fixed EventPipe window:
/// <c>System.Net.Http</c> (outbound HttpClient request lifecycle + connection pool + time-in-queue),
/// <c>System.Net.NameResolution</c> (DNS), <c>System.Net.Security</c> (TLS handshakes) and
/// <c>System.Net.Sockets</c> (socket connects + byte volume), plus each provider's EventCounters.
/// Latency percentiles are paired by EventSource activity id and are best-effort: when activity
/// correlation is unavailable the counts are still reported and a note is added.
/// </summary>
public sealed record NetworkingSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    // System.Net.Http
    long HttpRequestsStarted,
    long HttpRequestsStopped,
    long HttpRequestsFailed,
    long HttpConnectionsEstablished,
    long HttpConnectionsClosed,
    long HttpRequestsLeftQueue,
    TimeSpan HttpRequestP50,
    TimeSpan HttpRequestP95,
    TimeSpan HttpRequestMax,
    TimeSpan TimeInQueueP50,
    TimeSpan TimeInQueueP95,
    TimeSpan TimeInQueueMax,
    // System.Net.NameResolution
    long DnsLookupsStarted,
    long DnsLookupsStopped,
    long DnsLookupsFailed,
    TimeSpan DnsP50,
    TimeSpan DnsP95,
    TimeSpan DnsMax,
    // System.Net.Security
    long TlsHandshakesStarted,
    long TlsHandshakesStopped,
    long TlsHandshakesFailed,
    TimeSpan TlsP50,
    TimeSpan TlsP95,
    TimeSpan TlsMax,
    // System.Net.Sockets
    long SocketConnectsStarted,
    long SocketConnectsStopped,
    long SocketConnectsFailed,
    IReadOnlyList<NetworkingCounterSample> Counters,
    IReadOnlyList<NetworkingHttpGroup> ByOperation,
    IReadOnlyList<string> TlsProtocols,
    IReadOnlyList<string> Notes);

/// <summary>Latest value of a single networking EventCounter captured in the window.</summary>
public sealed record NetworkingCounterSample(
    string Provider,
    string Name,
    string DisplayName,
    double Value,
    string? Unit);

/// <summary>Outbound HTTP request volume + latency aggregated by scheme://host:port + request path.</summary>
public sealed record NetworkingHttpGroup(
    string Host,
    string Path,
    int Count,
    TimeSpan TotalDuration,
    TimeSpan P95Duration,
    TimeSpan MaxDuration);
