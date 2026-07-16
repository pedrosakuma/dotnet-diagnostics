using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Drilldown;

/// <summary>
/// Bounded in-memory <see cref="IDiagnosticHandleStore"/> for a single process. Capacity eviction
/// removes the artifact whose TTL deadline is earliest (then the oldest registration on ties).
/// A small FIFO tombstone set preserves TTL-versus-capacity loss reasons without retaining artifacts.
/// </summary>
public sealed class MemoryDiagnosticHandleStore : IDiagnosticHandleStore
{
    /// <summary>Meter emitted by the handle store; tags never include handle ids or artifacts.</summary>
    public const string MeterName = "DotnetDiagnostics.Core.DiagnosticHandles";

    private const int TombstonesPerEntry = 4;
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Registrations = Meter.CreateCounter<long>(
        "dotnet_diagnostics_handle_registrations_total",
        description: "Total diagnostic artifact handle registrations by kind.");
    private static readonly Counter<long> Evictions = Meter.CreateCounter<long>(
        "dotnet_diagnostics_handle_evictions_total",
        description: "Total diagnostic artifact removals by reason and kind.");
    private static readonly Counter<long> Lookups = Meter.CreateCounter<long>(
        "dotnet_diagnostics_handle_lookups_total",
        description: "Total diagnostic handle lookups by outcome.");
    private static readonly Counter<long> DisposalFailures = Meter.CreateCounter<long>(
        "dotnet_diagnostics_handle_disposal_failures_total",
        description: "Total failures disposing removed diagnostic artifacts.");

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiagnosticHandleTombstone> _tombstones = new(StringComparer.Ordinal);
    private readonly Queue<string> _tombstoneOrder = new();
    private readonly object _gate = new();
    private readonly int _maxEntries;
    private readonly int _maxTombstones;
    private readonly TimeProvider _clock;
    private readonly ILogger<MemoryDiagnosticHandleStore> _logger;
    private long _registrationSequence;

