using System.Text;
using DotnetDiagnosticsMcp.Cli;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Cli.Tests;

/// <summary>
/// Validation-path coverage for the <c>collect</c> command (issue #288 PR2). Every case here
/// short-circuits with exit code 2 <b>before</b> the Core host is built, so these tests never spawn
/// a live process or open an EventPipe session — they only exercise option parsing and the
/// <see cref="CliCommands.TryValidateCollect"/> guard.
/// </summary>
public sealed class CliCollectValidationTests
{
    [Fact]
    public async Task RunAsync_CollectWithoutKind_ReturnsTwo()
    {
        var (exit, stdout, stderr) = await RunAsync("collect");

        exit.Should().Be(2);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("requires --kind");
        stderr.Should().Contain("Usage:");
    }

    [Fact]
    public async Task RunAsync_CollectUnknownKind_ReturnsTwo()
    {
        var (exit, _, stderr) = await RunAsync("collect", "--kind", "bogus");

        exit.Should().Be(2);
        stderr.Should().Contain("Unknown collect kind 'bogus'");
    }

    [Fact]
    public async Task RunAsync_EventSourceWithoutProvider_ReturnsTwo()
    {
        var (exit, _, stderr) = await RunAsync("collect", "--kind", "event_source");

        exit.Should().Be(2);
        stderr.Should().Contain("requires --provider");
    }

    [Fact]
    public async Task RunAsync_CollectInvalidDepth_ReturnsTwo()
    {
        var (exit, _, stderr) = await RunAsync("collect", "--kind", "gc", "--depth", "loud");

        exit.Should().Be(2);
        stderr.Should().Contain("Unknown --depth 'loud'");
    }

    [Theory]
    [InlineData("counters")]
    [InlineData("exceptions")]
    [InlineData("gc")]
    [InlineData("activities")]
    [InlineData("logs")]
    [InlineData("jit")]
    [InlineData("threadpool")]
    [InlineData("contention")]
    [InlineData("db")]
    public void TryValidateCollect_KnownKindsWithoutProvider_Succeed(string kind)
    {
        var options = CliOptions.Parse(new[] { "collect", "--kind", kind }, out _)!;

        CliCommands.TryValidateCollect(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateCollect_EventSourceWithProvider_Succeeds()
    {
        var options = CliOptions.Parse(
            new[] { "collect", "--kind", "event_source", "--provider", "System.Net.Http" }, out _)!;

        CliCommands.TryValidateCollect(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateCollect_ValidDepthForms_Succeed()
    {
        foreach (var depth in new[] { "summary", "Detail", "RAW" })
        {
            var options = CliOptions.Parse(new[] { "collect", "--kind", "gc", "--depth", depth }, out _)!;
            CliCommands.TryValidateCollect(options, out var error).Should().BeTrue($"depth '{depth}' is valid");
            error.Should().BeNull();
        }
    }

    [Fact]
    public void CollectKinds_MatchMcpDiscriminatorSet()
    {
        // Keep the CLI's accepted kinds aligned with the MCP collect_events discriminator family.
        CliCommands.CollectKinds.Should().BeEquivalentTo(new[]
        {
            "counters", "exceptions", "gc", "catalog", "event_source", "activities",
            "logs", "jit", "threadpool", "contention", "db",
        });
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
