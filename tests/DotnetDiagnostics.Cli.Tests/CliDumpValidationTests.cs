using System.Text;
using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Validation-path coverage for the <c>dump</c> command (issue #288 PR3b). The invalid-dump-type
/// case short-circuits with exit code 2 <b>before</b> the Core host is built, so it never attaches
/// to a target — it only exercises option parsing and the <see cref="CliCommands.TryValidateDump"/>
/// guard.
/// </summary>
public sealed class CliDumpValidationTests
{
    [Theory]
    [InlineData("Mini")]
    [InlineData("triage")]
    [InlineData("WithHeap")]
    [InlineData("FULL")]
    public void TryValidateDump_KnownDumpTypes_Succeed(string dumpType)
    {
        var options = CliOptions.Parse(new[] { "dump", "--dump-type", dumpType }, out _)!;

        CliCommands.TryValidateDump(options, out var error).Should().BeTrue($"dump type '{dumpType}' is valid");
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateDump_NoDumpType_Succeeds()
    {
        var options = CliOptions.Parse(new[] { "dump" }, out _)!;

        CliCommands.TryValidateDump(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateDump_UnknownDumpType_Fails()
    {
        var options = CliOptions.Parse(new[] { "dump", "--dump-type", "bogus" }, out _)!;

        CliCommands.TryValidateDump(options, out var error).Should().BeFalse();
        error.Should().Contain("Unknown --dump-type 'bogus'");
    }

    [Fact]
    public async Task RunAsync_DumpUnknownDumpType_ReturnsTwo()
    {
        var (exit, stdout, stderr) = await RunAsync("dump", "--dump-type", "bogus");

        exit.Should().Be(2);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("Unknown --dump-type 'bogus'");
        stderr.Should().Contain("Usage:");
    }

    [Fact]
    public void DumpTypes_MatchProcessDumpTypeNames()
    {
        CliCommands.DumpTypes.Should().BeEquivalentTo(new[] { "Mini", "Triage", "WithHeap", "Full" });
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
