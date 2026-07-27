using System.Runtime.CompilerServices;

namespace DotnetDiagnostics.Core.Drilldown;

/// <summary>
/// Compatibility overlay for producer metadata when an older/custom handle store implements only
/// <see cref="IDiagnosticHandleStore.Register"/> and therefore cannot persist additive metadata.
/// </summary>
public static class DiagnosticHandleMetadata
{
    private const int MaxEntriesPerStore = 4096;
    private static readonly ConditionalWeakTable<IDiagnosticHandleStore, StoreOverlay> Overlays = new();

    /// <summary>Resolves producer metadata from the handle first, then the compatibility overlay.</summary>
    public static string? ResolveProducingTool(IDiagnosticHandleStore store, HandleLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (lookup.Handle.ProducingTool is not null)
        {
            return lookup.Handle.ProducingTool;
        }

        return Overlays.TryGetValue(store, out var overlay)
            ? overlay.TryGet(lookup.Handle.Id)
            : null;
    }

    internal static void Record(IDiagnosticHandleStore store, string handleId, string? producingTool)
    {
        if (string.IsNullOrWhiteSpace(producingTool))
        {
            return;
        }

        Overlays.GetValue(store, static _ => new StoreOverlay())
            .Record(handleId, producingTool);
    }

    private sealed class StoreOverlay
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _byHandle = new(StringComparer.Ordinal);
        private readonly Queue<string> _insertionOrder = new();

        internal void Record(string handleId, string producingTool)
        {
            lock (_gate)
            {
                if (_byHandle.ContainsKey(handleId))
                {
                    _byHandle[handleId] = producingTool;
                    return;
                }

                while (_byHandle.Count >= MaxEntriesPerStore && _insertionOrder.Count > 0)
                {
                    _byHandle.Remove(_insertionOrder.Dequeue());
                }

                _byHandle.Add(handleId, producingTool);
                _insertionOrder.Enqueue(handleId);
            }
        }

        internal string? TryGet(string handleId)
        {
            lock (_gate)
            {
                return _byHandle.GetValueOrDefault(handleId);
            }
        }
    }
}
