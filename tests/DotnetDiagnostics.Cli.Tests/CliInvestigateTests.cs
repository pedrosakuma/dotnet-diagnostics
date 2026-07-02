using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Parsing and validation tests for the <c>investigate</c> and <c>export-summary</c> commands (issue #488).
/// These are pure unit tests: they exercise <see cref="CliOptions.Parse"/> and
/// <see cref="CliCommands.TryValidateInvestigate"/> / <see cref="CliCommands.TryValidateExportSummary"/>
/// with no live process or DI host required.
/// </summary>
public sealed class CliInvestigateTests
{
    // ──────────────────────────────────────── investigate ──────────────────────────────────────

    [Fact]
    public void Investigate_IsRegistered_InCommandsList()
    {
        CliCommands.Commands.Should().Contain("investigate");
    }

    [Fact]
    public void Parse_InvestigateCommand_NoOptions_ParsesOk()
    {
        var opts = CliOptions.Parse(new[] { "investigate", "--pid", "1234" }, out var error);

        error.Should().BeNull();
        opts.Should().NotBeNull();
        opts!.Command.Should().Be("investigate");
        opts.Pid.Should().Be(1234);
        opts.Symptom.Should().BeNull();
        opts.Hypothesis.Should().BeNull();
        opts.MaxToolCalls.Should().BeNull();
    }

    [Fact]
    public void Parse_InvestigateCommand_AllOptions_ParsesOk()
    {
        var opts = CliOptions.Parse(
            new[] { "investigate", "--pid", "42", "--symptom", "high CPU", "--hypothesis", "lock contention", "--max-tool-calls", "5" },
            out var error);

        error.Should().BeNull();
        opts.Should().NotBeNull();
        opts!.Command.Should().Be("investigate");
        opts.Pid.Should().Be(42);
        opts.Symptom.Should().Be("high CPU");
        opts.Hypothesis.Should().Be("lock contention");
        opts.MaxToolCalls.Should().Be(5);
    }

    [Fact]
    public void Parse_Symptom_MissingValue_ReturnsError()
    {
        var opts = CliOptions.Parse(new[] { "investigate", "--symptom" }, out var error);

        opts.Should().BeNull();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void Parse_MaxToolCalls_NonInteger_ReturnsError()
    {
        var opts = CliOptions.Parse(new[] { "investigate", "--max-tool-calls", "abc" }, out var error);

        opts.Should().BeNull();
        error.Should().Contain("expects an integer");
    }

    [Fact]
    public void TryValidateInvestigate_SymptomOnly_ReturnsTrue()
    {
        var opts = new CliOptions { Command = "investigate", Symptom = "requests timing out" };

        var valid = CliCommands.TryValidateInvestigate(opts, out var error);

        valid.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateInvestigate_MaxToolCallsZero_ReturnsFalse()
    {
        var opts = new CliOptions { Command = "investigate", MaxToolCalls = 0 };

        var valid = CliCommands.TryValidateInvestigate(opts, out var error);

        valid.Should().BeFalse();
        error.Should().Contain("max-tool-calls");
    }

    [Fact]
    public void TryValidateInvestigate_MaxToolCallsNegative_ReturnsFalse()
    {
        var opts = new CliOptions { Command = "investigate", MaxToolCalls = -1 };

        var valid = CliCommands.TryValidateInvestigate(opts, out var error);

        valid.Should().BeFalse();
        error.Should().Contain("max-tool-calls");
    }

    [Fact]
    public void TryValidateInvestigate_MaxToolCallsPositive_ReturnsTrue()
    {
        var opts = new CliOptions { Command = "investigate", MaxToolCalls = 1, Symptom = "high CPU" };

        var valid = CliCommands.TryValidateInvestigate(opts, out var error);

        valid.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateInvestigate_NoSymptomNoHypothesis_ReturnsFalse()
    {
        var opts = new CliOptions { Command = "investigate", MaxToolCalls = 8 };

        var valid = CliCommands.TryValidateInvestigate(opts, out var error);

        valid.Should().BeFalse();
        error.Should().Contain("symptom");
    }

    [Fact]
    public void TryValidateInvestigate_HypothesisWithoutSymptom_ReturnsTrue()
    {
        var opts = new CliOptions { Command = "investigate", Hypothesis = "lock contention on the cache" };

        var valid = CliCommands.TryValidateInvestigate(opts, out var error);

        valid.Should().BeTrue();
        error.Should().BeNull();
    }

    // ──────────────────────────────────────── export-summary ───────────────────────────────────

    [Fact]
    public void ExportSummary_IsRegistered_InCommandsList()
    {
        CliCommands.Commands.Should().Contain("export-summary");
    }

    [Fact]
    public void Parse_ExportSummaryCommand_AllOptions_ParsesOk()
    {
        var opts = CliOptions.Parse(
            new[] { "export-summary", "--handle", "h-abc123", "--out", "./inv.json", "--top-hotspots", "5" },
            out var error);

        error.Should().BeNull();
        opts.Should().NotBeNull();
        opts!.Command.Should().Be("export-summary");
        opts.Handle.Should().Be("h-abc123");
        opts.OutDir.Should().Be("./inv.json");
        opts.TopHotspots.Should().Be(5);
    }

    [Fact]
    public void Parse_TopHotspots_MissingValue_ReturnsError()
    {
        var opts = CliOptions.Parse(new[] { "export-summary", "--handle", "h1", "--top-hotspots" }, out var error);

        opts.Should().BeNull();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void TryValidateExportSummary_NoHandle_ReturnsFalse()
    {
        var opts = new CliOptions { Command = "export-summary" };

        var valid = CliCommands.TryValidateExportSummary(opts, out var error);

        valid.Should().BeFalse();
        error.Should().Contain("--handle");
    }

    [Fact]
    public void TryValidateExportSummary_WithHandle_ReturnsTrue()
    {
        var opts = new CliOptions { Command = "export-summary", Handle = "h-abc123" };

        var valid = CliCommands.TryValidateExportSummary(opts, out var error);

        valid.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateExportSummary_TopHotspotsZero_ReturnsFalse()
    {
        var opts = new CliOptions { Command = "export-summary", Handle = "h-abc123", TopHotspots = 0 };

        var valid = CliCommands.TryValidateExportSummary(opts, out var error);

        valid.Should().BeFalse();
        error.Should().Contain("top-hotspots");
    }

    [Fact]
    public void TryValidateExportSummary_TopHotspotsPositive_ReturnsTrue()
    {
        var opts = new CliOptions { Command = "export-summary", Handle = "h-abc123", TopHotspots = 3 };

        var valid = CliCommands.TryValidateExportSummary(opts, out var error);

        valid.Should().BeTrue();
        error.Should().BeNull();
    }

    // ──────────────────────────────── TryValidateCommand delegation ────────────────────────────

    [Fact]
    public void TryValidateCommand_Investigate_ValidOptions_ReturnsTrue()
    {
        var opts = new CliOptions { Command = "investigate", Pid = 1234, Symptom = "high CPU" };

        var valid = CliCommands.TryValidateCommand(opts, out var error);

        valid.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateCommand_ExportSummary_MissingHandle_ReturnsFalse()
    {
        var opts = new CliOptions { Command = "export-summary" };

        var valid = CliCommands.TryValidateCommand(opts, out var error);

        valid.Should().BeFalse();
        error.Should().Contain("--handle");
    }

    // ──────────────────────────────── CLI projection (no MCP leak) ──────────────────────────────

    [Fact]
    public void Project_ColdPlan_ExposesCliCommandsNotMcpToolNames()
    {
        var plan = new Core.Investigation.InvestigationPlanner().Plan(
            new Core.Investigation.InvestigationRequest(
                ProcessId: 4242,
                Symptom: "high CPU",
                Hypothesis: null,
                Constraints: new Core.Investigation.InvestigationConstraints(MaxToolCalls: 8)));

        var cli = CliInvestigationProjection.Project(plan);

        // The vitals step maps to the CLI 'collect' command, never the MCP 'collect_events' tool name.
        cli.NextStep.Command.Should().Be("collect");
        cli.AllSteps.Should().OnlyContain(s => s.Command == null || !s.Command.Contains('_'));

        // Serialize the projected DTO (this is exactly what the --json envelope emits) and assert no
        // MCP vocabulary survives anywhere in the payload.
        var json = System.Text.Json.JsonSerializer.Serialize(cli);
        foreach (var token in CliHintProjection.LeakTokens)
        {
            json.Should().NotContain(token, $"projected investigate output must not leak MCP token '{token}'");
        }
    }

    [Theory]
    [InlineData("collect_events(kind=\"counters\")", "collect --kind counters")]
    [InlineData("Drill with query_snapshot(handle, view=\"call-tree\").", "Drill with query --view call-tree.")]
    public void Scrub_RewritesMcpCallSyntaxToCliVocabulary(string input, string expected)
    {
        CliInvestigationProjection.Scrub(input).Should().Be(expected);
    }
}
