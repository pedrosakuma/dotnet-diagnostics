using System.Text;
using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Validation-path coverage for the <c>inspect-heap</c> command (issue #288 PR3b). Every case here
/// short-circuits with exit code 2 <b>before</b> the Core host is built, so these tests never attach
/// to a live process or open a dump — they only exercise option parsing and the
/// <see cref="CliCommands.TryResolveHeapSource"/> / <see cref="CliCommands.TryValidateInspectHeap"/>
/// guards.
/// </summary>
public sealed class CliInspectHeapValidationTests
{
    [Fact]
    public void Parse_GcDumpWithExportTrace_SetsFlag()
    {
        var options = CliOptions.Parse(new[] { "inspect-heap", "--source", "gcdump", "--export-trace" }, out _)!;

        options.ExportTrace.Should().BeTrue();
        CliCommands.TryResolveHeapSource(options, out var source, out _).Should().BeTrue();
        source.Should().Be("gcdump");
    }

    [Fact]
    public void TryResolveHeapSource_NoSourceNoDumpFile_InfersLive()
    {
        var options = CliOptions.Parse(new[] { "inspect-heap" }, out _)!;

        CliCommands.TryResolveHeapSource(options, out var source, out var error).Should().BeTrue();
        source.Should().Be("live");
        error.Should().BeNull();
    }

    [Fact]
    public void TryResolveHeapSource_DumpFileWithoutSource_InfersDump()
    {
        var options = CliOptions.Parse(new[] { "inspect-heap", "--dump-file", "./app.dmp" }, out _)!;

        CliCommands.TryResolveHeapSource(options, out var source, out var error).Should().BeTrue();
        source.Should().Be("dump");
        error.Should().BeNull();
    }

    [Fact]
    public void TryResolveHeapSource_UnknownSource_Fails()
    {
        var options = CliOptions.Parse(new[] { "inspect-heap", "--source", "bogus" }, out _)!;

        CliCommands.TryResolveHeapSource(options, out _, out var error).Should().BeFalse();
        error.Should().Contain("Unknown --source 'bogus'");
    }

    [Fact]
    public void TryResolveHeapSource_MultipleSources_Fails()
    {
        var options = CliOptions.Parse(new[] { "inspect-heap", "--source", "live", "--source", "dump" }, out _)!;

        CliCommands.TryResolveHeapSource(options, out _, out var error).Should().BeFalse();
        error.Should().Contain("single --source");
    }

    [Fact]
    public void TryResolveHeapSource_DumpWithoutDumpFile_Fails()
    {
        var options = CliOptions.Parse(new[] { "inspect-heap", "--source", "dump" }, out _)!;

        CliCommands.TryResolveHeapSource(options, out _, out var error).Should().BeFalse();
        error.Should().Contain("requires --dump-file");
    }

    [Fact]
    public void TryResolveHeapSource_DumpWithPid_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "inspect-heap", "--source", "dump", "--dump-file", "./app.dmp", "--pid", "1234" }, out _)!;

        CliCommands.TryResolveHeapSource(options, out _, out var error).Should().BeFalse();
        error.Should().Contain("does not accept --pid");
    }

    [Fact]
    public void TryResolveHeapSource_LiveWithDumpFile_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "inspect-heap", "--source", "live", "--dump-file", "./app.dmp" }, out _)!;

        CliCommands.TryResolveHeapSource(options, out _, out var error).Should().BeFalse();
        error.Should().Contain("does not accept --dump-file");
    }

    [Fact]
    public async Task RunAsync_InspectHeapDumpWithoutFile_ReturnsTwo()
    {
        var (exit, stdout, stderr) = await RunAsync("inspect-heap", "--source", "dump");

        exit.Should().Be(2);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("requires --dump-file");
        stderr.Should().Contain("Usage:");
    }

    [Fact]
    public async Task RunAsync_InspectHeapUnknownSource_ReturnsTwo()
    {
        var (exit, _, stderr) = await RunAsync("inspect-heap", "--source", "bogus");

        exit.Should().Be(2);
        stderr.Should().Contain("Unknown --source 'bogus'");
    }

    [Fact]
    public void HeapSources_AreLiveDumpAndGcDump()
    {
        CliCommands.HeapSources.Should().BeEquivalentTo(new[] { "live", "dump", "gcdump" });
    }

    [Fact]
    public void TryResolveHeapSource_GcDump_Succeeds()
    {
        var options = CliOptions.Parse(new[] { "inspect-heap", "--source", "gcdump", "--pid", "1234" }, out _)!;

        CliCommands.TryResolveHeapSource(options, out var source, out var error).Should().BeTrue();
        source.Should().Be("gcdump");
        error.Should().BeNull();
    }

    [Fact]
    public void TryResolveHeapSource_GcDumpWithDumpFile_Fails()
    {
        var options = CliOptions.Parse(
            new[] { "inspect-heap", "--source", "gcdump", "--dump-file", "./app.dmp" }, out _)!;

        CliCommands.TryResolveHeapSource(options, out _, out var error).Should().BeFalse();
        error.Should().Contain("does not accept --dump-file");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