    /// <summary>Creates a strictly bounded store.</summary>
    public MemoryDiagnosticHandleStore(
        int maxEntries = DiagnosticHandleStoreOptions.DefaultMaxEntries,
        TimeProvider? clock = null,
        ILogger<MemoryDiagnosticHandleStore>? logger = null)
    {
        if (maxEntries is < 1 or > DiagnosticHandleStoreOptions.MaxAllowedEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEntries),
                maxEntries,
                $"Must be between 1 and {DiagnosticHandleStoreOptions.MaxAllowedEntries}.");
        }

        _maxEntries = maxEntries;
        _maxTombstones = checked(maxEntries * TombstonesPerEntry);
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<MemoryDiagnosticHandleStore>.Instance;
    }

    public DiagnosticHandle Register(
        int processId,
        string kind,
        object artifact,
        TimeSpan ttl,
        bool evictWhenProcessExits = true,
        HandleOrigin? origin = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        }

        List<Removal> removals;
        DiagnosticHandle handle;
        int entryCount;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            removals = EvictExpiredLocked(now);
            while (_entries.Count >= _maxEntries)
            {
                var victim = _entries.Values
                    .OrderBy(static entry => entry.Handle.ExpiresAt)
                    .ThenBy(static entry => entry.Sequence)
                    .First();
                removals.Add(RemoveLocked(victim.Handle.Id, RemovalReason.Capacity, now, retainTombstone: true));
            }

            var id = NewUniqueHandleIdLocked();
            var effectiveOrigin = origin ?? (evictWhenProcessExits ? HandleOrigin.Live : HandleOrigin.Dump);
            handle = new DiagnosticHandle(id, now.Add(ttl), processId, kind) { Origin = effectiveOrigin };
            _entries.Add(id, new Entry(handle, artifact, evictWhenProcessExits, _registrationSequence++));
            entryCount = _entries.Count;
        }

        ObserveRemovals(removals);
        Registrations.Add(1, new KeyValuePair<string, object?>("kind", kind));
        _logger.LogInformation(
            "Registered diagnostic handle {HandleId} kind {Kind} for process {ProcessId}; store usage is {EntryCount}/{Capacity}, expires at {ExpiresAt}.",
            handle.Id,
            handle.Kind,
            handle.ProcessId,
            entryCount,
            _maxEntries,
            handle.ExpiresAt);
        return handle;
    }

    public T? TryGet<T>(string handle) where T : class
        => LookupWithKind(handle).Lookup?.Artifact as T;

    public HandleLookup? TryGetWithKind(string handle)
        => LookupWithKind(handle).Lookup;

    public DiagnosticHandleLookupResult LookupWithKind(string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);

        Removal? expired = null;
        DiagnosticHandleLookupResult result;
        lock (_gate)
        {
            if (_entries.TryGetValue(handle, out var entry))
            {
                var now = _clock.GetUtcNow();
                if (entry.Handle.ExpiresAt <= now)
                {
                    expired = RemoveLocked(handle, RemovalReason.Expired, now, retainTombstone: true);
                    result = FromTombstone(_tombstones[handle]);
                }
                else
                {
                    result = DiagnosticHandleLookupResult.Found(new HandleLookup(entry.Handle, entry.Artifact));
                }
            }
            else if (_tombstones.TryGetValue(handle, out var tombstone))
            {
                result = FromTombstone(tombstone);
            }
            else
            {
                result = DiagnosticHandleLookupResult.Unknown;
            }
        }

        if (expired is { } removal)
        {
            ObserveRemoval(removal);
        }

        Lookups.Add(
            1,
            new KeyValuePair<string, object?>("outcome", LookupOutcome(result.Status)));
        return result;
    }

    public bool Invalidate(string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        Removal? removal = null;
        lock (_gate)
        {
            if (_entries.ContainsKey(handle))
            {
                removal = RemoveLocked(handle, RemovalReason.Invalidated, _clock.GetUtcNow(), retainTombstone: false);
            }
        }

        if (removal is not { } removed)
        {
            return false;
        }

        ObserveRemoval(removed);
        return true;
    }

    public int InvalidateForProcess(int processId)
    {
        List<Removal> removals;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var victims = _entries.Values
                .Where(entry => entry.EvictWhenProcessExits && entry.Handle.ProcessId == processId)
                .Select(static entry => entry.Handle.Id)
                .ToArray();
            removals = new List<Removal>(victims.Length);
            foreach (var key in victims)
            {
                removals.Add(RemoveLocked(key, RemovalReason.ProcessExited, now, retainTombstone: false));
            }
        }

        ObserveRemovals(removals);
        return removals.Count;
    }

    /// <summary>Distinct PIDs referenced by live handles that opt in to process-exit eviction.</summary>
    public IReadOnlyCollection<int> RegisteredProcessIds()
    {
        List<Removal> expired;
        int[] processIds;
        lock (_gate)
        {
            expired = EvictExpiredLocked(_clock.GetUtcNow());
            processIds = _entries.Values
                .Where(static entry => entry.EvictWhenProcessExits)
                .Select(static entry => entry.Handle.ProcessId)
                .Distinct()
                .ToArray();
        }

        ObserveRemovals(expired);
        return processIds;
    }

    internal int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal int TombstoneCount
    {
        get
        {
            lock (_gate)
            {
                return _tombstones.Count;
            }
        }
    }

    internal int TombstoneCapacity => _maxTombstones;

    private List<Removal> EvictExpiredLocked(DateTimeOffset now)
    {
        var victims = _entries.Values
            .Where(entry => entry.Handle.ExpiresAt <= now)
            .Select(static entry => entry.Handle.Id)
            .ToArray();
        var removals = new List<Removal>(victims.Length);
        foreach (var key in victims)
        {
            removals.Add(RemoveLocked(key, RemovalReason.Expired, now, retainTombstone: true));
        }

        return removals;
    }

    private Removal RemoveLocked(
        string handleId,
        RemovalReason reason,
        DateTimeOffset removedAt,
        bool retainTombstone)
    {
        var entry = _entries[handleId];
        _entries.Remove(handleId);
        if (retainTombstone)
        {
            AddTombstoneLocked(entry.Handle, reason, removedAt);
        }

        return new Removal(entry, reason);
    }

    private void AddTombstoneLocked(DiagnosticHandle handle, RemovalReason reason, DateTimeOffset removedAt)
    {
        var status = reason == RemovalReason.Capacity
            ? DiagnosticHandleLookupStatus.CapacityEvicted
            : DiagnosticHandleLookupStatus.Expired;
        _tombstones.Add(
            handle.Id,
            new DiagnosticHandleTombstone(handle.Id, status, removedAt, handle.ProcessId, handle.Kind));
        _tombstoneOrder.Enqueue(handle.Id);

        while (_tombstones.Count > _maxTombstones)
        {
            _tombstones.Remove(_tombstoneOrder.Dequeue());
        }
    }

    private string NewUniqueHandleIdLocked()
    {
        string id;
        do
        {
            id = NewHandleId();
        }
        while (_entries.ContainsKey(id) || _tombstones.ContainsKey(id));

        return id;
    }

    private void ObserveRemovals(IEnumerable<Removal> removals)
    {
        foreach (var removal in removals)
        {
            ObserveRemoval(removal);
        }
    }

    private void ObserveRemoval(Removal removal)
    {
        var reason = RemovalReasonName(removal.Reason);
        Evictions.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("kind", removal.Entry.Handle.Kind));

        if (removal.Reason == RemovalReason.Capacity)
        {
            _logger.LogWarning(
                "Capacity-evicted diagnostic handle {HandleId} kind {Kind} for process {ProcessId}; configured capacity is {Capacity}.",
                removal.Entry.Handle.Id,
                removal.Entry.Handle.Kind,
                removal.Entry.Handle.ProcessId,
                _maxEntries);
        }
        else if (removal.Reason == RemovalReason.Expired)
        {
            _logger.LogInformation(
                "TTL-expired diagnostic handle {HandleId} kind {Kind} for process {ProcessId}.",
                removal.Entry.Handle.Id,
                removal.Entry.Handle.Kind,
                removal.Entry.Handle.ProcessId);
        }

        if (removal.Entry.Artifact is not IDisposable disposable)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            DisposalFailures.Add(
                1,
                new KeyValuePair<string, object?>("reason", reason),
                new KeyValuePair<string, object?>("kind", removal.Entry.Handle.Kind));
            _logger.LogWarning(
                ex,
                "Failed to dispose diagnostic artifact for handle {HandleId} kind {Kind} after {Reason} removal.",
                removal.Entry.Handle.Id,
                removal.Entry.Handle.Kind,
                reason);
        }
    }

    private static DiagnosticHandleLookupResult FromTombstone(DiagnosticHandleTombstone tombstone) =>
        new(tombstone.Status, null, tombstone);

    private static string LookupOutcome(DiagnosticHandleLookupStatus status) => status switch
    {
        DiagnosticHandleLookupStatus.Found => "found",
        DiagnosticHandleLookupStatus.Expired => "expired",
        DiagnosticHandleLookupStatus.CapacityEvicted => "capacity_evicted",
        _ => "unknown",
    };

    private static string RemovalReasonName(RemovalReason reason) => reason switch
    {
        RemovalReason.Expired => "ttl",
        RemovalReason.Capacity => "capacity",
        RemovalReason.ProcessExited => "process_exit",
        _ => "invalidate",
    };

    private static string NewHandleId()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return ToCrockford(bytes);
    }

    private static readonly char[] Crockford =
    {
        '0','1','2','3','4','5','6','7','8','9',
        'A','B','C','D','E','F','G','H','J','K','M','N','P','Q','R','S','T','V','W','X','Y','Z',
    };

    private static string ToCrockford(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[20];
        int bitBuffer = 0;
        int bitCount = 0;
        int outIndex = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            bitBuffer = (bitBuffer << 8) | bytes[i];
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                chars[outIndex++] = Crockford[(bitBuffer >> bitCount) & 0x1F];
            }
        }

        if (bitCount > 0)
        {
            chars[outIndex++] = Crockford[(bitBuffer << (5 - bitCount)) & 0x1F];
        }

        return new string(chars, 0, outIndex);
    }

    private sealed record Entry(
        DiagnosticHandle Handle,
        object Artifact,
        bool EvictWhenProcessExits,
        long Sequence);

    private readonly record struct Removal(Entry Entry, RemovalReason Reason);

    private enum RemovalReason
    {
        Expired,
        Capacity,
        ProcessExited,
        Invalidated,
    }
}
