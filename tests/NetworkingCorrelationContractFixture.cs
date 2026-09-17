using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.ProcessDiscovery;
using System.Text.Json;
using Xunit;

namespace DotnetDiagnostics.TestSupport;

internal static class NetworkingCorrelationContractFixture
{
    internal static NetworkingSnapshot CreateLatencyScenario(string scenario)
    {
        if (scenario == "legacy") return Create(true);
        if (scenario == "legacy-counts")
        {
            var old = new NetworkingCorrelationCounts(1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, false);
            return Create() with { Correlation = new(old, old, old) };
        }
        var samples = scenario == "reservoir" ? 5000 : scenario is "absent" or "incomplete" or "loss-empty" or "unpaired" ? 0 : 1;
        var retained = Math.Min(samples, 4096);
        var failed = scenario == "failed" ? samples : 0;
        var unfinished = scenario == "incomplete" ? 1 : 0;
        var ambiguous = scenario is "partial" or "reservoir" or "unpaired" ? 2 : 0;
        var started = samples + unfinished + ambiguous;
        var counts = new NetworkingCorrelationCounts(started, samples, 0, ambiguous, 0, 0, 0, unfinished, 0,
            ambiguous, 0, 0, false);
        counts = counts with
        {
            Counts = new Dictionary<string, long>(counts.Counts, StringComparer.Ordinal)
            {
                ["latencyPopulationVersion"] = 2, ["pairedFailed"] = failed,
                ["pairedWithoutFailure"] = samples - failed, ["percentileSamples"] = retained,
                ["invalidTimestampFailures"] = 0, ["unfinishedFailed"] = 0,
            },
        };
        var queueSamples = scenario == "invalid-queue" ? 0 : samples;
        var http = counts with
        {
            Counts = new Dictionary<string, long>(counts.Counts, StringComparer.Ordinal)
            {
                ["queueSamples"] = queueSamples, ["queuePercentileSamples"] = Math.Min(queueSamples, 4096),
                ["queueRejectedSamples"] = scenario == "invalid-queue" ? 1 : 0,
                ["httpStatusErrorStops"] = 0,
            },
        };
        var duration = samples == 0 || scenario == "zero" ? TimeSpan.Zero : TimeSpan.FromMilliseconds(100);
        return Create() with
        {
            HttpRequestsStarted = started, HttpRequestsStopped = samples + ambiguous, HttpRequestsFailed = failed,
            DnsLookupsStarted = started, DnsLookupsStopped = samples + ambiguous, DnsLookupsFailed = failed,
            TlsHandshakesStarted = started, TlsHandshakesStopped = samples + ambiguous, TlsHandshakesFailed = failed,
            HttpRequestsLeftQueue = scenario == "invalid-queue" ? 1 : queueSamples,
            HttpRequestP50 = duration, HttpRequestP95 = duration, HttpRequestMax = duration,
            DnsP50 = duration, DnsP95 = duration, DnsMax = duration,
            TlsP50 = duration, TlsP95 = duration, TlsMax = duration,
            TimeInQueueP50 = queueSamples == 0 ? TimeSpan.Zero : duration,
            TimeInQueueP95 = queueSamples == 0 ? TimeSpan.Zero : duration,
            TimeInQueueMax = queueSamples == 0 ? TimeSpan.Zero : duration,
            Correlation = new(http, counts, counts),
            CaptureQuality = new(scenario == "incomplete" ? "early" : "normal",
                scenario == "unknown-loss" ? null : scenario is "loss" or "loss-empty" or "reservoir" ? 7 : 0,
                TimeSpan.FromMilliseconds(250), scenario == "invalid-queue" ? 1 : 0),
            ByOperation = samples == 0 ? [] :
                [new("http://localhost", "/accepted", samples, duration * samples, duration, duration) { PercentileSamples = retained }],
        };
    }

    internal static void AssertLatencyScenario(string scenario, JsonElement data, string? summary = null)
    {
        var web = data.TryGetProperty("latencyAvailability", out _);
        string Name(string pascal) => web ? char.ToLowerInvariant(pascal[0]) + pascal[1..] : pascal;
        var expected = scenario switch
        {
            "legacy" or "legacy-counts" => "unknown",
            "absent" => "not-observed",
            "incomplete" or "loss-empty" => "incomplete",
            "unpaired" => "uncorrelatable",
            _ => "measured",
        };
        foreach (var kind in new[] { "http", "queue", "dns", "tls" })
        {
            Assert.Equal(kind == "queue" && scenario == "invalid-queue" ? "unavailable"
                : kind == "queue" && scenario == "unpaired" ? "not-observed" : expected,
                data.GetProperty(Name("LatencyAvailability")).GetProperty(kind).GetString());
        }
        if (summary is not null)
        {
            Assert.Contains(expected == "measured" ? scenario == "zero" ? "Request p95=0.0ms" : "Request p95=100.0ms"
                : $"Request p95=unavailable ({expected})", summary, StringComparison.Ordinal);
            if (expected != "measured") Assert.DoesNotContain("p95=0.0ms", summary, StringComparison.Ordinal);
            if (scenario is "loss" or "loss-empty" or "unknown-loss" or "incomplete" or "reservoir")
                Assert.Contains("Observation is incomplete or uncertain", summary, StringComparison.Ordinal);
            if (scenario == "reservoir")
            {
                Assert.Contains("HTTP 4096/5000", summary, StringComparison.Ordinal);
                Assert.Contains("HTTP 5000/5002", summary, StringComparison.Ordinal);
            }
        }
        if (data.TryGetProperty(Name("ByOperation"), out var groups))
        {
            foreach (var group in groups.EnumerateArray())
            {
                Assert.Equal(scenario is "legacy" or "legacy-counts" ? "unknown" : "measured",
                    group.GetProperty(Name("LatencyAvailability")).GetString());
                if (scenario is not ("legacy" or "legacy-counts"))
                    Assert.Equal(scenario == "reservoir" ? 4096 : 1, group.GetProperty(Name("PercentileSamples")).GetInt64());
            }
        }
        if (scenario is "legacy" or "legacy-counts") return;
        var samples = scenario == "reservoir" ? 5000 : scenario is "absent" or "incomplete" or "loss-empty" or "unpaired" ? 0 : 1;
        var kinds = data.GetProperty(Name("Correlation")).GetProperty(Name("ByKind"));
        foreach (var kind in new[] { "http", "dns", "tls" })
        {
            var counts = kinds.GetProperty(kind).GetProperty(Name("Counts"));
            Assert.Equal(samples, counts.GetProperty("paired").GetInt64());
            Assert.Equal(Math.Min(samples, 4096), counts.GetProperty("percentileSamples").GetInt64());
        }
        Assert.Equal(scenario == "invalid-queue" ? 0 : samples,
            kinds.GetProperty("http").GetProperty(Name("Counts")).GetProperty("queueSamples").GetInt64());
    }

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
