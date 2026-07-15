using System.Text;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Validation-path coverage for the <c>inspect</c> command (issue #486). Every case here
/// short-circuits with exit code 2 <b>before</b> the Core host is built, so these tests never
/// spawn a live process or open an EventPipe session — they only exercise option parsing and the
/// <see cref="CliCommands.TryValidateInspect"/> guard.
/// </summary>
public sealed class CliInspectValidationTests
{
    [Fact]
    public async Task RunAsync_InspectWithoutView_ReturnsTwo()
    {
        var (exit, _, stderr) = await RunAsync("inspect");

        exit.Should().Be(2);
        stderr.Should().Contain("requires --view");
        stderr.Should().Contain("Usage:");
    }

    [Fact]
    public async Task RunAsync_InspectUnknownView_ReturnsTwo()
    {
        var (exit, _, stderr) = await RunAsync("inspect", "--view", "bogus");

        exit.Should().Be(2);
        stderr.Should().Contain("Unknown --view 'bogus'");
    }

    [Fact]
    public async Task RunAsync_InspectTriageNegativeDuration_ReturnsTwo()
    {
        var (exit, _, stderr) = await RunAsync("inspect", "--view", "triage", "--duration", "0");

        exit.Should().Be(2);
        stderr.Should().Contain("--duration must be >= 1");
    }

    [Theory]
    [InlineData("triage")]
    [InlineData("runtime-config")]
    [InlineData("container")]
    public void TryValidateInspect_KnownViews_Succeed(string view)
    {
        var options = CliOptions.Parse(["inspect", "--view", view], out _)!;

        CliCommands.TryValidateInspect(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateInspect_UnknownView_Fails()
    {
        var options = CliOptions.Parse(["inspect", "--view", "unknown"], out _)!;

        CliCommands.TryValidateInspect(options, out var error).Should().BeFalse();
        error.Should().Contain("Unknown --view 'unknown'");
        error.Should().Contain("triage");
        error.Should().Contain("runtime-config");
        error.Should().Contain("container");
    }

    [Fact]
    public void TryValidateInspect_MissingView_Fails()
    {
        var options = CliOptions.Parse(["inspect"], out _)!;

        CliCommands.TryValidateInspect(options, out var error).Should().BeFalse();
        error.Should().Contain("requires --view");
    }

    [Fact]
    public void TryValidateInspect_TriageDurationZero_Fails()
    {
        var options = CliOptions.Parse(["inspect", "--view", "triage", "--duration", "0"], out _)!;

        CliCommands.TryValidateInspect(options, out var error).Should().BeFalse();
        error.Should().Contain("--duration must be >= 1");
    }

    [Fact]
    public void TryValidateInspect_TriagePositiveDuration_Succeeds()
    {
        var options = CliOptions.Parse(["inspect", "--view", "triage", "--duration", "3"], out _)!;

        CliCommands.TryValidateInspect(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void Parse_InspectView_Captured()
    {
        var options = CliOptions.Parse(["inspect", "--view", "triage", "--pid", "1234"], out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("inspect");
        options.View.Should().Be("triage");
        options.Pid.Should().Be(1234);
    }

    [Fact]
    public void Parse_InspectRuntimeConfigView_Captured()
    {
        var options = CliOptions.Parse(["inspect", "--view", "runtime-config"], out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("inspect");
        options.View.Should().Be("runtime-config");
    }

    [Fact]
    public void Parse_InspectContainerView_Captured()
    {
        var options = CliOptions.Parse(["inspect", "--view", "container"], out var error);

        error.Should().BeNull();
        options!.View.Should().Be("container");
    }

    [Fact]
    public void Parse_InspectDuration_Captured()
    {
        var options = CliOptions.Parse(["inspect", "--view", "triage", "--duration", "10"], out var error);

        error.Should().BeNull();
        options!.DurationSeconds.Should().Be(10);
    }

    [Fact]
    public void InspectViews_ContainExpectedViews()
    {
        CliCommands.InspectViews.Should().BeEquivalentTo(new[] { "triage", "runtime-config", "container" });
    }

    [Fact]
    public async Task RunAsync_InspectContainer_CurrentProcess_Succeeds()
    {
        var (exit, stdout, stderr) = await RunAsync("inspect", "--view", "container", "--pid", Environment.ProcessId.ToString());

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("Scope");
    }

    [Fact]
    public async Task RunAsync_InspectTriageJson_ProjectsV2AndLegacyFields()
    {
        var (exit, stdout, stderr) = await RunAsync(
            "inspect",
            "--view",
            "triage",
            "--pid",
            Environment.ProcessId.ToString(),
            "--duration",
            "3",
            "--json");

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        var data = JsonNode.Parse(stdout)!["data"]!;
        data["modelVersion"]!.GetValue<int>().Should().Be(2);
        data["assessment"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        data["observedSignals"].Should().NotBeNull();
        data["hypotheses"].Should().NotBeNull();
        data["verdict"].Should().NotBeNull(
            "the deprecated field remains serialized during the migration window");
    }

    [Fact]
    public async Task RunAsync_InspectHelp_ExplainsEvidenceBackedTriage()
    {
        var (exit, stdout, stderr) = await RunAsync("inspect", "--help");

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("observed signals");
        stdout.Should().Contain("evidence-backed");
        stdout.Should().Contain("supporting/contradicting evidence");
        stdout.Should().Contain("deprecated until v1.0");
    }

    [Fact]
    public void Commands_ContainsInspect()
    {
        CliCommands.Commands.Should().Contain("inspect");
    }

    [Fact]
    public void LaunchableCommands_ContainsInspect()
    {
        CliCommands.LaunchableCommands.Should().Contain("inspect");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
