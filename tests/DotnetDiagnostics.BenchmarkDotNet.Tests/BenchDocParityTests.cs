using DotnetDiagnostics.BenchmarkDotNet;
using FluentAssertions;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

/// <summary>
/// Guardrail (#500): keeps the bench diagnoser's kind surface, the discoverable enum, and the package
/// README in lock-step. Mirrors the CLI's <c>CliDocParityTests</c> approach — the reference is the live
/// <see cref="InProcessDiagnosticCollector.SupportedKinds"/> / <see cref="BenchmarkDiagnosticKind"/>
/// surface plus the README, not a hand-maintained copy — so it cannot silently drift. Wiring a new kind
/// (or a new out-of-scope entry) without documenting it fails the build.
/// </summary>
public sealed class BenchDocParityTests
{
    /// <summary>
    /// Tokens the README must call out as intentionally out of scope. These use the engine's canonical
    /// discriminators (e.g. the sample kind is <c>off_cpu</c>, not <c>off-cpu</c>).
    /// </summary>
    private static readonly string[] OutOfScopeKinds =
    {
        "event_source", "startup", "crash-guard", "sweep", "off_cpu", "native-alloc",
    };

    private static string ReadReadme()
    {
        // [CallerFilePath] collapses to "/_/…" in deterministic CI, so walk up from the test assembly
        // to the repo root (marked by DotnetDiagnostics.slnx) and read the package README.
        var dir = Path.GetDirectoryName(typeof(BenchDocParityTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "DotnetDiagnostics.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            throw new FileNotFoundException(
                "Could not locate repo root (DotnetDiagnostics.slnx) by walking up from " +
                typeof(BenchDocParityTests).Assembly.Location);
        }

        return File.ReadAllText(Path.Combine(dir, "src", "DotnetDiagnostics.BenchmarkDotNet", "README.md"));
    }

    [Fact]
    public void Enum_And_SupportedKinds_AreInLockStep()
    {
        var enumTokens = Enum.GetValues<BenchmarkDiagnosticKind>()
            .Select(BenchmarkDiagnosticKinds.Token)
            .ToHashSet(StringComparer.Ordinal);

        enumTokens.Should().BeEquivalentTo(
            InProcessDiagnosticCollector.SupportedKinds,
            "every BenchmarkDiagnosticKind must map to a dispatched kind and vice-versa");
    }

    [Fact]
    public void Readme_DocumentsEverySupportedKind()
    {
        var readme = ReadReadme();

        foreach (var kind in InProcessDiagnosticCollector.SupportedKinds)
        {
            readme.Should().Contain($"`{kind}`",
                $"README.md must document the supported kind '{kind}'.");
        }
    }

    [Fact]
    public void Readme_DocumentsEveryOutOfScopeKind()
    {
        var readme = ReadReadme();

        foreach (var kind in OutOfScopeKinds)
        {
            readme.Should().Contain($"`{kind}`",
                $"README.md must list the out-of-scope kind '{kind}' under 'Not captured'.");
            InProcessDiagnosticCollector.IsSupported(kind).Should().BeFalse(
                $"'{kind}' is documented as out-of-scope, so it must not be dispatched.");
        }
    }
}
