using System.Diagnostics.Tracing;
using System.Globalization;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Kestrel;

/// <summary>
/// Subscribes to the <c>Microsoft-AspNetCore-Server-Kestrel</c> EventSource and aggregates its
/// connection / request / TLS events plus its queue-length EventCounters into a
/// <see cref="KestrelSnapshot"/>. The Configuration event (id 11, <see cref="EventLevel.LogAlways"/>)
/// is emitted at session enable and carries the live <c>KestrelServerOptions</c> JSON for free.
/// </summary>
public sealed class EventPipeKestrelCollector : IKestrelCollector
{
    private const string KestrelProviderName = "Microsoft-AspNetCore-Server-Kestrel";
    private const string ConnectionQueueLengthCounter = "connection-queue-length";
    private const string RequestQueueLengthCounter = "request-queue-length";

    private readonly ILogger<EventPipeKestrelCollector> _logger;

    public EventPipeKestrelCollector(ILogger<EventPipeKestrelCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeKestrelCollector>.Instance;
    }

    public async Task<KestrelSnapshot> CollectAsync(
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

        var providers = new[]
        {
            new EventPipeProvider(
                KestrelProviderName,
                EventLevel.Verbose,
                (long)EventKeywords.All,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["EventCounterIntervalSec"] = intervalSeconds.ToString(CultureInfo.InvariantCulture),
                }),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var notes = new HashSet<string>(StringComparer.Ordinal);

        long connectionsStarted = 0, connectionsStopped = 0, connectionsRejected = 0;
        long requestsStarted = 0, requestsStopped = 0;
        long tlsStarted = 0, tlsStopped = 0, tlsFailed = 0;
        long peakConnectionQueue = 0, peakRequestQueue = 0;

        var pendingConnections = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var pendingRequests = new Dictionary<string, PendingRequest>(StringComparer.Ordinal);
        var pendingTls = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        var connectionDurations = new BoundedDurationSampler();
        var tlsDurations = new BoundedDurationSampler();
        var byOperation = new Dictionary<string, MutableRequestGroup>(StringComparer.Ordinal);
        var requestDurations = new BoundedDurationSampler();
        var queuePoints = new List<KestrelQueuePoint>();
        var counters = new Dictionary<string, KestrelCounterSample>(StringComparer.Ordinal);
        var tlsProtocols = new HashSet<string>(StringComparer.Ordinal);
        string? configurationJson = null;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.ProviderName, KestrelProviderName, StringComparison.Ordinal))
                    {
                        return;
                    }

                    try
                    {
                        var timestamp = ToUtcOffset(traceEvent.TimeStamp);
                        switch (traceEvent.EventName)
                        {
                            case "EventCounters":
                                HandleCounter(traceEvent, timestamp, counters, queuePoints, ref peakConnectionQueue, ref peakRequestQueue);
                                break;

                            case "ConnectionStart":
                            case "Connection/Start":
                                connectionsStarted++;
                                pendingConnections[PayloadString(traceEvent, "connectionId")] = timestamp;
                                break;

                            case "ConnectionStop":
                            case "Connection/Stop":
                                connectionsStopped++;
                                RecordPairedDuration(pendingConnections, PayloadString(traceEvent, "connectionId"), timestamp, connectionDurations);
                                break;

                            case "ConnectionRejected":
                            case "Connection/Rejected":
                                connectionsRejected++;
                                break;

                            case "RequestStart":
                            case "Request/Start":
                                requestsStarted++;
                                pendingRequests[RequestKey(traceEvent)] = new PendingRequest(
                                    timestamp,
                                    PayloadString(traceEvent, "method"),
                                    PayloadString(traceEvent, "path"),
                                    PayloadString(traceEvent, "httpVersion"));
                                break;

                            case "RequestStop":
                            case "Request/Stop":
                                requestsStopped++;
                                HandleRequestStop(traceEvent, timestamp, pendingRequests, byOperation, requestDurations);
                                break;

                            case "TlsHandshakeStart":
                            case "TlsHandshake/Start":
                                tlsStarted++;
                                pendingTls[PayloadString(traceEvent, "connectionId")] = timestamp;
                                var startProtocols = PayloadString(traceEvent, "sslProtocols");
                                if (!string.IsNullOrWhiteSpace(startProtocols))
                                {
                                    tlsProtocols.Add(startProtocols);
                                }

                                break;

                            case "TlsHandshakeStop":
                            case "TlsHandshake/Stop":
                                tlsStopped++;
                                RecordPairedDuration(pendingTls, PayloadString(traceEvent, "connectionId"), timestamp, tlsDurations);
                                var stopProtocols = PayloadString(traceEvent, "sslProtocols");
                                if (!string.IsNullOrWhiteSpace(stopProtocols))
                                {
                                    tlsProtocols.Add(stopProtocols);
                                }

                                break;

                            case "TlsHandshakeFailed":
                            case "TlsHandshake/Failed":
                                tlsFailed++;
                                pendingTls.Remove(PayloadString(traceEvent, "connectionId"));
                                break;

                            case "Configuration":
                                var config = PayloadString(traceEvent, "configuration");
                                if (!string.IsNullOrWhiteSpace(config))
                                {
                                    configurationJson = config;
                                }

                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"Warning: failed to parse {traceEvent.EventName}: {ex.GetType().Name}.");
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kestrel EventPipe source ended for pid {Pid}.", processId);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            try { await processingTask.ConfigureAwait(false); } catch (Exception) { }
            session.Dispose();
        }

        var totalEvents = connectionsStarted + requestsStarted + tlsStarted;
        if (totalEvents == 0 && counters.Count == 0)
        {
            notes.Add("No Kestrel events or counters were captured in the window. Confirm the target hosts an ASP.NET Core app on Kestrel and that traffic flows during collection (start the session before the load).");
        }

        if (configurationJson is null)
        {
            notes.Add("No Configuration event was observed; KestrelServerOptions JSON is unavailable (the event fires at session enable for each registered server — the target may not have an active Kestrel server).");
        }

        if (requestDurations.IsApproximate
            || tlsDurations.IsApproximate
            || connectionDurations.IsApproximate
            || byOperation.Values.Any(static g => g.IsApproximate))
        {
            notes.Add($"Latency percentiles are exact up to {BoundedPercentileSampler.ExactSampleCapacity} samples per aggregate and become reservoir-sampled approximations above that cap; max values remain exact.");
        }

        var operations = byOperation.Values
            .Select(static g => g.ToRecord())
            .OrderByDescending(static g => g.TotalDuration)
            .ThenByDescending(static g => g.Count)
            .ThenBy(static g => g.Path, StringComparer.Ordinal)
            .ToList();

        return new KestrelSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            ConnectionsStarted: connectionsStarted,
            ConnectionsStopped: connectionsStopped,
            ConnectionsRejected: connectionsRejected,
            RequestsStarted: requestsStarted,
            RequestsStopped: requestsStopped,
            TlsHandshakesStarted: tlsStarted,
            TlsHandshakesStopped: tlsStopped,
            TlsHandshakesFailed: tlsFailed,
            PeakConnectionQueueLength: peakConnectionQueue,
            PeakRequestQueueLength: peakRequestQueue,
            RequestP50: requestDurations.GetPercentile(0.50),
            RequestP95: requestDurations.GetPercentile(0.95),
            RequestMax: requestDurations.Max,
            TlsHandshakeP50: tlsDurations.GetPercentile(0.50),
            TlsHandshakeP95: tlsDurations.GetPercentile(0.95),
            TlsHandshakeMax: tlsDurations.Max,
            ConnectionDurationP50: connectionDurations.GetPercentile(0.50),
            ConnectionDurationP95: connectionDurations.GetPercentile(0.95),
            ConnectionDurationMax: connectionDurations.Max,
            Counters: counters.Values.OrderBy(static c => c.Name, StringComparer.Ordinal).ToList(),
            QueuePoints: queuePoints,
            ByOperation: operations,
            TlsProtocols: tlsProtocols.OrderBy(static p => p, StringComparer.Ordinal).ToList(),
            ConfigurationJson: configurationJson,
            Notes: notes.OrderBy(static note => note, StringComparer.Ordinal).ToList());
    }

    private static void HandleRequestStop(
        TraceEvent traceEvent,
        DateTimeOffset timestamp,
        Dictionary<string, PendingRequest> pendingRequests,
        Dictionary<string, MutableRequestGroup> byOperation,
        BoundedDurationSampler requestDurations)
    {
        var key = RequestKey(traceEvent);
        if (!pendingRequests.Remove(key, out var pending))
        {
            return;
        }

        var elapsed = timestamp - pending.StartedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        requestDurations.Add(elapsed);

        var groupKey = $"{pending.Method} {pending.Path} {pending.HttpVersion}";
        if (!byOperation.TryGetValue(groupKey, out var group))
        {
            group = new MutableRequestGroup(pending.Method, pending.Path, pending.HttpVersion);
            byOperation[groupKey] = group;
        }

        group.Add(elapsed);
    }

    private static void HandleCounter(
        TraceEvent traceEvent,
        DateTimeOffset timestamp,
        Dictionary<string, KestrelCounterSample> counters,
        List<KestrelQueuePoint> queuePoints,
        ref long peakConnectionQueue,
        ref long peakRequestQueue)
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

        counters[name] = new KestrelCounterSample(
            name,
            string.IsNullOrEmpty(display) ? name : display,
            value,
            string.IsNullOrEmpty(unit) ? null : unit);

        if (string.Equals(name, ConnectionQueueLengthCounter, StringComparison.Ordinal))
        {
            queuePoints.Add(new KestrelQueuePoint(timestamp, name, value));
            peakConnectionQueue = Math.Max(peakConnectionQueue, (long)Math.Round(value));
        }
        else if (string.Equals(name, RequestQueueLengthCounter, StringComparison.Ordinal))
        {
            queuePoints.Add(new KestrelQueuePoint(timestamp, name, value));
            peakRequestQueue = Math.Max(peakRequestQueue, (long)Math.Round(value));
        }
    }

    private static void RecordPairedDuration(
        Dictionary<string, DateTimeOffset> pending,
        string key,
        DateTimeOffset stoppedAt,
        BoundedDurationSampler durations)
    {
        if (string.IsNullOrEmpty(key) || !pending.Remove(key, out var startedAt))
        {
            return;
        }

        durations.Add(stoppedAt - startedAt);
    }

    private static string RequestKey(TraceEvent traceEvent)
        => $"{PayloadString(traceEvent, "connectionId")}:{PayloadString(traceEvent, "requestId")}";

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

    private static string AsString(IDictionary<string, object> data, string key)
        => data.TryGetValue(key, out var v) && v is string s ? s : string.Empty;

    private static double ToDouble(object value)
        => value switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            null => 0,
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        };

    private sealed record PendingRequest(DateTimeOffset StartedAt, string Method, string Path, string HttpVersion);

    private sealed class MutableRequestGroup
    {
        private readonly BoundedDurationSampler _durations = new();

        public MutableRequestGroup(string method, string path, string httpVersion)
        {
            Method = method;
            Path = path;
            HttpVersion = httpVersion;
        }

        public string Method { get; }

        public string Path { get; }

        public string HttpVersion { get; }

        public bool IsApproximate => _durations.IsApproximate;
        public int Count { get; private set; }
        public TimeSpan TotalDuration { get; private set; }

        public void Add(TimeSpan duration)
        {
            _durations.Add(duration);
            Count++;
            TotalDuration += duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        }

        public KestrelRequestGroup ToRecord()
        {
            return new KestrelRequestGroup(
                Method,
                Path,
                HttpVersion,
                Count,
                TotalDuration,
                _durations.GetPercentile(0.95),
                _durations.Max);
        }
    }
}
