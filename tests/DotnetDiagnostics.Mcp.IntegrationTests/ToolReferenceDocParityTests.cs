using System.Reflection;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Guardrail: keeps <c>docs/tool-reference.md</c> in step with the shipping MCP tool
/// surface. Every <c>[McpServerTool]</c> across the tool-surface types must have a
/// dedicated <c>## `tool_name`</c> section in the reference, so registering a new tool
/// (or renaming one) without documenting it fails the build. The reference is the live
/// reflected surface — not a hand-maintained list — so it cannot silently drift, which is
/// exactly how <c>query_snapshot</c> / <c>inspect_heap</c> ended up under-documented before.
/// Mirrors <c>CliDocParityTests</c> / <c>BenchDocParityTests</c> for the other deliverables.
/// </summary>
public sealed class ToolReferenceDocParityTests
{
    // The shipping tool surface, mirrored from ToolScopeAttributesTests
    // (ToolScopeRegistry_Production_Surface_Has_Full_Coverage). Keep in sync: any type
    // that carries [McpServerTool] methods and is registered in DiagnosticServiceRegistration
    // belongs here.
    private static readonly Type[] ToolSurfaceTypes =
    {
        typeof(DiagnosticTools),
        typeof(OrchestratorTools),
        typeof(ListOrchestratorTool),
        typeof(InspectProcessTool),
        typeof(CollectEventsTool),
        typeof(CollectSampleTool),
        typeof(QuerySnapshotTool),
        typeof(InspectHeapTool),
        typeof(GetBytesTool),
        typeof(DiscoverAzureTool),
    };

    public static TheoryData<string> ToolNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in EnumerateToolNames())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void ToolReference_DocumentsEveryTool(string toolName)
    {
        var doc = ReadToolReference();

        // Line-anchored H2 match: a bare substring check would also be satisfied by a
        // "### `tool`" subsection (which contains "## `tool`" from its 2nd '#'), so require a
        // line that is exactly the dedicated "## `tool`" heading.
        var heading = $"## `{toolName}`";
        var hasDedicatedSection = doc
            .Split('\n')
            .Any(line => line.TrimEnd('\r', ' ', '\t') == heading);

        hasDedicatedSection.Should().BeTrue(
            $"docs/tool-reference.md must have a dedicated '{heading}' H2 section for the MCP tool.");
    }

    [Fact]
    public void ToolSurface_ExposesTheExpectedToolCount()
    {
        // Anchors the reflected surface so an accidental drop (or a stray extra tool) is caught
        // alongside the doc-parity check rather than silently changing what the theory enumerates.
        EnumerateToolNames().Should().HaveCount(16);
    }

    [Fact]
    public void RootReadme_ToolOverviewListsEveryReflectedTool()
    {
        var readme = ReadRepoFile("README.md");
        var start = readme.IndexOf("## Tools Overview", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = readme.IndexOf("## Documentation", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        var overview = readme[start..end];

        foreach (var toolName in EnumerateToolNames())
        {
            overview.Should().Contain(
                $"`{toolName}`",
                $"the root README tool overview must list the reflected '{toolName}' MCP tool");
        }
    }

    [Fact]
    public void PublicSummaries_ReportTheReflectedFullToolCount()
    {
        var count = EnumerateToolNames().Count;

        ReadRepoFile("README.md").Should().Contain($"**Status:** {count} unified tools");
        ReadRepoFile("AGENTS.md").Should().Contain($"full **{count}-tool** MCP surface");
    }

    [Fact]
    public void PackageReadmesAndMetadata_StateTheRepositoryMitLicense()
    {
        ReadRepoFile("LICENSE").Should().StartWith("MIT License");

        var packageReadmes = new[]
        {
            "README.md",
            Path.Combine("src", "DotnetDiagnostics.Core", "README.md"),
            Path.Combine("src", "DotnetDiagnostics.Cli", "README.md"),
            Path.Combine("src", "DotnetDiagnostics.BenchmarkDotNet", "README.md"),
        };

        foreach (var path in packageReadmes)
        {
            var readme = ReadRepoFile(path);
            readme.Should().Contain("## License");
            readme.Should().Contain("MIT");
        }

        var packageProjects = new[]
        {
            Path.Combine("src", "DotnetDiagnostics.Mcp", "DotnetDiagnostics.Mcp.csproj"),
            Path.Combine("src", "DotnetDiagnostics.Core", "DotnetDiagnostics.Core.csproj"),
            Path.Combine("src", "DotnetDiagnostics.Cli", "DotnetDiagnostics.Cli.csproj"),
            Path.Combine("src", "DotnetDiagnostics.BenchmarkDotNet", "DotnetDiagnostics.BenchmarkDotNet.csproj"),
        };

        foreach (var path in packageProjects)
        {
            ReadRepoFile(path).Should().Contain("<PackageLicenseExpression>MIT</PackageLicenseExpression>");
        }
    }

    [Fact]
    public void ToolReference_DocumentsSweepFieldsUnderTheSweepProjection()
    {
        var doc = ReadToolReference();

        doc.Should().Contain("`data.sweep.triage`");
        doc.Should().Contain("`data.sweep.handles`");
        doc.Should().Contain("`data.sweep.failures`");
        doc.Should().NotContain("`data.triage`");
        doc.Should().NotContain("`data.handles`");
        doc.Should().NotContain("`data.failures`");
    }

    private static IReadOnlyList<string> EnumerateToolNames()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in ToolSurfaceTypes)
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr?.Name is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }

        return names.ToList();
    }

    private static string ReadToolReference()
        => ReadRepoFile(Path.Combine("docs", "tool-reference.md"));

    private static string ReadRepoFile(string relativePath)
    {
        // [CallerFilePath] collapses to "/_/…" in deterministic CI builds, so walk up from the test
        // assembly to the repo root (marked by DotnetDiagnostics.slnx).
        var dir = Path.GetDirectoryName(typeof(ToolReferenceDocParityTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "DotnetDiagnostics.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            throw new FileNotFoundException(
                "Could not locate repo root (DotnetDiagnostics.slnx) by walking up from " +
                typeof(ToolReferenceDocParityTests).Assembly.Location);
        }

        return File.ReadAllText(Path.Combine(dir, relativePath));
    }
}
