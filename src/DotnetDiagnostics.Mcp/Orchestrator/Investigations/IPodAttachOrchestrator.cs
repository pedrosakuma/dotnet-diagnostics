using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Request payload for <see cref="IPodAttachOrchestrator.AttachAsync"/>. Mirrors the
/// <c>attach_to_pod</c> MCP tool signature 1:1 so the tool layer is a thin shim.
/// </summary>
/// <param name="Namespace">Pod namespace. Required.</param>
/// <param name="PodName">Pod name. Required.</param>
/// <param name="ContainerName">Target container name. Defaults to the first container on the Pod when null.</param>
/// <param name="TtlSeconds">Per-handle TTL override; null uses <c>OrchestratorOptions.DefaultInvestigationTtlSeconds</c>.</param>
/// <param name="RequirePreparedTarget">When true (default), refuse to attach to an unprepared Pod.</param>
/// <param name="AllowReuseExistingSession">When true (default), return an existing Active/Attaching handle for the same target instead of patching a second ephemeral container.</param>
/// <param name="OwnerBearerName">Display name of the caller, retained for diagnostics only. Authorization uses <paramref name="OwnerPrincipalKey"/>.</param>
/// <param name="OwnerPrincipalKey">Stable provider-namespaced identity used for ownership authorization.</param>
/// <param name="ProcessSelector">Optional transport-neutral process identity resolved inside the attached Pod before replica fan-out.</param>
public sealed record AttachRequest(
    string Namespace,
    string PodName,
    string? ContainerName = null,
    int? TtlSeconds = null,
    bool RequirePreparedTarget = true,
    bool AllowReuseExistingSession = true,
    // Display name retained for diagnostics only; never use it for authorization.
    string? OwnerBearerName = null,
    // Stable provider-namespaced caller identity used for ownership checks.
    string? OwnerPrincipalKey = null,
    // Optional transport-neutral process identity resolved inside the attached Pod before
    // replica fan-out. Null preserves the previous default-process behavior.
    InvestigationProcessSelector? ProcessSelector = null);

/// <summary>
/// Two-phase attach: validate the target, patch the ephemeral container, wait for
/// readiness, register the handle. Implementations isolate the kube API surface
/// so tests can simulate slow / failing patches.
/// </summary>
/// <remarks>
/// P3b-1 ships the attach mechanics; the proxy that makes the returned handle
/// actually call-able lands in P3b-2. Until then, callers receive a handle in
/// <see cref="InvestigationState.Active"/> state but no diagnostic tool will yet
/// honour it for transport — that wiring is the deliberate next slice.
/// </remarks>
public interface IPodAttachOrchestrator
{
    Task<InvestigationHandle> AttachAsync(AttachRequest request, CancellationToken cancellationToken);
}
