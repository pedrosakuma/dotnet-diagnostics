using DotnetDiagnostics.Mcp.Security;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>Central owner comparison for investigation handles.</summary>
internal static class InvestigationOwnership
{
    internal static bool IsOwnedBy(
        InvestigationHandle handle,
        BearerPrincipal? principal)
        => IsOwnedBy(handle, principal?.OwnershipKey);

    internal static bool IsOwnedBy(
        InvestigationHandle handle,
        string? principalOwnershipKey)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.OwnerPrincipalKey is not null)
        {
            return string.Equals(
                handle.OwnerPrincipalKey,
                principalOwnershipKey,
                StringComparison.Ordinal);
        }

        // A display owner without a stable key is a legacy handle. Fail closed:
        // display-name equality is not an authentication boundary.
        if (handle.OwnerBearerName is not null)
        {
            return false;
        }

        // Truly ownerless handles preserve stdio/framework compatibility.
        return true;
    }
}
