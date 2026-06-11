using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliLaunchValidationTests
{
    [Fact]
    public void Parse_LaunchFlagAndArgv_AreCaptured()
    {
        var options = CliOptions.Parse(
            new[] { "inspect-heap", "--top-types", "5", "--launch", "--", "dotnet", "App.dll", "--port", "0" },
            out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("inspect-heap");
        options.Launch.Should().BeTrue();
        options.LaunchArgs.Should().Equal("dotnet", "App.dll", "--port", "0");
    }

    [Fact]
    public void Parse_DoubleDashStopsOptionParsing()
    {
        // Flags after `--` belong to the launched program, not the CLI.
        var options = CliOptions.Parse(
            new[] { "session", "--launch", "--", "dotnet", "App.dll", "--json" },
            out var error);

        error.Should().BeNull();
        options!.Json.Should().BeFalse();
        options.LaunchArgs.Should().Equal("dotnet", "App.dll", "--json");
    }

    [Fact]
    public void Validate_LaunchWithoutArgv_Fails()
    {
        var options = CliOptions.Parse(new[] { "inspect-heap", "--launch" }, out _)!;

        CliCommands.TryValidateLaunch(options, out var error).Should().BeFalse();
        error.Should().Contain("requires a program after '--'");
    }

    [Fact]
    public void Validate_ArgvWithoutLaunchFlag_Fails()
    {
        var options = CliOptions.Parse(new[] { "inspect-heap", "--", "dotnet", "App.dll" }, out _)!;

        CliCommands.TryValidateLaunch(options, out var error).Should().BeFalse();
        error.Should().Contain("require --launch");
    }

    [Fact]
    public void Validate_LaunchWithPid_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "inspect-heap", "--pid", "5", "--launch", "--", "dotnet", "App.dll" },
            out _)!;

        CliCommands.TryValidateLaunch(options, out var error).Should().BeFalse();
        error.Should().Contain("cannot be combined with --pid");
    }

    [Theory]
    [InlineData("processes")]
    [InlineData("query")]
    [InlineData("compare")]
    public void Validate_LaunchOnUnsupportedCommand_Fails(string command)
    {
        var options = CliOptions.Parse(
            new[] { command, "--launch", "--", "dotnet", "App.dll" },
            out _)!;

        CliCommands.TryValidateLaunch(options, out var error).Should().BeFalse();
        error.Should().Contain($"--launch is not supported by '{command}'");
    }

    [Theory]
    [InlineData("session")]
    [InlineData("capabilities")]
    [InlineData("collect")]
    [InlineData("dump")]
    [InlineData("inspect-heap")]
    [InlineData("get-bytes")]
    public void Validate_LaunchOnSupportedLiveCommand_Succeeds(string command)
    {
        var options = CliOptions.Parse(
            new[] { command, "--launch", "--", "dotnet", "App.dll" },
            out _)!;

        CliCommands.TryValidateLaunch(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void Validate_LaunchWithInspectHeapDump_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "inspect-heap", "--source", "dump", "--launch", "--", "dotnet", "App.dll" },
            out _)!;

        CliCommands.TryValidateLaunch(options, out var error).Should().BeFalse();
        error.Should().Contain("live target");
    }

    [Fact]
    public void Validate_LaunchWithGetBytesDump_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "get-bytes", "--kind", "dump", "--launch", "--", "dotnet", "App.dll" },
            out _)!;

        CliCommands.TryValidateLaunch(options, out var error).Should().BeFalse();
        error.Should().Contain("live target");
    }

    [Fact]
    public void Validate_NoLaunchNoArgv_Succeeds()
    {
        var options = CliOptions.Parse(new[] { "processes" }, out _)!;

        CliCommands.TryValidateLaunch(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }
}
