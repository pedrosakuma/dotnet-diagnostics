using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.ProcessDiscovery;

namespace DotnetDiagnostics.TestSupport;

internal static class NetworkingCorrelationContractFixture
{
    internal static NetworkingSnapshot Create(bool legacy = false)
    {
        var counts = new NetworkingCorrelationCounts(3, 1, 0, 2, 0, 0, 0, 0, 0, 2, 0, 0, false);
        counts = counts with
        {
            Counts = new Dictionary<string, long>(counts.Counts, StringComparer.Ordinal)
            {
                ["latencyPopulationVersion"] = 2, ["pairedFailed"] = 1, ["pairedWithoutFailure"] = 0,
                ["matchedFailureEvents"] = 1, ["repeatedFailureEvents"] = 0,
                ["invalidTimestampFailures"] = 0, ["unfinishedFailed"] = 0,
            },
        };
        var httpCounts = counts with
        {
            Counts = new Dictionary<string, long>(counts.Counts, StringComparer.Ordinal)
            {
                ["httpResponseStops"] = 1, ["httpStatusErrorStops"] = 1, ["httpStopsWithoutStatus"] = 2,
            },
        };
        return new NetworkingSnapshot(
            ProcessId: Environment.ProcessId, StartedAt: DateTimeOffset.UtcNow, Duration: TimeSpan.FromSeconds(1),
            HttpRequestsStarted: 3, HttpRequestsStopped: 3, HttpRequestsFailed: 1,
            HttpConnectionsEstablished: 0, HttpConnectionsClosed: 0, HttpRequestsLeftQueue: 0,
            HttpRequestP50: TimeSpan.FromMilliseconds(100), HttpRequestP95: TimeSpan.FromMilliseconds(100),
            HttpRequestMax: TimeSpan.FromMilliseconds(100), TimeInQueueP50: default, TimeInQueueP95: default,
            TimeInQueueMax: default, DnsLookupsStarted: 3, DnsLookupsStopped: 3, DnsLookupsFailed: 1,
            DnsP50: default, DnsP95: default, DnsMax: default, TlsHandshakesStarted: 3,
            TlsHandshakesStopped: 3, TlsHandshakesFailed: 1, TlsP50: default, TlsP95: default, TlsMax: default,
            SocketConnectsStarted: 0, SocketConnectsStopped: 0, SocketConnectsFailed: 0, Counters: [],
            ByOperation: [new("http://localhost", "/accepted", 1, TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100))],
            TlsProtocols: [], Notes: ["Correlation fixture: overlapping identities excluded."])
        {
            Correlation = legacy ? null : new(httpCounts, counts, counts),
            CaptureQuality = legacy ? null : new("early", 7, TimeSpan.FromMilliseconds(250), 1),
        };
    }

    internal sealed class Collector(NetworkingSnapshot snapshot) : INetworkingCollector
    {
        public Task<NetworkingSnapshot> CollectAsync(int processId, TimeSpan duration, int intervalSeconds = 1,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    internal sealed class Resolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? processId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(new(Environment.ProcessId, RuntimeFlavor.CoreClr,
                true, true, false), null));
    }
}
