using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticIntegrationGroup
{
    public const string Name = "DiagnosticIntegration";
}
