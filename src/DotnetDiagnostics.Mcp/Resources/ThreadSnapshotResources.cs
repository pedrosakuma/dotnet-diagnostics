using System.ComponentModel;
using System.Text.Json;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Threads;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Resources;

/// <summary>
/// Templated Resource that exposes a previously-captured <see cref="ThreadSnapshotArtifact"/>
/// keyed by its drilldown handle as a read-only JSON blob.
/// </summary>
[McpServerResourceType]
public sealed class ThreadSnapshotResources
{
    [McpServerResource(
        UriTemplate = "thread://snapshot/{handle}",
        Name = "thread-snapshot",
        Title = "Drilldown thread + lock snapshot",
        MimeType = "application/json")]
    [Description(
        "JSON snapshot of the ThreadSnapshotArtifact registered under a drilldown handle by " +
        "collect_thread_snapshot. This Resource exposes the complete retained capture: runtime info, managed threads (state, stack frames " +
        "with MethodIdentity handoff for dotnet-assembly-mcp, inferred wait reason), the lock " +
        "(SyncBlock) graph with owners + waiter counts, and optional ThreadPool counters/queues " +
        "when captured by the backend. It may be large; prefer bounded query_snapshot thread/lock pages for LLM use. Returns an error contents block when the handle is unknown " +
        "or expired.")]
    public static string ReadSnapshot(IDiagnosticHandleStore handles, string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);

        var snapshot = handles.TryGet<ThreadSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return JsonSerializer.Serialize(
                new ThreadSnapshotErrorPayload(
                    Kind: "unknown",
                    Error: $"Handle '{handle}' is unknown or expired. Re-run collect_thread_snapshot to issue a fresh handle."),
                ThreadSnapshotJsonContext.Default.ThreadSnapshotErrorPayload);
        }

        return JsonSerializer.Serialize(snapshot, ThreadSnapshotJsonContext.Default.ThreadSnapshotArtifact);
    }
}
