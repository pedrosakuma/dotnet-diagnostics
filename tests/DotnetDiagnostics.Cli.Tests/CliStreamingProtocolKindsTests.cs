using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Coverage for the #1099 generalization of <see cref="CliStreamingProtocol"/>: multi-kind
/// <c>start</c> requests composed behind one <see cref="DotnetDiagnostics.Core.Counters.ComposedDiagnosticSession"/>,
/// and the new one-shot <c>capture</c> request/response. Validation-path cases run fully
/// in-process via <see cref="CliHost.RunAsync(string[], TextReader, TextWriter, TextWriter, CancellationToken, CliRuntimeOptions?)"/>
/// (no live process, no EventPipe session — they fail before any process/session is touched).
/// The concurrent-kinds and capture-round-trip cases need a genuine live target, so they spawn a
/// real CLI child process against <c>CoreClrSample</c>, mirroring <see cref="CliStreamingProtocolTests"/>.
/// </summary>
[Collection(nameof(CliStreamingProtocolTests))]
public sealed class CliStreamingProtocolKindsTests
{
    [Fact]
    public async Task Start_WithUnknownKind_ReturnsInvalidStartError()
    {
        var (frames, exit) = await RunInProcessAsync(
            new { type = "hello", protocolVersion = 1 },
            new
            {
                type = "start",
                requestId = "r1",
                processId = 999_999,
                kinds = new object[] { new { kind = "bogus" } },
            });

        exit.Should().Be(0);
        frames[0].RootElement.GetProperty("type").GetString().Should().Be("hello");
        frames[1].RootElement.GetProperty("type").GetString().Should().Be("error");
        frames[1].RootElement.GetProperty("code").GetString().Should().Be("invalid_start");
        frames[1].RootElement.GetProperty("message").GetString().Should().Contain("recognized 'kind'");
    }

    [Fact]
    public async Task Start_WithEmptyKindsArray_ReturnsInvalidStartError()
    {
        var (frames, _) = await RunInProcessAsync(
            new { type = "hello", protocolVersion = 1 },
            new { type = "start", requestId = "r1", processId = 999_999, kinds = Array.Empty<object>() });

        frames[1].RootElement.GetProperty("code").GetString().Should().Be("invalid_start");
        frames[1].RootElement.GetProperty("message").GetString().Should().Contain("non-empty array");
    }

    [Fact]
    public async Task Start_WithDuplicateKind_ReturnsInvalidStartError()
    {
        var (frames, _) = await RunInProcessAsync(
            new { type = "hello", protocolVersion = 1 },
            new
            {
                type = "start",
                requestId = "r1",
                processId = 999_999,
                kinds = new object[] { new { kind = "counters" }, new { kind = "counters" } },
            });

        frames[1].RootElement.GetProperty("code").GetString().Should().Be("invalid_start");
        frames[1].RootElement.GetProperty("message").GetString().Should().Contain("must not repeat kind 'counters'");
    }

    [Fact]
    public async Task Capture_WithUnsupportedKind_ReturnsUnsupportedCaptureKindError()
    {
        var (frames, _) = await RunInProcessAsync(
            new { type = "hello", protocolVersion = 1 },
            new { type = "capture", requestId = "c1", kind = "memory", processId = 999_999 });

        frames[1].RootElement.GetProperty("type").GetString().Should().Be("error");
        frames[1].RootElement.GetProperty("code").GetString().Should().Be("unsupported_capture_kind");
        frames[1].RootElement.GetProperty("requestId").GetString().Should().Be("c1");
    }

    [Fact]
    public async Task Capture_WithMissingProcessId_ReturnsInvalidCaptureError()
    {
        var (frames, _) = await RunInProcessAsync(
            new { type = "hello", protocolVersion = 1 },
            new { type = "capture", requestId = "c1", kind = "cpu" });

        frames[1].RootElement.GetProperty("code").GetString().Should().Be("invalid_capture");
        frames[1].RootElement.GetProperty("message").GetString().Should().Contain("processId");
    }

    [Fact]
    public async Task Capture_WithOutOfRangeDuration_ReturnsInvalidCaptureError()
    {
        var (frames, _) = await RunInProcessAsync(
            new { type = "hello", protocolVersion = 1 },
            new { type = "capture", requestId = "c1", kind = "cpu", processId = 999_999, durationSeconds = 10_000 });

        frames[1].RootElement.GetProperty("code").GetString().Should().Be("invalid_capture");
        frames[1].RootElement.GetProperty("message").GetString().Should().Contain("durationSeconds");
    }

