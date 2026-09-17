namespace DotnetDiagnostics.Core.Networking;

/// <summary>
/// Single-reader EventPipe correlation. Never reuses an identity within a capture, even after a
/// successful pair, failure, TTL expiry or eviction: a late Stop cannot identify a new lifecycle.
/// </summary>
internal sealed class NetworkingActivityCorrelator<T>(
    int maxPending = NetworkingActivityCorrelator<T>.MaxPendingActivities,
    int maxIdentities = NetworkingActivityCorrelator<T>.MaxRememberedIdentities)
{
    internal const int MaxPendingActivities = 4096;
    internal const int MaxRememberedIdentities = 65536;
    internal static readonly TimeSpan PendingActivityTtl = TimeSpan.FromMinutes(2);

    private readonly Dictionary<Guid, Entry> _pending = [];
    private readonly HashSet<Guid> _seen = [];
    private long _started, _paired, _emptyStarts, _ambiguousStarts, _expired, _evicted;
    private long _suppressed, _unmatchedStops, _emptyStops, _invalidStops, _unmatchedFailures;
    private long _pairedFailed, _matchedFailureEvents, _repeatedFailureEvents, _invalidTimestampFailures;
    private bool _capacityReached;

    internal int PendingCount => _pending.Count;
    internal int RememberedCount => _seen.Count;

    internal void Start(Guid id, DateTimeOffset timestamp, T value)
    {
        Expire(timestamp);
        _started++;
        if (id == Guid.Empty)
        {
            _emptyStarts++;
            return;
        }
        if (_capacityReached)
        {
            _suppressed++;
            return;
        }
        if (_seen.Contains(id))
        {
            // Discard BOTH starts; neither subsequent Stop identifies which lifecycle completed.
            _ambiguousStarts += _pending.Remove(id) ? 2 : 1;
            return;
        }
        if (!Remember(id))
        {
            _suppressed++;
            return;
        }
        if (_pending.Count >= maxPending)
        {
            var oldest = _pending.MinBy(static entry => entry.Value.Timestamp);
            _pending.Remove(oldest.Key);
            _evicted++;
        }
        _pending.Add(id, new Entry(timestamp, value));
    }

    internal bool Stop(Guid id, DateTimeOffset timestamp, out T value, out TimeSpan elapsed)
        => Stop(id, timestamp, out value, out elapsed, out _);

    internal bool Stop(Guid id, DateTimeOffset timestamp, out T value, out TimeSpan elapsed, out bool failed)
    {
        Expire(timestamp);
        value = default!;
        elapsed = default;
        failed = false;
        if (id == Guid.Empty)
        {
            _emptyStops++;
            return false;
        }
        if (!_pending.Remove(id, out var entry))
        {
            _unmatchedStops++;
            Remember(id);
            return false;
        }
        if (timestamp < entry.Timestamp || timestamp < entry.LastFailure)
        {
            _invalidStops++;
            _ambiguousStarts++;
            return false;
        }
        _paired++;
        failed = entry.LastFailure is not null;
        if (failed) _pairedFailed++;
        value = entry.Value;
        elapsed = timestamp - entry.Timestamp;
        return true;
    }

    internal bool Fail(Guid id, DateTimeOffset timestamp)
    {
        Expire(timestamp);
        if (id != Guid.Empty && _pending.TryGetValue(id, out var entry))
        {
            if (timestamp < entry.Timestamp || timestamp < entry.LastFailure)
            {
                _pending.Remove(id);
                _ambiguousStarts++;
                _invalidTimestampFailures++;
                return false;
            }
            _matchedFailureEvents++;
            if (entry.LastFailure is not null) _repeatedFailureEvents++;
            // Failed is an outcome marker, not the terminal event. Only Stop contributes latency.
            entry.LastFailure = timestamp;
            return true;
        }
        _unmatchedFailures++;
        if (id != Guid.Empty) Remember(id);
        return false;
    }

    internal NetworkingCorrelationCounts Snapshot()
    {
        var result = new NetworkingCorrelationCounts(
            _started, _paired, _emptyStarts, _ambiguousStarts, _expired, _evicted, 0,
            _pending.Count, _suppressed, _unmatchedStops, _emptyStops, _invalidStops, _capacityReached, _unmatchedFailures);
        return result with
        {
            Counts = new Dictionary<string, long>(result.Counts, StringComparer.Ordinal)
            {
                ["latencyPopulationVersion"] = 2,
                ["pairedFailed"] = _pairedFailed,
                ["pairedWithoutFailure"] = _paired - _pairedFailed,
                ["matchedFailureEvents"] = _matchedFailureEvents,
                ["repeatedFailureEvents"] = _repeatedFailureEvents,
                ["invalidTimestampFailures"] = _invalidTimestampFailures,
                ["unfinishedFailed"] = _pending.Values.LongCount(static entry => entry.LastFailure is not null),
            },
        };
    }

    private bool Remember(Guid id)
    {
        if (_capacityReached) return false;
        if (_seen.Contains(id)) return true;
        if (_seen.Count < maxIdentities)
        {
            _seen.Add(id);
            return true;
        }
        // Do not evict tombstones: the stream provides no bound on late events or ID reuse.
        // Once exact identity history cannot be retained, stop all pairing for this kind.
        _capacityReached = true;
        _suppressed += _pending.Count;
        _pending.Clear();
        return false;
    }

    private void Expire(DateTimeOffset timestamp)
    {
        var cutoff = timestamp - PendingActivityTtl;
        foreach (var key in _pending.Where(entry => entry.Value.Timestamp <= cutoff)
                     .Select(static entry => entry.Key).ToArray())
        {
            _pending.Remove(key);
            _expired++;
        }
    }

    private sealed record Entry(DateTimeOffset Timestamp, T Value)
    {
        internal DateTimeOffset? LastFailure { get; set; }
    }
}
