using System;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Transport metadata for an investigation handle that routes through a named
/// operator-configured external MCP endpoint (see <see cref="ExternalMcpProfile"/>).
/// </summary>
/// <remarks>
/// <para>
/// Stored in <see cref="InvestigationHandle.ExternalMcp"/>. The profile name identifies
/// the operator-configured endpoint; the bearer token is injected by the transport layer
/// and is never exposed to callers.
/// </para>
/// <para>
/// <see cref="BearerToken"/> is <see cref="JsonIgnoreAttribute"/> for the same reason as
/// <see cref="KubernetesInvestigationTarget.PodLocalBearerToken"/>: the credential must
/// never appear in serialized handles, list responses, error envelopes, or log lines.
/// </para>
/// </remarks>
/// <param name="ProfileName">
/// Name of the operator-configured profile
/// (<c>Orchestrator:ExternalMcpProfiles:{name}</c>). Used in logs, display names, and
/// error messages; never the URL so any bearer token embedded in the URL is not
/// inadvertently surfaced.
/// </param>
/// <param name="Url">
/// Validated URL of the external MCP endpoint. Always ends with <c>/mcp</c>, always
/// absolute, always has a scheme of <c>http</c> or <c>https</c>. Stored here so the
/// transport layer can use it without re-reading configuration.
/// </param>
/// <param name="BearerToken">
/// ****** injected as <c>Authorization: ******;token&gt;</c> by the transport
/// layer. Never returned to clients. <see cref="JsonIgnoreAttribute"/> ensures it is
/// absent from all serialized representations.
/// </param>
public sealed record ExternalMcpInvestigationTarget(
    string ProfileName,
    Uri Url,
    [property: JsonIgnore] string? BearerToken = null);
