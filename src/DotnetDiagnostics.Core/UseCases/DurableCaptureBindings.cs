using System.Runtime.CompilerServices;
using DotnetDiagnostics.Core.Drilldown;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>Weak snapshot ownership, with a finite per-snapshot alias bound and fail-closed overflow.</summary>
internal sealed class DurableCaptureBindings(IDiagnosticHandleStore handles)
{
    private readonly ConditionalWeakTable<object, SnapshotBindings> _snapshots = new();

    internal void Set(DiagnosticHandle handle, object artifact, DurableCaptureHandleBinding binding)
        => _snapshots.GetValue(artifact, static _ => new()).Set(handle, binding);

    internal DurableCaptureHandleBinding? Lookup(string handle)
        => handles.TryGetWithKind(handle) is { } lookup &&
            _snapshots.TryGetValue(lookup.Artifact, out var bindings) ? bindings.Lookup(handle) : null;

    private sealed class SnapshotBindings
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _handles = new(StringComparer.Ordinal);
        private DurableCaptureHandleBinding? _overflow;

        internal void Set(DiagnosticHandle handle, DurableCaptureHandleBinding binding)
        {
            lock (_gate)
            {
                var entry = new Entry(handle.ExpiresAt, binding);
                if (_handles.ContainsKey(handle.Id)) { _handles[handle.Id] = entry; return; }
                if (_handles.Count == DiagnosticHandleStoreOptions.MaxAllowedEntries)
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var expired in _handles.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                        _handles.Remove(expired);
                }
                if (_handles.Count == DiagnosticHandleStoreOptions.MaxAllowedEntries)
                {
                    // A custom store can exceed Core's handle ceiling. Unknown aliases must never
                    // fall through to an ephemeral authorization path after a binding-cap hit.
                    _overflow = binding with { Artifact = null, SupportedViews = Array.Empty<string>() };
                    return;
                }
                _handles.Add(handle.Id, entry);
            }
        }

        internal DurableCaptureHandleBinding? Lookup(string handle)
        {
            lock (_gate) return _handles.GetValueOrDefault(handle)?.Binding ?? _overflow;
        }

        private sealed record Entry(DateTimeOffset ExpiresAt, DurableCaptureHandleBinding Binding);
    }
}
