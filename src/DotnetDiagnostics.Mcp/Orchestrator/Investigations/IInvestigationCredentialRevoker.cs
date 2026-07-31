using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

public interface IInvestigationCredentialRevoker
{
    Task RevokeAsync(InvestigationHandle handle, CancellationToken cancellationToken);
}

internal sealed class NoOpInvestigationCredentialRevoker : IInvestigationCredentialRevoker
{
    public static readonly NoOpInvestigationCredentialRevoker Instance = new();

    private NoOpInvestigationCredentialRevoker()
    {
    }

    public Task RevokeAsync(InvestigationHandle handle, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
