using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Guards the load-bearing acceptance bar for the #283 layering seam: the standalone CLI must
/// reference <b>Core only</b>, never the MCP server assembly. A regression here (an accidental
/// <c>using DotnetDiagnostics.Mcp…</c> that drags in a Server type) would silently re-couple
/// the front-end to the transport layer.
/// </summary>
public sealed class NoServerReferenceTests
{
    [Fact]
    public void CliAssembly_DoesNotReferenceServerAssembly()
    {
        var referenced = typeof(CliHost).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referenced.Should().NotContain("DotnetDiagnostics.Mcp");
    }

    [Fact]
    public void CliAssembly_ReferencesCoreAssembly()
    {
        var referenced = typeof(CliHost).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referenced.Should().Contain("DotnetDiagnostics.Core");
    }
}
