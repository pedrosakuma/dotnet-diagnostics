using System.Globalization;

using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.CaptureRecording;

namespace DotnetDiagnostics.Core.Db;

internal sealed class DbEventAggregationState(ICaptureObservationSink? observationSink = null)
{
    private const int NPlusOneThreshold = 10;
    internal const int MaxTrackedPendingCommands = 2048;
    internal const int MaxTrackedCommandAggregates = 256;
    internal const int MaxTrackedNPlusOneIncidents = 256;
    internal const int MaxRecordedCommandIdentities = 65536;
    private static readonly TimeSpan PendingCommandTtl = TimeSpan.FromMinutes(2);
    internal const string OverflowCommandTextHash = "(overflow)";
    private const string OverflowCommandText = "(overflow: additional distinct command shapes omitted)";
    private const string OverflowConnectionString = "(multiple)";

    private readonly Dictionary<string, PendingCommand> _pendingCommandsByProviderAndObjectId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutableCommandAggregate> _aggregates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutableNPlusOne> _nPlusOne = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutableConnectionPoolStats> _connectionPools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notes = new(StringComparer.Ordinal);
    private readonly HashSet<string>? _recordedCommandIdentities = observationSink is null ? null : new(StringComparer.Ordinal);
    private MutableCommandAggregate? _overflowAggregate;
    private int _expiredPendingCommands;
    private int _evictedPendingCommands;
    private int _overflowedCommandShapes;
    private int _overflowedNPlusOneKeys;

    public long TotalCommands { get; private set; }
    internal int TrackedPendingCommandCount => _pendingCommandsByProviderAndObjectId.Count;
    internal int TrackedAggregateCount => _aggregates.Count;
    internal int TrackedNPlusOneCount => _nPlusOne.Count;

    public void SetPendingCommand(string providerObjectKey, PendingCommand pending)
    {
        if (_recordedCommandIdentities is { } identities)
        {
            // SqlClient IDs can be reused. Preserve the legacy snapshot, but do not label
            // an ambiguous last-start-wins match as a durable command completion.
            pending = pending with
            {
                CapturePairingUncertain = identities.Count >= MaxRecordedCommandIdentities || !identities.Add(providerObjectKey),
            };
        }
        ExpirePendingCommands(pending.StartedAt);
        if (!_pendingCommandsByProviderAndObjectId.ContainsKey(providerObjectKey))
        {
            while (_pendingCommandsByProviderAndObjectId.Count >= MaxTrackedPendingCommands)
            {
                RemoveOldestPendingCommand();
                _evictedPendingCommands++;
            }
        }

        _pendingCommandsByProviderAndObjectId[providerObjectKey] = pending;
    }

    public bool TryCompletePendingCommand(string providerObjectKey, DateTimeOffset stoppedAt, long? threadId = null)
    {
        ExpirePendingCommands(stoppedAt);
        if (!_pendingCommandsByProviderAndObjectId.Remove(providerObjectKey, out var pending))
        {
            if (_recordedCommandIdentities is { Count: < MaxRecordedCommandIdentities } identities)
                identities.Add(providerObjectKey);
            return false;
        }

        if (observationSink is not null && stoppedAt < pending.StartedAt)
            pending = pending with { CapturePairingUncertain = true };
        CompleteCommand(pending, stoppedAt, Math.Max(0, (stoppedAt - pending.StartedAt).TotalMilliseconds), threadId);
        return true;
    }

