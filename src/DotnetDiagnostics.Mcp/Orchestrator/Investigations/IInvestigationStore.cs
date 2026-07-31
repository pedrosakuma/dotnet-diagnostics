using System;
using System.Collections.Generic;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// In-memory registry of investigation handles minted by <c>attach_to_pod</c>. Lookup
/// is by opaque handle id; reuse lookup is by (namespace, pod, container) tuple.
/// </summary>
/// <remarks>
/// The orchestrator is stateless across restarts by design (see
/// docs/central-orchestrator-design.md §5.7) — no implementation persists handles.
/// A typed interface still exists so unit tests can swap behavior and so a future
/// distributed-orchestrator implementation could plug in without touching call sites.
/// </remarks>
public interface IInvestigationStore
{
    /// <summary>Adds a fresh handle. Throws if <see cref="InvestigationHandle.HandleId"/> already exists.</summary>
    void Add(InvestigationHandle handle);

    /// <summary>
    /// Atomically reserves a target tuple in <see cref="InvestigationState.Attaching"/>. An
    /// Active/Attaching handle blocks the reservation when reuse is allowed. A terminal handle
    /// whose credential cleanup is still pending always blocks it, regardless of reuse policy,
    /// until revocation and Secret deletion complete. The blocking handle is returned through
    /// <paramref name="existing"/>; otherwise the supplied <paramref name="newHandle"/> is registered.
    /// </summary>
    /// <returns>True when the supplied <paramref name="newHandle"/> was registered; false when an existing handle still reserves the target.</returns>
    bool TryReserveTarget(InvestigationHandle newHandle, bool allowReuse, out InvestigationHandle? existing);

    /// <summary>Updates an existing handle (e.g. state transition). Throws if the id is unknown.</summary>
    void Update(InvestigationHandle handle);

    /// <summary>
    /// Atomically transitions a handle to a terminal state (Closed / Expired / Failed),
    /// under the store lock. Returns the outcome so the caller can distinguish
    /// "transitioned now", "already terminal" (lost the race or prior close), and
    /// "unknown handle".
    /// </summary>
    /// <param name="handleId">Target handle id.</param>
    /// <param name="targetState">Terminal state to transition into. Must be Closed, Expired or Failed.</param>
    /// <param name="failureReason">Optional reason; ignored for Closed (which preserves any existing reason).</param>
    /// <param name="previousState">Out: state observed before the (attempted) transition. Null when the handle is unknown.</param>
    InvestigationTerminalTransition TryTransitionToTerminal(
        string handleId,
        InvestigationState targetState,
        string? failureReason,
        out InvestigationState? previousState);

    /// <summary>Returns the handle with the given id, or null if unknown.</summary>
    InvestigationHandle? GetById(string handleId);

    /// <summary>
    /// Returns an existing <see cref="InvestigationState.Active"/> or
    /// <see cref="InvestigationState.Attaching"/> handle whose
    /// <see cref="InvestigationHandle.ReservationKey"/> matches the given key, or null
    /// if none. Used by <c>attach_to_pod</c> to honour the reuse policy from §5.5.
    /// </summary>
    InvestigationHandle? FindReusableTarget(string reservationKey);

    /// <summary>
    /// Returns the most recently registered terminal (<see cref="InvestigationState.Closed"/>,
    /// <see cref="InvestigationState.Expired"/>, or <see cref="InvestigationState.Failed"/>) handle
    /// whose <see cref="InvestigationHandle.EphemeralContainerName"/> matches
    /// <paramref name="ephemeralContainerName"/> for the given pod, or null if none exists.
    /// Retained for store compatibility and inventory lookups. Attachment code must not
    /// adopt a terminal container or reuse its credentials; every reattach creates a new
    /// container and credential pair.
    /// </summary>
    InvestigationHandle? FindTerminalHandleByEphemeralName(
        string podNamespace, string podName, string ephemeralContainerName);

    /// <summary>Snapshot of every known handle. Order is unspecified.</summary>
    IReadOnlyCollection<InvestigationHandle> Snapshot();
}

/// <summary>
/// Optional atomic activation capability. Kept separate so existing
/// <see cref="IInvestigationStore"/> implementations remain binary-compatible.
/// </summary>
public interface IInvestigationStoreActivation
{
    bool TryTransitionToActive(string handleId, out InvestigationHandle? active);
}

