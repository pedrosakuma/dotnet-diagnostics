using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Guardrail (#490): keeps <c>docs/cli-reference.md</c> in step with the CLI surface. Every command in
/// <see cref="CliCommands.Commands"/> and every kind in <see cref="CliCommands.CollectKinds"/> must be
/// documented, so wiring a new command/kind without documenting it fails the build. The reference is the
/// live CLI allowlists, not a hand-maintained copy, so the test cannot drift out of sync with the code.
/// </summary>
public sealed class CliDocParityTests
{
    private static string ReadCliReference()
    {
        // [CallerFilePath] collapses to "/_/…" in deterministic CI builds, so walk up from the test
        // assembly to the repo root (marked by DotnetDiagnostics.slnx) and read docs/cli-reference.md.
        var dir = Path.GetDirectoryName(typeof(CliDocParityTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "DotnetDiagnostics.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            throw new FileNotFoundException(
                "Could not locate repo root (DotnetDiagnostics.slnx) by walking up from " +
                typeof(CliDocParityTests).Assembly.Location);
        }

        return File.ReadAllText(Path.Combine(dir, "docs", "cli-reference.md"));
    }

    [Fact]
    public void CliReference_DocumentsEveryCommand()
    {
        var doc = ReadCliReference();

        foreach (var command in CliCommands.Commands)
        {
            // Most commands have a "### `command`" section; `completion` is documented under its own
            // top-level "## Shell completion" section instead, so accept either form.
            var documented = doc.Contains($"### `{command}`", StringComparison.Ordinal)
                || doc.Contains($"dotnet-diagnostics-cli {command}", StringComparison.Ordinal);
            documented.Should().BeTrue(
                $"docs/cli-reference.md must document the '{command}' command.");
        }
    }

    [Fact]
    public void CliReference_DocumentsEveryCollectKind()
    {
        var doc = ReadCliReference();

        foreach (var kind in CliCommands.CollectKinds)
        {
            doc.Should().Contain($"`{kind}`",
                $"docs/cli-reference.md must mention the collect kind '{kind}'.");
        }
    }

    [Fact]
    public void CliReference_DocumentsFrameVarsView()
    {
        // frame-vars is the CLI-only addition on top of the Core thread-snapshot session views (#487).
        var doc = ReadCliReference();

        doc.Should().Contain("`frame-vars`",
            "docs/cli-reference.md must document the thread-snapshot 'frame-vars' drilldown view.");
    }

    [Fact]
    public void CliReference_DocumentsEveryAdvertisedCompletionFlag()
    {
        // Every long flag the shell-completion catalog advertises must be documented, so completion can
        // never surface a flag that the reference omits (or, conversely, one that the CLI cannot reach).
        var doc = ReadCliReference();

        foreach (var flag in CliCompletionScripts.AllCommandOptionFlags)
        {
            // Match on a flag boundary so a prefix flag (e.g. `--top`) is not spuriously satisfied by a
            // longer one (`--top-types`). Flags appear in the reference as `--flag`, `--flag <v>`, etc.
            var documented = System.Text.RegularExpressions.Regex.IsMatch(
                doc, $@"(?<![\w-]){System.Text.RegularExpressions.Regex.Escape(flag)}(?![\w-])");
            documented.Should().BeTrue(
                $"docs/cli-reference.md must document the completion-advertised flag '{flag}'.");
        }
    }
}
