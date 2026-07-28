using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Default <see cref="IExternalProfileAttachOrchestrator"/> implementation. Registers an
/// operator-configured external MCP profile (<see cref="ExternalMcpProfile"/>) as an
/// investigation handle and attempts to initialize the transport. The handle transitions
/// to <see cref="InvestigationState.Active"/> only after the transport is successfully set up;
/// on any failure it transitions to <see cref="InvestigationState.Failed"/> and the
/// exception is surfaced to the caller.
/// </summary>
internal sealed class ExternalProfileAttachOrchestrator : IExternalProfileAttachOrchestrator
{
    private readonly IInvestigationStore _store;
    private readonly IInvestigationTransportManager _transportManager;
    private readonly IInvestigationProxyClient _proxyClient;
    private readonly OrchestratorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExternalProfileAttachOrchestrator> _logger;

    public ExternalProfileAttachOrchestrator(
        IInvestigationStore store,
        IInvestigationTransportManager transportManager,
        IInvestigationProxyClient proxyClient,
        OrchestratorOptions options,
        ILogger<ExternalProfileAttachOrchestrator> logger)
        : this(store, transportManager, proxyClient, options, TimeProvider.System, logger)
    {
    }

    internal ExternalProfileAttachOrchestrator(
        IInvestigationStore store,
        IInvestigationTransportManager transportManager,
        IInvestigationProxyClient proxyClient,
        OrchestratorOptions options,
        TimeProvider timeProvider,
        ILogger<ExternalProfileAttachOrchestrator> logger)
    {
        _store = store;
        _transportManager = transportManager;
        _proxyClient = proxyClient;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<InvestigationHandle> AttachAsync(ExternalProfileAttachRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ProfileName))
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.InvalidArgument,
                "profileName is required for external profile attachment.");
        }

        if (!_options.ExternalMcpProfiles.TryGetValue(request.ProfileName, out var profile))
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.ExternalMcpProfileInvalid,
                $"External MCP profile '{request.ProfileName}' is not in the server configuration. " +
                "Use list_orchestrator(kind=\"external-profiles\") to see the available profiles.");
        }

        var now = _timeProvider.GetUtcNow();
        var ttl = TimeSpan.FromSeconds(request.TtlSeconds ?? _options.DefaultInvestigationTtlSeconds);
        var handleId = "inv_" + RandomHex(16);
        var reservationKey = $"external:{request.ProfileName}";

        // Unlike the Kubernetes attach path — where the orchestrator controls the target
        // pod and can inject a freshly-generated per-handle secret into it via exec at
        // attach time — an external MCP endpoint is a standalone, already-running server
        // the orchestrator does not control. It can only verify a delegation token against
        // whatever static secret its own MCP_INTERNAL_SCOPE_DELEGATION_KEY was started
        // with, so the delegation key here must be the operator-configured, per-profile
        // static secret (profile.DelegationKey), not a random per-handle value. If the
        // profile has none configured, proxied tool calls through this handle will be
        // refused (see InvestigationProxyCallToolFilter's "delegation unavailable" guard)
        // rather than silently sent unsigned.
        var delegationKey = string.IsNullOrEmpty(profile.DelegationKey) ? null : profile.DelegationKey;

        var handle = new InvestigationHandle(
            HandleId: handleId,
            Kubernetes: null,
            State: InvestigationState.Attaching,
            AttachedAt: now,
            ExpiresAt: now + ttl,
            OwnerBearerName: request.OwnerBearerName,
            OwnerPrincipalKey: request.OwnerPrincipalKey,
            InternalScopeDelegationKey: delegationKey,
            ExternalMcp: new ExternalMcpInvestigationTarget(
                ProfileName: request.ProfileName,
                Url: new Uri(profile.Url),
                BearerToken: string.IsNullOrEmpty(profile.BearerToken) ? null : profile.BearerToken));

        // Atomic check-and-reserve: when reuse is allowed and an Active/Attaching handle
        // already exists for this profile, return it instead of creating a duplicate.
        if (!_store.TryReserveTarget(handle, request.AllowReuseExistingSession, out var existing))
        {
            if (!InvestigationOwnership.IsOwnedBy(existing!, request.OwnerPrincipalKey))
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.PermissionDenied,
                    $"An investigation for profile '{request.ProfileName}' is already active and owned by a different identity. " +
                    "Wait for it to expire or use detach_from_pod to close it first.");
            }
            _logger.LogInformation(
                "Reusing existing investigation {HandleId} for external profile '{ProfileName}'.",
                existing!.HandleId, request.ProfileName);
            return existing!;
        }

        // Attempt to initialize the transport. This builds the SSRF-safe HTTP client
        // (DNS/CIDR validation happens lazily on first connect, inside the transport's
        // ConnectCallback). The handle transitions to Active only after that connect
        // AND a real MCP `initialize` handshake both succeed — any failure marks it
        // Failed and tears down the half-built transport so a retry starts clean.
        try
        {
            await _transportManager.GetOrCreateClientAsync(handle, cancellationToken).ConfigureAwait(false);
            // issue #711: prove the endpoint is reachable and actually speaks MCP
            // before advancing the handle to Active — building the HttpClient above
            // performs no I/O by itself.
            await _proxyClient.EnsureInitializedAsync(handle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MarkFailedAsync(handle, ex.Message).ConfigureAwait(false);
            var failKind = ex is OrchestratorException oe ? oe.ErrorKind : OrchestratorErrorKinds.ExternalMcpConnectFailed;
            throw new OrchestratorException(failKind, $"Failed to initialize transport for external profile '{request.ProfileName}': {ex.Message}", ex);
        }
        catch (OperationCanceledException)
        {
            await MarkFailedAsync(handle, "Attach canceled by caller during transport initialization.").ConfigureAwait(false);
            throw;
        }

        // Transition to Active.
        if (_store is not IInvestigationStoreActivation activation
            || !activation.TryTransitionToActive(handle.HandleId, out var active)
            || active is null)
        {
            var msg = $"Investigation {handle.HandleId} became inactive during external profile transport initialization.";
            throw new OrchestratorException(OrchestratorErrorKinds.AttachFailed, msg);
        }

        _logger.LogInformation(
            "Attached investigation {HandleId} to external profile '{ProfileName}'.",
            active.HandleId, request.ProfileName);
        return active;
    }

    private async Task MarkFailedAsync(InvestigationHandle handle, string reason)
    {
        _store.TryTransitionToTerminal(
            handle.HandleId,
            InvestigationState.Failed,
            reason,
            out _);
        // Tear down any half-built transport/MCP client so a subsequent attach for the
        // same profile does not resume from a transport that never completed handshake.
        await _proxyClient.DisposeForHandleAsync(handle.HandleId).ConfigureAwait(false);
        await _transportManager.CloseAsync(handle.HandleId).ConfigureAwait(false);
    }

    private static string RandomHex(int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
