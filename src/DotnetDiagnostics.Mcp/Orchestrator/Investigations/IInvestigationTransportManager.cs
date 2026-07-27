using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Transport-neutral per-handle HTTP data plane between the orchestrator and an
/// investigation target's Pod-local diagnostics MCP. Implementations own the
/// lifetime of the <see cref="System.Net.Http.HttpClient"/> — including upstream
/// credential injection — so callers remain unaware of transport details.
/// </summary>
/// <remarks>
/// <para>
/// The returned <see cref="System.Net.Http.HttpClient"/> must carry all required upstream
/// credentials in <see cref="System.Net.Http.HttpClient.DefaultRequestHeaders"/> so proxy
/// endpoints can forward requests without knowledge of the concrete transport. For
/// Kubernetes, the per-attach Pod-local bearer token is injected here; a future
/// external-MCP transport would inject its own credentials here too.
/// </para>
/// <para>
/// Lifecycle is tied to <see cref="InvestigationHandle.HandleId"/>: callers retrieve
/// the (lazily created) <see cref="System.Net.Http.HttpClient"/> via
/// <see cref="GetOrCreateClientAsync"/> on every proxied call, and release the transport
/// via <see cref="CloseAsync"/> on detach / TTL expiry / attach failure.
/// </para>
/// </remarks>
public interface IInvestigationTransportManager
{
    /// <summary>
    /// Returns the cached <see cref="System.Net.Http.HttpClient"/> for an investigation,
    /// creating it on first call. Idempotent — repeat calls for the same handle id return
    /// the same client instance.
    /// </summary>
    Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the transport associated with a handle. Idempotent — closing an unknown
    /// or already-closed handle is a no-op. Always non-throwing.
    /// </summary>
    Task CloseAsync(string handleId);
}
