using System;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Lifecycle state of an orchestrator investigation handle, per
/// docs/central-orchestrator-design.md §5.3.
/// </summary>
public enum InvestigationState
{
    /// <summary>Ephemeral container patch accepted; readiness wait in progress; no proxied calls allowed yet.</summary>
    Attaching = 0,
    /// <summary>Ephemeral container is Running; the handle is usable (proxy plumbing lands in P3b-2).</summary>
    Active = 1,
    /// <summary>Caller invoked <c>detach</c>; transport resources released.</summary>
    Closed = 2,
    /// <summary>TTL elapsed; orchestrator closed the session.</summary>
    Expired = 3,
    /// <summary>Attach never became usable, or transport could not be established.</summary>
    Failed = 4,
}

/// <summary>
/// One opaque investigation produced by <c>attach_to_pod</c>. Owned by an
/// <c>IInvestigationStore</c>; the orchestrator hands the <see cref="HandleId"/> back to
/// the client and looks the rest of the state up by id on every subsequent call.
/// </summary>
/// <remarks>
/// <para>
/// The bearer token is generated per-attach and embedded into the ephemeral
/// container's environment. It is never returned to the external client — the proxy
/// (P3b-2) injects it on the orchestrator side of the boundary. This is the
/// "per-attach Pod-local bearer token" mitigation called out in
/// docs/central-orchestrator-design.md §6.4.
/// </para>
/// <para>
/// <see cref="EphemeralContainerName"/> is informational: ephemeral containers cannot
/// be removed once added (Kubernetes constraint), so the name is surfaced to operators
/// who audit a Pod's <c>ephemeralContainerStatuses</c> after detach.
/// </para>
/// </remarks>
public sealed record InvestigationHandle(
    string HandleId,
    string Namespace,
    string PodName,
    string TargetContainerName,
    string EphemeralContainerName,
    [property: JsonIgnore] string PodLocalBearerToken,
    InvestigationState State,
    DateTimeOffset AttachedAt,
    DateTimeOffset ExpiresAt,
    string? FailureReason = null,
    // Identity of the bearer principal that minted this handle. Authorization is
    // bound to the authenticated bearer and its scopes, not to protocol session
    // headers. Null preserves stdio / synthetic-root ergonomics for transports
    // that do not project a bearer identity into the tool call.
    // Hidden from the client-safe AttachSession projection so the LLM cannot
    // enumerate other callers' handles.
    [property: JsonIgnore] string? OwnerBearerName = null);
