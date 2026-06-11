using System.Text;
using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliCompletionTests
{
    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("pwsh")]
    public async Task RunAsync_CompletionKnownShell_EmitsScript(string shell)
    {
        var (exit, stdout, stderr) = await RunAsync("completion", shell);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().NotBeNullOrWhiteSpace();
        stdout.Should().Contain("collect");
        stdout.Should().Contain("inspect-heap");
        stdout.Should().Contain("counters");
        stdout.Should().Contain("WithHeap");
        stdout.Should().Contain("live");
        stdout.Should().Contain("dump");
        stdout.Should().Contain("pwsh");
    }

    [Fact]
    public async Task RunAsync_CompletionUnknownShell_ReturnsTwoWithValidShells()
    {
        var (exit, stdout, stderr) = await RunAsync("completion", "fish");

        exit.Should().Be(2);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("Unknown completion shell 'fish'");
        stderr.Should().Contain("bash");
        stderr.Should().Contain("zsh");
        stderr.Should().Contain("pwsh");
    }

    [Fact]
    public async Task RunAsync_CompletionMissingShell_ReturnsTwoWithValidShells()
    {
        var (exit, stdout, stderr) = await RunAsync("completion");

        exit.Should().Be(2);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("requires a shell argument");
        stderr.Should().Contain("bash");
        stderr.Should().Contain("zsh");
        stderr.Should().Contain("pwsh");
    }

    [Fact]
    public async Task RunAsync_CompletionBash_IncludesEveryCollectKind()
    {
        var (exit, stdout, stderr) = await RunAsync("completion", "bash");

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        foreach (var kind in CliCommands.CollectKinds)
        {
            stdout.Should().Contain(kind);
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
