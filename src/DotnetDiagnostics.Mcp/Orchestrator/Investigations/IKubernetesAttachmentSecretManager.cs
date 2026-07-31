using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

public interface IKubernetesAttachmentSecretManager
{
    Task CreateAsync(
        InvestigationHandle handle,
        string bearerToken,
        string delegationKey,
        CancellationToken cancellationToken);

    Task DeleteAsync(InvestigationHandle handle, CancellationToken cancellationToken);
}

internal sealed class NoOpKubernetesAttachmentSecretManager : IKubernetesAttachmentSecretManager
{
    public static readonly NoOpKubernetesAttachmentSecretManager Instance = new();

    private NoOpKubernetesAttachmentSecretManager()
    {
    }

    public Task CreateAsync(
        InvestigationHandle handle,
        string bearerToken,
        string delegationKey,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task DeleteAsync(InvestigationHandle handle, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
