using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliCommandExecutionOutputTests
{
    [Fact]
    public async Task SessionOutput_OmitsPidAlreadySuppliedByBoundTarget()
    {
        var envelope = DiagnosticResult.Ok(
            new object(),
            "summary",
            new NextActionHint("collect", "Run: collect --kind threadpool --pid 4321 --duration 10"));
        var result = new CliCommandResult(
            IsError: false,
            Cancelled: false,
            Envelope: envelope,
            Human: "summary\n  next:\n    - collect: Run: collect --kind threadpool --pid 4321 --duration 10");
        var options = CliOptions.Parse(["inspect", "--view", "triage"], out var error)!;
        error.Should().BeNull();

        var human = await RenderAsync(result, options, CliExecutionContext.Session, json: false, boundTargetPid: 4321);
        var json = await RenderAsync(result, options, CliExecutionContext.Session, json: true, boundTargetPid: 4321);

        human.Should().Contain("collect --kind threadpool --duration 10").And.NotContain("--pid 4321");
        json.Should().Contain("collect --kind threadpool --duration 10").And.NotContain("--pid 4321");
    }

    [Fact]
    public async Task OneShotHandleOutput_ExplainsThatTheHandleCannotBeQueriedLater()
    {
        var envelope = DiagnosticResult.OkWithHandle(
            new object(),
            "captured",
            "h-1",
            DateTimeOffset.UtcNow.AddMinutes(10));
        var result = new CliCommandResult(false, false, envelope, "captured")
        {
            Handle = "h-1",
        };
        var options = CliOptions.Parse(["collect", "--kind", "cpu"], out var error)!;
        error.Should().BeNull();

        var human = await RenderAsync(result, options, CliExecutionContext.OneShot, json: false, boundTargetPid: null);
        var json = await RenderAsync(result, options, CliExecutionContext.OneShot, json: true, boundTargetPid: null);

        human.Should().Contain("later invocation cannot query it").And.Contain("'session' REPL");
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("handleNotice").GetString()
            .Should().Contain("later invocation cannot query it");
    }

    private static async Task<string> RenderAsync(
        CliCommandResult result,
        CliOptions options,
        CliExecutionContext context,
        bool json,
        int? boundTargetPid)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        await CliCommandExecution.WriteCompletedResultAsync(
            result,
            options with { Json = json },
            stdout,
            stderr,
            new CliExecutionOptions(
                context,
                AnsiEnabled: false,
                ShowProgress: false,
                BoundTargetPid: boundTargetPid));
        stderr.ToString().Should().BeEmpty();
        return stdout.ToString();
    }
}
