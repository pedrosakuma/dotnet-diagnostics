using System.Text;
using System.Text.Json;
using DotnetDiagnosticsMcp.Cli;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Cli.Tests;

public sealed class CliHostTests
{
    [Fact]
    public async Task RunAsync_Help_PrintsUsageToStdoutAndReturnsZero()
    {
        var (exit, stdout, stderr) = await RunAsync("--help");

        exit.Should().Be(0);
        stdout.Should().Contain("dotnet-diagnostics");
        stdout.Should().Contain("processes");
        stdout.Should().Contain("capabilities");
        stdout.Should().Contain("collect");
        stdout.Should().Contain("inspect-heap");
        stdout.Should().Contain("dump");
        stdout.Should().Contain("query");
        stdout.Should().Contain("get-bytes");
        stdout.Should().Contain("compare");
        stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_NoCommand_PrintsUsageToStderrAndReturnsTwo()
    {
        var (exit, stdout, stderr) = await RunAsync();

        exit.Should().Be(2);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("No command specified.");
        stderr.Should().Contain("Usage:");
    }

    [Fact]
    public async Task RunAsync_CommandHelp_PrintsFocusedHelpForThatCommand()
    {
        var (exit, stdout, stderr) = await RunAsync("dump", "--help");

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        // Focused screen: this command's options + examples, not other commands' option blocks.
        stdout.Should().Contain("dump options:");
        stdout.Should().Contain("--confirm");
        stdout.Should().NotContain("collect options:");
        stdout.Should().NotContain("inspect-heap options:");
        // Item 3 (#302): the dump-preview-exits-0 scripting signal is documented in dump help.
        stdout.Should().Contain("confirmation_required").And.Contain("dump_written");
    }

    [Fact]
    public async Task RunAsync_CommandHelp_CollectShowsOnlyCollectOptions()
    {
        var (exit, stdout, _) = await RunAsync("collect", "--help");

        exit.Should().Be(0);
        stdout.Should().Contain("collect options:").And.Contain("--kind");
        stdout.Should().NotContain("dump options:");
        stdout.Should().NotContain("get-bytes options:");
    }

    [Fact]
    public async Task RunAsync_UnknownCommand_ReturnsTwo()
    {
        var (exit, _, stderr) = await RunAsync("frobnicate");

        exit.Should().Be(2);
        stderr.Should().Contain("Unknown command 'frobnicate'.");
    }

    [Fact]
    public async Task RunAsync_ParseError_ReturnsTwo()
    {
        var (exit, _, stderr) = await RunAsync("capabilities", "--pid", "not-a-number");

        exit.Should().Be(2);
        stderr.Should().Contain("expects an integer");
    }

    [Fact]
    public async Task RunAsync_ProcessesJson_EmitsValidEnvelopeAndReturnsZero()
    {
        // `processes` never attaches to a target — ListProcesses returns an Ok envelope even when no
        // .NET process is visible, so this smoke test is environment-independent.
        var (exit, stdout, _) = await RunAsync("processes", "--json");

        exit.Should().Be(0);

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.TryGetProperty("summary", out var summary).Should().BeTrue();
        summary.GetString().Should().NotBeNullOrWhiteSpace();
        // Envelope always carries the data payload (the process list, possibly empty).
        doc.RootElement.TryGetProperty("data", out _).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_Processes_HumanOutputHasHeaderOrEmptyHint()
    {
        var (exit, stdout, _) = await RunAsync("processes");

        exit.Should().Be(0);
        stdout.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunAsync_Help_ListsSessionCommand()
    {
        var (exit, stdout, _) = await RunAsync("--help");

        exit.Should().Be(0);
        stdout.Should().Contain("session");
    }

    [Fact]
    public async Task RunAsync_Session_RunsReplFromStdin_AndExitsCleanly()
    {
        // Full-stack: routes through the `session` branch (builds the Core host once with a
        // MutableArtifactRootProvider, runs the REPL, cleans up the temp root). EOF exits with 0.
        var stdin = new StringReader("help\nexit\n");
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());

        var exit = await CliHost.RunAsync(
            new[] { "session" }, stdin, stdout, stderr, CancellationToken.None);

        exit.Should().Be(0);
        stdout.ToString().Should().Contain("dotnet-diagnostics session");
        stdout.ToString().Should().Contain("Session commands:");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
