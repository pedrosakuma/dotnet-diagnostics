using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Kubernetes-specific investigation target metadata. Stored as the <c>Kubernetes</c>
/// property of an <see cref="InvestigationHandle"/> when the handle was produced by
/// <c>attach_to_pod</c> against a running Kubernetes Pod.
/// </summary>
/// <remarks>
/// <para>
/// Keeping K8s-specific state in this nested type — rather than directly on
/// <see cref="InvestigationHandle"/> — ensures the core handle, store, proxy endpoint,
/// and fan-out paths remain transport-neutral and can accommodate a future
/// <c>ExternalMcp</c> target without forking their logic.
/// </para>
/// <para>
/// <see cref="PodLocalBearerToken"/> is the per-attach bearer token delivered through a
/// short-lived Kubernetes Secret reference. It is never returned to external callers — the
/// transport layer injects it as the upstream <c>Authorization</c> header via
/// <see cref="System.Net.Http.HttpClient.DefaultRequestHeaders"/> so proxy endpoints
/// and MCP tool calls do not need to know the token value.
/// </para>
/// <para>
/// <see cref="CredentialsMayBeInUse"/> is set atomically immediately before the
/// ephemeral-container patch and cleared after a definitive rejection. A definite
/// pre-runtime failure can therefore scrub generated plaintext without attempting an
/// impossible revocation, while ambiguous outcomes fail closed.
/// </para>
/// </remarks>
public sealed record KubernetesInvestigationTarget(
    string Namespace,
    string PodName,
    string TargetContainerName,
    string EphemeralContainerName,
    [property: JsonIgnore] string PodLocalBearerToken,
    string? CredentialSecretName = null,
    string? PodUid = null,
    bool CredentialsMayBeInUse = true);
