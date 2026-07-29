using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Guardrail for issue #742: the documented cpu-burn profiling expectation must stay aligned with the
/// BadCodeSample fixture. The endpoint still burns CPU by hashing with SHA256, but the observable managed
/// call tree currently bottoms out at the endpoint lambda rather than a <c>System.Security.Cryptography.SHA256</c>
/// child frame, so the docs must describe both sides of that contract.
/// </summary>
public sealed class BadCodeScenarioDocParityTests
{
    [Fact]
    public void CpuBurnFixture_StillUsesSha256()
    {
        var source = ReadRepoFile(Path.Combine("samples", "BadCodeSample", "Program.cs"));

        source.Should().Contain("app.MapGet(\"/cpu-burn\"");
        source.Should().Contain("SHA256.HashData(input);",
            "docs/bad-code-scenarios.md explains cpu-burn as a SHA256-based CPU burn");
    }

    [Fact]
    public void BadCodeScenarioDoc_DescribesCpuBurnObservableCallTree()
    {
        var doc = ReadRepoFile(Path.Combine("docs", "bad-code-scenarios.md"));

        doc.Should().Contain("> **CPU-burn call-tree note (scenario 1).**");
        doc.Should().Contain("sampling identifies the hot endpoint lambda even when the managed call tree does not expose the hashing child frame");
        doc.Should().Contain("`SHA256.HashData(...)` inside its tight loop");
        doc.Should().Contain("may still bottom out at");
        doc.Should().Contain("the endpoint lambda rather than surfacing a");
        doc.Should().Contain("`System.Security.Cryptography.SHA256` child frame");
        doc.Should().Contain("- localize `/cpu-burn` to its hot endpoint lambda");
        doc.Should().NotContain("- explain *why* `/cpu-burn` is hot (SHA256 in a tight loop),",
            "the old claim promised an observable SHA256 leaf that live smoke audits disproved");
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = Path.GetDirectoryName(typeof(BadCodeScenarioDocParityTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "DotnetDiagnostics.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            throw new FileNotFoundException(
                "Could not locate repo root (DotnetDiagnostics.slnx) by walking up from " +
                typeof(BadCodeScenarioDocParityTests).Assembly.Location);
        }

        return File.ReadAllText(Path.Combine(dir, relativePath));
    }
}
