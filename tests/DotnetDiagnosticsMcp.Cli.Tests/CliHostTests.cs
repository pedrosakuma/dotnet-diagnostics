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

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
