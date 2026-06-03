using System.Text.Json;
using DotnetDiagnosticsMcp.Server.Cli;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Cli;

/// <summary>
/// Tests for the <c>diag</c> host-mode spike (issue #287). They exercise the parser, the usage /
/// exit-code contract and one end-to-end smoke (<c>processes --json</c>) that builds the real Core
/// host and renders the envelope — without needing a live target process, because
/// <c>ListProcesses</c> returns a success envelope even when nothing is attachable.
/// </summary>
public class DiagHostModeTests
{
    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DiagHostMode.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task NoCommand_PrintsUsage_AndExitsTwo()
    {
        var (code, _, err) = await RunAsync("diag");

        code.Should().Be(2);
        err.Should().Contain("Usage:").And.Contain("processes");
    }

    [Fact]
    public async Task Help_PrintsUsageToStdout_AndExitsZero()
    {
        var (code, output, _) = await RunAsync("diag", "--help");

        code.Should().Be(0);
        output.Should().Contain("Usage:").And.Contain("inspect-heap");
    }

    [Fact]
    public async Task UnknownCommand_PrintsError_AndExitsTwo()
    {
        var (code, _, err) = await RunAsync("diag", "bogus");

        code.Should().Be(2);
        err.Should().Contain("Unknown command 'bogus'");
    }

    [Fact]
    public async Task NonIntegerPid_IsUsageError()
    {
        var (code, _, err) = await RunAsync("diag", "capabilities", "--pid", "abc");

        code.Should().Be(2);
        err.Should().Contain("expects an integer");
    }

    [Fact]
    public async Task UnknownOption_IsUsageError()
    {
        var (code, _, err) = await RunAsync("diag", "processes", "--frobnicate");

        code.Should().Be(2);
        err.Should().Contain("Unknown option '--frobnicate'");
    }

    [Fact]
    public async Task Processes_Json_EmitsValidEnvelope_OnStdout_WithCleanStderr()
    {
        var (code, output, err) = await RunAsync("diag", "processes", "--json");

        code.Should().Be(0);
        err.Should().BeEmpty("logs route to stderr at Warning+ only; a healthy run emits none");

        // stdout must be a single parseable DiagnosticResult envelope, not a human table.
        using var doc = JsonDocument.Parse(output);
        doc.RootElement.TryGetProperty("summary", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("hints", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Processes_Human_RendersSummaryLine()
    {
        var (code, output, _) = await RunAsync("diag", "processes");

        code.Should().Be(0);
        // Human output is a table, never JSON.
        output.TrimStart().Should().NotStartWith("{");
    }
}

/// <summary>Unit tests for the <c>diag</c> flag parser (issue #287).</summary>
public class DiagOptionsTests
{
    private static DiagOptions Parse(params string[] args)
    {
        var options = DiagOptions.Parse(args, out var error);
        error.Should().BeNull();
        options.Should().NotBeNull();
        return options!;
    }

    [Fact]
    public void Parse_ExtractsCommandAndFlags()
    {
        var options = Parse("collect", "--kind", "gc", "--pid", "4321", "--duration", "7", "--json");

        options.Command.Should().Be("collect");
        options.Kind.Should().Be("gc");
        options.Pid.Should().Be(4321);
        options.DurationSeconds.Should().Be(7);
        options.Json.Should().BeTrue();
    }

    [Fact]
    public void Parse_ShortFlags_AreEquivalent()
    {
        var options = Parse("collect", "-k", "counters", "-p", "9", "-d", "3");

        options.Kind.Should().Be("counters");
        options.Pid.Should().Be(9);
        options.DurationSeconds.Should().Be(3);
    }

    [Fact]
    public void Parse_SecondCommand_IsError()
    {
        var options = DiagOptions.Parse(new[] { "processes", "capabilities" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("Only one command is accepted");
    }

    [Fact]
    public void Parse_MissingFlagValue_IsError()
    {
        var options = DiagOptions.Parse(new[] { "collect", "--kind" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("requires a value");
    }
}
