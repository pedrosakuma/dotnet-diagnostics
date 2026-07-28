using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Mcp.Orchestrator;

/// <summary>
/// Operator-configured named external MCP endpoint that the orchestrator can register
/// as an investigation handle without the model supplying a URI or upstream bearer.
/// </summary>
/// <remarks>
/// <para>
/// Profiles are declared under <c>Orchestrator:ExternalMcpProfiles:{name}</c>. They are
/// validated at startup: the URL must be an absolute <c>http</c> or <c>https</c> URL
/// whose path is exactly <c>/mcp</c> with no userinfo, query, fragment, or dot segments.
/// At least one <see cref="AllowedCidrs"/> and one <see cref="AllowedPorts"/> entry is
/// required; profiles that fail validation prevent the server from starting.
/// </para>
/// <para>
/// <see cref="BearerToken"/> is marked <see cref="JsonIgnoreAttribute"/> so it is never
/// serialized into any investigation handle, log entry, or error message returned to
/// callers. The transport layer injects it via
/// <c>HttpClient.DefaultRequestHeaders.Authorization</c>.
/// </para>
/// </remarks>
public sealed class ExternalMcpProfile
{
    /// <summary>
    /// Absolute URL of the external MCP endpoint. Must be <c>http</c> or <c>https</c>,
    /// path exactly <c>/mcp</c>, no userinfo, no query, no fragment. Validated at startup.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Bearer token injected as <c>Authorization: Bearer &lt;token&gt;</c> on every
    /// outbound request. Never returned to clients, logs, or errors.
    /// </summary>
    [JsonIgnore]
    public string BearerToken { get; set; } = string.Empty;

    /// <summary>
    /// Static, operator-configured shared secret used to sign internal scope-delegation
    /// tokens (docs/authorization.md#scopes) for tool calls proxied through this profile.
    /// Unlike the Kubernetes attach path — where the orchestrator controls the target pod
    /// and can inject a freshly-generated per-handle secret via exec at attach time — an
    /// external MCP endpoint is a standalone server the orchestrator does not control, so
    /// this key must match the value the target server itself was started with (its
    /// <c>MCP_INTERNAL_SCOPE_DELEGATION_KEY</c> environment variable). If left unset,
    /// tool calls proxied through a handle attached to this profile are refused with a
    /// "delegation unavailable" error rather than sent unsigned. Never serialized into
    /// any investigation handle, log entry, or error message returned to callers.
    /// </summary>
    [JsonIgnore]
    public string? DelegationKey { get; set; }

    /// <summary>
    /// Explicit CIDR blocks the DNS-resolved addresses of <see cref="Url"/> must fall
    /// within. Both IPv4 (e.g. <c>10.0.0.0/8</c>) and IPv6 (e.g. <c>fd00::/8</c>) are
    /// accepted. IPv4-mapped IPv6 addresses (<c>::ffff:x.x.x.x</c>) are unwrapped to
    /// their IPv4 form before checking to prevent bypass via the mapped family.
    /// Must be non-empty; profiles without any allowed CIDR fail startup validation.
    /// </summary>
    public IList<string> AllowedCidrs { get; } = new List<string>();

    /// <summary>
    /// TCP ports the endpoint is allowed to bind on. Must be non-empty and must include
    /// the port in <see cref="Url"/>. Profiles without any allowed port fail validation.
    /// </summary>
    public IList<int> AllowedPorts { get; } = new List<int>();

    /// <summary>
    /// TCP connect timeout in seconds. Default: 10.
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// MCP <c>initialize</c> handshake timeout in seconds. Applied to
    /// <c>McpClient.CreateAsync</c> so a slow or unresponsive upstream does not pin
    /// the first caller indefinitely. Default: 15.
    /// </summary>
    public int InitializeTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Per-tool-call timeout in seconds applied to the underlying
    /// <see cref="System.Net.Http.HttpClient.Timeout"/>. Default: 120.
    /// </summary>
    public int CallTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum response body size in bytes enforced by a
    /// <c>Content-Length</c> pre-check before reading the response. Default: 4 MiB.
    /// </summary>
    public long MaxResponseBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>
    /// Maximum number of concurrent outstanding HTTP calls to this endpoint. Default: 4.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;
}
