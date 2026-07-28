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
    private readonly OrchestratorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExternalProfileAttachOrchestrator> _logger;

    public ExternalProfileAttachOrchestrator(
        IInvestigationStore store,
        IInvestigationTransportManager transportManager,
        OrchestratorOptions options,
        ILogger<ExternalProfileAttachOrchestrator> logger)
        : this(store, transportManager, options, TimeProvider.System, logger)
    {
    }

    internal ExternalProfileAttachOrchestrator(
        IInvestigationStore store,
        IInvestigationTransportManager transportManager,
        OrchestratorOptions options,
        TimeProvider timeProvider,
        ILogger<ExternalProfileAttachOrchestrator> logger)
    {
        _store = store;
        _transportManager = transportManager;
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
        var delegationKey = RandomHex(32);
        var reservationKey = $"external:{request.ProfileName}";

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

        // Attempt to initialize the transport. This validates that the profile is
        // reachable (SSRF check) and builds the HTTP client. The handle transitions to
        // Active only if this succeeds — any failure marks it Failed.
        try
        {
            await _transportManager.GetOrCreateClientAsync(handle, cancellationToken).ConfigureAwait(false);
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

    private Task MarkFailedAsync(InvestigationHandle handle, string reason)
    {
        _store.TryTransitionToTerminal(
            handle.HandleId,
            InvestigationState.Failed,
            reason,
            out _);
        return Task.CompletedTask;
    }

    private static string RandomHex(int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
