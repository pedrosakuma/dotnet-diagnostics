using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Shared cleanup pipeline invoked by <c>detach_from_pod</c> (caller-initiated close)
/// and the TTL reaper (server-initiated eviction). Centralises the order so both
/// paths flip the handle into a terminal state, revoke Pod-local credentials, delete
/// residual Secret material, dispose the cached MCP client, close the port-forward
/// transport after confirmed revocation, and unbind every MCP session pointed at the
/// handle — in that order.
/// </summary>
/// <remarks>
/// <para>
/// Order matters: revocation must traverse the still-live port-forward before the
/// proxy client and transport are disposed. Secret deletion follows as idempotent
/// cleanup. Confirmed steps scrub only the material they no longer need: runtime
/// plaintext after revocation, Secret metadata after deletion. If revocation fails,
/// the internal port-forward remains available for a later cleanup retry while the
/// terminal handle and disposed proxy prevent diagnostic calls. Unbinding sessions
/// last guarantees callers cannot keep using a cleanup-pending handle.
/// </para>
/// <para>
/// Ephemeral containers cannot be removed once added. Close stops the Pod-local
/// diagnostics process after revoking its credentials, but operators auditing
/// <c>ephemeralContainerStatuses</c> will still see the terminated entry.
/// </para>
/// </remarks>
public sealed class InvestigationCloser
{
    private readonly IInvestigationStore _store;
    private readonly IInvestigationProxyClient _proxyClient;
    private readonly IInvestigationTransportManager _transportManager;
    private readonly IInvestigationSessionBinder _sessionBinder;
    private readonly IInvestigationCredentialRevoker _credentialRevoker;
    private readonly IKubernetesAttachmentSecretManager _secretManager;
    private readonly ILogger<InvestigationCloser> _logger;

    public InvestigationCloser(
        IInvestigationStore store,
        IInvestigationProxyClient proxyClient,
        IInvestigationTransportManager transportManager,
        IInvestigationSessionBinder sessionBinder,
        IInvestigationCredentialRevoker credentialRevoker,
        IKubernetesAttachmentSecretManager secretManager,
        ILogger<InvestigationCloser>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _proxyClient = proxyClient ?? throw new ArgumentNullException(nameof(proxyClient));
        _transportManager = transportManager ?? throw new ArgumentNullException(nameof(transportManager));
        _sessionBinder = sessionBinder ?? throw new ArgumentNullException(nameof(sessionBinder));
        _credentialRevoker = credentialRevoker ?? throw new ArgumentNullException(nameof(credentialRevoker));
        _secretManager = secretManager ?? throw new ArgumentNullException(nameof(secretManager));
        _logger = logger ?? NullLogger<InvestigationCloser>.Instance;
    }

