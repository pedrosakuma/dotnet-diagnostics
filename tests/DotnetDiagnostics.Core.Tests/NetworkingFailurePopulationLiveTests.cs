using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core.Networking;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class NetworkingFailurePopulationLiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("net8.0", "failures-mixed", 4, 2, 2, 2, 1)]
    [InlineData("net8.0", "failures-only", 2, 2, 0, 0, 0)]
    [InlineData("net9.0", "failures-mixed", 4, 2, 2, 2, 1)]
    [InlineData("net9.0", "failures-only", 2, 2, 0, 0, 0)]
    [InlineData("net10.0", "failures-mixed", 4, 2, 2, 2, 1)]
    [InlineData("net10.0", "failures-only", 2, 2, 0, 0, 0)]
    public async Task FailedOperations_ContributeTerminalLatencySamples(
        string framework,
        string command,
        int expectedHttp,
        int expectedHttpFailed,
        int expectedResponseStops,
        int expectedStopsWithoutFailure,
        int expectedStatusErrors)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        var token = budget.Token;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DotnetDiagnostics.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var target = Path.Combine(root.FullName, "tests", "NetworkingCorrelationTarget", "bin", configuration,
            framework, "NetworkingCorrelationTarget.dll");
        Assert.True(File.Exists(target), target);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                ArgumentList = { target },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        Assert.True(process.Start());
        var errors = process.StandardError.ReadToEndAsync();
        Task<NetworkingSnapshot>? capture = null;
        try
        {
            output.WriteLine(await ReadLine());
            var lifecycle = new List<LifecycleEvent>();
            var lifecycleGate = new object();
            var sequence = 0;
            var collector = new EventPipeNetworkingCollector
            {
                ConfigureSource = source => source.Dynamic.All += traceEvent =>
                {
                    var kind = traceEvent.ProviderName switch
                    {
                        "System.Net.Http" when traceEvent.EventName is
                            "RequestStart" or "Request/Start" or
                            "RequestFailed" or "Request/Failed" or
                            "RequestStop" or "Request/Stop" => "http",
                        "System.Net.Security" when traceEvent.EventName is
                            "HandshakeStart" or "Handshake/Start" or
                            "HandshakeFailed" or "Handshake/Failed" or
                            "HandshakeStop" or "Handshake/Stop" => "tls",
                        _ => null,
                    };
                    if (kind is null) return;
                    var path = kind == "http" && traceEvent.EventName is "RequestStart" or "Request/Start"
                        ? Convert.ToString(traceEvent.PayloadByName("pathAndQuery"), CultureInfo.InvariantCulture)
                        : null;
                    lock (lifecycleGate)
                    {
                        if (lifecycle.Count < 100)
                            lifecycle.Add(new LifecycleEvent(
                                sequence++,
                                kind,
                                CanonicalName(traceEvent.EventName),
                                traceEvent.ActivityID,
                                path,
                                traceEvent.TimeStamp.ToUniversalTime()));
                    }
                },
            };
            capture = collector.CollectAsync(
                process.Id,
                TimeSpan.FromSeconds(6),
                cancellationToken: captureCancellation.Token);
            await process.StandardInput.WriteLineAsync("observe");
            Assert.Equal("OBSERVING", await ReadLine());
            await process.StandardInput.WriteLineAsync(command);
            var witnesses = new List<FailureWitness>();
            while (await ReadLine() is { } line && line != "DONE")
            {
                output.WriteLine(line);
                witnesses.Add(JsonSerializer.Deserialize<FailureWitness>(line)!);
            }

            var snapshot = await capture;
            capture = null;
            List<LifecycleEvent> raw;
            lock (lifecycleGate) raw = [.. lifecycle];
            output.WriteLine($"Snapshot: {JsonSerializer.Serialize(snapshot)}");
            output.WriteLine($"Raw lifecycle: {JsonSerializer.Serialize(raw)}");

            AssertWitnesses(command, witnesses);
            Assert.Equal(expectedHttp, snapshot.HttpRequestsStarted);
            Assert.Equal(expectedHttp, snapshot.HttpRequestsStopped);
            Assert.Equal(expectedHttpFailed, snapshot.HttpRequestsFailed);
            var http = Assert.IsType<NetworkingCorrelationCounts>(snapshot.Correlation?.Http);
            AssertCompletePopulation(
                http,
                expectedHttp,
                expectedHttpFailed,
                expectedStopsWithoutFailure);
            Assert.Equal(expectedResponseStops, http.HttpResponseStops);
            Assert.Equal(expectedStatusErrors, http.HttpStatusErrorStops);
            Assert.Equal(expectedHttpFailed, http.HttpStopsWithoutStatus);

            Assert.Equal(1, snapshot.TlsHandshakesStarted);
            Assert.Equal(1, snapshot.TlsHandshakesStopped);
            Assert.Equal(1, snapshot.TlsHandshakesFailed);
            var tls = Assert.IsType<NetworkingCorrelationCounts>(snapshot.Correlation?.Tls);
            AssertCompletePopulation(tls, 1, 1, 0);
            Assert.Equal("measured", snapshot.LatencyAvailability["http"]);
            Assert.Equal("measured", snapshot.LatencyAvailability["tls"]);
            Assert.Equal(expectedHttp, http.PercentileSamples);
            Assert.Equal(1, tls.PercentileSamples);

            Assert.Equal(expectedHttp, snapshot.ByOperation.Sum(static group => group.Count));
            foreach (var witness in witnesses.Where(static witness => witness.Kind == "http"))
            {
                var group = Assert.Single(snapshot.ByOperation, group => group.Path == witness.Path);
                Assert.Equal(1, group.Count);
                Assert.Equal(1, group.PercentileSamples);
                Assert.Equal("measured", group.LatencyAvailability);
                Assert.True(group.TotalDuration > TimeSpan.Zero);
                Assert.InRange(
                    Math.Abs(group.TotalDuration.TotalMilliseconds - witness.ElapsedMs),
                    0,
                    25 + witness.ElapsedMs * 0.10);
            }
            var tlsWitness = Assert.Single(witnesses, static witness => witness.Kind == "tls");
            Assert.True(snapshot.TlsP95 > TimeSpan.Zero);
            Assert.True(snapshot.TlsMax > TimeSpan.Zero);
            Assert.InRange(
                Math.Abs(snapshot.TlsMax.TotalMilliseconds - tlsWitness.ElapsedMs),
                0,
                25 + tlsWitness.ElapsedMs * 0.10);
            if (command == "failures-only")
            {
                Assert.True(snapshot.HttpRequestP95 > TimeSpan.Zero);
                Assert.True(snapshot.HttpRequestMax > TimeSpan.Zero);
            }

            AssertRawLifecycle(raw, command);
            var quality = Assert.IsType<NetworkingCaptureQuality>(snapshot.CaptureQuality);
            Assert.Equal("normal", quality.Completion);
            Assert.Equal(0, quality.EventsLost);
            Assert.Equal(0, quality.ParseErrors);
            Assert.False(quality.HasLimitations);

            await process.StandardInput.WriteLineAsync("quit");
            await process.WaitForExitAsync(token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await errors);
        }
        finally
        {
            await captureCancellation.CancelAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (capture is not null)
            {
                try { await capture; }
                catch (Exception ex) { output.WriteLine($"Capture cleanup: {ex.GetType().Name}: {ex.Message}"); }
            }
            output.WriteLine($"Owned target {process.Id} exited={process.HasExited}; stderr={await errors}");
        }

        async Task<string> ReadLine()
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            Assert.NotNull(line);
            return line;
        }
    }

    private static void AssertCompletePopulation(
        NetworkingCorrelationCounts counts,
        long paired,
        long pairedFailed,
        long pairedWithoutFailure)
    {
        Assert.Equal(2, counts.LatencyPopulationVersion);
        Assert.Equal(paired, counts.Started);
        Assert.Equal(paired, counts.Paired);
        Assert.Equal(pairedFailed, counts.PairedFailed);
        Assert.Equal(pairedWithoutFailure, counts.PairedWithoutFailure);
        Assert.Equal(pairedFailed, counts.MatchedFailureEvents);
        Assert.Equal(0, counts.RepeatedFailureEvents);
        Assert.Equal(0, counts.InvalidTimestampFailures);
        Assert.Equal(0, counts.UnfinishedFailed);
        Assert.Equal(0, counts.Unfinished);
        Assert.Equal(0, counts.UnmatchedFailures);
        Assert.Equal(0, counts.UnmatchedStops);
        Assert.False(counts.HasLimitations);
    }

    private static void AssertWitnesses(string command, IReadOnlyList<FailureWitness> witnesses)
    {
        var expectedHttp = command == "failures-mixed"
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/success-200"] = "status-200",
                ["/status-503"] = "status-503",
                ["/cancelled"] = "cancelled",
                ["/timed-out"] = "timed-out",
            }
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/cancelled"] = "cancelled",
                ["/timed-out"] = "timed-out",
            };
        var http = witnesses.Where(static witness => witness.Kind == "http").ToList();
        Assert.Equal(expectedHttp.Count, http.Count);
        foreach (var witness in http)
        {
            Assert.NotNull(witness.Path);
            Assert.Equal(expectedHttp[witness.Path], witness.Outcome);
            Assert.True(witness.ElapsedMs > 0);
        }
        Assert.InRange(Assert.Single(http, static witness => witness.Path == "/cancelled").ElapsedMs, 150, 600);
        Assert.InRange(Assert.Single(http, static witness => witness.Path == "/timed-out").ElapsedMs, 250, 700);
        var tls = Assert.Single(witnesses, static witness => witness.Kind == "tls");
        Assert.Equal("authentication-failed", tls.Outcome);
        Assert.Contains(
            tls.Exception,
            new[] { "System.Security.Authentication.AuthenticationException", "System.IO.IOException" });
        Assert.InRange(tls.ElapsedMs, 150, 1000);
    }

    private static void AssertRawLifecycle(IReadOnlyList<LifecycleEvent> events, string command)
    {
        var expectedHttpPaths = command == "failures-mixed"
            ? new[] { "/success-200", "/status-503", "/cancelled", "/timed-out" }
            : new[] { "/cancelled", "/timed-out" };
        var httpStarts = events.Where(static item => item.Kind == "http" && item.Name == "Start").ToList();
        Assert.Equal(expectedHttpPaths.Length, httpStarts.Count);
        foreach (var path in expectedHttpPaths)
        {
            var start = Assert.Single(httpStarts, item => item.Path == path);
            Assert.NotEqual(Guid.Empty, start.ActivityId);
            var lifecycle = events.Where(item => item.Kind == "http" && item.ActivityId == start.ActivityId)
                .OrderBy(static item => item.Sequence)
                .ToList();
            var expected = path is "/cancelled" or "/timed-out"
                ? new[] { "Start", "Failed", "Stop" }
                : new[] { "Start", "Stop" };
            Assert.Equal(expected, lifecycle.Select(static item => item.Name));
            Assert.True(lifecycle.Zip(lifecycle.Skip(1), static (left, right) => left.Timestamp <= right.Timestamp)
                .All(static ordered => ordered));
        }

        var status503 = httpStarts.SingleOrDefault(static item => item.Path == "/status-503");
        if (status503 is not null)
            Assert.DoesNotContain(events, item => item.ActivityId == status503.ActivityId && item.Name == "Failed");

        var tlsStart = Assert.Single(events, static item => item.Kind == "tls" && item.Name == "Start");
        var tlsLifecycle = events.Where(item => item.Kind == "tls" && item.ActivityId == tlsStart.ActivityId)
            .OrderBy(static item => item.Sequence)
            .Select(static item => item.Name);
        Assert.Equal(new[] { "Start", "Failed", "Stop" }, tlsLifecycle);
    }

    private static string CanonicalName(string eventName) => eventName switch
    {
        "RequestStart" or "Request/Start" or "HandshakeStart" or "Handshake/Start" => "Start",
        "RequestFailed" or "Request/Failed" or "HandshakeFailed" or "Handshake/Failed" => "Failed",
        "RequestStop" or "Request/Stop" or "HandshakeStop" or "Handshake/Stop" => "Stop",
        _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null),
    };

    private sealed record FailureWitness(
        string Kind,
        string? Path,
        string Outcome,
        double ElapsedMs,
        string? Exception);

    private sealed record LifecycleEvent(
        int Sequence,
        string Kind,
        string Name,
        Guid ActivityId,
        string? Path,
        DateTime Timestamp);
}
