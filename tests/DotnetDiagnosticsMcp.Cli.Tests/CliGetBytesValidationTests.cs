using System.Text;
using DotnetDiagnosticsMcp.Cli;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Cli.Tests;

/// <summary>
/// Validation-path coverage for the <c>get-bytes</c> command (issue #288 PR4). Every case here
/// short-circuits with exit code 2 <b>before</b> the Core host is built (or returns <c>true</c> from
/// <see cref="CliCommands.TryValidateGetBytes"/>) — none of them attach to a target or touch disk, so
/// they exercise only option parsing + the validation guard.
/// </summary>
public sealed class CliGetBytesValidationTests
{
    private const string SampleMvid = "11112222-3333-4444-5555-666677778888";

    [Fact]
    public void TryValidateGetBytes_ModuleWithMvidAndOut_Succeeds()
    {
        var options = CliOptions.Parse(
            new[] { "get-bytes", "--kind", "module", "--mvid", SampleMvid, "--out", "./app.dll" }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateGetBytes_DumpWithDumpFileAndOut_Succeeds()
    {
        var options = CliOptions.Parse(
            new[] { "get-bytes", "--kind", "dump", "--dump-file", "./app.dmp", "--out", "./copy.dmp" }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateGetBytes_NoKind_Fails()
    {
        var options = CliOptions.Parse(new[] { "get-bytes", "--out", "./x" }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeFalse();
        error.Should().Contain("requires --kind");
    }

    [Fact]
    public void TryValidateGetBytes_UnknownKind_Fails()
    {
        var options = CliOptions.Parse(new[] { "get-bytes", "--kind", "bogus", "--out", "./x" }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeFalse();
        error.Should().Contain("Unknown get-bytes kind 'bogus'");
    }

    [Fact]
    public void TryValidateGetBytes_NoOut_Fails()
    {
        var options = CliOptions.Parse(new[] { "get-bytes", "--kind", "module", "--mvid", SampleMvid }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeFalse();
        error.Should().Contain("requires --out");
    }

    [Fact]
    public void TryValidateGetBytes_ModuleWithoutMvid_Fails()
    {
        var options = CliOptions.Parse(new[] { "get-bytes", "--kind", "module", "--out", "./x" }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeFalse();
        error.Should().Contain("requires --mvid");
    }

    [Fact]
    public void TryValidateGetBytes_ModuleWithInvalidMvid_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "get-bytes", "--kind", "module", "--mvid", "not-a-guid", "--out", "./x" }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeFalse();
        error.Should().Contain("not a valid GUID");
    }

    [Fact]
    public void TryValidateGetBytes_ModuleWithUnknownAsset_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "get-bytes", "--kind", "module", "--mvid", SampleMvid, "--asset", "bogus", "--out", "./x" }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeFalse();
        error.Should().Contain("Unknown --asset 'bogus'");
    }

    [Fact]
    public void TryValidateGetBytes_ModuleWithDumpFile_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "get-bytes", "--kind", "module", "--mvid", SampleMvid, "--dump-file", "./x.dmp", "--out", "./x" }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeFalse();
        error.Should().Contain("does not accept --dump-file");
    }

    [Fact]
    public void TryValidateGetBytes_DumpWithoutDumpFile_Fails()
    {
        var options = CliOptions.Parse(new[] { "get-bytes", "--kind", "dump", "--out", "./x" }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeFalse();
        error.Should().Contain("requires --dump-file");
    }

    [Fact]
    public void TryValidateGetBytes_DumpWithPid_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "get-bytes", "--kind", "dump", "--dump-file", "./x.dmp", "--pid", "1234", "--out", "./x" }, out _)!;

        CliCommands.TryValidateGetBytes(options, out var error).Should().BeFalse();
        error.Should().Contain("does not accept --pid");
    }

    [Fact]
    public async Task RunAsync_GetBytesNoKind_ReturnsTwo()
    {
        var (exit, stdout, stderr) = await RunAsync("get-bytes", "--out", "./x");

        exit.Should().Be(2);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("requires --kind");
        stderr.Should().Contain("Usage:");
    }

    [Fact]
    public async Task RunAsync_GetBytesModuleNoMvid_ReturnsTwo()
    {
        var (exit, _, stderr) = await RunAsync("get-bytes", "--kind", "module", "--out", "./x");

        exit.Should().Be(2);
        stderr.Should().Contain("requires --mvid");
    }

    [Fact]
    public void ByteKinds_AreModuleAndDump()
    {
        CliCommands.ByteKinds.Should().BeEquivalentTo(new[] { "module", "dump" });
    }

    [Fact]
    public void ByteAssets_ArePeAndPdb()
    {
        CliCommands.ByteAssets.Should().BeEquivalentTo(new[] { "pe", "pdb" });
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