    /// <summary>
    /// Closes an investigation. Returns the outcome so the caller (tool / reaper) can
    /// project it into the appropriate response shape.
    /// </summary>
    /// <param name="handleId">Handle id to close. May reference an unknown handle.</param>
    /// <param name="targetState">Terminal state to transition into — <see cref="InvestigationState.Closed"/>
    /// for caller-initiated detach, <see cref="InvestigationState.Expired"/> for TTL eviction,
    /// <see cref="InvestigationState.Failed"/> for attach-failure cleanup.</param>
    /// <param name="failureReason">Optional reason carried onto the handle when transitioning
    /// to <see cref="InvestigationState.Failed"/> or <see cref="InvestigationState.Expired"/>.
    /// Ignored for <see cref="InvestigationState.Closed"/>.</param>
    public async Task<InvestigationCloseOutcome> CloseAsync(
        string handleId,
        InvestigationState targetState,
        string? failureReason = null)
    {
        if (string.IsNullOrEmpty(handleId))
        {
            return new InvestigationCloseOutcome(
                HandleId: handleId ?? string.Empty,
                Found: false,
                AlreadyTerminal: false,
                PreviousState: null,
                NewState: null,
                UnboundSessionIds: Array.Empty<string>(),
                CleanupErrorCount: 0);
        }

        var handle = _store.GetById(handleId);
        var transition = _store.TryTransitionToTerminal(
            handleId,
            targetState,
            failureReason,
            out var previousState);

        if (transition == InvestigationTerminalTransition.NotFound)
        {
            return new InvestigationCloseOutcome(
                HandleId: handleId,
                Found: false,
                AlreadyTerminal: false,
                PreviousState: null,
                NewState: null,
                UnboundSessionIds: Array.Empty<string>(),
                CleanupErrorCount: 0);
        }

        // For AlreadyTerminal we still drain the cleanup pipeline idempotently — a
        // partial prior close (process restart, exception mid-pipeline, racing closer
        // that lost) may have left a port-forward or session binding behind.
        var cleanupErrors = 0;
        var revocationSucceeded = true;
        if (handle is not null)
        {
            if (InvestigationCredentialCleanup.HasRuntimeCredentials(handle))
            {
                revocationSucceeded =
                    !InvestigationCredentialCleanup.RequiresRuntimeRevocation(handle) ||
                    await SafeRevokeCredentialsAsync(handle).ConfigureAwait(false);
                if (revocationSucceeded)
                {
                    ScrubCredentials(handleId, InvestigationCredentialMaterial.RuntimeCredentials);
                }
                else
                {
                    cleanupErrors++;
                }
            }

            if (InvestigationCredentialCleanup.RequiresSecretDeletion(handle))
            {
                var secretDeletionSucceeded =
                    await SafeDeleteSecretAsync(handle).ConfigureAwait(false);
                if (secretDeletionSucceeded)
                {
                    ScrubCredentials(handleId, InvestigationCredentialMaterial.SecretReference);
                }
                else
                {
                    cleanupErrors++;
                }
            }
        }
        cleanupErrors += await SafeDisposeProxyAsync(handleId).ConfigureAwait(false);
        if (revocationSucceeded)
        {
            cleanupErrors += await SafeClosePortForwardAsync(handleId).ConfigureAwait(false);
        }
        var unbound = _sessionBinder.UnbindAllForHandle(handleId);

        var alreadyTerminal = transition == InvestigationTerminalTransition.AlreadyTerminal;
        return new InvestigationCloseOutcome(
            HandleId: handleId,
            Found: true,
            AlreadyTerminal: alreadyTerminal,
            PreviousState: previousState,
            NewState: alreadyTerminal ? previousState : targetState,
            UnboundSessionIds: unbound,
            CleanupErrorCount: cleanupErrors);
    }

    private void ScrubCredentials(
        string handleId,
        InvestigationCredentialMaterial material)
        => (_store as IInvestigationStoreCredentialScrubber)?
            .ScrubCredentials(handleId, material);

    private async Task<bool> SafeRevokeCredentialsAsync(InvestigationHandle handle)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _credentialRevoker.RevokeAsync(handle, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Revoking pod-local credentials for handle {HandleId} threw; continuing close pipeline.",
                handle.HandleId);
            return false;
        }
    }

    private async Task<bool> SafeDeleteSecretAsync(InvestigationHandle handle)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _secretManager.DeleteAsync(handle, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deleting attachment credential Secret for handle {HandleId} threw; continuing close pipeline.",
                handle.HandleId);
            return false;
        }
    }

    private async Task<int> SafeDisposeProxyAsync(string handleId)
    {
        try
        {
            await _proxyClient.DisposeForHandleAsync(handleId).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing proxy MCP client for handle {HandleId} threw; continuing close pipeline.", handleId);
            return 1;
        }
    }

    private async Task<int> SafeClosePortForwardAsync(string handleId)
    {
        try
        {
            await _transportManager.CloseAsync(handleId).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing transport for handle {HandleId} threw; continuing close pipeline.", handleId);
            return 1;
        }
    }
}

/// <summary>
/// Result of <see cref="InvestigationCloser.CloseAsync"/>. <see cref="Found"/> is false
/// when the handle id was never registered; <see cref="AlreadyTerminal"/> is true when
/// the handle existed but was already Closed/Expired/Failed before this call.
/// </summary>
public sealed record InvestigationCloseOutcome(
    string HandleId,
    bool Found,
    bool AlreadyTerminal,
    InvestigationState? PreviousState,
    InvestigationState? NewState,
    IReadOnlyCollection<string> UnboundSessionIds,
    int CleanupErrorCount);
