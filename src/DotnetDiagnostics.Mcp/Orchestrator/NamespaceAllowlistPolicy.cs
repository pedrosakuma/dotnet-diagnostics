namespace DotnetDiagnostics.Mcp.Orchestrator;

internal static class NamespaceAllowlistPolicy
{
    internal static string? ResolveAndValidate(
        string? requestedNamespace,
        OrchestratorOptions options,
        bool allowEmptyWhenWildcard,
        string missingNamespaceMessage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(missingNamespaceMessage);

        var ns = string.IsNullOrWhiteSpace(requestedNamespace)
            ? options.DefaultNamespace
            : requestedNamespace;

        var allowlist = options.NamespaceAllowlist;
        var wildcard = allowlist.Count == 1 && allowlist[0] == "*";

        if (string.IsNullOrWhiteSpace(ns))
        {
            if (allowEmptyWhenWildcard && wildcard)
            {
                return null;
            }

            throw new OrchestratorException(
                OrchestratorErrorKinds.NamespaceNotAllowed,
                missingNamespaceMessage);
        }

        if (wildcard || allowlist.Contains(ns, StringComparer.Ordinal))
        {
            return ns;
        }

        throw new OrchestratorException(
            OrchestratorErrorKinds.NamespaceNotAllowed,
            $"Namespace '{ns}' is not in the orchestrator's NamespaceAllowlist. " +
            $"Allowed: [{string.Join(", ", allowlist)}].");
    }
}
