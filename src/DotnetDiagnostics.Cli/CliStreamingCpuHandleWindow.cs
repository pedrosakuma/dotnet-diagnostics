using DotnetDiagnostics.Core.Drilldown;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Keeps exactly the latest baseline/current CPU pair for one JSONL connection. The shared store
/// remains independently capacity- and TTL-bounded; this narrower window prevents a streaming
/// client from accumulating a CPU profile history accidentally.
/// </summary>
internal sealed class CliStreamingCpuHandleWindow
{
    internal const int RetainedHandleLimit = 2;

    private readonly Queue<string> _handles = new();

    internal void Track(IDiagnosticHandleStore store, string handle)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        if (_handles.Contains(handle, StringComparer.Ordinal))
        {
            return;
        }

        _handles.Enqueue(handle);
        while (_handles.Count > RetainedHandleLimit)
        {
            store.Invalidate(_handles.Dequeue());
        }
    }
}
