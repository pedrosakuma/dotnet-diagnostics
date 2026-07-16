using System.Reflection;
using DotnetDiagnostics.Mcp.Hosting;
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

    [Fact]
    public void RemovedToolAliases_AreConfinedToExplicitMigrationHistory()
    {
        var aliases = new[]
        {
            "get_module_bytes", "get_dump_bytes", "get_trace_bytes",
            "inspect_dump", "inspect_live_heap",
            "query_heap_snapshot", "query_thread_snapshot", "query_off_cpu_snapshot", "query_collection", "get_call_tree",
            "snapshot_counters", "collect_exceptions", "collect_gc_events", "collect_event_source", "collect_activities",
            "list_dotnet_processes", "get_process_info", "get_diagnostic_capabilities", "get_container_signals", "get_memory_trend",
            "collect_cpu_sample", "collect_off_cpu_sample", "collect_allocation_sample",
            "list_pods", "list_active_investigations",
            "get_collection_status", "cancel_collection",
        };
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["src/DotnetDiagnostics.Cli/CliHintProjection.cs|list_dotnet_processes"] = 2,
            ["src/DotnetDiagnostics.Cli/CliHintProjection.cs|collect_off_cpu_sample"] = 2,
            ["src/DotnetDiagnostics.Cli/CliHintProjection.cs|inspect_live_heap"] = 3,
            ["src/DotnetDiagnostics.Cli/CliHintProjection.cs|inspect_dump"] = 4,
            ["src/DotnetDiagnostics.Cli/CliHintProjection.cs|query_heap_snapshot"] = 2,
            ["src/DotnetDiagnostics.Cli/CliHintProjection.cs|collect_cpu_sample"] = 1,
            ["src/DotnetDiagnostics.Cli/CliHintProjection.cs|query_thread_snapshot"] = 1,
            ["src/DotnetDiagnostics.Mcp/Tools/CollectSampleTool.cs|collect_cpu_sample"] = 1,
            ["src/DotnetDiagnostics.Mcp/Tools/CollectSampleTool.cs|collect_off_cpu_sample"] = 1,
            ["src/DotnetDiagnostics.Mcp/Tools/CollectSampleTool.cs|collect_allocation_sample"] = 1,
            ["src/DotnetDiagnostics.Mcp/Tools/GetBytesTool.cs|get_module_bytes"] = 1,
            ["src/DotnetDiagnostics.Mcp/Tools/GetBytesTool.cs|get_dump_bytes"] = 1,
            ["docs/client-setup.md|get_collection_status"] = 1,
            ["docs/client-setup.md|cancel_collection"] = 1,
            ["docs/tool-reference.md|get_collection_status"] = 1,
            ["docs/tool-reference.md|cancel_collection"] = 1,
        };

        var root = FindRepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "deploy"), "*.md", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "deploy"), "*.yml", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "deploy"), "*.yaml", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "README.md"))
            .Append(Path.Combine(root, "AGENTS.md"));
        var actual = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in files)
        {
            var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            var text = File.ReadAllText(path);
            foreach (var alias in aliases)
            {
                var count = CountOccurrences(text, alias);
                if (count > 0)
                {
                    actual[$"{relativePath}|{alias}"] = count;
                }
            }
        }

        actual.Should().BeEquivalentTo(
            expected,
            "removed names may survive only in the enumerated migration-history or backward-compatibility locations");
    }

    [Fact]
    public void SuggestedArgumentBags_DoNotContainAngleBracketPlaceholders()
    {
        var root = FindRepoRoot();
        var placeholderAssignments = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => System.Text.RegularExpressions.Regex.Matches(
                    File.ReadAllText(path),
                    "new Dictionary<string, object\\?>\\s*\\{[^}]*\"<[^\"\\r\\n]+>\"")
                .Select(match => $"{Path.GetRelativePath(root, path).Replace('\\', '/')}:{match.Value}"))
            .ToArray();

        placeholderAssignments.Should().BeEmpty(
            "suggested arguments are replayed as typed tool inputs and must contain concrete schema-valid values");
    }

    [Fact]
    public void LiteralCollectorHintConstructions_DeclareExplicitKind()
    {
        // Supplemental lexical guard only; dynamic planner/recovery factories are exercised
        // behaviorally in NextActionHintReplayabilityTests and QuerySnapshotHandleSecurityTests.
        var root = FindRepoRoot();
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(path);
            foreach (var (offset, call) in EnumerateCollectorHintConstructions(source))
            {
                var arguments = SplitTopLevelArguments(call);
                if (arguments.Count < 3 || string.Equals(arguments[2].Trim(), "null", StringComparison.Ordinal))
                {
                    continue;
                }

                var kindMatch = System.Text.RegularExpressions.Regex.Match(
                    arguments[2],
                    @"\[""kind""\]\s*=\s*""([^""]+)""",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                var hasKind = System.Text.RegularExpressions.Regex.IsMatch(
                    arguments[2],
                    @"\[""kind""\]\s*=",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                var allowedKinds = call.Contains("\"collect_events\"", StringComparison.Ordinal)
                    ? CollectEventsTool.AllowedKinds
                    : CollectSampleTool.AllowedKinds;
                if (!hasKind
                    || (kindMatch.Success
                        && !allowedKinds.Contains(kindMatch.Groups[1].Value, StringComparer.Ordinal)))
                {
                    var line = source.AsSpan(0, offset).Count('\n') + 1;
                    violations.Add($"{Path.GetRelativePath(root, path).Replace('\\', '/')}:{line}");
                }
            }
        }

        violations.Should().BeEmpty(
            "every replayable collect_events/collect_sample hint must preserve its canonical kind instead of invoking the tool default");
    }

    private static IReadOnlyList<string> EnumerateToolNames()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in PodLocalToolSurfaces.GetSurfaceTypes(
                     enableOrchestratorTools: true,
                     enableAzureDiscoveryTools: true))
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
        => File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

    private static string FindRepoRoot()
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

        return dir;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = text.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }

    private static IEnumerable<(int Offset, string Call)> EnumerateCollectorHintConstructions(string source)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?:new\s+NextActionHint|new)\s*\(\s*""collect_(?:events|sample)""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        foreach (System.Text.RegularExpressions.Match match in pattern.Matches(source))
        {
            var open = source.IndexOf('(', match.Index);
            var end = FindMatchingParenthesis(source, open);
            if (end > open)
            {
                yield return (match.Index, source[match.Index..(end + 1)]);
            }
        }
    }

    private static IReadOnlyList<string> SplitTopLevelArguments(string call)
    {
        var open = call.IndexOf('(');
        var close = FindMatchingParenthesis(call, open);
        var arguments = new List<string>();
        var start = open + 1;
        var depth = 0;
        var state = LexicalState.Code;
        for (var index = start; index < close; index++)
        {
            AdvanceLexicalState(call, ref index, ref state, out var character);
            if (state != LexicalState.Code)
            {
                continue;
            }

            if (character is '(' or '[' or '{')
            {
                depth++;
            }
            else if (character is ')' or ']' or '}')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                arguments.Add(call[start..index]);
                start = index + 1;
                if (arguments.Count == 2)
                {
                    arguments.Add(call[start..close]);
                    return arguments;
                }
            }
        }

        arguments.Add(call[start..close]);
        return arguments;
    }

    private static int FindMatchingParenthesis(string source, int open)
    {
        var depth = 0;
        var state = LexicalState.Code;
        for (var index = open; index < source.Length; index++)
        {
            AdvanceLexicalState(source, ref index, ref state, out var character);
            if (state != LexicalState.Code)
            {
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static void AdvanceLexicalState(
        string source,
        ref int index,
        ref LexicalState state,
        out char character)
    {
        character = source[index];
        var next = index + 1 < source.Length ? source[index + 1] : '\0';
        switch (state)
        {
            case LexicalState.Code when character == '"':
                state = LexicalState.String;
                break;
            case LexicalState.Code when character == '\'':
                state = LexicalState.Character;
                break;
            case LexicalState.Code when character == '/' && next == '/':
                state = LexicalState.LineComment;
                index++;
                break;
            case LexicalState.Code when character == '/' && next == '*':
                state = LexicalState.BlockComment;
                index++;
                break;
            case LexicalState.String when character == '\\':
            case LexicalState.Character when character == '\\':
                index++;
                break;
            case LexicalState.String when character == '"':
            case LexicalState.Character when character == '\'':
                state = LexicalState.Code;
                break;
            case LexicalState.LineComment when character == '\n':
                state = LexicalState.Code;
                break;
            case LexicalState.BlockComment when character == '*' && next == '/':
                state = LexicalState.Code;
                index++;
                break;
        }
    }

    private enum LexicalState
    {
        Code,
        String,
        Character,
        LineComment,
        BlockComment,
    }
}
