using System.Collections;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DotnetDiagnosticsMcp.Core.Security;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Db;

public sealed class EventPipeDbCollector : IDbCollector
{
    private const string DiagnosticSourceProviderName = "Microsoft-Diagnostics-DiagnosticSource";
    private const string EfCoreSourceName = "Microsoft.EntityFrameworkCore";
    private const string FilterArgumentName = "FilterAndPayloadSpecs";
    private const string EfCoreStopBridgeEvent = "Stop";
    private const long DiagnosticSourceKeywords = 0x1 | 0x2;
    private const string ActivityTransformSuffix = ":-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;Tags=TagObjects.*Enumerate";
    private const string MicrosoftSqlClientProviderName = "Microsoft.Data.SqlClient.EventSource";
    private const string SystemSqlClientProviderName = "System.Data.SqlClient.EventSource";
    private const int NPlusOneThreshold = 10;

    private readonly SensitiveDataRedactor _redactor;
    private readonly ILogger<EventPipeDbCollector> _logger;

    public EventPipeDbCollector(SensitiveDataRedactor redactor, ILogger<EventPipeDbCollector>? logger = null)
    {
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _logger = logger ?? NullLogger<EventPipeDbCollector>.Instance;
    }

    public async Task<DbSnapshot> CollectAsync(
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
                DiagnosticSourceProviderName,
                EventLevel.Verbose,
                DiagnosticSourceKeywords,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [FilterArgumentName] = $"[AS]{EfCoreSourceName}/{EfCoreStopBridgeEvent}{ActivityTransformSuffix}",
                }),
            new EventPipeProvider(
                MicrosoftSqlClientProviderName,
                EventLevel.Verbose,
                (long)EventKeywords.All,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["EventCounterIntervalSec"] = intervalSeconds.ToString(CultureInfo.InvariantCulture),
                }),
            new EventPipeProvider(
                SystemSqlClientProviderName,
                EventLevel.Verbose,
                (long)EventKeywords.All,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["EventCounterIntervalSec"] = intervalSeconds.ToString(CultureInfo.InvariantCulture),
                }),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB: 128, cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var pendingCommandsByProviderAndObjectId = new Dictionary<string, PendingCommand>(StringComparer.Ordinal);
        var aggregates = new Dictionary<string, MutableCommandAggregate>(StringComparer.Ordinal);
        var nPlusOne = new Dictionary<string, MutableNPlusOne>(StringComparer.Ordinal);
        var connectionPools = new Dictionary<string, MutableConnectionPoolStats>(StringComparer.Ordinal);
        var notes = new HashSet<string>(StringComparer.Ordinal);
        long totalCommands = 0;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    try
                    {
                        switch (traceEvent.ProviderName)
                        {
                            case DiagnosticSourceProviderName:
                                HandleEfCoreEvent(traceEvent, aggregates, nPlusOne, ref totalCommands);
                                break;
                            case MicrosoftSqlClientProviderName:
                            case SystemSqlClientProviderName:
                                HandleSqlClientEvent(traceEvent, pendingCommandsByProviderAndObjectId, aggregates, nPlusOne, connectionPools, notes, ref totalCommands);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"Warning: failed to parse {traceEvent.ProviderName}/{traceEvent.EventName}: {ex.GetType().Name}.");
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DB EventPipe source ended for pid {Pid}.", processId);
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

        var byCommand = aggregates.Values
            .Select(static aggregate => aggregate.ToRecord())
            .OrderByDescending(static aggregate => aggregate.TotalMs)
            .ThenByDescending(static aggregate => aggregate.Count)
            .ThenBy(static aggregate => aggregate.CommandTextHash, StringComparer.Ordinal)
            .ToList();

        var nPlusOneIncidents = nPlusOne.Values
            .Where(static incident => incident.Count > NPlusOneThreshold)
            .Select(static incident => incident.ToRecord())
            .OrderByDescending(static incident => incident.Count)
            .ThenBy(static incident => incident.ScopeId, StringComparer.Ordinal)
            .ToList();

        var connectionPool = connectionPools.Values
            .Select(static stats => stats.ToRecord())
            .OrderBy(static stats => stats.Provider, StringComparer.Ordinal)
            .ToList();

        if (byCommand.Count == 0)
        {
            notes.Add("No EF Core or SqlClient commands were observed in the collection window.");
        }

        return new DbSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalCommands: totalCommands,
            ByCommand: byCommand,
            NPlusOne: nPlusOneIncidents,
            ConnectionPool: connectionPool,
            Notes: notes.OrderBy(static note => note, StringComparer.Ordinal).ToList());
    }

    private void HandleEfCoreEvent(
        TraceEvent traceEvent,
        Dictionary<string, MutableCommandAggregate> aggregates,
        Dictionary<string, MutableNPlusOne> nPlusOne,
        ref long totalCommands)
    {
        if (!traceEvent.EventName.EndsWith(EfCoreStopBridgeEvent, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var arguments = ExtractArguments(traceEvent.PayloadByName("Arguments"));
        var sourceName = FirstNonEmpty(ConvertToString(traceEvent.PayloadByName("ActivitySourceName")), ConvertToString(traceEvent.PayloadByName("SourceName")));
        if (!string.Equals(sourceName, EfCoreSourceName, StringComparison.Ordinal))
        {
            return;
        }

        var tags = ParseTagPairs(GetArgument(arguments, "Tags"));
        var rawCommandText = FirstNonEmpty(GetTag(tags, "db.statement"), GetTag(tags, "db.query.text"), GetTag(tags, "db.command.text"));
        if (string.IsNullOrWhiteSpace(rawCommandText))
        {
            return;
        }

        var sanitizedCommandText = _redactor.RedactSqlText(rawCommandText) ?? string.Empty;
        var rawConnectionString = FirstNonEmpty(
            GetTag(tags, "db.connection_string"),
            BuildConnectionString(GetTag(tags, "server.address"), FirstNonEmpty(GetTag(tags, "db.name"), GetTag(tags, "db.namespace"))));
        var sanitizedConnectionString = _redactor.Redact(rawConnectionString) ?? string.Empty;
        var stoppedAt = new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero);
        var duration = ParseDuration(arguments);
        var startedAt = ParseStartedAt(arguments) ?? (duration is { } observedDuration ? stoppedAt - observedDuration : stoppedAt);
        var pending = new PendingCommand(
            Provider: EfCoreSourceName,
            Key: BuildAggregateKey(sanitizedCommandText, sanitizedConnectionString),
            CommandTextHash: HashCommandText(sanitizedCommandText),
            CommandTextSanitized: sanitizedCommandText,
            ConnectionStringSanitized: sanitizedConnectionString,
            ScopeId: BuildScopeId(GetArgument(arguments, "TraceId"), GetArgument(arguments, "ParentSpanId"), traceEvent.RelatedActivityID, traceEvent.ActivityID),
            StartedAt: startedAt);

        CompleteCommand(pending, stoppedAt, Math.Max(0, (duration ?? (stoppedAt - startedAt)).TotalMilliseconds), aggregates, nPlusOne, ref totalCommands);
    }

    private void HandleSqlClientEvent(
        TraceEvent traceEvent,
        Dictionary<string, PendingCommand> pendingCommandsByProviderAndObjectId,
        Dictionary<string, MutableCommandAggregate> aggregates,
        Dictionary<string, MutableNPlusOne> nPlusOne,
        Dictionary<string, MutableConnectionPoolStats> connectionPools,
        HashSet<string> notes,
        ref long totalCommands)
    {
        if (string.Equals(traceEvent.EventName, "EventCounters", StringComparison.Ordinal))
        {
            var payload = ExtractCounterPayload(traceEvent);
            if (payload is not null)
            {
                var stats = GetOrAddPoolStats(connectionPools, traceEvent.ProviderName);
                stats.ObserveCounter(payload.Name, payload.Value);
            }

            return;
        }

        if (string.Equals(traceEvent.EventName, "BeginExecute", StringComparison.Ordinal))
        {
            var objectId = PayloadInt32(traceEvent, 0);
            if (objectId == 0)
            {
                return;
            }

            var sanitizedCommandText = _redactor.RedactSqlText(PayloadString(traceEvent, 3)) ?? string.Empty;
            var sanitizedConnectionString = _redactor.Redact(BuildConnectionString(PayloadString(traceEvent, 1), PayloadString(traceEvent, 2))) ?? string.Empty;
            pendingCommandsByProviderAndObjectId[BuildProviderObjectKey(traceEvent.ProviderName, objectId)] = new PendingCommand(
                Provider: traceEvent.ProviderName,
                Key: BuildAggregateKey(sanitizedCommandText, sanitizedConnectionString),
                CommandTextHash: HashCommandText(sanitizedCommandText),
                CommandTextSanitized: sanitizedCommandText,
                ConnectionStringSanitized: sanitizedConnectionString,
                ScopeId: BuildScopeId(null, null, traceEvent.RelatedActivityID, traceEvent.ActivityID),
                StartedAt: new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero));
            return;
        }

        if (string.Equals(traceEvent.EventName, "EndExecute", StringComparison.Ordinal))
        {
            var objectId = PayloadInt32(traceEvent, 0);
            var key = BuildProviderObjectKey(traceEvent.ProviderName, objectId);
            if (pendingCommandsByProviderAndObjectId.Remove(key, out var pending))
            {
                var stoppedAt = new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero);
                CompleteCommand(pending, stoppedAt, Math.Max(0, (stoppedAt - pending.StartedAt).TotalMilliseconds), aggregates, nPlusOne, ref totalCommands);
            }

            return;
        }

        var message = ExtractFreeformMessage(traceEvent);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (LooksLikePoolExhausted(message))
        {
            var stats = GetOrAddPoolStats(connectionPools, traceEvent.ProviderName);
            stats.PoolExhaustedCount++;
            notes.Add($"Connection pool exhaustion signalled by {traceEvent.ProviderName}/{traceEvent.EventName}.");
        }
    }

    private static MutableConnectionPoolStats GetOrAddPoolStats(
        Dictionary<string, MutableConnectionPoolStats> connectionPools,
        string providerName)
    {
        if (!connectionPools.TryGetValue(providerName, out var stats))
        {
            stats = new MutableConnectionPoolStats(providerName);
            connectionPools[providerName] = stats;
        }

        return stats;
    }

    private static bool LooksLikePoolExhausted(string message) =>
        (message.Contains("connection from the pool", StringComparison.OrdinalIgnoreCase)
            && message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        || message.Contains("pool exhausted", StringComparison.OrdinalIgnoreCase)
        || message.Contains("max pool size", StringComparison.OrdinalIgnoreCase);

    private static string ExtractFreeformMessage(TraceEvent traceEvent)
    {
        foreach (var payloadName in traceEvent.PayloadNames ?? Array.Empty<string>())
        {
            var value = traceEvent.PayloadByName(payloadName);
            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        return string.Empty;
    }

    private static DbCounterPayload? ExtractCounterPayload(TraceEvent traceEvent)
    {
        if (traceEvent.PayloadValue(0) is not IDictionary<string, object> outer)
        {
            return null;
        }

        if (!outer.TryGetValue("Payload", out var inner) || inner is not IDictionary<string, object> data)
        {
            return null;
        }

        var name = AsString(data, "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (data.TryGetValue("Mean", out var meanObj))
        {
            return new DbCounterPayload(name, ToDouble(meanObj));
        }

        if (data.TryGetValue("Increment", out var incrementObj))
        {
            return new DbCounterPayload(name, ToDouble(incrementObj));
        }

        return null;
    }

    private static string AsString(IDictionary<string, object> data, string key)
        => data.TryGetValue(key, out var value) && value is string s ? s : string.Empty;

    private static string BuildProviderObjectKey(string providerName, int objectId)
        => string.Create(CultureInfo.InvariantCulture, $"{providerName}/{objectId}");

    private static string BuildAggregateKey(string commandText, string connectionString)
        => string.Create(CultureInfo.InvariantCulture, $"{HashCommandText(commandText)}\u001f{connectionString}");

    private static string BuildScopeId(string? traceId, string? parentSpanId, Guid relatedActivityId, Guid activityId)
    {
        if (!string.IsNullOrWhiteSpace(traceId) && !string.IsNullOrWhiteSpace(parentSpanId))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{traceId}:{parentSpanId}");
        }

        if (!string.IsNullOrWhiteSpace(traceId))
        {
            return traceId!;
        }

        if (relatedActivityId != Guid.Empty)
        {
            return relatedActivityId.ToString("N");
        }

        return activityId != Guid.Empty
            ? activityId.ToString("N")
            : "global";
    }

    private static string BuildConnectionString(string? dataSource, string? database)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(dataSource))
        {
            parts.Add($"Data Source={dataSource}");
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            parts.Add($"Database={database}");
        }

        return parts.Count == 0 ? string.Empty : string.Join(';', parts);
    }

    private static string HashCommandText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static IReadOnlyDictionary<string, string> ExtractArguments(object? payload)
    {
        if (payload is null)
        {
            return EmptyArguments.Instance;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload is IEnumerable enumerable && payload is not string)
        {
            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                if (TryGetKeyValue(item, out var key, out var value))
                {
                    result[key] = value;
                }
            }
        }

        return result;
    }

    private static bool TryGetKeyValue(object item, out string key, out string value)
    {
        switch (item)
        {
            case IDictionary<string, object> dictionary:
                key = dictionary.TryGetValue("Key", out var dictionaryKey) ? ConvertToString(dictionaryKey) : string.Empty;
                value = dictionary.TryGetValue("Value", out var dictionaryValue) ? ConvertToString(dictionaryValue) : string.Empty;
                return !string.IsNullOrWhiteSpace(key);
            case DictionaryEntry entry:
                key = ConvertToString(entry.Key);
                value = ConvertToString(entry.Value);
                return !string.IsNullOrWhiteSpace(key);
            case IDictionary nonGenericDictionary:
                key = nonGenericDictionary.Contains("Key") ? ConvertToString(nonGenericDictionary["Key"]) : string.Empty;
                value = nonGenericDictionary.Contains("Value") ? ConvertToString(nonGenericDictionary["Value"]) : string.Empty;
                return !string.IsNullOrWhiteSpace(key);
            default:
                var type = item.GetType();
                var keyProperty = type.GetProperty("Key");
                var valueProperty = type.GetProperty("Value");
                if (keyProperty is not null && valueProperty is not null)
                {
                    key = ConvertToString(keyProperty.GetValue(item));
                    value = ConvertToString(valueProperty.GetValue(item));
                    return !string.IsNullOrWhiteSpace(key);
                }

                key = string.Empty;
                value = string.Empty;
                return false;
        }
    }

    private static string ConvertToString(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string? GetArgument(IReadOnlyDictionary<string, string> arguments, string key)
        => arguments.TryGetValue(key, out var value) ? value : null;

    private static string? GetTag(IReadOnlyDictionary<string, string> tags, string key)
        => tags.TryGetValue(key, out var value) ? value : null;

    private static IReadOnlyDictionary<string, string> ParseTagPairs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return EmptyArguments.Instance;
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in TagPairRegex().Matches(raw))
        {
            var content = match.Groups[1].Value;
            var separator = content.IndexOf(", ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = content[..separator];
            var value = content[(separator + 2)..];
            if (!string.IsNullOrWhiteSpace(key))
            {
                tags[key] = value;
            }
        }

        return tags;
    }

    private static TimeSpan? ParseDuration(IReadOnlyDictionary<string, string> arguments)
    {
        var rawTicks = GetArgument(arguments, "DurationTicks");
        return long.TryParse(rawTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks >= 0
            ? TimeSpan.FromTicks(ticks)
            : null;
    }

    private static DateTimeOffset? ParseStartedAt(IReadOnlyDictionary<string, string> arguments)
    {
        var rawTicks = GetArgument(arguments, "StartTimeTicks");
        return long.TryParse(rawTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0
            ? new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc), TimeSpan.Zero)
            : null;
    }

    private static string PayloadString(TraceEvent traceEvent, int index)
        => traceEvent.PayloadValue(index)?.ToString() ?? string.Empty;

    private static int PayloadInt32(TraceEvent traceEvent, int index)
        => traceEvent.PayloadValue(index) switch
        {
            int i => i,
            long l => checked((int)l),
            null => 0,
            var value => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        };

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    private static void CompleteCommand(
        PendingCommand pending,
        DateTimeOffset stoppedAt,
        double durationMs,
        Dictionary<string, MutableCommandAggregate> aggregates,
        Dictionary<string, MutableNPlusOne> nPlusOne,
        ref long totalCommands)
    {
        totalCommands++;

        if (!aggregates.TryGetValue(pending.Key, out var aggregate))
        {
            aggregate = new MutableCommandAggregate(
                pending.CommandTextHash,
                pending.CommandTextSanitized,
                pending.ConnectionStringSanitized,
                pending.Provider,
                pending.StartedAt,
                stoppedAt);
            aggregates[pending.Key] = aggregate;
        }

        aggregate.AddObservation(pending.Provider, durationMs, pending.StartedAt, stoppedAt);

        var nPlusOneKey = string.Create(CultureInfo.InvariantCulture, $"{pending.ScopeId}\u001f{pending.CommandTextHash}\u001f{pending.ConnectionStringSanitized}");
        if (!nPlusOne.TryGetValue(nPlusOneKey, out var incident))
        {
            incident = new MutableNPlusOne(
                pending.ScopeId,
                pending.CommandTextHash,
                pending.CommandTextSanitized,
                pending.ConnectionStringSanitized,
                pending.Provider,
                pending.StartedAt,
                stoppedAt);
            nPlusOne[nPlusOneKey] = incident;
        }

        incident.AddObservation(pending.Provider, pending.StartedAt, stoppedAt);
    }

    private sealed record PendingCommand(
        string Provider,
        string Key,
        string CommandTextHash,
        string CommandTextSanitized,
        string ConnectionStringSanitized,
        string ScopeId,
        DateTimeOffset StartedAt);

    private sealed class MutableCommandAggregate
    {
        private readonly List<double> _durationsMs = new();
        private readonly HashSet<string> _providers = new(StringComparer.Ordinal);

        public MutableCommandAggregate(
            string commandTextHash,
            string commandTextSanitized,
            string connectionStringSanitized,
            string provider,
            DateTimeOffset firstSeenAt,
            DateTimeOffset lastSeenAt)
        {
            CommandTextHash = commandTextHash;
            CommandTextSanitized = commandTextSanitized;
            ConnectionStringSanitized = connectionStringSanitized;
            _providers.Add(provider);
            FirstSeenAt = firstSeenAt;
            LastSeenAt = lastSeenAt;
        }

        public string CommandTextHash { get; }
        public string CommandTextSanitized { get; }
        public string ConnectionStringSanitized { get; }
        public long Count { get; private set; }
        public double TotalMs { get; private set; }
        public double MaxMs { get; private set; }
        public DateTimeOffset FirstSeenAt { get; private set; }
        public DateTimeOffset LastSeenAt { get; private set; }

        public void AddObservation(string provider, double durationMs, DateTimeOffset startedAt, DateTimeOffset stoppedAt)
        {
            Count++;
            TotalMs += durationMs;
            MaxMs = Math.Max(MaxMs, durationMs);
            _durationsMs.Add(durationMs);
            _providers.Add(provider);
            if (startedAt < FirstSeenAt)
            {
                FirstSeenAt = startedAt;
            }

            if (stoppedAt > LastSeenAt)
            {
                LastSeenAt = stoppedAt;
            }
        }

        public DbCommandAggregate ToRecord()
        {
            _durationsMs.Sort();
            var percentileIndex = Math.Max(0, (int)Math.Ceiling(_durationsMs.Count * 0.95) - 1);
            var p95Ms = _durationsMs.Count == 0 ? 0 : _durationsMs[percentileIndex];
            return new DbCommandAggregate(
                CommandTextHash,
                CommandTextSanitized,
                ConnectionStringSanitized,
                _providers.OrderBy(static provider => provider, StringComparer.Ordinal).ToList(),
                Count,
                TotalMs,
                MaxMs,
                p95Ms,
                FirstSeenAt,
                LastSeenAt);
        }
    }

    private sealed class MutableNPlusOne
    {
        private readonly HashSet<string> _providers = new(StringComparer.Ordinal);

        public MutableNPlusOne(
            string scopeId,
            string commandTextHash,
            string commandTextSanitized,
            string connectionStringSanitized,
            string provider,
            DateTimeOffset firstSeenAt,
            DateTimeOffset lastSeenAt)
        {
            ScopeId = scopeId;
            CommandTextHash = commandTextHash;
            CommandTextSanitized = commandTextSanitized;
            ConnectionStringSanitized = connectionStringSanitized;
            _providers.Add(provider);
            FirstSeenAt = firstSeenAt;
            LastSeenAt = lastSeenAt;
        }

        public string ScopeId { get; }
        public string CommandTextHash { get; }
        public string CommandTextSanitized { get; }
        public string ConnectionStringSanitized { get; }
        public int Count { get; private set; }
        public DateTimeOffset FirstSeenAt { get; private set; }
        public DateTimeOffset LastSeenAt { get; private set; }

        public void AddObservation(string provider, DateTimeOffset startedAt, DateTimeOffset stoppedAt)
        {
            Count++;
            _providers.Add(provider);
            if (startedAt < FirstSeenAt)
            {
                FirstSeenAt = startedAt;
            }

            if (stoppedAt > LastSeenAt)
            {
                LastSeenAt = stoppedAt;
            }
        }

        public DbNPlusOneIncident ToRecord() => new(
            ScopeId,
            CommandTextHash,
            CommandTextSanitized,
            ConnectionStringSanitized,
            _providers.OrderBy(static provider => provider, StringComparer.Ordinal).ToList(),
            Count,
            FirstSeenAt,
            LastSeenAt);
    }

    private sealed class MutableConnectionPoolStats
    {
        private readonly HashSet<string> _notes = new(StringComparer.Ordinal);

        public MutableConnectionPoolStats(string provider)
        {
            Provider = provider;
        }

        public string Provider { get; }
        public double? LatestOpenConnections { get; private set; }
        public double? MaxOpenConnections { get; private set; }
        public double? LatestPooledConnections { get; private set; }
        public double? MaxPooledConnections { get; private set; }
        public int PoolExhaustedCount { get; set; }

        public void ObserveCounter(string name, double value)
        {
            switch (name)
            {
                case "active-hard-connections":
                    LatestOpenConnections = value;
                    MaxOpenConnections = Math.Max(MaxOpenConnections ?? value, value);
                    break;
                case "number-of-pooled-connections":
                    LatestPooledConnections = value;
                    MaxPooledConnections = Math.Max(MaxPooledConnections ?? value, value);
                    break;
                default:
                    _notes.Add($"Observed {name}={value.ToString(CultureInfo.InvariantCulture)}.");
                    break;
            }
        }

        public DbConnectionPoolStats ToRecord() => new(
            Provider,
            LatestOpenConnections,
            MaxOpenConnections,
            LatestPooledConnections,
            MaxPooledConnections,
            PoolExhaustedCount,
            _notes.OrderBy(static note => note, StringComparer.Ordinal).ToList());
    }

    private static Regex TagPairRegex() => new(@"\[(.*?)\]", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private sealed record DbCounterPayload(string Name, double Value);

    private sealed class EmptyArguments : Dictionary<string, string>
    {
        public static EmptyArguments Instance { get; } = new();
    }
}
