using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Parsing + validation coverage for the bounded threshold-gated capture options (issue #419):
/// <c>--capture-when</c>, <c>--capture</c>, <c>--window</c>, <c>--max-captures</c>. These exercise
/// only <see cref="CliOptions.Parse"/> and <see cref="CliCommands.TryValidateCollect"/> /
/// <see cref="CliCommands.TryValidateWatch"/> — no live process is spawned.
/// </summary>
public sealed class CliGatedCaptureValidationTests
{
    [Fact]
    public void Parse_GatedOptions_PopulatesFields()
    {
        var options = CliOptions.Parse(
            new[]
            {
                "collect", "--kind", "counters", "--pid", "1234",
                "--capture-when", "cpu>85", "--capture", "cpu-sample",
                "--window", "60", "--max-captures", "3", "--watch", "2",
            },
            out var error)!;

        error.Should().BeNull();
        options.CaptureWhen.Should().Be("cpu>85");
        options.CaptureKind.Should().Be("cpu-sample");
        options.WindowSeconds.Should().Be(60);
        options.MaxCaptures.Should().Be(3);
        options.WatchIntervalSeconds.Should().Be(2);
    }

    [Fact]
    public void TryValidateCollect_FullGatedRequest_Succeeds()
    {
        var options = CliOptions.Parse(
            new[]
            {
                "collect", "--kind", "counters", "--pid", "1",
                "--capture-when", "cpu>85", "--capture", "cpu-sample", "--window", "60",
            },
            out _)!;

        CliCommands.TryValidateCollect(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateCollect_GatedWithNonCountersKind_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "collect", "--kind", "gc", "--capture-when", "cpu>85", "--capture", "cpu-sample", "--window", "60" },
            out _)!;

        CliCommands.TryValidateCollect(options, out var error).Should().BeFalse();
        error.Should().Contain("--kind counters");
    }

    [Fact]
    public void TryValidateCollect_CaptureWhenWithoutCapture_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "collect", "--kind", "counters", "--capture-when", "cpu>85", "--window", "60" },
            out _)!;

        CliCommands.TryValidateCollect(options, out var error).Should().BeFalse();
        error.Should().Contain("--capture");
    }

    [Fact]
    public void TryValidateCollect_GatedWithoutWindow_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "collect", "--kind", "counters", "--capture-when", "cpu>85", "--capture", "dump" },
            out _)!;

        CliCommands.TryValidateCollect(options, out var error).Should().BeFalse();
        error.Should().Contain("--window");
    }

    [Fact]
    public void TryValidateWatch_GatedModeAllowsWatchWithJson()
    {
        // In gated mode --watch is the sample interval, not the redraw loop, so the usual
        // "--watch is incompatible with --json" restriction must not apply.
        var options = CliOptions.Parse(
            new[]
            {
                "collect", "--kind", "counters", "--json", "--watch", "2",
                "--capture-when", "cpu>85", "--capture", "cpu-sample", "--window", "60",
            },
            out _)!;

        CliCommands.TryValidateWatch(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }
}
