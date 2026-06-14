using System.Text;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Artifacts;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Guards the artifact-root scoping invariant introduced in #288 PR4: commands that need a
/// command-specific artifact sandbox (<c>dump --out</c>, <c>get-bytes --kind dump</c>) override the
/// DI <see cref="IArtifactRootProvider"/> with a <see cref="FixedArtifactRootProvider"/> instead of
/// mutating the process-global <c>MCP_ARTIFACT_ROOT</c> environment variable — which would otherwise
/// leak across commands sharing the process (e.g. the whole test run).
/// </summary>
public sealed class CliArtifactRootTests
{
    [Fact]
    public void FixedArtifactRootProvider_ResolvesAbsoluteRoot_AndCreatesDirectory()
    {
        var relative = Path.Combine("cli-artifact-root-tests", Guid.NewGuid().ToString("N"));
        var expected = Path.GetFullPath(relative);
        try
        {
            var provider = new FixedArtifactRootProvider(relative);

            provider.Root.Should().Be(expected);
            Path.IsPathRooted(provider.Root).Should().BeTrue();
            Directory.Exists(provider.Root).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(expected))
            {
                Directory.Delete(expected, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_DumpWithOut_DoesNotMutateGlobalArtifactRootEnv()
    {
        var name = EnvironmentArtifactRootProvider.EnvironmentVariableName;
        var before = Environment.GetEnvironmentVariable(name);
        var outDir = Path.Combine(Path.GetTempPath(), "cli-artifact-leak-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A bogus pid fails resolution fast (exit 1) but BuildHost — where the old code mutated the
            // env var — has already run by then. The env var must be exactly as it was before.
            var (_, _, _) = await RunAsync("dump", "--pid", "999999999", "--out", outDir, "--confirm");

            Environment.GetEnvironmentVariable(name).Should().Be(before, "BuildHost must not mutate the global artifact-root env var");
        }
        finally
        {
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
