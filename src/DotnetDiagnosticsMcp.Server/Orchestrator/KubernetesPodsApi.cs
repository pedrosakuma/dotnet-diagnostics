using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Models;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Production implementation of <see cref="IKubernetesPodsApi"/>: a thin pass-through
/// to the official <see cref="IKubernetes"/> client.
/// </summary>
internal sealed class KubernetesPodsApi : IKubernetesPodsApi
{
    private readonly IKubernetesClientFactory _factory;

    public KubernetesPodsApi(IKubernetesClientFactory factory)
    {
        _factory = factory;
    }

    public Task<V1PodList> ListPodsAsync(
        string? namespaceName,
        string? labelSelector,
        string? fieldSelector,
        int? limit,
        string? continueToken,
        CancellationToken cancellationToken)
    {
        var client = _factory.GetClient();
        if (string.IsNullOrEmpty(namespaceName))
        {
            return client.CoreV1.ListPodForAllNamespacesAsync(
                labelSelector: labelSelector,
                fieldSelector: fieldSelector,
                limit: limit,
                continueParameter: continueToken,
                cancellationToken: cancellationToken);
        }

        return client.CoreV1.ListNamespacedPodAsync(
            namespaceParameter: namespaceName,
            labelSelector: labelSelector,
            fieldSelector: fieldSelector,
            limit: limit,
            continueParameter: continueToken,
            cancellationToken: cancellationToken);
    }

    public Task<V1Pod> ReadPodAsync(string namespaceName, string name, CancellationToken cancellationToken)
    {
        var client = _factory.GetClient();
        return client.CoreV1.ReadNamespacedPodAsync(name: name, namespaceParameter: namespaceName, cancellationToken: cancellationToken);
    }

    public async Task<V1Pod> AddEphemeralContainerAsync(
        string namespaceName,
        string name,
        V1EphemeralContainer ephemeralContainer,
        CancellationToken cancellationToken)
    {
        var client = _factory.GetClient();
        // The pods/ephemeralcontainers subresource is patched, not POSTed. Strategic-merge
        // patch with the canonical { "spec": { "ephemeralContainers": [ ... ] } } shape
        // appends the container without disturbing existing fields.
        var patch = new V1Pod
        {
            Spec = new V1PodSpec
            {
                EphemeralContainers = new List<V1EphemeralContainer> { ephemeralContainer },
            },
        };
        var patchJson = KubernetesJson.Serialize(patch);
        var body = new V1Patch(patchJson, V1Patch.PatchType.StrategicMergePatch);

        return await client.CoreV1.PatchNamespacedPodEphemeralcontainersAsync(
            body: body,
            name: name,
            namespaceParameter: namespaceName,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