    public void CompleteCommand(PendingCommand pending, DateTimeOffset stoppedAt, double durationMs, long? threadId = null)
    {
        TotalCommands++;
        observationSink?.TryAppend(ProviderObservationProjection.Create(
            pending.CapturePairingUncertain ? "db.command.ambiguous-aggregate-input" : "db.command.completion",
            stoppedAt, threadId, pending.CommandTextSanitized,
            ("provider", pending.Provider), ("commandTextSanitized", pending.CommandTextSanitized),
            ("commandTextHash", pending.CommandTextHash), ("connectionStringSanitized", pending.ConnectionStringSanitized),
            ("scopeId", pending.ScopeId), ("startedAtUtc", pending.CapturePairingUncertain || pending.CaptureTimingUnavailable ? null : pending.StartedAt),
            ("durationMs", pending.CapturePairingUncertain || pending.CaptureTimingUnavailable ? null : durationMs),
            ("timingUnavailable", pending.CaptureTimingUnavailable),
            ("correlation", pending.CapturePairingUncertain ? "ambiguous-or-identity-capacity; snapshot uses last-start-wins" : "provider-parser"),
            ("maxRecordedCommandIdentities", MaxRecordedCommandIdentities)));

        var aggregate = GetOrCreateAggregate(pending, stoppedAt);
        aggregate.AddObservation(pending.Provider, durationMs, pending.StartedAt, stoppedAt);

        var nPlusOneKey = string.Create(CultureInfo.InvariantCulture, $"{pending.ScopeId}\u001f{pending.CommandTextHash}\u001f{pending.ConnectionStringSanitized}");
        var incident = GetOrCreateNPlusOne(nPlusOneKey, pending, stoppedAt);
        incident?.AddObservation(pending.Provider, pending.StartedAt, stoppedAt);
    }

    public MutableConnectionPoolStats GetOrAddPoolStats(string providerName)
    {
        if (!_connectionPools.TryGetValue(providerName, out var stats))
        {
            stats = new MutableConnectionPoolStats(providerName);
            _connectionPools[providerName] = stats;
        }

        return stats;
    }

    internal void RecordPoolCounter(string provider, string name, double value, DateTimeOffset timestamp, long threadId)
    {
        GetOrAddPoolStats(provider).ObserveCounter(name, value);
        observationSink?.TryAppend(ProviderObservationProjection.Create(
            "db.pool.counter.aggregate", timestamp, threadId, name,
            ("provider", provider), ("value", value), ("unit", null)));
    }

    public void AddNote(string note) => _notes.Add(note);
    internal void ReportSourceLoss(long? count) => observationSink?.ReportSourceLoss("db", count);

