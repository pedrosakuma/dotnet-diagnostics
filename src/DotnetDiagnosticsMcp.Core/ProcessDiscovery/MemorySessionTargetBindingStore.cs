using System.Collections.Concurrent;

namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// Default in-memory <see cref="ISessionTargetBindingStore"/>. Backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed on the MCP session id.
/// </summary>
/// <remarks>
/// Lazy eviction on read: <see cref="TryGet"/> drops bindings whose
/// <see cref="SessionTargetBinding.ExpiresAt"/> has passed. There is no background reaper
/// at this layer — the orchestrator (Phase 4, issue #20) owns its own reaper for the
/// reverse-proxy lifetime and will call <see cref="Remove"/> explicitly on detach.
/// </remarks>
public sealed class MemorySessionTargetBindingStore : ISessionTargetBindingStore
{
    private readonly ConcurrentDictionary<string, SessionTargetBinding> _bindings = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public MemorySessionTargetBindingStore()
        : this(TimeProvider.System)
    {
    }

    public MemorySessionTargetBindingStore(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public SessionTargetBinding? TryGet(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;

        if (!_bindings.TryGetValue(sessionId, out var binding)) return null;

        if (binding.ExpiresAt is { } expiresAt && _clock.GetUtcNow() >= expiresAt)
        {
            // Best-effort: only drop the entry if it's still the same reference we observed.
            // A concurrent Set/Remove can race here; that's harmless because the only effect
            // is one extra TryGet returning null until the next caller re-reads.
            _bindings.TryRemove(new KeyValuePair<string, SessionTargetBinding>(sessionId, binding));
            return null;
        }

        return binding;
    }

    public void SetBinding(string sessionId, SessionTargetBinding binding)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(binding);

        _bindings[sessionId] = binding;
    }

    public bool Remove(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        return _bindings.TryRemove(sessionId, out _);
    }
}
