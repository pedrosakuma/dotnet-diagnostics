using DotnetDiagnostics.Core.ProcessDiscovery;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Transport-neutral identity used to select one .NET process inside an attached Pod.
/// The selector is resolved against <c>inspect_process(view="list")</c> inside each Pod;
/// OS process ids are deliberately not persisted because they are Pod-local and ephemeral.
/// </summary>
public sealed record InvestigationProcessSelector(
    string? ManagedEntrypointAssemblyName = null,
    string? CommandLineContains = null)
{
    internal InvestigationProcessSelector Normalize()
        => new(
            NormalizeValue(ManagedEntrypointAssemblyName),
            NormalizeValue(CommandLineContains));

    internal bool IsEmpty
        => ManagedEntrypointAssemblyName is null && CommandLineContains is null;

    internal bool Matches(DotnetProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        return (ManagedEntrypointAssemblyName is null ||
                string.Equals(
                    process.ManagedEntrypointAssemblyName,
                    ManagedEntrypointAssemblyName,
                    StringComparison.OrdinalIgnoreCase)) &&
               (CommandLineContains is null ||
                process.CommandLine.Contains(CommandLineContains, StringComparison.OrdinalIgnoreCase));
    }

    internal bool IsEquivalentTo(InvestigationProcessSelector other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(
                   ManagedEntrypointAssemblyName,
                   other.ManagedEntrypointAssemblyName,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   CommandLineContains,
                   other.CommandLineContains,
                   StringComparison.OrdinalIgnoreCase);
    }

    internal string Describe()
    {
        var parts = new List<string>(2);
        if (ManagedEntrypointAssemblyName is not null)
        {
            parts.Add($"managedEntrypointAssemblyName='{ManagedEntrypointAssemblyName}'");
        }

        if (CommandLineContains is not null)
        {
            parts.Add($"commandLineContains='{CommandLineContains}'");
        }

        return string.Join(", ", parts);
    }

    private static string? NormalizeValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
