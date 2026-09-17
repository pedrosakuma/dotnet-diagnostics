using System.Diagnostics.Tracing;
using System.Globalization;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Networking;

/// <summary>
/// Fans out across the stable .NET networking EventSources (<c>System.Net.Http</c>,
/// <c>System.Net.NameResolution</c>, <c>System.Net.Security</c>, <c>System.Net.Sockets</c>),
/// pairs Start/Stop events by EventSource activity id to compute request / DNS / TLS latency, reads
/// time-in-queue directly from <c>RequestLeftQueue</c>, and snapshots each provider's EventCounters.
/// </summary>
public sealed class EventPipeNetworkingCollector : INetworkingCollector
{
    private const string HttpProvider = "System.Net.Http";
    private const string DnsProvider = "System.Net.NameResolution";
    private const string TlsProvider = "System.Net.Security";
    private const string SocketsProvider = "System.Net.Sockets";
    internal const int MaxTrackedOperationGroups = 256;
    private const string OverflowHost = "(other)";
    private const string OverflowPath = "(overflow)";

    private static readonly string[] Providers = { HttpProvider, DnsProvider, TlsProvider, SocketsProvider };

    private readonly ILogger<EventPipeNetworkingCollector> _logger;
    internal Action<EventPipeEventSource>? ConfigureSource { get; init; }
    internal Func<long?, long?>? TransformReportedLoss { get; init; }
    internal Action<TraceEvent>? BeforeParse { get; init; }

    public EventPipeNetworkingCollector(ILogger<EventPipeNetworkingCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeNetworkingCollector>.Instance;
    }

