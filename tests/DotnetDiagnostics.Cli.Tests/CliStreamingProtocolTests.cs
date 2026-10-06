using System.Diagnostics;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

[CollectionDefinition(nameof(CliStreamingProtocolTests), DisableParallelization = true)]
public sealed class CliStreamingProtocolTestCollection;

[Collection(nameof(CliStreamingProtocolTests))]
public sealed class CliStreamingProtocolTests
{
    [Fact(Timeout = 90_000)]
    public async Task ChildProcess_StreamsCounters_CancelsAndStopsOnEof()
    {
        await using var target = await LiveSampleProcess.StartPublishedAsync(
            "CoreClrSample",
            new LiveSampleOptions
            {
                BindHttpPort = true,
                HarvestListeningUrl = true,
                DiagnosticTimeout = TimeSpan.FromSeconds(30),
            });

        using var cli = StartCliProcess();
        var stderrTask = cli.StandardError.ReadToEndAsync();
        try
        {
            await WriteRequestAsync(cli, new { type = "hello", protocolVersion = 1 });
            using (var hello = await ReadFrameAsync(cli))
            {
                hello.RootElement.GetProperty("type").GetString().Should().Be("hello");
                hello.RootElement.GetProperty("protocolVersion").GetInt32().Should().Be(1);
            }

            await WriteRequestAsync(cli, new
            {
                type = "start",
                requestId = "first",
                processId = target.ProcessId,
                providers = new[] { "System.Runtime" },
                intervalSeconds = 1,
                observationCapacity = 32,
            });
            using var started = await ReadFrameAsync(cli);
            started.RootElement.GetProperty("type").GetString().Should().Be("started");
            var sessionId = started.RootElement.GetProperty("sessionId").GetString();
            sessionId.Should().NotBeNullOrWhiteSpace();

            using var observation = await ReadFrameAsync(cli);
            observation.RootElement.GetProperty("type").GetString()
                .Should().Be("observation", observation.RootElement.GetRawText());
            observation.RootElement.GetProperty("sequence").GetInt64().Should().BePositive();
            observation.RootElement.GetProperty("counter").GetProperty("provider").GetString()
                .Should().Be("System.Runtime");

            await WriteRequestAsync(cli, new { type = "cancel", sessionId });
            using (var terminal = await ReadUntilTypeAsync(cli, "terminal"))
            {
                terminal.RootElement.GetProperty("status").GetString().Should().Be("stopped");
                terminal.RootElement.GetProperty("droppedObservations").GetInt64().Should().BeGreaterThanOrEqualTo(0);
            }

            await WriteRequestAsync(cli, new
            {
                type = "start",
                requestId = "second",
                processId = target.ProcessId,
                providers = new[] { "System.Runtime" },
                intervalSeconds = 1,
            });
            using (var secondStarted = await ReadFrameAsync(cli))
            {
                secondStarted.RootElement.GetProperty("type").GetString().Should().Be("started");
                var secondSessionId = secondStarted.RootElement.GetProperty("sessionId").GetString();
                await WriteRequestAsync(cli, new { type = "stop", sessionId = secondSessionId });
            }

            using (var stopped = await ReadUntilTypeAsync(cli, "terminal"))
            {
                stopped.RootElement.GetProperty("status").GetString().Should().Be("stopped");
            }

            await WriteRequestAsync(cli, new
            {
                type = "start",
                requestId = "third",
                processId = target.ProcessId,
                providers = new[] { "System.Runtime" },
                intervalSeconds = 1,
            });
            using (var thirdStarted = await ReadFrameAsync(cli))
            {
                thirdStarted.RootElement.GetProperty("type").GetString().Should().Be("started");
            }

            await cli.StandardInput.DisposeAsync();
            using (var eofTerminal = await ReadUntilTypeAsync(cli, "terminal"))
            {
                eofTerminal.RootElement.GetProperty("status").GetString().Should().Be("stopped");
            }

            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            cli.ExitCode.Should().Be(0);
            (await stderrTask).Should().BeEmpty();
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

    internal static Process StartCliProcess()
    {
        var assemblyPath = typeof(CliHost).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("stream");
        startInfo.ArgumentList.Add("--protocol");
        startInfo.ArgumentList.Add("jsonl");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the CLI child process.");
    }

    internal static async Task WriteRequestAsync(Process process, object request)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        await process.StandardInput.FlushAsync();
    }

    internal static async Task<JsonDocument> ReadFrameAsync(Process process, TimeSpan? timeout = null)
    {
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(20));
        line.Should().NotBeNull("the streaming CLI should emit a protocol frame");
        return JsonDocument.Parse(line!);
    }

    internal static async Task<JsonDocument> ReadUntilTypeAsync(Process process, string expectedType)
    {
        while (true)
        {
            var frame = await ReadFrameAsync(process);
            if (frame.RootElement.GetProperty("type").GetString() == expectedType)
            {
                return frame;
            }

            frame.Dispose();
        }
    }
}
