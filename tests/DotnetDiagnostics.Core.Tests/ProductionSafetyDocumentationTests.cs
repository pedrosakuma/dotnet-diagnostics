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
    private const string ChecklistStartMarker = "<!-- BEGIN PRODUCTION READINESS CHECKLIST -->";
    private const string ChecklistEndMarker = "<!-- END PRODUCTION READINESS CHECKLIST -->";
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
    public void ProductionReadinessChecklist_CoversRequiredGatesAndCanonicalLinks()
    {
        var checklist = ExtractMarkedSection(
            File.ReadAllText(RepoFile("docs", "production-safety.md")),
            ChecklistStartMarker,
            ChecklistEndMarker);

        foreach (var gate in new[]
                 {
                     "Topology and diagnostic socket",
                     "Transport boundary",
                     "Named least-privilege identity",
                     "Linux live-memory privilege",
                     "Operating profile",
                     "MCP client approvals",
                     "Evidence ownership",
                     "Low-risk smoke",
                     "High/critical approval smoke",
                     "Rollback and cleanup",
                 })
        {
            var row = checklist.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .SingleOrDefault(line => line.Contains($"**{gate}**", StringComparison.Ordinal));
            row.Should().NotBeNull($"the checklist must contain the '{gate}' gate");
            row.Should().Contain("**PASS:**", $"the '{gate}' gate needs an explicit pass condition");
        }

        foreach (var link in new[]
                 {
                     "./consumer-install.md#2-run-it-as-a-supervised-service",
                     "../deploy/k8s/README.md#sidecar-topology-refresher",
                     "./client-setup.md#transport-security-non-loopback",
                     "../deploy/k8s/README.md#how-the-orchestrator-reaches-the-pod-local-mcp-server",
                     "./authorization.md#scopes",
                     "./authorization.md#oidc--jwt-issuers-claims--scopes",
                     "./consumer-install.md#15-linux-enabling-live-memory-readers-kernel-ptrace",
                     "#production-operating-profiles",
                     "./client-setup.md#safety-aware-toolscall",
                     "./authorization.md#per-call-confirmation",
                     "#retention-access-and-disposal",
                     "./client-setup.md#4-quick-smoke-test-with-curl",
                     "./consumer-install.md#first-diagnostic-low-risk",
                     "./consumer-install.md#uninstall",
                 })
        {
            checklist.Should().Contain($"({link})", $"the checklist must link to canonical detail '{link}'");
        }

        var introduction = ExtractMarkedSection(
            File.ReadAllText(RepoFile("docs", "production-safety.md")),
            "<a id=\"production-readiness-checklist\"></a>",
            ChecklistStartMarker);
        introduction.Should().Contain("Observe-only GO");
        introduction.Should().Contain("Privileged-response GO");
        checklist.Should().Contain("| Both |");
        checklist.Should().Contain("| Privileged response |");

        var transportRow = checklist.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("**Transport boundary**", StringComparison.Ordinal));
        foreach (var requiredNuance in new[]
                 {
                     "MCP_ALLOW_INSECURE_HTTP",
                     "non-loopback",
                     "loopback-only",
                     "per-attach child",
                 })
        {
            transportRow.Should().Contain(requiredNuance);
        }
    }

    [Fact]
    public void ProductionReadinessEntryPoints_LinkToTheChecklist()
    {
        foreach (var (relativePath, link) in new[]
                 {
                     ("README.md", "./docs/production-safety.md#production-readiness-checklist"),
                     (Path.Combine("docs", "README.md"), "./production-safety.md#production-readiness-checklist"),
                     (Path.Combine("deploy", "k8s", "README.md"), "../../docs/production-safety.md#production-readiness-checklist"),
                     (Path.Combine("deploy", "helm", "README.md"), "../../docs/production-safety.md#production-readiness-checklist"),
                 })
        {
            var doc = File.ReadAllText(RepoFile(relativePath.Split(Path.DirectorySeparatorChar)));
            doc.Should().Contain($"({link})");
        }
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

    [Fact]
    public void LinuxPtraceGuidance_IsCapabilityFirstAndProductionSafe()
    {
        var canonical = File.ReadAllText(RepoFile("docs", "consumer-install.md"));
        foreach (var term in new[]
                 {
                     "Canonical security note on `ptrace_scope=0`",
                     "host-wide",
                     "personal-development",
                     "shared",
                     "production",
                     "CAP_SYS_PTRACE",
                     "diagnostics sidecar",
                     "EventPipe",
                     "--launch",
                     "inspect_heap(source=\"dump\")",
                 })
        {
            canonical.Should().Contain(term);
        }

        foreach (var relativePath in new[]
                 {
                     "AGENTS.md",
                     "README.md",
                     Path.Combine("docs", "cli-reference.md"),
                     Path.Combine("docs", "tool-reference.md"),
                     Path.Combine("docs", "output-examples.md"),
                     Path.Combine("docs", "local-docker-sidecar.md"),
                     Path.Combine("src", "DotnetDiagnostics.Cli", "README.md"),
                 })
        {
            var doc = File.ReadAllText(RepoFile(relativePath.Split(Path.DirectorySeparatorChar)));
            if (!doc.Contains("ptrace_scope=0", StringComparison.Ordinal))
            {
                continue;
            }

            doc.Should().Contain("host-wide");
            doc.Should().Contain("personal-development");
            doc.Should().Contain("shared");
            doc.Should().Contain("production");
            doc.Should().Contain(
                "consumer-install.md#15-linux-enabling-live-memory-readers-kernel-ptrace");

            doc.IndexOf("SYS_PTRACE", StringComparison.Ordinal).Should().BeLessThan(
                doc.IndexOf("ptrace_scope=0", StringComparison.Ordinal),
                $"'{relativePath}' must present scoped capability guidance before host relaxation");
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
        => ExtractMarkedSection(document, StartMarker, EndMarker);

    private static string ExtractMarkedSection(string document, string startMarker, string endMarker)
    {
        var normalized = Normalize(document);
        var start = normalized.IndexOf(startMarker, StringComparison.Ordinal);
        var end = normalized.IndexOf(endMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        return normalized[(start + startMarker.Length)..end].Trim();
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
