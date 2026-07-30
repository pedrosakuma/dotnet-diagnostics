using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

internal sealed class KubernetesAttachmentSecretManager : IKubernetesAttachmentSecretManager
{
    internal const string BearerTokenKey = "bearer-token";
    internal const string DelegationKeyKey = "scope-delegation-key";

    private readonly IKubernetesClientFactory _clientFactory;

    public KubernetesAttachmentSecretManager(IKubernetesClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task CreateAsync(
        InvestigationHandle handle,
        string bearerToken,
        string delegationKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(delegationKey);
        var target = handle.Kubernetes
            ?? throw new ArgumentException("A Kubernetes target is required.", nameof(handle));
        if (string.IsNullOrWhiteSpace(target.CredentialSecretName))
        {
            throw new ArgumentException("The Kubernetes target has no credential Secret name.", nameof(handle));
        }

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = target.CredentialSecretName,
                NamespaceProperty = target.Namespace,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["app.kubernetes.io/managed-by"] = "dotnet-diagnostics-orchestrator",
                    ["diagnostics.dotnet.io/attachment"] = handle.HandleId,
                },
            },
            Immutable = true,
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [BearerTokenKey] = Encoding.UTF8.GetBytes(bearerToken),
                [DelegationKeyKey] = Encoding.UTF8.GetBytes(delegationKey),
            },
        };
        if (!string.IsNullOrWhiteSpace(target.PodUid))
        {
            secret.Metadata.OwnerReferences =
            [
                new V1OwnerReference
                {
                    ApiVersion = "v1",
                    Kind = "Pod",
                    Name = target.PodName,
                    Uid = target.PodUid,
                },
            ];
        }

        await _clientFactory.GetClient().CoreV1.CreateNamespacedSecretAsync(
            body: secret,
            namespaceParameter: target.Namespace,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(InvestigationHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var target = handle.Kubernetes;
        if (target is null || string.IsNullOrWhiteSpace(target.CredentialSecretName))
        {
            return;
        }

        try
        {
            await _clientFactory.GetClient().CoreV1.DeleteNamespacedSecretAsync(
                name: target.CredentialSecretName,
                namespaceParameter: target.Namespace,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // Idempotent cleanup after the post-start deletion or a racing closer.
        }
    }
}
