using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Routes transport management to either the Kubernetes port-forward manager or the
/// SSRF-safe external MCP transport manager based on the handle's provider metadata.
/// </summary>
/// <remarks>
/// <para>
/// This is the <see cref="IInvestigationTransportManager"/> registered in DI. It replaces
/// the previous direct registration of <see cref="KubernetesPortForwardManager"/> so that
/// both transport providers can coexist without the proxy endpoint or proxy client knowing
/// which one is in use.
/// </para>
/// <para>
/// Routing logic:
/// <list type="bullet">
/// <item>Handle has <see cref="InvestigationHandle.Kubernetes"/> set → delegate to
///   <see cref="KubernetesPortForwardManager"/>.</item>
/// <item>Handle has <see cref="InvestigationHandle.ExternalMcp"/> set → delegate to
///   <see cref="SsrfSafeExternalMcpTransportManager"/>.</item>
/// <item>Neither → throws <see cref="InvalidOperationException"/>.</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class CompositeInvestigationTransportManager : IInvestigationTransportManager
{
    private readonly IPortForwardManager _k8s;
    private readonly SsrfSafeExternalMcpTransportManager _external;

    public CompositeInvestigationTransportManager(
        IPortForwardManager k8s,
        SsrfSafeExternalMcpTransportManager external)
    {
        ArgumentNullException.ThrowIfNull(k8s);
        ArgumentNullException.ThrowIfNull(external);
        _k8s = k8s;
        _external = external;
    }

    /// <inheritdoc/>
    public Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Delegate(handle).GetOrCreateClientAsync(handle, cancellationToken);
    }

    /// <inheritdoc/>
    public Task CloseAsync(string handleId)
    {
        // Close from both managers; the one that doesn't own the handle is idempotent.
        // We don't try to look up which manager owns the handle by id because the
        // handle has already been removed from the store at this point.
        var t1 = _k8s.CloseAsync(handleId);
        var t2 = _external.CloseAsync(handleId);
        return Task.WhenAll(t1, t2);
    }

    private IInvestigationTransportManager Delegate(InvestigationHandle handle)
    {
        if (handle.Kubernetes is not null) return _k8s;
        if (handle.ExternalMcp is not null) return _external;

        throw new InvalidOperationException(
            $"Investigation handle {handle.HandleId} has neither Kubernetes nor ExternalMcp metadata. " +
            "Cannot determine which transport manager to use.");
    }
}