    /// <summary>
    /// Runs the streaming protocol fully in-process (no child process) against a fixed sequence of
    /// request lines, feeding EOF immediately afterward so <see cref="CliStreamingProtocol.RunAsync"/>
    /// returns on its own. Returns every frame written to stdout in order, plus the exit code.
    /// </summary>
    private static async Task<(IReadOnlyList<JsonDocument> Frames, int ExitCode)> RunInProcessAsync(
        params object[] requests)
    {
        var input = string.Join('\n', requests.Select(request => JsonSerializer.Serialize(request))) + "\n";
        using var stdin = new StringReader(input);
        var stdoutBuilder = new StringBuilder();
        await using var stdout = new StringWriter(stdoutBuilder);
        var stderrBuilder = new StringBuilder();
        await using var stderr = new StringWriter(stderrBuilder);

        var exit = await CliHost.RunAsync(
            ["stream", "--protocol", "jsonl"],
            stdin,
            stdout,
            stderr,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        var frames = stdoutBuilder.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToList();
        return (frames, exit);
    }

    [Fact(Timeout = 120_000)]
    public async Task ChildProcess_StreamsCountersAndGcConcurrently_InOneComposedSession()
    {
        await using var target = await LiveSampleProcess.StartPublishedAsync(
            "CoreClrSample",
            new LiveSampleOptions
            {
                BindHttpPort = true,
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/weatherforecast",
                DiagnosticTimeout = TimeSpan.FromSeconds(30),
                HttpTimeout = TimeSpan.FromSeconds(60),
            });

        using var cli = CliStreamingProtocolTests.StartCliProcess();
        try
        {
            await CliStreamingProtocolTests.WriteRequestAsync(cli, new { type = "hello", protocolVersion = 1 });
            using (var hello = await CliStreamingProtocolTests.ReadFrameAsync(cli))
            {
                hello.RootElement.GetProperty("type").GetString().Should().Be("hello");
            }

            // Induce real GC collections concurrently with the composed session's lifetime.
            using var httpClient = new HttpClient { BaseAddress = new Uri(target.BaseUrl) };
            using var gcLoadCts = new CancellationTokenSource();
            var gcLoadTask = Task.Run(async () =>
            {
                while (!gcLoadCts.IsCancellationRequested)
                {
                    try
                    {
                        using var response = await httpClient.GetAsync("/render?count=2000", gcLoadCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (HttpRequestException)
                    {
                    }
                }
            }, gcLoadCts.Token);

            await CliStreamingProtocolTests.WriteRequestAsync(cli, new
            {
                type = "start",
                requestId = "multi",
                processId = target.ProcessId,
                kinds = new object[]
                {
                    new { kind = "counters", providers = new[] { "System.Runtime" }, intervalSeconds = 1 },
                    new { kind = "gc" },
                },
            });

            using var started = await CliStreamingProtocolTests.ReadFrameAsync(cli);
            started.RootElement.GetProperty("type").GetString().Should().Be("started");
            var sessionId = started.RootElement.GetProperty("sessionId").GetString();
            var startedKinds = started.RootElement.GetProperty("kinds").EnumerateArray()
                .Select(element => element.GetString()).ToArray();
            startedKinds.Should().BeEquivalentTo(["counters", "gc"]);

            var sawCounters = false;
            var sawGc = false;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((!sawCounters || !sawGc) && DateTime.UtcNow < deadline)
            {
                using var frame = await CliStreamingProtocolTests.ReadFrameAsync(cli);
                if (frame.RootElement.GetProperty("type").GetString() != "observation")
                {
                    continue;
                }

                frame.RootElement.GetProperty("sessionId").GetString().Should().Be(sessionId);
                var kind = frame.RootElement.GetProperty("kind").GetString();
                if (kind == "counters")
                {
                    sawCounters = true;
                }
                else if (kind == "gc")
                {
                    sawGc = true;
                }
            }

            sawCounters.Should().BeTrue("a counters observation should be forwarded from the composed session");
            sawGc.Should().BeTrue("a gc observation should be forwarded from the composed session");

            await gcLoadCts.CancelAsync();
            await gcLoadTask;

            await CliStreamingProtocolTests.WriteRequestAsync(cli, new { type = "cancel", sessionId });
            using var terminal = await CliStreamingProtocolTests.ReadUntilTypeAsync(cli, "terminal");
            terminal.RootElement.GetProperty("status").GetString().Should().Be("stopped");
            terminal.RootElement.GetProperty("kinds").EnumerateArray()
                .Select(element => element.GetString()).Should().BeEquivalentTo(["counters", "gc"]);

            await cli.StandardInput.DisposeAsync();
            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            cli.ExitCode.Should().Be(0);
        }
        finally
        {
            if (!cli.HasExited)
            {
                await cli.StandardInput.DisposeAsync();
                try
                {
                    await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    cli.Kill(entireProcessTree: true);
                    await cli.WaitForExitAsync();
                }
            }
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task ChildProcess_CapturesCpu_ReturnsPointInTimeSummary()
    {
        await using var target = await LiveSampleProcess.StartPublishedAsync(
            "CoreClrSample",
            new LiveSampleOptions
            {
                BindHttpPort = true,
                HarvestListeningUrl = true,
                DiagnosticTimeout = TimeSpan.FromSeconds(30),
            });

        using var cli = CliStreamingProtocolTests.StartCliProcess();
        try
        {
            await CliStreamingProtocolTests.WriteRequestAsync(cli, new { type = "hello", protocolVersion = 1 });
            using (var hello = await CliStreamingProtocolTests.ReadFrameAsync(cli))
            {
                hello.RootElement.GetProperty("type").GetString().Should().Be("hello");
            }

            await CliStreamingProtocolTests.WriteRequestAsync(cli, new
            {
                type = "capture",
                requestId = "cap1",
                kind = "cpu",
                processId = target.ProcessId,
                durationSeconds = 2,
                topN = 10,
            });

            using var capture = await CliStreamingProtocolTests.ReadFrameAsync(cli, timeout: TimeSpan.FromSeconds(30));
            capture.RootElement.GetProperty("type").GetString().Should().Be("capture", capture.RootElement.GetRawText());
            capture.RootElement.GetProperty("requestId").GetString().Should().Be("cap1");
            capture.RootElement.GetProperty("kind").GetString().Should().Be("cpu");
            capture.RootElement.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Object);

            await cli.StandardInput.DisposeAsync();
            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            cli.ExitCode.Should().Be(0);
        }
        finally
        {
            if (!cli.HasExited)
            {
                await cli.StandardInput.DisposeAsync();
                try
                {
                    await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    cli.Kill(entireProcessTree: true);
                    await cli.WaitForExitAsync();
                }
            }
        }
    }
}
