using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Mcp.Hosting;
using k8s.Autorest;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

internal sealed class KubernetesInvestigationCredentialRevoker : IInvestigationCredentialRevoker
{
    private readonly IInvestigationTransportManager _transportManager;
    private readonly IKubernetesPodsApi _podsApi;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _revocations = new(StringComparer.Ordinal);

    public KubernetesInvestigationCredentialRevoker(
        IInvestigationTransportManager transportManager,
        IKubernetesPodsApi podsApi,
        TimeProvider timeProvider)
    {
        _transportManager = transportManager;
        _podsApi = podsApi;
        _timeProvider = timeProvider;
    }

    public async Task RevokeAsync(InvestigationHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.Kubernetes is null)
        {
            return;
        }

        var candidate = new Lazy<Task>(
            () => RevokeWithTimeoutAsync(handle),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var selected = _revocations.GetOrAdd(handle.HandleId, candidate);
        var revocation = selected.Value;
        try
        {
            await revocation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (revocation.IsCompleted)
            {
                _revocations.TryRemove(
                    new KeyValuePair<string, Lazy<Task>>(handle.HandleId, selected));
            }
            throw;
        }
    }

    private async Task RevokeWithTimeoutAsync(InvestigationHandle handle)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await RevokeCoreAsync(handle, timeout.Token).ConfigureAwait(false);
    }

    private async Task RevokeCoreAsync(
        InvestigationHandle handle,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await _transportManager
                .GetOrCreateClientAsync(handle, cancellationToken)
                .ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, EphemeralAttachmentLifetime.RevokePath);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.PortForwardFailed,
                    $"Pod-local credential revocation for investigation {handle.HandleId} returned HTTP {(int)response.StatusCode}. " +
                    "Detach reports this cleanup failure instead of silently assuming revocation.");
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A prior attempt may have received 204 and stopped the process before
            // Kubernetes published Terminated. In that state there is no endpoint to
            // retry, but an observed terminated container is authoritative success.
            if (await IsContainerTerminatedAsync(handle, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            throw;
        }

        await WaitForContainerExitAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsContainerTerminatedAsync(
        InvestigationHandle handle,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = handle.Kubernetes!;
            var pod = await _podsApi
                .ReadPodAsync(target.Namespace, target.PodName, cancellationToken)
                .ConfigureAwait(false);
            return pod.Status?.EphemeralContainerStatuses?
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, target.EphemeralContainerName, StringComparison.Ordinal))
                ?.State?.Terminated is not null;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // The Kubernetes API is authoritative here: if the target Pod no
            // longer exists, neither its process nor attachment credentials can
            // remain usable. This is distinct from a 404 returned by the
            // pod-local revoke endpoint, which remains a protocol failure.
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task WaitForContainerExitAsync(
        InvestigationHandle handle,
        CancellationToken cancellationToken)
    {
        var target = handle.Kubernetes!;
        var deadline = _timeProvider.GetUtcNow().AddSeconds(10);
        while (_timeProvider.GetUtcNow() < deadline)
        {
            k8s.Models.V1Pod pod;
            try
            {
                pod = await _podsApi
                    .ReadPodAsync(target.Namespace, target.PodName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }
            var status = pod.Status?.EphemeralContainerStatuses?
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, target.EphemeralContainerName, StringComparison.Ordinal));
            if (status?.State?.Terminated is not null)
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(250),
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }

        throw new OrchestratorException(
            OrchestratorErrorKinds.PortForwardFailed,
            $"Pod-local credentials for investigation {handle.HandleId} were revoked, but ephemeral container " +
            $"'{target.EphemeralContainerName}' did not terminate within 10 seconds. Detach reports this cleanup failure.");
    }
}
