using System;
using System.Collections.Generic;
using System.Linq;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Default in-process implementation of <see cref="IInvestigationSessionBinder"/>.
/// Thread-safe via a single lock — orchestrator concurrency keeps the binding map
/// tiny (one entry per active MCP session), so contention is not a concern.
/// </summary>
internal sealed class MemoryInvestigationSessionBinder : IInvestigationSessionBinder
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionBindingState> _bySessionId = new(StringComparer.Ordinal);

    public string? TryGetHandleId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (_gate)
        {
            return _bySessionId.TryGetValue(sessionId, out var state) ? state.PrimaryHandleId : null;
        }
    }

    public void Bind(string sessionId, string handleId)
    {
        if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId must be non-empty.", nameof(sessionId));
        if (string.IsNullOrEmpty(handleId)) throw new ArgumentException("handleId must be non-empty.", nameof(handleId));
        lock (_gate)
        {
            if (!_bySessionId.TryGetValue(sessionId, out var state))
            {
                state = new SessionBindingState();
                _bySessionId[sessionId] = state;
            }

            state.Bind(handleId);
        }
    }

    public string? Unbind(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (_gate)
        {
            if (_bySessionId.Remove(sessionId, out var state))
            {
                return state.PrimaryHandleId;
            }
            return null;
        }
    }

    public IReadOnlyCollection<string> UnbindAllForHandle(string handleId)
    {
        if (string.IsNullOrEmpty(handleId)) return Array.Empty<string>();
        lock (_gate)
        {
            var matches = new List<string>();
            foreach (var (sessionId, state) in _bySessionId.ToArray())
            {
                if (!state.Unbind(handleId))
                {
                    continue;
                }

                matches.Add(sessionId);
                if (state.Count == 0)
                {
                    _bySessionId.Remove(sessionId);
                }
            }
            return matches;
        }
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> Snapshot()
    {
        lock (_gate)
        {
            return _bySessionId
                .SelectMany(static kvp => kvp.Value.HandleIds.Select(handleId => new KeyValuePair<string, string>(kvp.Key, handleId)))
                .ToArray();
        }
    }

    private sealed class SessionBindingState
    {
        private readonly List<string> _handleIds = new();

        public string PrimaryHandleId => _handleIds[^1];
        public IReadOnlyList<string> HandleIds => _handleIds;
        public int Count => _handleIds.Count;

        public void Bind(string handleId)
        {
            _handleIds.RemoveAll(existing => string.Equals(existing, handleId, StringComparison.Ordinal));
            _handleIds.Add(handleId);
        }

        public bool Unbind(string handleId)
            => _handleIds.RemoveAll(existing => string.Equals(existing, handleId, StringComparison.Ordinal)) > 0;
    }
}
