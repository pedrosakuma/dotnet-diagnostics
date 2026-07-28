using System.Collections.Generic;

namespace DotnetDiagnostics.Mcp.Orchestrator;

/// <summary>
/// Non-secret summary of one operator-configured external MCP profile. Returned by
/// <c>list_orchestrator(kind="external-profiles")</c>. Credentials are intentionally
/// omitted — only the metadata needed to pick a profile for <c>attach_to_pod(profileName=…)</c>
/// is included.
/// </summary>
/// <param name="Name">Profile name as declared under <c>Orchestrator:ExternalMcpProfiles:{name}</c>.</param>
/// <param name="Url">Absolute URL of the external MCP endpoint.</param>
/// <param name="AllowedCidrs">CIDR blocks the profile's DNS-resolved address must fall within.</param>
/// <param name="AllowedPorts">TCP ports the endpoint is allowed to bind on.</param>
/// <param name="ConnectTimeoutSeconds">TCP connect timeout in seconds.</param>
/// <param name="MaxConcurrency">Maximum concurrent outstanding HTTP calls.</param>
public sealed record ExternalProfileEntry(
    string Name,
    string Url,
    IReadOnlyList<string> AllowedCidrs,
    IReadOnlyList<int> AllowedPorts,
    int ConnectTimeoutSeconds,
    int MaxConcurrency);

/// <summary>
/// Page returned by <c>list_orchestrator(kind="external-profiles")</c>.
/// </summary>
/// <param name="Items">All configured profiles exposed as non-secret metadata. Empty when no profiles are configured.</param>
public sealed record ExternalProfilePage(IReadOnlyList<ExternalProfileEntry> Items);
