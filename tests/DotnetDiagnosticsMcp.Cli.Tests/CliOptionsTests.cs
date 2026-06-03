using DotnetDiagnosticsMcp.Cli;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Cli.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_BareCommand_SetsCommand()
    {
        var options = CliOptions.Parse(new[] { "processes" }, out var error);

        error.Should().BeNull();
        options.Should().NotBeNull();
        options!.Command.Should().Be("processes");
        options.Pid.Should().BeNull();
        options.Json.Should().BeFalse();
        options.Help.Should().BeFalse();
    }

    [Fact]
    public void Parse_PidAndJson_AreCaptured()
    {
        var options = CliOptions.Parse(new[] { "capabilities", "--pid", "1234", "--json" }, out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("capabilities");
        options.Pid.Should().Be(1234);
        options.Json.Should().BeTrue();
    }

    [Theory]
    [InlineData("-p")]
    [InlineData("--pid")]
    public void Parse_PidShortAndLongForms_AreEquivalent(string flag)
    {
        var options = CliOptions.Parse(new[] { "capabilities", flag, "42" }, out var error);

        error.Should().BeNull();
        options!.Pid.Should().Be(42);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Parse_HelpFlag_SetsHelp(string flag)
    {
        var options = CliOptions.Parse(new[] { flag }, out var error);

        error.Should().BeNull();
        options!.Help.Should().BeTrue();
    }

    [Fact]
    public void Parse_PidMissingValue_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "capabilities", "--pid" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void Parse_PidNonInteger_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "capabilities", "--pid", "abc" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("expects an integer");
    }

    [Fact]
    public void Parse_UnknownOption_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "processes", "--bogus" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("Unknown option");
    }

    [Fact]
    public void Parse_SecondPositional_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "processes", "capabilities" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("Only one command is accepted");
    }

    [Fact]
    public void Parse_NoArgs_ReturnsEmptyOptions()
    {
        var options = CliOptions.Parse(Array.Empty<string>(), out var error);

        error.Should().BeNull();
        options!.Command.Should().BeNull();
        options.Help.Should().BeFalse();
    }
}
