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

    private readonly Func<V1Secret, string, CancellationToken, Task> _createSecretAsync;
    private readonly Func<string, string, CancellationToken, Task> _deleteSecretAsync;

    public KubernetesAttachmentSecretManager(IKubernetesClientFactory clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        _createSecretAsync = async (secret, namespaceName, cancellationToken) =>
        {
            await clientFactory.GetClient().CoreV1.CreateNamespacedSecretAsync(
                body: secret,
                namespaceParameter: namespaceName,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        };
        _deleteSecretAsync = async (name, namespaceName, cancellationToken) =>
        {
            await clientFactory.GetClient().CoreV1.DeleteNamespacedSecretAsync(
                name: name,
                namespaceParameter: namespaceName,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        };
    }

    internal KubernetesAttachmentSecretManager(
        Func<string, string, CancellationToken, Task> deleteSecretAsync)
    {
        _createSecretAsync = static (_, _, _) => throw new NotSupportedException();
        _deleteSecretAsync = deleteSecretAsync
            ?? throw new ArgumentNullException(nameof(deleteSecretAsync));
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

        await _createSecretAsync(secret, target.Namespace, cancellationToken)
            .ConfigureAwait(false);
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
            await _deleteSecretAsync(
                target.CredentialSecretName,
                target.Namespace,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // Idempotent cleanup after the post-start deletion or a racing closer.
        }
    }
}
