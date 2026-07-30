using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Mcp.Hosting;

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
                "The transport will be closed, but detach reports this cleanup failure instead of silently assuming revocation.");
        }

        await WaitForContainerExitAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForContainerExitAsync(
        InvestigationHandle handle,
        CancellationToken cancellationToken)
    {
        var target = handle.Kubernetes!;
        var deadline = _timeProvider.GetUtcNow().AddSeconds(10);
        while (_timeProvider.GetUtcNow() < deadline)
        {
            var pod = await _podsApi
                .ReadPodAsync(target.Namespace, target.PodName, cancellationToken)
                .ConfigureAwait(false);
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
