using System.Text;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Coverage for the <c>query</c> drill-down command (issue #288 PR4). The one-shot CLI cannot honour
/// MCP-session-scoped drill-down handles (per the #286 persistence decision), so <c>query</c> always
/// returns a structured <c>NotSupported</c> envelope and exit code 1 — even when <c>--handle</c> /
/// <c>--view</c> are supplied (they are parsed only so they don't trip "Unknown option").
/// </summary>
public sealed class CliQueryTests
{
    [Fact]
    public async Task RunAsync_Query_ReturnsOneWithNotSupported()
    {
        var (exit, stdout, stderr) = await RunAsync("query");

        exit.Should().Be(1);
        stdout.Should().Contain("not supported");
        stdout.Should().Contain("NotSupported");
        stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_QueryWithHandleAndView_StillReturnsOne()
    {
        var (exit, stdout, _) = await RunAsync("query", "--handle", "h-1", "--view", "top-types");

        exit.Should().Be(1);
        stdout.Should().Contain("NotSupported");
    }

    [Fact]
    public async Task RunAsync_QueryJson_EmitsErrorEnvelope()
    {
        var (exit, stdout, _) = await RunAsync("query", "--json");

        exit.Should().Be(1);
        stdout.Should().Contain("\"kind\"").And.Contain("NotSupported");
    }

    [Fact]
    public async Task RunAsync_Query_DoesNotAttachToTarget()
    {
        // No --pid, no live process: a NotSupported envelope must come back fast without any attach.
        var (exit, _, _) = await RunAsync("query");

        exit.Should().Be(1);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
