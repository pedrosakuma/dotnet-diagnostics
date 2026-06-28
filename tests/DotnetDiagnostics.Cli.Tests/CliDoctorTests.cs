using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// End-to-end tests for the <c>doctor</c> command (Phase 13 / G1). The findings depend on the host
/// environment (ptrace_scope, capabilities), so the assertions are written to be
/// environment-independent: they verify the contract (output shape, JSON validity, and the
/// blocker → non-zero-exit coupling) rather than a specific verdict.
/// </summary>
public sealed class CliDoctorTests
{
    [Fact]
    public async Task Doctor_HostOnly_PrintsPreflightReportAndCouplesExitToVerdict()
    {
        var (exit, stdout, stderr) = await RunAsync("doctor");

        stderr.Should().BeEmpty();
        stdout.Should().Contain("Preflight:");
        stdout.Should().Contain("OS:");
        // Host-only diagnosis: socket-uid is not applicable without a target.
        stdout.Should().Contain("Diagnostic IPC socket UID match");

        // Blocker ⇒ exit 1; otherwise exit 0. Never a hard failure exit (2) or crash.
        if (stdout.Contains("BLOCKED", StringComparison.Ordinal))
        {
            exit.Should().Be(1);
        }
        else
        {
            exit.Should().Be(0);
        }
    }

    [Fact]
    public async Task Doctor_Json_EmitsWellFormedEnvelopeWithChecks()
    {
        var (_, stdout, _) = await RunAsync("doctor", "--json");

        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("overall").GetString().Should().NotBeNullOrEmpty();
        var checks = data.GetProperty("checks");
        checks.GetArrayLength().Should().BeGreaterThan(0);
        // Status serializes as a string (JsonStringEnumConverter), not an integer.
        checks[0].GetProperty("status").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Doctor_AppearsInHelp()
    {
        var (exit, stdout, _) = await RunAsync("--help");

        exit.Should().Be(0);
        stdout.Should().Contain("doctor");
    }

    [Fact]
    public void Doctor_ResolvesPidName()
    {
        // doctor accepts a target (--pid) and must resolve a process *name* like the other
        // target-aware commands, otherwise a named --pid silently degrades to host-only.
        var options = new CliOptions { Command = "doctor", PidName = "MyApp" };

        CliHost.ShouldResolvePidName(options).Should().BeTrue();
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
