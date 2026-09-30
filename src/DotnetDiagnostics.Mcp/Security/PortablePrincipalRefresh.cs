namespace DotnetDiagnostics.Mcp.Security;

/// <summary>A host-authenticated refresh, never a principal or scope supplied by tool arguments.</summary>
internal static class PortablePrincipalRefresh
{
    internal const string ItemKey = "PortablePrincipalRefresh";

    internal static Func<CancellationToken, ValueTask<BearerPrincipal?>>? Get(HttpContext? context)
        => context?.Items[ItemKey] as Func<CancellationToken, ValueTask<BearerPrincipal?>>;
}
