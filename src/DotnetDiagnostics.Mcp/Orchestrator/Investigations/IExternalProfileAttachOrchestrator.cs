using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Request payload for <see cref="IExternalProfileAttachOrchestrator.AttachAsync"/>.
/// </summary>
/// <param name="ProfileName">
/// Name of the operator-configured profile
/// (<c>Orchestrator:ExternalMcpProfiles:{name}</c>). Required.
/// </param>
/// <param name="TtlSeconds">Per-handle TTL override; null uses <c>OrchestratorOptions.DefaultInvestigationTtlSeconds</c>.</param>
/// <param name="AllowReuseExistingSession">When true (default), return an existing Active/Attaching handle for the same profile instead of creating a duplicate.</param>
/// <param name="OwnerBearerName">Display name of the caller, retained for diagnostics only. Authorization uses <paramref name="OwnerPrincipalKey"/>.</param>
/// <param name="OwnerPrincipalKey">Stable provider-namespaced identity used for ownership authorization.</param>
public sealed record ExternalProfileAttachRequest(
    string ProfileName,
    int? TtlSeconds = null,
    bool AllowReuseExistingSession = true,
    string? OwnerBearerName = null,
    string? OwnerPrincipalKey = null);

/// <summary>
/// Registers an external MCP profile as an investigation handle, attempts to set up the
/// transport (HTTP client), and transitions the handle to Active only after the transport
/// is successfully initialized.
/// </summary>
public interface IExternalProfileAttachOrchestrator
{
    Task<InvestigationHandle> AttachAsync(ExternalProfileAttachRequest request, CancellationToken cancellationToken);
}
