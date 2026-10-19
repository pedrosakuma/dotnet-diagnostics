using System.Text.Json;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

[Collection(nameof(CliStreamingProtocolTests))]
public sealed class LiveEventsIndependenceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task EventsStartAndStop_KeepIndependentCountersAndGcObservedAcrossBothBoundaries()
    {
        await using var target = await LiveSampleProcess.StartPublishedAsync(
            "CoreClrSample",
            new LiveSampleOptions
            {
                BindHttpPort = true,
                WaitForHttpReady = true,
                ReadinessPath = "/weatherforecast",
                HttpTimeout = TimeSpan.FromSeconds(10),
                DiagnosticTimeout = TimeSpan.FromSeconds(10),
            });
        using var http = new HttpClient { BaseAddress = new Uri(target.BaseUrl) };
        using var counters = CliStreamingProtocolTests.StartCliProcess();
        using var events = CliStreamingProtocolTests.StartCliProcess();
        using var loadCancellation = new CancellationTokenSource();
        using var pumpCancellation = new CancellationTokenSource();
        var load = Task.CompletedTask;
        var pump = Task.CompletedTask;
        var phase = 0;
        var phaseStartedTicks = DateTimeOffset.UtcNow.UtcTicks;
        var counts = new int[3, 2];
        var witnessed = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        string? countersId = null;

        try
        {
            foreach (var cli in new[] { counters, events })
            {
                await CliStreamingProtocolTests.WriteRequestAsync(cli, new { type = "hello", protocolVersion = 2 });
                using var hello = await CliStreamingProtocolTests.ReadFrameAsync(cli);
                hello.RootElement.GetProperty("type").GetString().Should().Be("hello");
            }
            await CliStreamingProtocolTests.WriteRequestAsync(counters, new
            {
                type = "start", requestId = "independent-counters", processId = target.ProcessId,
                kinds = new object[]
                {
                    new { kind = "counters", providers = new[] { "System.Runtime" }, intervalSeconds = 1 },
                    new { kind = "gc" },
                },
            });
            using (var started = await CliStreamingProtocolTests.ReadFrameAsync(counters))
            {
                started.RootElement.GetProperty("type").GetString().Should().Be("started");
                countersId = started.RootElement.GetProperty("sessionId").GetString();
            }
            pump = ObserveCountersAsync();
            load = Task.Run(async () =>
            {
                while (!loadCancellation.IsCancellationRequested)
                {
                    try
                    {
                        using var response = await http.GetAsync("/render?count=2000", loadCancellation.Token);
                        response.EnsureSuccessStatusCode();
                    }
                    catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested) { break; }
                }
            });
            await AwaitPhaseAsync(0);

            await CliStreamingProtocolTests.WriteRequestAsync(events, new
            {
                type = "start", requestId = "independent-events", processId = target.ProcessId,
                kinds = new[]
                {
                    new
                    {
                        kind = "event_source", observationCapacity = 32,
                        providers = new[] { new { name = "Microsoft-Windows-DotNETRuntime", eventLevel = 5, keywords = "-1" } },
                        acknowledgeRisk = "high",
                    },
                },
            });
            string? eventsId;
            using (var started = await CliStreamingProtocolTests.ReadFrameAsync(events))
            {
                started.RootElement.GetProperty("type").GetString().Should().Be("started");
                eventsId = started.RootElement.GetProperty("sessionId").GetString();
            }
            using (var observation = await CliStreamingProtocolTests.ReadUntilTypeAsync(events, "observation")
                .WaitAsync(TimeSpan.FromSeconds(10)))
            {
                observation.RootElement.GetProperty("kind").GetString().Should().Be("event_source");
                observation.RootElement.GetProperty("sessionId").GetString().Should().Be(eventsId);
            }
            BeginPhase(1);
            await AwaitPhaseAsync(1);
            await CliStreamingProtocolTests.WriteRequestAsync(events, new { type = "cancel", sessionId = eventsId });
            using (var terminal = await CliStreamingProtocolTests.ReadUntilTypeAsync(events, "terminal")
                .WaitAsync(TimeSpan.FromSeconds(10)))
                terminal.RootElement.GetProperty("status").GetString().Should().Be("stopped");
            await events.StandardInput.DisposeAsync();
            await events.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            events.ExitCode.Should().Be(0);

            BeginPhase(2);
            await AwaitPhaseAsync(2);
            counters.HasExited.Should().BeFalse("Events stop must not terminate the independent counters/GC child");
            await CliStreamingProtocolTests.WriteRequestAsync(counters, new { type = "cancel", sessionId = countersId });
            await pump.WaitAsync(TimeSpan.FromSeconds(10));
            await counters.StandardInput.DisposeAsync();
            await counters.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            counters.ExitCode.Should().Be(0);
            for (var index = 0; index < 3; index++)
            {
                counts[index, 0].Should().BePositive($"phase {index} must observe counters on the original session");
                counts[index, 1].Should().BePositive($"phase {index} must observe GC on the original session");
                output.WriteLine($"Phase {index}: counters={counts[index, 0]}, GC={counts[index, 1]}; original session={countersId}");
            }
        }
        finally
        {
            await pumpCancellation.CancelAsync();
            await loadCancellation.CancelAsync();
            try { await load.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally
            {
                foreach (var cli in new[] { events, counters })
                {
                    if (!cli.HasExited)
                    {
                        cli.Kill(entireProcessTree: true);
                        await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    cli.HasExited.Should().BeTrue("every owned CLI must be reaped even when a witness fails");
                }
                try { await pump; }
                catch (OperationCanceledException) when (pumpCancellation.IsCancellationRequested) { }
            }
        }

        void BeginPhase(int index)
        {
            Volatile.Write(ref phaseStartedTicks, DateTimeOffset.UtcNow.UtcTicks);
            Volatile.Write(ref phase, index);
        }

        async Task AwaitPhaseAsync(int index)
        {
            var winner = await Task.WhenAny(witnessed[index].Task, pump, load,
                Task.Delay(TimeSpan.FromSeconds(10)));
            if (winner == pump) await pump;
            if (winner == load) await load;
            witnessed[index].Task.IsCompletedSuccessfully.Should().BeTrue(
                $"phase {index} must observe counters and GC within 10s; "
                + $"counter={Volatile.Read(ref counts[index, 0])}, GC={Volatile.Read(ref counts[index, 1])}, "
                + $"counters exited={counters.HasExited}, Events exited={events.HasExited}");
        }

        async Task ObserveCountersAsync()
        {
            while (true)
            {
                var line = await counters.StandardOutput.ReadLineAsync(pumpCancellation.Token)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                line.Should().NotBeNull("the independent counters connection must remain readable");
                using var frame = JsonDocument.Parse(line!);
                var root = frame.RootElement;
                if (root.GetProperty("type").GetString() == "terminal")
                {
                    root.GetProperty("status").GetString().Should().Be("stopped");
                    Volatile.Read(ref phase).Should().Be(2);
                    return;
                }
                if (root.GetProperty("type").GetString() != "observation") continue;
                root.GetProperty("sessionId").GetString().Should().Be(countersId);
                var reportedKind = root.GetProperty("kind").GetString();
                reportedKind.Should().BeOneOf("counters", "gc");
                var kind = reportedKind == "counters" ? 0 : 1;
                var current = Volatile.Read(ref phase);
                if (root.GetProperty("timestamp").GetDateTimeOffset().UtcTicks
                    < Volatile.Read(ref phaseStartedTicks)) continue;
                Interlocked.Increment(ref counts[current, kind]);
                if (Volatile.Read(ref counts[current, 0]) > 0 && Volatile.Read(ref counts[current, 1]) > 0)
                    witnessed[current].TrySetResult();
            }
        }
    }
}
