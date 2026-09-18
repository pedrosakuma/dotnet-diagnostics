using System.Diagnostics;
using System.Text.Json;
using DotnetDiagnostics.Core.Networking;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class NetworkingCaptureQualityLiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("normal")]
    [InlineData("empty")]
    [InlineData("actual-early-exit")]
    [InlineData("injected-source-failure")]
    [InlineData("injected-payload-failure")]
    [InlineData("injected-transport-loss")]
    [InlineData("injected-unavailable-loss")]
    [InlineData("caller-cancellation")]
    public async Task RealSession_PreservesAcquisitionFacts(string scenario)
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
            "net10.0", "NetworkingCorrelationTarget.dll");
        Assert.True(File.Exists(target), target);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                ArgumentList = { target }, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false,
            },
        };
        Assert.True(process.Start());
        var errors = process.StandardError.ReadToEndAsync();
        Task<NetworkingSnapshot>? capture = null;
        try
        {
            output.WriteLine(await ReadLine());
            var starts = 0;
            var collector = new EventPipeNetworkingCollector
            {
                ConfigureSource = scenario == "injected-source-failure" ? source =>
                {
                    source.Dynamic.All += e =>
                    {
                        if (e.ProviderName == "System.Net.Http" && e.EventName is "RequestStart" or "Request/Start"
                            && ++starts == 3)
                            throw new IOException("Injected failure in actual source.Process callback.");
                    };
                } : null,
                BeforeParse = scenario == "injected-payload-failure" ? e =>
                {
                    if (e.ProviderName == "System.Net.Http" && e.EventName is "RequestStart" or "Request/Start"
                        && ++starts == 3)
                        throw new FormatException("Injected failure in actual payload parsing boundary.");
                } : null,
                TransformReportedLoss = scenario switch
                {
                    "injected-transport-loss" => loss => { Assert.Equal(0, loss); return 7; },
                    "injected-unavailable-loss" => loss => { Assert.Equal(0, loss); return null; },
                    _ => null,
                },
            };
            var duration = TimeSpan.FromSeconds(scenario == "actual-early-exit" ? 8 : 6);
            var elapsed = Stopwatch.StartNew();
            capture = collector.CollectAsync(process.Id, duration, cancellationToken: captureCancellation.Token);
            await process.StandardInput.WriteLineAsync("observe");
            Assert.Equal("OBSERVING", await ReadLine());
            if (scenario == "empty")
            {
                var snapshot = await capture;
                output.WriteLine($"empty (no workload requested): {JsonSerializer.Serialize(snapshot)}");
                Assert.Equal(0, snapshot.HttpRequestsStarted);
                Assert.Equal(0, snapshot.HttpRequestsStopped);
                Assert.Equal(0, snapshot.HttpRequestsFailed);
                Assert.Equal(0, snapshot.SocketConnectsStarted);
                Assert.Equal(0, snapshot.DnsLookupsStarted);
                Assert.Equal(0, snapshot.TlsHandshakesStarted);
                Assert.Empty(snapshot.ByOperation);
                Assert.All(snapshot.LatencyAvailability.Values, availability => Assert.Equal("not-observed", availability));
                var quality = Assert.IsType<NetworkingCaptureQuality>(snapshot.CaptureQuality);
                Assert.Equal("normal", quality.Completion);
                Assert.Equal(0, quality.EventsLost);
                Assert.Equal(0, quality.ParseErrors);
                Assert.False(quality.HasLimitations);
                Assert.NotEmpty(snapshot.Notes);
            }
            else if (scenario == "caller-cancellation")
            {
                await captureCancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), $"Cancellation took {elapsed.Elapsed}");
                capture = null;
                // A fresh session must work after cancellation; the target remains alive.
                capture = new EventPipeNetworkingCollector().CollectAsync(process.Id, TimeSpan.FromSeconds(2),
                    cancellationToken: token);
                var next = await capture;
                Assert.Equal("normal", next.CaptureQuality!.Completion);
                Assert.All(next.LatencyAvailability.Values, availability => Assert.Equal("not-observed", availability));
                Assert.Equal(0, next.Correlation!.Http.LatencySamples);
                Assert.Equal(0, next.Correlation.Http.QueueSamples);
            }
            else
            {
                await process.StandardInput.WriteLineAsync("capture");
                var measuredRequests = 0;
                while (await ReadLine() is { } line && line != "DONE")
                {
                    output.WriteLine(line);
                    using var json = JsonDocument.Parse(line);
                    if (json.RootElement.TryGetProperty("Path", out _)) measuredRequests++;
                }
                Assert.Equal(6, measuredRequests);
                if (scenario == "actual-early-exit")
                {
                    await process.StandardInput.WriteLineAsync("quit");
                    await process.WaitForExitAsync(token);
                    Assert.Equal(0, process.ExitCode);
                }
                var snapshot = await capture;
                output.WriteLine($"{scenario}: {JsonSerializer.Serialize(snapshot)}; wall={elapsed.Elapsed}");
                Assert.Equal(duration, snapshot.Duration);
                var quality = Assert.IsType<NetworkingCaptureQuality>(snapshot.CaptureQuality);
                Assert.NotNull(quality.StreamReadDuration);
                Assert.True(quality.StreamReadDuration > TimeSpan.Zero);
                Assert.Equal(scenario switch
                {
                    "actual-early-exit" => "early",
                    "injected-source-failure" => "source-failure",
                    _ => "normal",
                }, quality.Completion);
                Assert.Equal(scenario is "injected-source-failure" or "injected-unavailable-loss" ? null
                    : scenario == "injected-transport-loss" ? 7L : 0L, quality.EventsLost);
                Assert.Equal(scenario == "injected-payload-failure" ? 1 : 0, quality.ParseErrors);
                Assert.Equal(scenario != "normal", quality.HasLimitations);
                Assert.Equal(scenario == "injected-source-failure" ? 2 : scenario == "injected-payload-failure" ? 5 : 6,
                    snapshot.ByOperation.Sum(g => g.Count));
                Assert.Equal("measured", snapshot.LatencyAvailability["http"]);
                Assert.Equal(snapshot.Correlation!.Http.Paired, snapshot.Correlation.Http.PercentileSamples);
                Assert.All(snapshot.ByOperation, group =>
                {
                    Assert.Equal("measured", group.LatencyAvailability);
                    Assert.Equal(group.Count, group.PercentileSamples);
                });
                Assert.Equal(snapshot.HttpRequestsLeftQueue, snapshot.Correlation.Http.QueueSamples);
                Assert.Equal(0, snapshot.Correlation.Http.QueueRejectedSamples);
                Assert.Contains(snapshot.Notes, n => n.Contains($"completion={quality.Completion}", StringComparison.Ordinal));
                if (scenario is "actual-early-exit" or "injected-source-failure")
                    Assert.True(quality.StreamReadDuration < duration, JsonSerializer.Serialize(quality));
            }
            capture = null;
            if (!process.HasExited) await process.StandardInput.WriteLineAsync("quit");
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
                catch (Exception ex) { output.WriteLine($"Observed cleanup: {ex.GetType().Name}: {ex.Message}"); }
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
}
