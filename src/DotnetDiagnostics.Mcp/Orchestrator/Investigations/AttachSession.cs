using System;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Client-safe projection of an <see cref="InvestigationHandle"/>, returned by the
/// <c>attach_to_pod</c> MCP tool. Deliberately omits the Pod-local bearer token stored
/// in <see cref="KubernetesInvestigationTarget.PodLocalBearerToken"/> — that secret is
/// generated per-attach, delivered through a short-lived Kubernetes Secret reference,
/// and injected by the investigation transport manager on the server side of the boundary so the
/// external LLM client never sees it. See docs/central-orchestrator-design.md §6.4.
/// </summary>
public sealed record AttachSession(
    string HandleId,
    string Namespace,
    string PodName,
    string TargetContainerName,
    string EphemeralContainerName,
    InvestigationState State,
    DateTimeOffset AttachedAt,
    DateTimeOffset ExpiresAt,
    string? FailureReason = null,
    string? ProxyBaseUrl = null,
    InvestigationProcessSelector? ProcessSelector = null,
    // Populated when the investigation targets an operator-configured external MCP
    // profile (attach_to_pod(profileName=…)). Null for Kubernetes pod-attach handles.
    string? ProfileName = null)
{
    /// <summary>
    /// Projects an internal handle into the client-safe shape, dropping the bearer token.
    /// When <paramref name="proxyBaseUrl"/> is supplied it is attached so the client knows
    /// the URL prefix subsequent diagnostic tool calls should target. The orchestrator's
    /// reverse proxy strips the prefix, injects the per-attach bearer token, and forwards
    /// to the Pod-local diagnostics MCP.
    /// </summary>
    public static AttachSession FromHandle(InvestigationHandle handle, string? proxyBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return new AttachSession(
            HandleId: handle.HandleId,
            Namespace: handle.Namespace,
            PodName: handle.PodName,
            TargetContainerName: handle.TargetContainerName,
            EphemeralContainerName: handle.EphemeralContainerName,
            State: handle.State,
            AttachedAt: handle.AttachedAt,
            ExpiresAt: handle.ExpiresAt,
            FailureReason: handle.FailureReason,
            ProxyBaseUrl: proxyBaseUrl,
            ProcessSelector: handle.ProcessSelector,
            ProfileName: handle.ExternalMcp?.ProfileName);
    }
}
