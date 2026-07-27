using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Mcp.Observability;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Default <see cref="IPodAttachOrchestrator"/> implementation backed by the
/// orchestrator's <see cref="IKubernetesPodsApi"/> + <see cref="OrchestratorOptions"/>.
/// </summary>
/// <remarks>
/// <para>Flow per docs/central-orchestrator-design.md §5.4:</para>
/// <list type="number">
/// <item>Validate namespace via the existing allowlist policy.</item>
/// <item>Reuse an in-flight or active handle for the same target when allowed.</item>
/// <item>Read the Pod to confirm phase=Running, the target container exists, and (when required) it carries the prepared label.</item>
/// <item>Mint a fresh per-attach bearer token, build a <see cref="V1EphemeralContainer"/> pinned to the target container's PID namespace, and patch the Pod.</item>
/// <item>Register the handle in Attaching state, poll <c>ephemeralContainerStatuses</c> until Running or timeout, transition to Active or Failed accordingly.</item>
/// </list>
/// <para>The proxy that makes a returned handle usable as a transport lands in P3b-2.</para>
/// </remarks>
internal sealed class KubernetesPodAttachOrchestrator : IPodAttachOrchestrator
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly IKubernetesPodsApi _podsApi;
    private readonly IInvestigationStore _store;
    private readonly InvestigationCloser _closer;
    private readonly OrchestratorObservability _observability;
    private readonly OrchestratorOptions _options;
    private readonly DotnetDiagnostics.Core.Security.SecurityOptions _securityOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KubernetesPodAttachOrchestrator> _logger;
    private readonly TimeSpan _pollInterval;

    public KubernetesPodAttachOrchestrator(
        IKubernetesPodsApi podsApi,
        IInvestigationStore store,
        InvestigationCloser closer,
        OrchestratorObservability observability,
        OrchestratorOptions options,
        DotnetDiagnostics.Core.Security.SecurityOptions securityOptions,
        ILogger<KubernetesPodAttachOrchestrator> logger)
        : this(
            podsApi,
            store,
            closer,
            observability,
            options,
            securityOptions,
            TimeProvider.System,
            DefaultPollInterval,
            logger)
    {
    }

    internal KubernetesPodAttachOrchestrator(
        IKubernetesPodsApi podsApi,
        IInvestigationStore store,
        InvestigationCloser closer,
        OrchestratorObservability observability,
        OrchestratorOptions options,
        TimeProvider timeProvider,
        TimeSpan pollInterval,
        ILogger<KubernetesPodAttachOrchestrator> logger)
        : this(
            podsApi,
            store,
            closer,
            observability,
            options,
            new DotnetDiagnostics.Core.Security.SecurityOptions(),
            timeProvider,
            pollInterval,
            logger)
    {
    }

    internal KubernetesPodAttachOrchestrator(
        IKubernetesPodsApi podsApi,
        IInvestigationStore store,
        InvestigationCloser closer,
        OrchestratorObservability observability,
        OrchestratorOptions options,
        DotnetDiagnostics.Core.Security.SecurityOptions securityOptions,
        TimeProvider timeProvider,
        TimeSpan pollInterval,
        ILogger<KubernetesPodAttachOrchestrator> logger)
    {
        _podsApi = podsApi;
        _store = store;
        _closer = closer;
        _observability = observability;
        _options = options;
        _securityOptions = securityOptions;
        _timeProvider = timeProvider;
        _pollInterval = pollInterval;
        _logger = logger;
    }

    public async Task<InvestigationHandle> AttachAsync(AttachRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ns = ResolveAndValidateNamespace(request.Namespace);
        if (string.IsNullOrWhiteSpace(request.PodName))
        {
            throw new OrchestratorException(OrchestratorErrorKinds.InvalidArgument, "podName is required.");
        }

        var pod = await ReadPodOrThrowAsync(ns, request.PodName, cancellationToken).ConfigureAwait(false);
        var container = SelectContainerOrThrow(pod, request.ContainerName);
        ValidatePodRunning(pod);
        ValidatePodPrepared(pod, request.RequirePreparedTarget);
        var processSelector = NormalizeProcessSelector(request.ProcessSelector);

        var now = _timeProvider.GetUtcNow();
        var ttl = TimeSpan.FromSeconds(request.TtlSeconds ?? _options.DefaultInvestigationTtlSeconds);

        // Before reserving a fresh target, scan for stale Running ephemeral containers
        // from previous sessions. Kubernetes does not allow ephemeral containers to be
        // removed once added, so after detach_from_pod the sidecar continues listening
        // on ProxyPodPort inside the pod. A second attach that tries to patch a new
        // container would fail with "address already in use" on that port.
        //
        // When there is NO Active/Attaching handle in the store (normal reattach after
        // detach), inspect the pod's ephemeralContainerStatuses directly:
        //   • Matching closed handle with a recoverable token → reuse the running
        //     container transparently (skip the patch, register a fresh handle with the
        //     same token/name, transition directly to Active).
        //   • Running container with our prefix but no recoverable token (e.g. server
        //     restart) → surface a structured EphemeralContainerStale error so the
        //     caller knows why reattachment cannot proceed.
        if (_store.FindReusableTarget($"k8s:{ns}/{request.PodName}/{container.Name}") is null)
        {
            var staleReuse = TryFindStaleReuseCandidate(pod, ns, request.PodName, request.OwnerPrincipalKey);
            if (staleReuse is not null)
            {
                return ReviveStaleHandle(staleReuse, request, ns, container.Name, processSelector, now, ttl);
            }
        }

        var token = GenerateBearerToken();
        var delegationKey = GenerateBearerToken();
        var ephemeralName = BuildEphemeralContainerName();
        var handleId = "inv_" + RandomHex(16);

        var handle = new InvestigationHandle(
            HandleId: handleId,
            Kubernetes: new KubernetesInvestigationTarget(
                Namespace: ns,
                PodName: request.PodName,
                TargetContainerName: container.Name,
                EphemeralContainerName: ephemeralName,
                PodLocalBearerToken: token),
            State: InvestigationState.Attaching,
            AttachedAt: now,
            ExpiresAt: now + ttl,
            OwnerBearerName: request.OwnerBearerName,
            OwnerPrincipalKey: request.OwnerPrincipalKey,
            InternalScopeDelegationKey: delegationKey,
            ProcessSelector: processSelector);

        // Atomic check-and-reserve: when reuse is allowed and a target tuple already has an
        // Active/Attaching handle, return it instead of patching a second ephemeral container.
        // The single lock-protected operation prevents two concurrent attaches for the same
        // target from both creating an ephemeral container.
        if (!_store.TryReserveTarget(handle, request.AllowReuseExistingSession, out var existing))
        {
            return ReuseHandleOrThrow(existing!, request, ns, container.Name, processSelector);
        }

        try
        {
            var spec = BuildEphemeralContainerSpec(ephemeralName, container, token, delegationKey);
            await PatchEphemeralContainerAsync(ns, request.PodName, spec, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await MarkFailedAsync(handle, "Attach canceled by caller before the ephemeral container patch completed.").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(handle, ex.Message).ConfigureAwait(false);
            throw;
        }

        try
        {
            await WaitForEphemeralRunningAsync(ns, request.PodName, ephemeralName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await MarkFailedAsync(handle, "Attach canceled by caller while waiting for ephemeral container readiness.").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(handle, ex.Message).ConfigureAwait(false);
            throw;
        }

        if (_store is not IInvestigationStoreActivation activation
            || !activation.TryTransitionToActive(handle.HandleId, out var active)
            || active is null)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.AttachFailed,
                $"Investigation {handle.HandleId} became inactive while the diagnostics container was starting.");
        }
        _logger.LogInformation(
            "Attached investigation {HandleId} to {Namespace}/{Pod}/{Container} as ephemeral '{EphemeralName}'.",
            active.HandleId, ns, request.PodName, container.Name, ephemeralName);
        return active;
    }

    /// <summary>
    /// Validates ownership and process-selector compatibility on an existing Active/Attaching
    /// handle returned by <see cref="IInvestigationStore.TryReserveTarget"/> and either
    /// returns the handle or throws an <see cref="OrchestratorException"/>.
    /// </summary>
    private InvestigationHandle ReuseHandleOrThrow(
        InvestigationHandle reusable,
        AttachRequest request,
        string ns,
        string containerName,
        InvestigationProcessSelector? processSelector)
    {
        // H6 / B3 review (issue #164): reuse is owner-aware. A reused handle is only
        // returned to the caller when the caller owns it. Otherwise we surface a
        // structured error rather than binding the caller to another session's
        // investigation (which would let them reach the pod via the in-process
        // call-tool forward and bypass the HTTP proxy's ownership check).
        // Un-owned handles (stdio / framework) remain reusable by anyone.
        if (!InvestigationOwnership.IsOwnedBy(reusable, request.OwnerPrincipalKey))
        {
            _logger.LogInformation(
                "Refusing to reuse handle {HandleId} for {Namespace}/{Pod}/{Container}: owned by a different MCP session.",
                reusable.HandleId, ns, request.PodName, containerName);
            throw new OrchestratorException(
                OrchestratorErrorKinds.PermissionDenied,
                $"An investigation for {ns}/{request.PodName}/{containerName} is already active in another MCP session. " +
                "Wait for that session to detach, or have its owner share their session id, before attaching here.");
        }

        if (processSelector is not null)
        {
            if (reusable.ProcessSelector is null)
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.InvalidArgument,
                    $"Investigation {reusable.HandleId} was attached without a process selector; " +
                    $"detach it before attaching the same Pod with selector ({processSelector.Describe()}).");
            }
            else if (!reusable.ProcessSelector.IsEquivalentTo(processSelector))
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.InvalidArgument,
                    $"Investigation {reusable.HandleId} already has process selector " +
                    $"({reusable.ProcessSelector.Describe()}); detach it before attaching the same Pod " +
                    $"with a different selector ({processSelector.Describe()}).");
            }
        }

        _logger.LogInformation(
            "Reusing investigation handle {HandleId} for {Namespace}/{Pod}/{Container} (state={State}).",
            reusable.HandleId, ns, request.PodName, containerName, reusable.State);
        return reusable;
    }

    /// <summary>
    /// Scans the pod's <c>ephemeralContainerStatuses</c> for a Running container with the
    /// orchestrator's name prefix that was left behind by a previous detach. Returns the
    /// matching terminal handle (with its recoverable token) when one is found; throws
    /// <see cref="OrchestratorErrorKinds.EphemeralContainerStale"/> when a running container
    /// is found but the token cannot be recovered; returns null when no stale container exists.
    /// </summary>
    private InvestigationHandle? TryFindStaleReuseCandidate(
        V1Pod pod, string ns, string podName, string? callerOwnerKey)
    {
        var prefix = _options.EphemeralContainerNamePrefix;
        var runningStale = pod.Status?.EphemeralContainerStatuses?
            .Where(s =>
                s.Name?.StartsWith(prefix, StringComparison.Ordinal) == true &&
                s.State?.Running is not null)
            .ToList();

        if (runningStale is null || runningStale.Count == 0)
        {
            return null;
        }

        // Prefer the most recently attached container (highest name suffix sorts last alphabetically,
        // but we rely on the store's AttachedAt ordering via FindTerminalHandleByEphemeralName).
        foreach (var status in runningStale)
        {
            var closed = _store.FindTerminalHandleByEphemeralName(ns, podName, status.Name!);
            if (closed is not null)
            {
                // Ownership check: the stale container embeds the old session's token; only
                // the original owner (or an ownerless handle) may reconnect to it.
                if (!InvestigationOwnership.IsOwnedBy(closed, callerOwnerKey))
                {
                    _logger.LogInformation(
                        "Stale ephemeral container '{EphemeralName}' on {Namespace}/{Pod} belongs to a different MCP session " +
                        "(previous handle {ClosedHandleId}).",
                        status.Name, ns, podName, closed.HandleId);
                    throw new OrchestratorException(
                        OrchestratorErrorKinds.PermissionDenied,
                        $"Pod '{ns}/{podName}' has a stale ephemeral diagnostics container '{status.Name}' " +
                        $"still running from a previous session owned by a different MCP session. " +
                        "Wait for that container to exit, or ask its owner to detach and restart the pod.");
                }

                if (closed.PodLocalBearerToken is null)
                {
                    // Token was scrubbed or handle is malformed — can't reconnect.
                    continue;
                }

                return closed;
            }
        }

        // Running container(s) with our prefix exist but no token is recoverable
        // (server was restarted, handle was evicted, etc.).
        var names = string.Join(", ", runningStale.Select(s => $"'{s.Name}'"));
        _logger.LogWarning(
            "Pod {Namespace}/{Pod} has stale ephemeral container(s) [{Names}] still Running; " +
            "their tokens are not recoverable in the current server process.",
            ns, podName, names);
        throw new OrchestratorException(
            OrchestratorErrorKinds.EphemeralContainerStale,
            $"Pod '{ns}/{podName}' has stale ephemeral diagnostics container(s) [{names}] still running " +
            "from a previous session whose bearer token is no longer recoverable (the server may have restarted). " +
            "Restart the pod to clear stale containers, or wait for them to exit on their own.");
    }

    /// <summary>
    /// Registers a fresh investigation handle that reuses a previously detached ephemeral
    /// container. The old bearer token (embedded in the running container's environment)
    /// is carried forward on the new handle so the proxy can authenticate without patching
    /// a new container — skipping both the K8s patch and the readiness wait.
    /// </summary>
    private InvestigationHandle ReviveStaleHandle(
        InvestigationHandle stale,
        AttachRequest request,
        string ns,
        string containerName,
        InvestigationProcessSelector? processSelector,
        DateTimeOffset now,
        TimeSpan ttl)
    {
        var revived = new InvestigationHandle(
            HandleId: "inv_" + RandomHex(16),
            Namespace: stale.Namespace,
            PodName: stale.PodName,
            TargetContainerName: stale.TargetContainerName,
            // Carry forward the same ephemeral container name and bearer token: the
            // running sidecar was started with these values and will only accept the
            // original token. Generating a fresh token here would cause 401 errors.
            EphemeralContainerName: stale.EphemeralContainerName,
            PodLocalBearerToken: stale.PodLocalBearerToken,
            State: InvestigationState.Attaching,
            AttachedAt: now,
            ExpiresAt: now + ttl,
            OwnerBearerName: request.OwnerBearerName,
            OwnerPrincipalKey: request.OwnerPrincipalKey,
            // Reuse the delegation key the sidecar was started with; a new key would
            // invalidate the sidecar's HMAC verification for delegated tool calls.
            InternalScopeDelegationKey: stale.InternalScopeDelegationKey,
            ProcessSelector: processSelector);

        // TryReserveTarget is atomic: if a concurrent attach won the race and already
        // claimed the slot (Active/Attaching), validate and return their handle instead.
        if (!_store.TryReserveTarget(revived, request.AllowReuseExistingSession, out var existing))
        {
            return ReuseHandleOrThrow(existing!, request, ns, containerName, processSelector);
        }

        // The container is already Running — transition directly to Active; no patch,
        // no readiness wait.
        if (_store is not IInvestigationStoreActivation activation
            || !activation.TryTransitionToActive(revived.HandleId, out var active)
            || active is null)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.AttachFailed,
                $"Investigation {revived.HandleId} became inactive while reviving stale container '{stale.EphemeralContainerName}'.");
        }

        _logger.LogInformation(
            "Revived investigation {HandleId} on {Namespace}/{Pod}/{Container} by reusing stale ephemeral container " +
            "'{EphemeralName}' (previous handle {StaleHandleId} was {StaleState}).",
            active.HandleId, ns, request.PodName, containerName,
            stale.EphemeralContainerName, stale.HandleId, stale.State);
        return active;
    }

    private static InvestigationProcessSelector? NormalizeProcessSelector(
        InvestigationProcessSelector? selector)
    {
        if (selector is null)
        {
            return null;
        }

        var normalized = selector.Normalize();
        if (normalized.IsEmpty)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.InvalidArgument,
                "processSelector must set managedEntrypointAssemblyName, commandLineContains, or both.");
        }

        return normalized;
    }

    private async Task MarkFailedAsync(InvestigationHandle handle, string reason)
    {
        await _closer.CloseAsync(handle.HandleId, InvestigationState.Failed, reason).ConfigureAwait(false);
        _observability.RecordDetach(principal: null, handle.HandleId, "error", "success");
    }

    private string ResolveAndValidateNamespace(string? requested)
        => NamespaceAllowlistPolicy.ResolveAndValidate(
            requested,
            _options,
            allowEmptyWhenWildcard: false,
            "No namespace supplied and no DefaultNamespace configured.")!;

    private async Task<V1Pod> ReadPodOrThrowAsync(string ns, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await _podsApi.ReadPodAsync(ns, name, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PodNotFound,
                $"Pod '{ns}/{name}' was not found.", ex);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PermissionDenied,
                $"Kubernetes API rejected the read pod call with {(int?)ex.Response?.StatusCode}. " +
                "Check the orchestrator ServiceAccount has 'pods' get in the requested namespace.", ex);
        }
        catch (HttpOperationException ex)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.KubeApiUnavailable,
                $"Kubernetes API call failed: {(int?)ex.Response?.StatusCode} {ex.Message}", ex);
        }
    }

    private static V1Container SelectContainerOrThrow(V1Pod pod, string? requested)
    {
        var containers = pod.Spec?.Containers;
        if (containers is null || containers.Count == 0)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.ContainerNotFound,
                $"Pod '{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}' has no containers.");
        }
        if (string.IsNullOrEmpty(requested)) return containers[0];

        var match = containers.FirstOrDefault(c => string.Equals(c.Name, requested, StringComparison.Ordinal));
        if (match is null)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.ContainerNotFound,
                $"Container '{requested}' not found on pod '{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}'. " +
                $"Available: [{string.Join(", ", containers.Select(c => c.Name))}].");
        }
        return match;
    }

    private static void ValidatePodRunning(V1Pod pod)
    {
        var phase = pod.Status?.Phase;
        if (!string.Equals(phase, "Running", StringComparison.Ordinal))
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PodNotRunning,
                $"Pod '{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}' is in phase '{phase ?? "Unknown"}'. " +
                "Only Running pods can be attached.");
        }
    }

    private void ValidatePodPrepared(V1Pod pod, bool callerRequiresPrepared)
    {
        if (!callerRequiresPrepared && !_options.RequirePreparedLabel) return;

        var labels = pod.Metadata?.Labels;
        var hasLabel = labels is not null &&
            labels.TryGetValue(_options.PreparedLabelKey, out var v) &&
            string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        if (hasLabel) return;

        throw new OrchestratorException(
            OrchestratorErrorKinds.PodNotPrepared,
            $"Pod '{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}' is missing opt-in label " +
            $"'{_options.PreparedLabelKey}=true'. Add the label (and a shared /tmp emptyDir + matching UID) or " +
            "set requirePreparedTarget=false (and Orchestrator:RequirePreparedLabel=false) to override.");
    }

    private string BuildEphemeralContainerName()
    {
        // Pod ephemeralContainers[*].name must be unique within the pod. Suffix a short
        // random tag so reattaching after a previous (non-removable) ephemeral container
        // doesn't collide.
        return _options.EphemeralContainerNamePrefix + RandomHex(4);
    }

    private V1EphemeralContainer BuildEphemeralContainerSpec(
        string ephemeralName,
        V1Container target,
        string token,
        string delegationKey)
    {
        // Inherit the target container's volumeMounts so any prepared shared /tmp
        // emptyDir (or equivalent) shows up under the same path in the ephemeral
        // container. Without this the diagnostic IPC socket the runtime creates at
        // /tmp/dotnet-diagnostic-<pid>-... would live in the target's mount namespace
        // only — sharing the PID namespace (TargetContainerName) is not sufficient by
        // itself. Mirrors the manual recipe in deploy/k8s/ephemeral-attach.patch.json.
        var volumeMounts = target.VolumeMounts is { Count: > 0 }
            ? new List<V1VolumeMount>(target.VolumeMounts.Select(CloneVolumeMount))
            : null;

        // Match the target container's UID/GID so the inherited socket file is
        // readable. The CoreCLR runtime creates the socket with the process's
        // effective uid; an ephemeral container running as a different uid sees
        // EACCES on connect even when the mount is shared.
        var securityContext = BuildEphemeralSecurityContext(target.SecurityContext);

        return new V1EphemeralContainer
        {
            Name = ephemeralName,
            Image = _options.EphemeralContainerImage,
            ImagePullPolicy = "IfNotPresent",
            // Required: join the target container's PID namespace so the diagnostic IPC
            // socket at /tmp/dotnet-diagnostic-<pid> is visible.
            TargetContainerName = target.Name,
            Env = BuildEphemeralEnvironment(token, delegationKey),
            // The shipped image's appsettings.json pins "Urls" to 127.0.0.1:8787, which
            // outranks ASPNETCORE_URLS in WebApplication.CreateBuilder's configuration
            // precedence. Pass --urls explicitly so the kestrel binding follows the
            // command-line override (highest precedence) and matches ProxyPodPort.
            Args = new List<string> { "--urls", $"http://0.0.0.0:{_options.ProxyPodPort}" },
            VolumeMounts = volumeMounts,
            SecurityContext = securityContext,
            TerminationMessagePolicy = "File",
        };
    }

    private List<V1EnvVar> BuildEphemeralEnvironment(string token, string delegationKey)
    {
        var environment = new List<V1EnvVar>
        {
            new() { Name = "MCP_BEARER_TOKEN", Value = token },
            new()
            {
                Name = DotnetDiagnostics.Mcp.Security.ToolScopeDelegation.EnvironmentVariableName,
                Value = delegationKey,
            },
            new() { Name = "ASPNETCORE_URLS", Value = $"http://0.0.0.0:{_options.ProxyPodPort}" },
            new()
            {
                Name = "Diagnostics__AllowSensitiveHeapValues",
                Value = _securityOptions.AllowSensitiveHeapValues.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            new()
            {
                Name = "Diagnostics__AllowMethodParameterCapture",
                Value = _securityOptions.AllowMethodParameterCapture.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
        AddArrayEnvironment(environment, "Diagnostics__SymbolServerAllowlist", _securityOptions.SymbolServerAllowlist);
        AddArrayEnvironment(environment, "Diagnostics__EventSourceAllowlist", _securityOptions.EventSourceAllowlist);
        AddArrayEnvironment(environment, "Diagnostics__RedactionPatterns", _securityOptions.RedactionPatterns);
        return environment;
    }

    private static void AddArrayEnvironment(
        List<V1EnvVar> environment,
        string prefix,
        List<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            environment.Add(new V1EnvVar
            {
                Name = $"{prefix}__{i}",
                Value = values[i],
            });
        }
    }

    private static V1VolumeMount CloneVolumeMount(V1VolumeMount mount)
        => new()
        {
            Name = mount.Name,
            MountPath = mount.MountPath,
            ReadOnlyProperty = mount.ReadOnlyProperty,
            SubPath = mount.SubPath,
            SubPathExpr = mount.SubPathExpr,
            MountPropagation = mount.MountPropagation,
            RecursiveReadOnly = mount.RecursiveReadOnly,
        };

    private static V1SecurityContext? BuildEphemeralSecurityContext(V1SecurityContext? source)
    {
        if (source is null)
        {
            return null;
        }

        // Inherit identity (UID/GID/non-root) and the target's non-elevating
        // restrictions so the ephemeral container survives Pod Security
        // "restricted" admission in the same namespace.
        //
        // Deliberately drop:
        //   * Privileged=true and AllowPrivilegeEscalation=true (never widen).
        //   * Capabilities.Add (workload-specific elevations).
        // We keep Capabilities.Drop so the ephemeral container is not *more*
        // permissive than the target.
        V1Capabilities? capabilities = null;
        if (source.Capabilities?.Drop is { Count: > 0 } drop)
        {
            capabilities = new V1Capabilities { Drop = new List<string>(drop) };
        }

        return new V1SecurityContext
        {
            RunAsUser = source.RunAsUser,
            RunAsGroup = source.RunAsGroup,
            RunAsNonRoot = source.RunAsNonRoot,
            AllowPrivilegeEscalation = source.AllowPrivilegeEscalation is false ? false : null,
            Capabilities = capabilities,
            SeccompProfile = source.SeccompProfile,
            SeLinuxOptions = source.SeLinuxOptions,
            WindowsOptions = source.WindowsOptions,
            ReadOnlyRootFilesystem = source.ReadOnlyRootFilesystem,
        };
    }

    private async Task PatchEphemeralContainerAsync(string ns, string name, V1EphemeralContainer ephemeral, CancellationToken cancellationToken)
    {
        try
        {
            await _podsApi.AddEphemeralContainerAsync(ns, name, ephemeral, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PermissionDenied,
                $"Kubernetes API rejected the ephemeralcontainers patch with {(int?)ex.Response?.StatusCode}. " +
                "Check the orchestrator ServiceAccount has 'pods/ephemeralcontainers' patch in the namespace.", ex);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.AttachAlreadyInProgress,
                "Kubernetes reported a conflict applying the ephemeralcontainers patch. " +
                "Another attach may be in flight for this pod.", ex);
        }
        catch (HttpOperationException ex)
        {
            // The patch was not accepted by the API server, so AttachFailed (which the design
            // reserves for an accepted-but-unhealthy ephemeral container) is the wrong kind.
            // Surface transient API failures as KubeApiUnavailable so the caller knows a retry
            // is appropriate without operator intervention.
            throw new OrchestratorException(
                OrchestratorErrorKinds.KubeApiUnavailable,
                $"Failed to patch ephemeralcontainers: {(int?)ex.Response?.StatusCode} {ex.Message}", ex);
        }
    }

    private async Task WaitForEphemeralRunningAsync(
        string ns,
        string name,
        string ephemeralName,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(_options.AttachReadinessTimeoutSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            V1Pod pod;
            try
            {
                pod = await _podsApi.ReadPodAsync(ns, name, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpOperationException ex)
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.AttachFailed,
                    $"Failed to poll ephemeral container readiness: {(int?)ex.Response?.StatusCode} {ex.Message}", ex);
            }

            var status = pod.Status?.EphemeralContainerStatuses?
                .FirstOrDefault(s => string.Equals(s.Name, ephemeralName, StringComparison.Ordinal));
            if (status is not null)
            {
                if (status.State?.Running is not null) return;
                if (status.State?.Terminated is not null)
                {
                    throw new OrchestratorException(
                        OrchestratorErrorKinds.AttachFailed,
                        $"Ephemeral container '{ephemeralName}' terminated before becoming ready " +
                        $"(reason={status.State.Terminated.Reason ?? "?"}, exitCode={status.State.Terminated.ExitCode}).");
                }
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.AttachTimeout,
                    $"Ephemeral container '{ephemeralName}' did not become Running within " +
                    $"{_options.AttachReadinessTimeoutSeconds}s on pod '{ns}/{name}'.");
            }

            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GenerateBearerToken() => RandomHex(32);

    private static string RandomHex(int byteCount)
    {
        Span<byte> buf = stackalloc byte[64];
        if (byteCount > buf.Length) buf = new byte[byteCount];
        else buf = buf[..byteCount];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }
}