/// <summary>
/// Optional terminal-state credential scrubbing capability. Close paths invoke this
/// only after Pod-local revocation and transport teardown have consumed the credentials.
/// </summary>
public interface IInvestigationStoreCredentialScrubber
{
    void ScrubCredentials(string handleId, InvestigationCredentialMaterial material);
}

public interface IInvestigationStoreCredentialDelivery
{
    bool TrySetCredentialsMayBeInUse(
        string handleId,
        bool mayBeInUse,
        out InvestigationHandle? updated);
}

[Flags]
public enum InvestigationCredentialMaterial
{
    None = 0,
    RuntimeCredentials = 1,
    SecretReference = 2,
    All = RuntimeCredentials | SecretReference,
}

internal static class InvestigationCredentialCleanup
{
    public static bool HasRuntimeCredentials(InvestigationHandle handle)
        => handle.Kubernetes is not null &&
           (!string.IsNullOrEmpty(handle.Kubernetes.PodLocalBearerToken) ||
            !string.IsNullOrEmpty(handle.InternalScopeDelegationKey));

    public static bool RequiresRuntimeRevocation(InvestigationHandle handle)
        => handle.Kubernetes?.CredentialsMayBeInUse == true &&
           HasRuntimeCredentials(handle);

    public static bool RequiresSecretDeletion(InvestigationHandle handle)
        => !string.IsNullOrEmpty(handle.Kubernetes?.CredentialSecretName);

    public static bool IsPending(InvestigationHandle handle)
        => HasRuntimeCredentials(handle) || RequiresSecretDeletion(handle);
}

/// <summary>
/// Optional atomic lease-touch capability. Kept separate so existing
/// <see cref="IInvestigationStore"/> implementations remain binary-compatible.
/// </summary>
public interface IInvestigationStoreLeaseTouch
{
    InvestigationLeaseTouchResult TryTouchSuccessfulCall(
        string handleId,
        DateTimeOffset successfulCallCompletedAt,
        out InvestigationHandle? updated);
}

/// <summary>
/// Optional atomic expiry-transition capability. Kept separate so existing
/// <see cref="IInvestigationStore"/> implementations remain binary-compatible.
/// </summary>
public interface IInvestigationStoreExpiry
{
    InvestigationExpiryTransition TryTransitionToExpiredIfStillExpired(
        string handleId,
        DateTimeOffset now,
        string failureReason,
        out InvestigationHandle? updated,
        out InvestigationState? previousState);
}

/// <summary>
/// Result of <see cref="IInvestigationStore.TryTransitionToTerminal"/>.
/// </summary>
public enum InvestigationTerminalTransition
{
    /// <summary>The handle id is not (or no longer) registered.</summary>
    NotFound,

    /// <summary>The handle was non-terminal and was atomically transitioned to the requested terminal state.</summary>
    Transitioned,

    /// <summary>The handle existed but was already terminal — no state change applied.</summary>
    AlreadyTerminal,
}

/// <summary>
/// Result of <see cref="IInvestigationStoreLeaseTouch.TryTouchSuccessfulCall"/>.
/// </summary>
public enum InvestigationLeaseTouchResult
{
    /// <summary>The handle id is not (or no longer) registered.</summary>
    NotFound,

    /// <summary>The active handle was atomically touched and its idle lease was refreshed.</summary>
    Touched,

    /// <summary>
    /// The handle existed but was not touchable at commit time (for example it had
    /// already become Attaching/Closed/Expired/Failed, or its effective lease had
    /// already elapsed).
    /// </summary>
    Skipped,
}

/// <summary>
/// Result of <see cref="IInvestigationStoreExpiry.TryTransitionToExpiredIfStillExpired"/>.
/// </summary>
public enum InvestigationExpiryTransition
{
    /// <summary>The handle id is not (or no longer) registered.</summary>
    NotFound,

    /// <summary>The handle was still reapable and expired, and was atomically transitioned to Expired.</summary>
    Transitioned,

    /// <summary>
    /// The handle existed but was no longer reapable or expired when the store re-checked
    /// the current state under its concurrency guard.
    /// </summary>
    Skipped,
}
