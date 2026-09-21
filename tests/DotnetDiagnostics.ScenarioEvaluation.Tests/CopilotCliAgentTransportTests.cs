using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class CopilotCliAgentTransportTests
{
    [Fact]
    public async Task Reader_SeparatesLargeKnownFramingFromSmallAssistantPayload()
    {
        const string payload = """{"observations":[],"uncertainty":"small"}""";
        var output = Event(
            "session.start",
            new
            {
                version = 1,
                producer = "copilot-agent",
                copilotVersion = "1.0.86",
                sessionId = "session",
                startTime = "2026-09-21T00:00:00Z",
                context = new { cwd = "/isolated" },
            })
            + "\n"
            + Event("user.message", new { content = new string('p', 96_000) })
            + "\n"
            + Event(
                "assistant.reasoning",
                new { reasoningId = "reasoning", content = new string('r', 96_000) })
            + "\n"
            + Event("assistant.message", new { messageId = "answer", content = payload, toolRequests = Array.Empty<object>() })
            + "\n"
            + JsonSerializer.Serialize(new
            {
                type = "result",
                timestamp = "2026-09-21T00:00:01Z",
                sessionId = "session",
                exitCode = 0,
                usage = new
                {
                    premiumRequests = 1,
                    totalApiDurationMs = 10,
                    sessionDurationMs = 20,
                    codeChanges = new { linesAdded = 0, linesRemoved = 0, filesModified = 0 },
                },
            })
            + "\n";

        var result = await ReadAsync(output, maximumResponseBytes: 256);

        result.AssistantContent.Should().Be(payload);
        result.FramingBytes.Should().BeGreaterThan(190_000);
        Encoding.UTF8.GetByteCount(result.AssistantContent).Should().BeLessThan(256);
    }

    [Fact]
    public async Task Reader_AcceptsSourceDerivedToolFreePromptLifecycleEvents()
    {
        string[] eventTypes =
        [
            "session.start",
            "session.custom_agents_updated",
            "session.extensions_loaded",
            "session.skills_loaded",
            "session.mcp_servers_loaded",
            "session.mcp_server_status_changed",
            "mcp.tools.list_changed",
            "mcp.resources.list_changed",
            "mcp.prompts.list_changed",
            "commands.changed",
            "capabilities.changed",
            "session.tools_updated",
            "user.message",
            "assistant.turn_start",
            "model.call_start",
            "assistant.message_start",
            "model.call_failure",
            "assistant.reasoning_delta",
            "assistant.reasoning",
            "assistant.message_delta",
            "model.call_finished",
            "assistant.turn_end",
            "assistant.idle",
            "session.info",
            "session.warning",
        ];
        var output = string.Join(
            '\n',
            eventTypes.Select(type => Event(type, new { sourceEvidence = "cli-1.0.86-and-1.0.87" })))
            + "\n"
            + AssistantEvent("{}", "answer")
            + "\n"
            + JsonSerializer.Serialize(new { type = "result", exitCode = 0 });

        var result = await ReadAsync(output, maximumResponseBytes: 64);

        result.AssistantContent.Should().Be("{}");
    }

    [Theory]
    [InlineData("session.idle")]
    [InlineData("session.shutdown")]
    [InlineData("session.model_change")]
    [InlineData("session.usage_info")]
    [InlineData("assistant.intent")]
    [InlineData("assistant.usage")]
    [InlineData("system.message")]
    public async Task Reader_FailsClosedIfWriterExcludedLifecycleEventUnexpectedlyAppears(
        string eventType)
    {
        var output = Event(eventType, new { sourceEvidence = "writer-excluded" })
            + "\n"
            + AssistantEvent("{}", "answer");

        var action = () => ReadAsync(output, maximumResponseBytes: 64);

        await action.Should().ThrowAsync<JsonException>()
            .WithMessage($"*unsupported event type '{eventType}'*");
    }

    [Fact]
    public async Task Reader_RejectsOversizedAssistantPayloadWithObservedCountAndNamedCap()
    {
        var output = Event(
            "assistant.message",
            new { messageId = "answer", content = new string('x', 101), toolRequests = Array.Empty<object>() });

        var action = () => ReadAsync(output, maximumResponseBytes: 100);

        await action.Should().ThrowAsync<AgentTransportException>()
            .WithMessage("*assistant payload*MaximumResponseBytes=100*observed 101*");
    }

    [Fact]
    public async Task Reader_RejectsOversizedAssistantEventEnvelope()
    {
        var output = Event(
            "assistant.message",
            new
            {
                messageId = "answer",
                content = "{}",
                reasoningText = new string('r', CopilotCliAgentTransport.MaximumCliEventEnvelopeBytes),
                toolRequests = Array.Empty<object>(),
            });

        var action = () => ReadAsync(output, maximumResponseBytes: 100);

        await action.Should().ThrowAsync<AgentTransportException>()
            .WithMessage(
                $"*MaximumCliEventEnvelopeBytes={CopilotCliAgentTransport.MaximumCliEventEnvelopeBytes}*observed*");
    }

    [Fact]
    public async Task Reader_RejectsCumulativeKnownFramingWithObservedCountAndNamedCap()
    {
        var eventLine = Event("user.message", new { content = new string('p', 220_000) }) + "\n";
        var output = string.Concat(Enumerable.Repeat(eventLine, 5))
            + Event("assistant.message", new { messageId = "answer", content = "{}", toolRequests = Array.Empty<object>() });

        var action = () => ReadAsync(output, maximumResponseBytes: 64);

        await action.Should().ThrowAsync<AgentTransportException>()
            .WithMessage(
                $"*framing*MaximumCliFramingBytes={CopilotCliAgentTransport.MaximumCliFramingBytes}*observed*");
    }

    [Fact]
    public async Task Reader_CountsLfDelimitedBlankLinesAtTheExactFramingBoundary()
    {
        const string payload = "{}";
        var answer = AssistantEvent(payload, "answer");
        var answerEnvelopeBytes = Encoding.UTF8.GetByteCount(answer)
            - Encoding.UTF8.GetByteCount(payload);
        var delimiterCount = CopilotCliAgentTransport.MaximumCliFramingBytes - answerEnvelopeBytes;
        var exactOutput = answer + new string('\n', delimiterCount);

        var result = await ReadAsync(exactOutput, maximumResponseBytes: 64);

        result.FramingBytes.Should().Be(CopilotCliAgentTransport.MaximumCliFramingBytes);
        var overflow = () => ReadAsync(exactOutput + "\n", maximumResponseBytes: 64);
        await overflow.Should().ThrowAsync<AgentTransportException>()
            .WithMessage(
                $"*MaximumCliFramingBytes={CopilotCliAgentTransport.MaximumCliFramingBytes}*"
                + $"observed {CopilotCliAgentTransport.MaximumCliFramingBytes + 1}*");
    }

    [Fact]
    public async Task Reader_CountsCrLfDelimitedBlankLinesAtTheExactFramingBoundary()
    {
        const string payload = "{}";
        var messageId = "answer";
        var answer = AssistantEvent(payload, messageId);
        var answerEnvelopeBytes = Encoding.UTF8.GetByteCount(answer)
            - Encoding.UTF8.GetByteCount(payload);
        if ((CopilotCliAgentTransport.MaximumCliFramingBytes - answerEnvelopeBytes) % 2 != 0)
        {
            answer = AssistantEvent(payload, messageId + "x");
            answerEnvelopeBytes++;
        }

        var delimiterCount = (
            CopilotCliAgentTransport.MaximumCliFramingBytes - answerEnvelopeBytes) / 2;
        var exactOutput = answer + string.Concat(Enumerable.Repeat("\r\n", delimiterCount));

        var result = await ReadAsync(exactOutput, maximumResponseBytes: 64);

        result.FramingBytes.Should().Be(CopilotCliAgentTransport.MaximumCliFramingBytes);
        var overflow = () => ReadAsync(exactOutput + "\r\n", maximumResponseBytes: 64);
        await overflow.Should().ThrowAsync<AgentTransportException>()
            .WithMessage(
                $"*MaximumCliFramingBytes={CopilotCliAgentTransport.MaximumCliFramingBytes}*"
                + $"observed {CopilotCliAgentTransport.MaximumCliFramingBytes + 2}*");
    }

    [Fact]
    public async Task Reader_RejectsSingleOversizedEventBeforeBufferingTheRemainder()
    {
        var output = Event("user.message", new { content = new string('p', 300_000) });

        var action = () => ReadAsync(output, maximumResponseBytes: 64);

        await action.Should().ThrowAsync<AgentTransportException>()
            .WithMessage("*stdout event*MaximumCliEventBytes=*observed at least*");
    }

    [Fact]
    public async Task Reader_RejectsMalformedDuplicateUnknownAndToolEvents()
    {
        var validAnswer = Event(
            "assistant.message",
            new { messageId = "answer", content = "{}", toolRequests = Array.Empty<object>() });
        var cases = new Dictionary<string, string>
        {
            ["malformed"] = """{"type":"session.start","data":""",
            ["duplicate"] = validAnswer + "\n" + validAnswer,
            ["unknown"] = Event("future.unreviewed", new { harmlessLooking = true }),
            ["tool-after-answer"] = validAnswer + "\n" + Event(
                "tool.execution_start",
                new { toolCallId = "call", toolName = "shell" }),
            ["nested-tool-request"] = Event(
                "session.info",
                new { nested = new { toolRequests = new[] { new { name = "shell" } } } }),
            ["unknown-tool-field"] = Event(
                "session.info",
                new { nested = new { toolCallId = "hidden-call" } }),
        };

        foreach (var (name, output) in cases)
        {
            var action = () => ReadAsync(output, maximumResponseBytes: 100);
            await action.Should().ThrowAsync<JsonException>(name);
        }
    }

    [Fact]
    public async Task Invocation_StderrOverflowKillsAndReapsOwnedProcessPromptly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = new SyntheticCliFiles(
            """
            dd if=/dev/zero bs=40000 count=1 2>/dev/null | tr '\000' x >&2
            sleep 30
            """);
        var transport = new CopilotCliAgentTransport(
            files.Executable,
            files.Home,
            files.Work,
            enforceOutsideRepository: false);
        var stopwatch = Stopwatch.StartNew();

        var action = () => transport.CompleteStructuredJsonAsync(
            Configuration(maximumResponseBytes: 128),
            "{}",
            CancellationToken.None);

        await action.Should().ThrowAsync<AgentTransportException>()
            .WithMessage(
                $"*stderr*MaximumCliStderrBytes={CopilotCliAgentTransport.MaximumCliStderrBytes}*");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        await files.AssertRecordedProcessExitedAsync();
    }

    [Fact]
    public async Task Invocation_CancellationKillsAndReapsOwnedProcessPromptly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = new SyntheticCliFiles("sleep 30");
        var transport = new CopilotCliAgentTransport(
            files.Executable,
            files.Home,
            files.Work,
            enforceOutsideRepository: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();

        var action = () => transport.CompleteStructuredJsonAsync(
            Configuration(maximumResponseBytes: 128),
            "{}",
            cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        await files.AssertRecordedProcessExitedAsync();
    }

    private static AgentModelConfiguration Configuration(int maximumResponseBytes)
        => new(
            "github-copilot-cli",
            "synthetic-model",
            new Uri("copilot-cli://local-process"),
            0,
            100,
            MaximumResponseBytes: maximumResponseBytes);

    private static async Task<CopilotCliAgentTransport.CliOutputReadResult> ReadAsync(
        string output,
        int maximumResponseBytes)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(output));
        return await CopilotCliAgentTransport.ReadCliOutputAsync(
            stream,
            maximumResponseBytes,
            CancellationToken.None);
    }

    private static string Event(string type, object data)
        => JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid(),
            timestamp = "2026-09-21T00:00:00Z",
            parentId = (string?)null,
            type,
            data,
        });

    private static string AssistantEvent(string content, string messageId)
        => Event(
            "assistant.message",
            new { messageId, content, toolRequests = Array.Empty<object>() });

    private sealed class SyntheticCliFiles : IDisposable
    {
        private readonly string _root;
        private readonly string _pidPath;

        [UnsupportedOSPlatform("windows")]
        public SyntheticCliFiles(string invocationBody)
        {
            _root = Path.Combine(
                AppContext.BaseDirectory,
                "dotnet-diagnostics-synthetic-copilot-" + Guid.NewGuid().ToString("n"));
            Home = Path.Combine(_root, "home");
            Work = Path.Combine(_root, "work");
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(Work);
            _pidPath = Path.Combine(_root, "owned.pid");
            Executable = Path.Combine(_root, "copilot");
            File.WriteAllText(
                Executable,
                "#!/bin/sh\n"
                + "case \" $* \" in\n"
                + "  *\" plugin\"*\" list \"*|*\" plugins\"*\" list \"*) printf '[]\\n'; exit 0;;\n"
                + "esac\n"
                + $"printf '%s\\n' \"$$\" > '{_pidPath}'\n"
                + invocationBody
                + "\n");
            File.SetUnixFileMode(
                Executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Executable { get; }

        public string Home { get; }

        public string Work { get; }

        public async Task AssertRecordedProcessExitedAsync()
        {
            for (var attempt = 0; attempt < 50 && !File.Exists(_pidPath); attempt++)
            {
                await Task.Delay(20);
            }

            File.Exists(_pidPath).Should().BeTrue();
            var processId = int.Parse(File.ReadAllText(_pidPath), System.Globalization.CultureInfo.InvariantCulture);
            var processExists = true;
            try
            {
                using var process = Process.GetProcessById(processId);
                processExists = !process.HasExited;
            }
            catch (ArgumentException)
            {
                processExists = false;
            }

            processExists.Should().BeFalse("the transport owns and must reap its CLI process");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
