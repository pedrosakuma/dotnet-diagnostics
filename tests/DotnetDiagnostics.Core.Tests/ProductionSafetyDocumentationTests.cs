using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotnetDiagnostics.Core.Safety;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class ProductionSafetyDocumentationTests
{
    private const string StartMarker = "<!-- BEGIN GENERATED SAFETY MATRIX -->";
    private const string EndMarker = "<!-- END GENERATED SAFETY MATRIX -->";
    private const string UpdateEnvironmentVariable = "DOTNET_DBG_MCP_UPDATE_SAFETY_DOCS";

    [Fact]
    public void ProductionSafetyMatrix_MatchesTheSharedRegistry()
    {
        var path = RepoFile("docs", "production-safety.md");
        var expected = RenderMatrix();
        if (string.Equals(
                Environment.GetEnvironmentVariable(UpdateEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            ReplaceGeneratedSection(path, expected);
        }

        ExtractGeneratedSection(File.ReadAllText(path)).Should().Be(
            expected,
            $"the generated matrix must match InvocationSafetyRegistry. " +
            $"Re-run this test with {UpdateEnvironmentVariable}=1 after an intentional registry change");
    }

    [Fact]
    public void ProductionSafetyGuidance_CoversProfilesSensitivePayloadsAndArtifactLifecycle()
    {
        var doc = CollapseWhitespace(File.ReadAllText(RepoFile("docs", "production-safety.md")));

        foreach (var heading in new[]
                 {
                     "## Production operating profiles",
                     "### `observe`",
                     "### `investigate`",
                     "### `privileged-response`",
                     "## EventPipe data-exposure boundary",
                     "## Retention, access, and disposal",
                 })
        {
            doc.Should().Contain(heading);
        }

        foreach (var term in new[]
                 {
                     "logs", "exceptions", "database statements", "activities",
                     "EventSource payloads", "networking", "requests",
                     "stack names", "type names", "method names",
                     "PII", "secrets", "confidential data",
                     "traces", "dumps", "raw bytes", "parameter values", "exported summaries",
                 })
        {
            doc.Should().Contain(term);
        }

        doc.Should().Contain("Redaction is defense in depth");
        doc.Should().Contain("does not prove that PII, secrets, or confidential data are absent");
    }

    [Fact]
    public void McpAndCliReferences_PointToTheCanonicalSharedMatrix()
    {
        foreach (var relativePath in new[]
                 {
                     Path.Combine("docs", "tool-reference.md"),
                     Path.Combine("docs", "cli-reference.md"),
                 })
        {
            var doc = CollapseWhitespace(
                File.ReadAllText(RepoFile(relativePath.Split(Path.DirectorySeparatorChar))));
            doc.Should().Contain("./production-safety.md");
            doc.Should().Contain("shared Core safety registry");
            doc.Should().Contain("observe");
            doc.Should().Contain("investigate");
            doc.Should().Contain("privileged-response");
        }
    }

    [Fact]
    public void SafetyDocumentation_NeverPromisesCompleteRedaction()
    {
        var forbiddenClaims = new[]
        {
            "fully redacted",
            "completely redacted",
            "pii-free",
            "secret-free",
            "all pii is removed",
            "all secrets are removed",
            "guarantees that pii",
            "guarantees that secrets",
        };

        foreach (var path in EnumerateOperatorDocumentation())
        {
            var doc = File.ReadAllText(path);
            foreach (var claim in forbiddenClaims)
            {
                doc.Should().NotContainEquivalentOf(
                    claim,
                    $"'{Path.GetRelativePath(FindRepoRoot(), path)}' must not promise complete redaction");
            }
        }
    }

    [Fact]
    public void OperatorDocumentation_DoesNotUseAmbiguousProductionSafeClaims()
    {
        foreach (var path in EnumerateOperatorDocumentation())
        {
            var doc = File.ReadAllText(path);
            AmbiguousSafeClaimRegex().Matches(doc).Should().BeEmpty(
                $"'{Path.GetRelativePath(FindRepoRoot(), path)}' must describe concrete impact and exposure instead of calling an operation safe");
        }
    }

    private static string RenderMatrix()
    {
        var builder = new StringBuilder();
        builder.AppendLine("### Operation coverage");
        builder.AppendLine();
        builder.AppendLine("| Operation | Discriminator | Values | Default | Conditional inputs | Maximum risk | Maximum approval |");
        builder.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var registration in InvocationSafetyRegistry.Operations)
        {
            builder.Append("| ").Append(Code(registration.Operation))
                .Append(" | ").Append(CodeOrNone(registration.DiscriminatorArgument))
                .Append(" | ").Append(CodeList(registration.DiscriminatorValues))
                .Append(" | ").Append(CodeOrNone(registration.DefaultDiscriminator))
                .Append(" | ").Append(CodeList(registration.ConditionalArguments))
                .Append(" | ").Append(Code(EnumToken(registration.MaximumSafety.RiskLevel)))
                .Append(" | ").Append(Code(EnumToken(registration.MaximumSafety.ApprovalPolicy)))
                .AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("### Resolved profiles");
        builder.AppendLine();
        builder.AppendLine("| Operation | Profile | Trigger | Risk | Approval | Target impact | Data exposure | Side effects | Reason and mitigations |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var registration in InvocationSafetyRegistry.Operations)
        {
            foreach (var profile in registration.Profiles
                         .OrderBy(static profile => profile.Id, StringComparer.Ordinal)
                         .ThenBy(static profile => FormatArguments(profile.Arguments), StringComparer.Ordinal))
            {
                var safety = profile.Safety;
                builder.Append("| ").Append(Code(registration.Operation))
                    .Append(" | ").Append(Code(profile.Id))
                    .Append(" | ").Append(FormatArguments(profile.Arguments))
                    .Append(" | ").Append(Code(EnumToken(safety.RiskLevel)))
                    .Append(" | ").Append(Code(EnumToken(safety.ApprovalPolicy)))
                    .Append(" | ").Append(EnumList(safety.TargetImpact))
                    .Append(" | ").Append(EnumList(safety.DataExposure))
                    .Append(" | ").Append(EnumList(safety.SideEffects))
                    .Append(" | ").Append(EscapeCell(safety.Reason));
                if (safety.Mitigations.Length > 0)
                {
                    builder.Append("<br>Mitigations: ")
                        .Append(string.Join("<br>", safety.Mitigations.Select(EscapeCell)));
                }

                builder.AppendLine(" |");
            }
        }

        return Normalize(builder.ToString()).TrimEnd();
    }

    private static string FormatArguments(IReadOnlyDictionary<string, string> arguments)
        => arguments.Count == 0
            ? "default"
            : string.Join(
                "<br>",
                arguments.OrderBy(static argument => argument.Key, StringComparer.Ordinal)
                    .Select(static argument => $"{Code(argument.Key)}={Code(argument.Value)}"));

    private static string EnumList<T>(IEnumerable<T> values)
        where T : struct, Enum
    {
        var tokens = values.Select(EnumToken).ToArray();
        return tokens.Length == 0 ? "none" : string.Join("<br>", tokens.Select(Code));
    }

    private static string CodeList(IEnumerable<string> values)
    {
        var tokens = values.ToArray();
        return tokens.Length == 0 ? "none" : string.Join("<br>", tokens.Select(Code));
    }

    private static string CodeOrNone(string? value)
        => value is null ? "none" : Code(value);

    private static string Code(string value)
        => $"`{value.Replace("`", "\\`", StringComparison.Ordinal)}`";

    private static string EnumToken<T>(T value)
        where T : struct, Enum
        => JsonSerializer.Serialize(value).Trim('"');

    private static string EscapeCell(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal);

    private static string ExtractGeneratedSection(string document)
    {
        var normalized = Normalize(document);
        var start = normalized.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = normalized.IndexOf(EndMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        return normalized[(start + StartMarker.Length)..end].Trim();
    }

    private static void ReplaceGeneratedSection(string path, string generated)
    {
        var document = Normalize(File.ReadAllText(path));
        var start = document.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = document.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException($"Missing generated safety matrix markers in '{path}'.");
        }

        var replacement = $"{StartMarker}\n{generated}\n{EndMarker}";
        var updated = document[..start] + replacement + document[(end + EndMarker.Length)..];
        File.WriteAllText(path, updated);
    }

    private static string Normalize(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string CollapseWhitespace(string value)
        => WhitespaceRegex().Replace(value, " ");

    private static string RepoFile(params string[] segments)
        => Path.Combine([FindRepoRoot(), .. segments]);

    private static string FindRepoRoot()
    {
        var directory = Path.GetDirectoryName(typeof(ProductionSafetyDocumentationTests).Assembly.Location);
        while (directory is not null && !File.Exists(Path.Combine(directory, "DotnetDiagnostics.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new FileNotFoundException(
            "Could not locate repo root by walking up from the test assembly.");
    }

    private static IEnumerable<string> EnumerateOperatorDocumentation()
    {
        var root = FindRepoRoot();
        return Directory.EnumerateFiles(
                Path.Combine(root, "docs"),
                "*.md",
                SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly));
    }

    [GeneratedRegex(
        @"\b(?:production-safe|prod-safe|eventpipe-safe|safe to run|safest investigation)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmbiguousSafeClaimRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
