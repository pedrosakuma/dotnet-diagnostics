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
/// One opaque investigation produced by <c>attach_to_pod</c> or an external MCP
/// registration. Owned by an <c>IInvestigationStore</c>; the orchestrator hands the
/// <see cref="HandleId"/> back to the client and looks the rest of the state up by id on
/// every subsequent call.
/// </summary>
/// <remarks>
/// <para>
/// Transport-specific metadata lives in provider-typed properties (<see cref="Kubernetes"/>
/// for Kubernetes pod-attach handles, <see cref="ExternalMcp"/> for operator-configured
/// external endpoints) rather than as flat fields on the record. This keeps the core
/// handle, proxy endpoint, and fan-out paths transport-neutral.
/// </para>
/// <para>
/// The bearer token and independent scope-delegation key are generated per-attach and
/// delivered through a short-lived Kubernetes Secret reference. The bearer token is kept in
/// the transport-specific metadata (<see cref="KubernetesInvestigationTarget.PodLocalBearerToken"/>
/// / <see cref="ExternalMcpInvestigationTarget.BearerToken"/>) and injected by the
/// transport implementation into <see cref="System.Net.Http.HttpClient.DefaultRequestHeaders"/>
/// — it is never returned to the external client.
/// The Secret is deleted as soon as the container reaches Running, while the
/// process-local credentials are revoked on detach and expire at the absolute lease
/// deadline. This is the "per-attach Pod-local bearer token" mitigation called out in
/// docs/central-orchestrator-design.md §6.4.
/// </para>
/// <para>
/// The backward-compatible computed properties <see cref="Namespace"/>, <see cref="PodName"/>,
/// <see cref="TargetContainerName"/>, and <see cref="EphemeralContainerName"/> delegate to
/// <see cref="Kubernetes"/> so existing callers that read these values compile without change.
/// Code that sets or creates handles must use <see cref="Kubernetes"/> directly.
/// </para>
/// </remarks>
public sealed record InvestigationHandle(
    string HandleId,
    KubernetesInvestigationTarget? Kubernetes,
    InvestigationState State,
    DateTimeOffset AttachedAt,
    InvestigationLease Lease,
    string? FailureReason = null,
    // Display-only name of the bearer principal that minted this handle.
    [property: JsonIgnore] string? OwnerBearerName = null,
    // Stable provider-namespaced identity used for authorization. A legacy handle
    // with OwnerBearerName but no OwnerPrincipalKey fails owner checks closed.
    [property: JsonIgnore] string? OwnerPrincipalKey = null,
    // Independent replay-protected signing secret used when the proxy delegates a narrowed
    // tool/scope subset to the Pod-local transport. Never exposed to the client.
    [property: JsonIgnore] string? InternalScopeDelegationKey = null,
    // Optional transport-neutral process selector resolved inside the attached Pod before
    // fan-out collectors run.
    InvestigationProcessSelector? ProcessSelector = null,
    // External MCP target metadata for handles registered via an operator-configured
    // ExternalMcpProfile. Null for Kubernetes handles.
    ExternalMcpInvestigationTarget? ExternalMcp = null)
{
    /// <summary>Deadline for the Attaching → Active transition.</summary>
    public DateTimeOffset AttachDeadline => Lease.AttachDeadline;

    /// <summary>Requested per-handle idle TTL captured at attach time.</summary>
    public TimeSpan IdleTtl => Lease.IdleTtl;

    /// <summary>Timestamp of the last successful proxied tool call, if any.</summary>
    public DateTimeOffset? LastSuccessfulUseAt => Lease.LastSuccessfulUseAt;

    /// <summary>Current idle-expiry deadline, refreshed only after successful proxied calls.</summary>
    public DateTimeOffset IdleExpiresAt => Lease.IdleExpiresAt;

    /// <summary>Hard wall-clock cap for the handle lifetime.</summary>
    public DateTimeOffset AbsoluteExpiresAt => Lease.AbsoluteExpiresAt;

    /// <summary>Backward-compatible effective expiry used by summaries and projections.</summary>
    public DateTimeOffset ExpiresAt => Lease.EffectiveExpiresAt;

    public InvestigationHandle(
        string HandleId,
        KubernetesInvestigationTarget? Kubernetes,
        InvestigationState State,
        DateTimeOffset AttachedAt,
        DateTimeOffset ExpiresAt,
        string? FailureReason = null,
        string? OwnerBearerName = null,
        string? OwnerPrincipalKey = null,
        string? InternalScopeDelegationKey = null,
        InvestigationProcessSelector? ProcessSelector = null,
        ExternalMcpInvestigationTarget? ExternalMcp = null)
        : this(
            HandleId,
            Kubernetes,
            State,
            AttachedAt,
            InvestigationLeasePolicy.FromLegacyExpiry(AttachedAt, ExpiresAt),
            FailureReason,
            OwnerBearerName,
            OwnerPrincipalKey,
            InternalScopeDelegationKey,
            ProcessSelector,
            ExternalMcp)
    {
    }

    /// <summary>
    /// Transport-neutral display label used in logs, error messages, and observability.
    /// For Kubernetes targets this is <c>namespace/pod/container</c>; for external MCP
    /// targets this is <c>external:{profileName}</c>; otherwise falls back to
    /// <see cref="HandleId"/>.
    /// </summary>
    public string TargetDisplayName => Kubernetes is { } k
        ? $"{k.Namespace}/{k.PodName}/{k.TargetContainerName}"
        : ExternalMcp is { } ext
            ? $"external:{ext.ProfileName}"
            : HandleId;

    /// <summary>
    /// Provider-specific deduplication key used by <see cref="IInvestigationStore"/> to
    /// prevent duplicate attachments to the same target. For Kubernetes this is
    /// <c>k8s:namespace/pod/container</c>; for external MCP targets this is
    /// <c>external:{profileName}</c>; for all other handles returns an empty string
    /// (no reservation).
    /// </summary>
    [JsonIgnore]
    public string ReservationKey => Kubernetes is { } k
        ? $"k8s:{k.Namespace}/{k.PodName}/{k.TargetContainerName}"
        : ExternalMcp is { } ext
            ? $"external:{ext.ProfileName}"
            : string.Empty;

    // ── Backward-compatible properties ──────────────────────────────────────────────────
    // These delegate to the Kubernetes target so existing callers that read them compile
    // without modification. They return empty string when the handle has no Kubernetes
    // metadata (e.g. future ExternalMcp handles).

    /// <summary>Kubernetes namespace; empty string for non-Kubernetes handles.</summary>
    public string Namespace => Kubernetes?.Namespace ?? string.Empty;

    /// <summary>Kubernetes pod name; empty string for non-Kubernetes handles.</summary>
    public string PodName => Kubernetes?.PodName ?? string.Empty;

    /// <summary>Target container name; empty string for non-Kubernetes handles.</summary>
    public string TargetContainerName => Kubernetes?.TargetContainerName ?? string.Empty;

    /// <summary>
    /// Name of the injected ephemeral diagnostics container; empty string for
    /// non-Kubernetes handles. Informational — ephemeral containers cannot be removed
    /// once added (Kubernetes constraint), so the name is surfaced to operators who audit
    /// a Pod's <c>ephemeralContainerStatuses</c> after detach.
    /// </summary>
    public string EphemeralContainerName => Kubernetes?.EphemeralContainerName ?? string.Empty;
}
