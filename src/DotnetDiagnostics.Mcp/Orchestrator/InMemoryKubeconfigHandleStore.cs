using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Mcp.Azure;

namespace DotnetDiagnostics.Mcp.Orchestrator;

/// <summary>
/// Default <see cref="IKubeconfigHandleStore"/> implementation: an in-memory
/// dictionary guarded by a single lock, with TTL-based eviction, an active
/// background sweep, a capacity bound, and zero-on-removal of the kubeconfig
/// bytes. Singleton lifetime — one store per server process.
/// </summary>
/// <remarks>
/// <para>
/// The store deliberately keeps a coarse single-lock design rather than a
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>.
/// Volume is small (a handful of clusters per investigation, capped at TTL) and
/// the lock makes the "zero-then-remove" expiry path race-free without explicit
/// memory ordering.
/// </para>
/// <para>
/// Handle ids are derived from 16 bytes of <see cref="RandomNumberGenerator"/>
/// output and rendered as 32-char lowercase hex with a <c>kc:</c> prefix. That
/// gives ~128 bits of unguessable entropy — comfortably above the bearer-token
/// strength the rest of the server relies on.
/// </para>
/// <para>
/// FIX 2 (#234 review): a background <see cref="Timer"/> sweeps expired entries
/// every <c>TTL/2</c> so a long-lived process whose <see cref="Register"/> /
/// <see cref="TryResolve"/> path goes idle never accumulates stale credential
/// material. A capacity bound (<see cref="AzureDiscoveryOptions.KubeconfigHandleMaxEntries"/>)
/// caps dictionary growth; when full, <see cref="Register"/> evicts the entry
/// closest to expiry (zeroed before removal).
/// </para>
/// </remarks>
internal sealed class InMemoryKubeconfigHandleStore : IKubeconfigHandleStore, IAsyncDisposable, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private readonly ITimer _sweepTimer;
    private int _disposed; // 0 = live, 1 = disposed

    public event EventHandler<KubeconfigHandleEvictedEventArgs>? HandleEvicted;

    public InMemoryKubeconfigHandleStore(AzureDiscoveryOptions? options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        var configured = options?.KubeconfigHandleTtl ?? TimeSpan.Zero;
        _ttl = configured > TimeSpan.Zero ? configured : TimeSpan.FromMinutes(10);

        var configuredMax = options?.KubeconfigHandleMaxEntries ?? 0;
        _maxEntries = configuredMax > 0 ? configuredMax : 256;

        // Sweep at TTL/2 so an expired entry is removed within at most TTL/2 of its
        // expiry moment even when the store is idle. Floor at 1s so a tiny test TTL
        // doesn't burn CPU and ceiling at 5min so a long TTL still gets reasonable
        // responsiveness.
        var period = _ttl.Ticks / 2;
        var sweepPeriod = TimeSpan.FromTicks(Math.Clamp(period, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromMinutes(5).Ticks));
        _sweepTimer = _clock.CreateTimer(SweepCallback, state: null, dueTime: sweepPeriod, period: sweepPeriod);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                EvictExpired_NoLock();
                return _entries.Count;
            }
        }
    }

    public KubeconfigHandleMint Register(byte[] kubeconfig)
    {
        ArgumentNullException.ThrowIfNull(kubeconfig);
        ThrowIfDisposed();

        var handle = MintHandleId();
        var expiresAt = _clock.GetUtcNow() + _ttl;
        string? evictedForCapacity = null;

        lock (_gate)
        {
            EvictExpired_NoLock();

            // Capacity bound: evict the entry closest to expiry to make room. This
            // policy prefers killing the credential that would naturally die soonest
            // anyway; an alternative would be oldest-registered, but earliest-expiry
            // collapses to the same set when every entry has the same TTL.
            if (_entries.Count >= _maxEntries)
            {
                string? victimKey = null;
                DateTimeOffset victimExpiry = DateTimeOffset.MaxValue;
                foreach (var kv in _entries)
                {
                    if (kv.Value.ExpiresAt < victimExpiry)
                    {
                        victimExpiry = kv.Value.ExpiresAt;
                        victimKey = kv.Key;
                    }
                }

                if (victimKey is not null && _entries.TryGetValue(victimKey, out var victim))
                {
                    Array.Clear(victim.Bytes, 0, victim.Bytes.Length);
                    _entries.Remove(victimKey);
                    evictedForCapacity = victimKey;
                }

                if (_entries.Count >= _maxEntries)
                {
                    // Should be unreachable — we evicted at least one. Belt-and-braces.
                    throw new InvalidOperationException(
                        "Kubeconfig handle store is at capacity and capacity-bound eviction failed to free space.");
                }
            }

            _entries[handle] = new Entry(kubeconfig, expiresAt);
        }

        if (evictedForCapacity is not null)
        {
            RaiseEvicted(evictedForCapacity);
        }

        return new KubeconfigHandleMint(handle, expiresAt);
    }

    public byte[]? TryResolve(string handle)
    {
        if (string.IsNullOrEmpty(handle)) return null;

        lock (_gate)
        {
            EvictExpired_NoLock();
            if (!_entries.TryGetValue(handle, out var entry)) return null;

            // Defensive copy: callers MUST NOT mutate or retain a reference into
            // the store's buffer. The store owns the lifecycle (including the
            // final Array.Clear on expiry).
            var copy = new byte[entry.Bytes.Length];
            Buffer.BlockCopy(entry.Bytes, 0, copy, 0, entry.Bytes.Length);
            return copy;
        }
    }

    public DateTimeOffset? TryPeekExpiry(string handle)
    {
        if (string.IsNullOrEmpty(handle)) return null;

        lock (_gate)
        {
            EvictExpired_NoLock();
            if (_entries.TryGetValue(handle, out var entry))
            {
                return entry.ExpiresAt;
            }
        }
        return null;
    }

    private void SweepCallback(object? state)
    {
        try
        {
            List<string>? evicted = null;
            lock (_gate)
            {
                evicted = EvictExpired_NoLock();
            }
            if (evicted is not null)
            {
                foreach (var key in evicted)
                {
                    RaiseEvicted(key);
                }
            }
        }
        catch
        {
            // Never let a single bad entry kill the timer — the store is best-effort
            // and the next sweep will retry. We deliberately swallow rather than log
            // because the only logger we could reach would need to be injected and
            // the handle subsystem is exquisitely careful about what flows into log
            // sinks (FIX 5).
        }
    }

    /// <summary>
    /// Evicts every entry whose <see cref="Entry.ExpiresAt"/> is in the past. Must
    /// be called under <see cref="_gate"/>. Returns the list of evicted handle ids
    /// so the caller can raise <see cref="HandleEvicted"/> OUTSIDE the lock — event
    /// handlers run user code (e.g. the Kubernetes client cache from FIX 3), which
    /// could deadlock if invoked while holding our lock.
    /// </summary>
    private List<string>? EvictExpired_NoLock()
    {
        var now = _clock.GetUtcNow();
        List<string>? doomed = null;
        foreach (var kv in _entries)
        {
            if (kv.Value.ExpiresAt <= now)
            {
                doomed ??= new List<string>();
                doomed.Add(kv.Key);
            }
        }
        if (doomed is null) return null;

        foreach (var key in doomed)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                // Zero the bytes BEFORE releasing the dictionary reference so a
                // GC-walker / heap-dump captures only zeros, not the kubeconfig.
                Array.Clear(entry.Bytes, 0, entry.Bytes.Length);
                _entries.Remove(key);
            }
        }
        return doomed;
    }

    private void RaiseEvicted(string handle)
    {
        var handlers = HandleEvicted;
        if (handlers is null) return;
        try
        {
            handlers(this, new KubeconfigHandleEvictedEventArgs(handle));
        }
        catch
        {
            // Subscribers must not break the store.
        }
    }

    private static string MintHandleId()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return "kc:" + Convert.ToHexString(buffer).ToLowerInvariant();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    public void Dispose()
    {
        DisposeCore();
    }

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Stop the timer FIRST so it cannot race with the final zero-and-clear pass.
        _sweepTimer.Dispose();

        List<string>? evicted = null;
        lock (_gate)
        {
            if (_entries.Count > 0)
            {
                evicted = new List<string>(_entries.Count);
                foreach (var kv in _entries)
                {
                    Array.Clear(kv.Value.Bytes, 0, kv.Value.Bytes.Length);
                    evicted.Add(kv.Key);
                }
                _entries.Clear();
            }
        }

        if (evicted is not null)
        {
            foreach (var key in evicted) RaiseEvicted(key);
        }
    }

    private readonly record struct Entry(byte[] Bytes, DateTimeOffset ExpiresAt);
}