    public async Task<NetworkingSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int intervalSeconds = 1,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (intervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "intervalSeconds must be >= 1.");
        }

        var counterArgs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EventCounterIntervalSec"] = intervalSeconds.ToString(CultureInfo.InvariantCulture),
        };

        var providers = Providers
            .Select(name => new EventPipeProvider(name, EventLevel.Verbose, (long)EventKeywords.All, counterArgs))
            .ToList();
        // TasksFlowActivityIds enables runtime ActivityTracker; task scheduling events are not requested.
        // ActivityTracker can remain enabled in the target after this session is stopped.
        providers.Add(new EventPipeProvider("System.Threading.Tasks.TplEventSource", EventLevel.Informational, 0x80));

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var notes = new HashSet<string>(StringComparer.Ordinal);

        long httpStarted = 0, httpStopped = 0, httpFailed = 0, connEstablished = 0, connClosed = 0, leftQueue = 0;
        long dnsStarted = 0, dnsStopped = 0, dnsFailed = 0;
        long tlsStarted = 0, tlsStopped = 0, tlsFailed = 0;
        long socketStarted = 0, socketStopped = 0, socketFailed = 0;

        var pendingHttp = new NetworkingActivityCorrelator<PendingHttp>();
        var pendingDns = new NetworkingActivityCorrelator<DateTimeOffset>();
        var pendingTls = new NetworkingActivityCorrelator<DateTimeOffset>();

        var httpDurations = new BoundedDurationSampler();
        var queueTimes = new BoundedDurationSampler();
        var dnsDurations = new BoundedDurationSampler();
        var tlsDurations = new BoundedDurationSampler();
        var byOperation = new Dictionary<string, MutableHttpGroup>(StringComparer.Ordinal);
        var overflowOperation = new MutableHttpGroup(OverflowHost, OverflowPath);
        var counters = new Dictionary<string, NetworkingCounterSample>(StringComparer.Ordinal);
        var tlsProtocols = new HashSet<string>(StringComparer.Ordinal);
        var overflowedOperations = 0;
        long httpResponseStops = 0, httpStatusErrorStops = 0, httpStopsWithoutStatus = 0;
        long parseErrors = 0;

        var completion = await EventPipeCollectionRunner.RunAsync(
            session,
            duration,
            source =>
            {
                ConfigureSource?.Invoke(source);
                source.Dynamic.All += traceEvent =>
                {
                    var provider = traceEvent.ProviderName;
                    if (!provider.StartsWith("System.Net.", StringComparison.Ordinal))
                    {
                        return;
                    }

                    try
                    {
                        BeforeParse?.Invoke(traceEvent);
                        var timestamp = ToUtcOffset(traceEvent.TimeStamp);
                        var name = traceEvent.EventName;

                        if (string.Equals(name, "EventCounters", StringComparison.Ordinal))
                        {
                            HandleCounter(traceEvent, provider, counters);
                            return;
                        }

                        switch (provider)
                        {
                            case HttpProvider:
                                HandleHttp(
                                    traceEvent, name, timestamp, pendingHttp, byOperation, overflowOperation, httpDurations, queueTimes,
                                    ref httpStarted, ref httpStopped, ref httpFailed, ref connEstablished, ref connClosed, ref leftQueue,
                                    ref overflowedOperations, ref httpResponseStops, ref httpStatusErrorStops, ref httpStopsWithoutStatus);
                                break;

                            case DnsProvider:
                                HandlePaired(
                                    name, "ResolutionStart", "Resolution/Start", "ResolutionStop", "Resolution/Stop",
                                    "ResolutionFailed", "Resolution/Failed", traceEvent.ActivityID, timestamp,
                                    pendingDns, dnsDurations, ref dnsStarted, ref dnsStopped, ref dnsFailed);
                                break;

                            case TlsProvider:
                                if (HandlePaired(
                                    name, "HandshakeStart", "Handshake/Start", "HandshakeStop", "Handshake/Stop",
                                    "HandshakeFailed", "Handshake/Failed", traceEvent.ActivityID, timestamp,
                                    pendingTls, tlsDurations, ref tlsStarted, ref tlsStopped, ref tlsFailed))
                                {
                                    var protocol = PayloadString(traceEvent, "protocol");
                                    if (!string.IsNullOrWhiteSpace(protocol))
                                    {
                                        tlsProtocols.Add(protocol);
                                    }
                                }

                                break;

                            case SocketsProvider:
                                HandleSocket(name, ref socketStarted, ref socketStopped, ref socketFailed);
                                break;
                        }
                    }
                    catch (Exception)
                    {
                        parseErrors++;
                    }
                };
            },
            ex => _logger.LogDebug(ex, "Networking EventPipe source ended for pid {Pid}.", processId),
            cancellationToken, stopOnProcessingEnd: true).ConfigureAwait(false);

        var quality = new NetworkingCaptureQuality(completion.Status,
            TransformReportedLoss is null ? completion.EventsLost : TransformReportedLoss(completion.EventsLost),
            completion.StreamReadDuration, parseErrors);
        notes.Add(quality.Describe());

        var totalEvents = httpStarted + dnsStarted + tlsStarted + socketStarted;
        if (totalEvents == 0 && counters.Count == 0)
        {
            notes.Add("No networking events or counters were captured in the window. Confirm the target makes outbound HTTP / DNS / socket calls during collection (start the session before the load).");
        }

        if (httpStarted > 0 && httpDurations.Count == 0)
        {
            notes.Add("HTTP request latency is unavailable: Start/Stop events could not be correlated by activity id in this window.");
        }

        if (httpDurations.IsApproximate
            || queueTimes.IsApproximate
            || dnsDurations.IsApproximate
            || tlsDurations.IsApproximate
            || byOperation.Values.Any(static g => g.IsApproximate))
        {
            notes.Add($"Latency percentiles are exact up to {BoundedPercentileSampler.ExactSampleCapacity} samples per aggregate and become reservoir-sampled approximations above that cap; max values remain exact.");
        }

        var operations = byOperation.Values
            .Concat(overflowOperation.Count == 0 ? Array.Empty<MutableHttpGroup>() : [overflowOperation])
            .Select(static g => g.ToRecord())
            .OrderByDescending(static g => g.TotalDuration)
            .ThenByDescending(static g => g.Count)
            .ThenBy(static g => g.Host, StringComparer.Ordinal)
            .ToList();

        if (overflowedOperations > 0)
        {
            notes.Add($"Grouped HTTP requests into at most {MaxTrackedOperationGroups} host/path buckets; {overflowedOperations} request(s) were aggregated into {OverflowHost} {OverflowPath}.");
        }

        var httpCorrelation = pendingHttp.Snapshot();
        httpCorrelation = httpCorrelation with
        {
            Counts = new Dictionary<string, long>(httpCorrelation.Counts, StringComparer.Ordinal)
            {
                ["httpResponseStops"] = httpResponseStops,
                ["httpStatusErrorStops"] = httpStatusErrorStops,
                ["httpStopsWithoutStatus"] = httpStopsWithoutStatus,
            },
        };

        var correlation = new NetworkingCorrelation(httpCorrelation, pendingDns.Snapshot(), pendingTls.Snapshot());
        notes.Add("Latency population v2: all accepted Start/Stop completions, including failed operations; paired is the sample count and pairedFailed/pairedWithoutFailure partition it. No observed failure is not proof of success. HTTP status errors are responses, not RequestFailed; failure events include cancellation/timeouts but do not reliably identify their cause. Missing/ambiguous/unfinished lifecycles have no latency sample; zero samples means unavailable, not measured zero.");
        AddCorrelationNote("HTTP", correlation.Http, notes);
        AddCorrelationNote("DNS", correlation.Dns, notes);
        AddCorrelationNote("TLS", correlation.Tls, notes);

        return new NetworkingSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            HttpRequestsStarted: httpStarted,
            HttpRequestsStopped: httpStopped,
            HttpRequestsFailed: httpFailed,
            HttpConnectionsEstablished: connEstablished,
            HttpConnectionsClosed: connClosed,
            HttpRequestsLeftQueue: leftQueue,
            HttpRequestP50: httpDurations.GetPercentile(0.50),
            HttpRequestP95: httpDurations.GetPercentile(0.95),
            HttpRequestMax: httpDurations.Max,
            TimeInQueueP50: queueTimes.GetPercentile(0.50),
            TimeInQueueP95: queueTimes.GetPercentile(0.95),
            TimeInQueueMax: queueTimes.Max,
            DnsLookupsStarted: dnsStarted,
            DnsLookupsStopped: dnsStopped,
            DnsLookupsFailed: dnsFailed,
            DnsP50: dnsDurations.GetPercentile(0.50),
            DnsP95: dnsDurations.GetPercentile(0.95),
            DnsMax: dnsDurations.Max,
            TlsHandshakesStarted: tlsStarted,
            TlsHandshakesStopped: tlsStopped,
            TlsHandshakesFailed: tlsFailed,
            TlsP50: tlsDurations.GetPercentile(0.50),
            TlsP95: tlsDurations.GetPercentile(0.95),
            TlsMax: tlsDurations.Max,
            SocketConnectsStarted: socketStarted,
            SocketConnectsStopped: socketStopped,
            SocketConnectsFailed: socketFailed,
            Counters: counters.Values.OrderBy(static c => c.Provider, StringComparer.Ordinal).ThenBy(static c => c.Name, StringComparer.Ordinal).ToList(),
            ByOperation: operations,
            TlsProtocols: tlsProtocols.OrderBy(static p => p, StringComparer.Ordinal).ToList(),
            Notes: notes.OrderBy(static note => note, StringComparer.Ordinal).ToList())
        {
            Correlation = correlation,
            CaptureQuality = quality,
        };
    }

    private static void AddCorrelationNote(string kind, NetworkingCorrelationCounts counts, HashSet<string> notes)
    {
        if (counts.HasLimitations)
        {
            notes.Add($"{kind} correlation is limited: paired={counts.Paired}/{counts.Started} observed starts; empty={counts.EmptyStarts}, ambiguous={counts.AmbiguousStarts}, expired={counts.Expired}, evicted={counts.Evicted}, failure-discarded={counts.FailureDiscarded}, unfinished={counts.Unfinished}, capacity-suppressed={counts.CapacitySuppressedStarts}; unmatched stops={counts.UnmatchedStops}, empty stops={counts.EmptyStops}, invalid-timestamp stops={counts.InvalidTimestampStops}, unmatched failures={counts.UnmatchedFailures}, invalid-timestamp failures={counts.InvalidTimestampFailures}. MaxPendingActivities=4096, TTL=2 minutes, MaxRememberedIdentities=65536; identity-capacity reached={counts.IdentityCapacityReached}. Latencies describe only accepted pairs, not all operations.");
        }
    }

    private static void HandleHttp(
        TraceEvent traceEvent,
        string name,
        DateTimeOffset timestamp,
        NetworkingActivityCorrelator<PendingHttp> pending,
        Dictionary<string, MutableHttpGroup> byOperation,
        MutableHttpGroup overflowOperation,
        BoundedDurationSampler httpDurations,
        BoundedDurationSampler queueTimes,
        ref long started,
        ref long stopped,
        ref long failed,
        ref long connEstablished,
        ref long connClosed,
        ref long leftQueue,
        ref int overflowedOperations,
        ref long responseStops,
        ref long statusErrorStops,
        ref long stopsWithoutStatus)
    {
        switch (name)
        {
            case "RequestStart":
            case "Request/Start":
                started++;
                pending.Start(traceEvent.ActivityID, timestamp, new PendingHttp(
                    timestamp,
                    BuildHost(traceEvent),
                    NormalizePath(PayloadString(traceEvent, "pathAndQuery"))));
                break;

            case "RequestStop":
            case "Request/Stop":
                stopped++;
                var status = ReadHttpStatus(traceEvent);
                if (status is >= 100 and <= 599)
                {
                    responseStops++;
                    if (status >= 400) statusErrorStops++;
                }
                else stopsWithoutStatus++;
                if (pending.Stop(traceEvent.ActivityID, timestamp, out var p, out var elapsed))
                {
                    httpDurations.Add(elapsed);
                    var key = $"{p.Host} {p.Path}";
                    if (!byOperation.TryGetValue(key, out var group))
                    {
                        if (byOperation.Count >= MaxTrackedOperationGroups)
                        {
                            overflowedOperations++;
                            overflowOperation.Add(elapsed);
                            break;
                        }

                        group = new MutableHttpGroup(p.Host, p.Path);
                        byOperation[key] = group;
                    }

                    group.Add(elapsed);
                }

                break;

            case "RequestFailed":
            case "Request/Failed":
                failed++;
                pending.Fail(traceEvent.ActivityID, timestamp);
                break;

            case "ConnectionEstablished":
                connEstablished++;
                break;

            case "ConnectionClosed":
                connClosed++;
                break;

            case "RequestLeftQueue":
                leftQueue++;
                var queueMs = PayloadDouble(traceEvent, "timeOnQueueMilliseconds");
                if (queueMs >= 0)
                {
                    queueTimes.Add(TimeSpan.FromMilliseconds(queueMs));
                }

                break;
        }
    }

    internal static bool HandlePaired(
        string name,
        string startName,
        string startSlash,
        string stopName,
        string stopSlash,
        string failedName,
        string failedSlash,
        Guid activityId,
        DateTimeOffset timestamp,
        NetworkingActivityCorrelator<DateTimeOffset> pending,
        BoundedDurationSampler durations,
        ref long started,
        ref long stopped,
        ref long failed)
    {
        if (name == startName || name == startSlash)
        {
            started++;
            pending.Start(activityId, timestamp, timestamp);
            return true;
        }

        if (name == stopName || name == stopSlash)
        {
            stopped++;
            if (pending.Stop(activityId, timestamp, out _, out var elapsed))
            {
                durations.Add(elapsed);
            }

            return true;
        }

        if (name == failedName || name == failedSlash)
        {
            failed++;
            pending.Fail(activityId, timestamp);
            return true;
        }

        return false;
    }

    private static void HandleSocket(string name, ref long started, ref long stopped, ref long failed)
    {
        switch (name)
        {
            case "ConnectStart":
            case "Connect/Start":
                started++;
                break;
            case "ConnectStop":
            case "Connect/Stop":
                stopped++;
                break;
            case "ConnectFailed":
            case "Connect/Failed":
                failed++;
                break;
        }
    }

    private static void HandleCounter(
        TraceEvent traceEvent,
        string provider,
        Dictionary<string, NetworkingCounterSample> counters)
    {
        if (traceEvent.PayloadValue(0) is not IDictionary<string, object> outer
            || !outer.TryGetValue("Payload", out var inner)
            || inner is not IDictionary<string, object> data)
        {
            return;
        }

        var name = AsString(data, "Name");
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var display = AsString(data, "DisplayName");
        var unit = data.TryGetValue("DisplayUnits", out var u) ? u as string : null;

        double value;
        if (data.TryGetValue("Mean", out var meanObj))
        {
            value = ToDouble(meanObj);
        }
        else if (data.TryGetValue("Increment", out var incObj))
        {
            value = ToDouble(incObj);
        }
        else
        {
            return;
        }

        var key = $"{provider}/{name}";
        counters[key] = new NetworkingCounterSample(
            provider,
            name,
            string.IsNullOrEmpty(display) ? name : display,
            value,
            string.IsNullOrEmpty(unit) ? null : unit);
    }

    private static string BuildHost(TraceEvent traceEvent)
    {
        var scheme = PayloadString(traceEvent, "scheme");
        var host = PayloadString(traceEvent, "host");
        var port = PayloadString(traceEvent, "port");
        if (string.IsNullOrEmpty(host))
        {
            return "(unknown)";
        }

        var prefix = string.IsNullOrEmpty(scheme) ? string.Empty : $"{scheme}://";
        return string.IsNullOrEmpty(port) ? $"{prefix}{host}" : $"{prefix}{host}:{port}";
    }

    private static int? ReadHttpStatus(TraceEvent traceEvent)
    {
        // The stable RequestStop payload uses -1 when no response was received.
        // Missing fields on another provider version remain unknown; malformed payloads hit ParseErrors.
        if (!traceEvent.PayloadNames.Contains("statusCode", StringComparer.Ordinal)) return null;
        var value = traceEvent.PayloadByName("statusCode");
        return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static string NormalizePath(string pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(pathAndQuery))
        {
            return "/";
        }

        var trimmed = pathAndQuery.Trim();
        var queryIndex = trimmed.IndexOf('?');
        return queryIndex >= 0 ? trimmed[..queryIndex] : trimmed;
    }

    private static DateTimeOffset ToUtcOffset(DateTime timestamp) =>
        new(timestamp.ToUniversalTime(), TimeSpan.Zero);

    private static string PayloadString(TraceEvent traceEvent, string name)
    {
        try
        {
            return traceEvent.PayloadByName(name)?.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static double PayloadDouble(TraceEvent traceEvent, string name)
    {
        try
        {
            var value = traceEvent.PayloadByName(name);
            return value is null ? -1 : ToDouble(value);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string AsString(IDictionary<string, object> data, string key)
        => data.TryGetValue(key, out var v) && v is string s ? s : string.Empty;

    private static double ToDouble(object value)
        => value switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            uint ui => ui,
            null => 0,
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        };

    private sealed record PendingHttp(DateTimeOffset StartedAt, string Host, string Path);

    private sealed class MutableHttpGroup
    {
        private readonly BoundedDurationSampler _durations = new();

        public MutableHttpGroup(string host, string path)
        {
            Host = host;
            Path = path;
        }

        public string Host { get; }

        public string Path { get; }

        public bool IsApproximate => _durations.IsApproximate;
        public int Count { get; private set; }
        public TimeSpan TotalDuration { get; private set; }

        public void Add(TimeSpan duration)
        {
            _durations.Add(duration);
            Count++;
            TotalDuration += duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        }

        public NetworkingHttpGroup ToRecord()
        {
            return new NetworkingHttpGroup(
                Host,
                Path,
                Count,
                TotalDuration,
                _durations.GetPercentile(0.95),
                _durations.Max);
        }
    }
}
