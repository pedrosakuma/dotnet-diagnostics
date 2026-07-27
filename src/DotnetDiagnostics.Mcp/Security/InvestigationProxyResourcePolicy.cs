namespace DotnetDiagnostics.Mcp.Security;

internal static class InvestigationProxyResourcePolicy
{
    internal const string InvestigationGuideUri = "diag://guides/investigation";

    public static bool CanTraverseProxy(string? resourceUri)
        => string.Equals(resourceUri, InvestigationGuideUri, StringComparison.Ordinal);
}
