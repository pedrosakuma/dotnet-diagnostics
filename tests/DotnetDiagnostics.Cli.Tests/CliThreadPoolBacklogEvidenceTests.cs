using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.ThreadPool;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliThreadPoolBacklogEvidenceTests
{
    [Fact]
    public void Options_ParseAndValidateCallerDeclaredProtocol()
    {
        var options = CliOptions.Parse(
            [
                "collect", "--kind", "counters",
                "--threadpool-queue-level", "12.5",
                "--threadpool-min-windows", "6",
                "--threadpool-drain-tail-windows", "3",
            ],
            out var parseError)!;

        parseError.Should().BeNull();
        options.ThreadPoolQueueLevel.Should().Be(12.5);
        options.ThreadPoolMinimumWindows.Should().Be(6);
        options.ThreadPoolDrainTailWindows.Should().Be(3);
        CliCommands.TryValidateCollect(options, out var validationError).Should().BeTrue(validationError);
    }

    [Theory]
    [InlineData("--threadpool-queue-level", "-1")]
    [InlineData("--threadpool-min-windows", "1")]
    [InlineData("--threadpool-drain-tail-windows", "5")]
    public void Options_RejectInvalidProtocolValues(string flag, string value)
    {
        var options = CliOptions.Parse(
            ["collect", "--kind", "counters", "--threadpool-queue-level", "10", flag, value],
            out var parseError)!;

        parseError.Should().BeNull();
        CliCommands.TryValidateCollect(options, out var validationError).Should().BeFalse();
        validationError.Should().Contain("threadpool");
    }

    [Fact]
    public async Task HumanAndJson_RenderExplicitAvailabilityProvenanceAndUnavailableP95()
    {
        var snapshot = Snapshot(Evidence());
        var result = CliCommands.BuildResult(
            DiagnosticResult.Ok(snapshot, "captured"),
            CliCommands.RenderCounterBacklogEvidence);
        var options = new CliOptions { Command = "collect", Kind = "counters" };

        var human = await RenderAsync(result, options, json: false);
        var json = await RenderAsync(result, options, json: true);

        human.Should().Contain("availability=available");
        human.Should().Contain("above-level: 4/4 (100.0%)");
        human.Should().Contain("runtime reason: state=unknown");
        human.Should().Contain("collector queue p95 median: unavailable");
        human.Should().Contain("does not establish starvation");
        using var document = JsonDocument.Parse(json);
        var evidence = document.RootElement.GetProperty("data").GetProperty("threadPoolBacklog");
        evidence.GetProperty("availability").GetString().Should().Be("available");
        evidence.GetProperty("proportionAboveLevel").GetDouble().Should().Be(1);
        evidence.GetProperty("reason").GetProperty("state").GetString().Should().Be("unknown");
        evidence.TryGetProperty("collectorQueueP95Median", out _).Should().BeFalse();
        evidence.GetProperty("unavailableFields")[0].GetProperty("field").GetString()
            .Should().Be("collectorQueueP95Median");
    }

    [Fact]
    public void JsonlTerminal_EmitsSameTypedEvidenceOnlyWhenRequested()
    {
        var startedAt = DateTimeOffset.UnixEpoch;
        var accumulator = new ThreadPoolBacklogAccumulator(
            "session-1",
            startedAt,
            new ThreadPoolBacklogProtocol(10));
        for (var index = 0; index < 4; index++)
        {
            accumulator.Observe(startedAt.AddSeconds(index), Counter("threadpool-queue-length", 12 + index));
            accumulator.Observe(startedAt.AddSeconds(index), Counter("threadpool-thread-count", 4 + index));
        }
        var completion = new DiagnosticSessionCompletion(
            DiagnosticSessionStatus.Stopped,
            startedAt,
            startedAt.AddSeconds(4),
            EventPipeEventsLost: 0,
            DroppedObservations: 0,
            Error: null);

        var withEvidence = CliStreamingProtocol.BuildTerminalFrame(
            "session-1", ["counters"], completion, accumulator);
        var withoutEvidence = CliStreamingProtocol.BuildTerminalFrame(
            "session-2", ["counters"], completion, backlogAccumulator: null);
        var abstained = CliStreamingProtocol.BuildTerminalFrame(
            "session-3",
            ["counters"],
            completion with { EventPipeEventsLost = 1 },
            accumulator);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            withEvidence,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var abstainedDocument = JsonDocument.Parse(JsonSerializer.Serialize(
            abstained,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        document.RootElement.GetProperty("threadPoolBacklog")
            .GetProperty("availability").GetString().Should().Be("available");
        var abstainedEvidence = abstainedDocument.RootElement.GetProperty("threadPoolBacklog");
        abstainedEvidence.GetProperty("availability").GetString().Should().Be("loss_detected");
        abstainedEvidence.GetProperty("windowsAboveLevel").ValueKind.Should().Be(JsonValueKind.Null);
        abstainedEvidence.GetProperty("queueMovement").ValueKind.Should().Be(JsonValueKind.Null);
        abstainedEvidence.GetProperty("collectorQueueP95Median").ValueKind.Should().Be(JsonValueKind.Null);
        withoutEvidence.Should().NotContainKey("threadPoolBacklog");
    }

    private static ThreadPoolBacklogEvidence Evidence()
    {
        var startedAt = DateTimeOffset.UnixEpoch;
        return ThreadPoolBacklogAnalyzer.Evaluate(new(
            "capture-1",
            startedAt,
            startedAt.AddSeconds(4),
            new ThreadPoolBacklogProtocol(10),
            Enumerable.Range(0, 4).Select(index =>
                new ThreadPoolBacklogWindow(startedAt.AddSeconds(index), 1, 12 + index)).ToArray(),
            Enumerable.Range(0, 4).Select(index =>
                new ThreadPoolBacklogWindow(startedAt.AddSeconds(index), 1, 4 + index)).ToArray(),
            new ThreadPoolBacklogReason(null, ThreadPoolEvidence.Missing, null, null)));
    }

    private static CounterSnapshot Snapshot(ThreadPoolBacklogEvidence evidence) =>
        new(42, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(4), [], [], [])
        {
            ThreadPoolBacklog = evidence,
        };

    private static CounterValue Counter(string name, double value) =>
        new("System.Runtime", name, name, value, CounterKind.Mean)
        {
            IntervalSec = 1,
        };

    private static async Task<string> RenderAsync(
        CliCommandResult result,
        CliOptions options,
        bool json)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        await CliCommandExecution.WriteCompletedResultAsync(
            result,
            options with { Json = json },
            stdout,
            stderr,
            new CliExecutionOptions(
                CliExecutionContext.OneShot,
                AnsiEnabled: false,
                ShowProgress: false));
        stderr.ToString().Should().BeEmpty();
        return stdout.ToString();
    }
}