    public DbSnapshot BuildSnapshot(int processId, DateTimeOffset startedAt, TimeSpan duration)
    {
        ExpirePendingCommands(startedAt + duration);
        observationSink?.TryAppend(ProviderObservationProjection.Create(
            "db.retention.aggregate", null, null, "db",
            ("expiredPendingCommands", _expiredPendingCommands), ("evictedPendingCommands", _evictedPendingCommands),
            ("overflowedCommandShapes", _overflowedCommandShapes), ("overflowedNPlusOneKeys", _overflowedNPlusOneKeys),
            ("unfinishedCommands", _pendingCommandsByProviderAndObjectId.Count),
            ("recordedCommandIdentities", _recordedCommandIdentities?.Count),
            ("maxRecordedCommandIdentities", MaxRecordedCommandIdentities)));

        var byCommand = _aggregates.Values
            .Select(static aggregate => aggregate.ToRecord())
            .Concat(_overflowAggregate is null ? Array.Empty<DbCommandAggregate>() : [_overflowAggregate.ToRecord()])
            .OrderByDescending(static aggregate => aggregate.TotalMs)
            .ThenByDescending(static aggregate => aggregate.Count)
            .ThenBy(static aggregate => aggregate.CommandTextHash, StringComparer.Ordinal)
            .ToList();

        var nPlusOneIncidents = _nPlusOne.Values
            .Where(static incident => incident.Count > NPlusOneThreshold)
            .Select(static incident => incident.ToRecord())
            .OrderByDescending(static incident => incident.Count)
            .ThenBy(static incident => incident.ScopeId, StringComparer.Ordinal)
            .ToList();

        var connectionPool = _connectionPools.Values
            .Select(static stats => stats.ToRecord())
            .OrderBy(static stats => stats.Provider, StringComparer.Ordinal)
            .ToList();

        if (_overflowedCommandShapes > 0)
        {
            _notes.Add($"Aggregated {_overflowedCommandShapes} additional distinct DB command shape(s) into the overflow bucket after reaching the cap of {MaxTrackedCommandAggregates}.");
        }

        if (_overflowedNPlusOneKeys > 0)
        {
            _notes.Add($"Dropped {_overflowedNPlusOneKeys} distinct N+1 scope/command key(s) after reaching the cap of {MaxTrackedNPlusOneIncidents}.");
        }

        if (_expiredPendingCommands > 0)
        {
            _notes.Add($"Expired {_expiredPendingCommands} pending SqlClient command(s) that exceeded the {PendingCommandTtl.TotalMinutes:F0}-minute completion TTL.");
        }

        if (_evictedPendingCommands > 0)
        {
            _notes.Add($"Evicted {_evictedPendingCommands} oldest pending SqlClient command(s) after reaching the cap of {MaxTrackedPendingCommands} in-flight commands.");
        }

        if (byCommand.Count == 0)
        {
            _notes.Add("No EF Core or SqlClient commands were observed in the collection window.");
        }

        return new DbSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalCommands: TotalCommands,
            ByCommand: byCommand,
            NPlusOne: nPlusOneIncidents,
            ConnectionPool: connectionPool,
            Notes: _notes.OrderBy(static note => note, StringComparer.Ordinal).ToList());
    }

    private MutableCommandAggregate GetOrCreateAggregate(PendingCommand pending, DateTimeOffset stoppedAt)
    {
        if (_aggregates.TryGetValue(pending.Key, out var aggregate))
        {
            return aggregate;
        }

        if (_aggregates.Count >= MaxTrackedCommandAggregates)
        {
            _overflowedCommandShapes++;
            _overflowAggregate ??= new MutableCommandAggregate(
                OverflowCommandTextHash,
                OverflowCommandText,
                OverflowConnectionString,
                pending.Provider,
                pending.StartedAt,
                stoppedAt);
            return _overflowAggregate;
        }

        aggregate = new MutableCommandAggregate(
            pending.CommandTextHash,
            pending.CommandTextSanitized,
            pending.ConnectionStringSanitized,
            pending.Provider,
            pending.StartedAt,
            stoppedAt);
        _aggregates[pending.Key] = aggregate;
        return aggregate;
    }

    private MutableNPlusOne? GetOrCreateNPlusOne(string nPlusOneKey, PendingCommand pending, DateTimeOffset stoppedAt)
    {
        if (_nPlusOne.TryGetValue(nPlusOneKey, out var incident))
        {
            return incident;
        }

        if (_nPlusOne.Count >= MaxTrackedNPlusOneIncidents)
        {
            _overflowedNPlusOneKeys++;
            return null;
        }

        incident = new MutableNPlusOne(
            pending.ScopeId,
            pending.CommandTextHash,
            pending.CommandTextSanitized,
            pending.ConnectionStringSanitized,
            pending.Provider,
            pending.StartedAt,
            stoppedAt);
        _nPlusOne[nPlusOneKey] = incident;
        return incident;
    }

    private void ExpirePendingCommands(DateTimeOffset now)
    {
        if (_pendingCommandsByProviderAndObjectId.Count == 0)
        {
            return;
        }

        var cutoff = now - PendingCommandTtl;
        foreach (var key in _pendingCommandsByProviderAndObjectId
                     .Where(entry => entry.Value.StartedAt <= cutoff)
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            _pendingCommandsByProviderAndObjectId.Remove(key);
            _expiredPendingCommands++;
        }
    }

    private void RemoveOldestPendingCommand()
    {
        var oldest = _pendingCommandsByProviderAndObjectId.MinBy(static entry => entry.Value.StartedAt);
        _pendingCommandsByProviderAndObjectId.Remove(oldest.Key);
    }
}

internal sealed record PendingCommand(
    string Provider,
    string Key,
    string CommandTextHash,
    string CommandTextSanitized,
    string ConnectionStringSanitized,
    string ScopeId,
    DateTimeOffset StartedAt)
{
    internal bool CapturePairingUncertain { get; init; }
    internal bool CaptureTimingUnavailable { get; init; }
}

internal sealed class MutableCommandAggregate
{
    private readonly BoundedPercentileSampler _durationsMs = new();
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

    public DbCommandAggregate ToRecord() => new(
        CommandTextHash,
        CommandTextSanitized,
        ConnectionStringSanitized,
        _providers.OrderBy(static provider => provider, StringComparer.Ordinal).ToList(),
        Count,
        TotalMs,
        MaxMs,
        _durationsMs.GetPercentile95(),
        FirstSeenAt,
        LastSeenAt);
}

internal sealed class MutableNPlusOne
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

internal sealed class MutableConnectionPoolStats
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
