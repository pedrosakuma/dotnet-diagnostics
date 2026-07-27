using System.Net.Http;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Kubernetes-specific transport interface for investigation handles produced by
/// <c>attach_to_pod</c>. Implementations open an in-process Kubernetes port-forward
/// stream (no kubectl shell-out) and expose it as an <see cref="HttpClient"/> with the
/// per-attach Pod-local bearer token pre-injected via
/// <see cref="HttpClient.DefaultRequestHeaders"/>.
/// </summary>
/// <remarks>
/// Extends <see cref="IInvestigationTransportManager"/>, which is the transport-neutral
/// interface consumed by the proxy endpoint and investigation closer. Kubernetes-specific
/// callers (the DI container, tests) can still use this interface name; all proxy and
/// fan-out paths should reference <see cref="IInvestigationTransportManager"/> instead.
/// </remarks>
public interface IPortForwardManager : IInvestigationTransportManager
{
}
