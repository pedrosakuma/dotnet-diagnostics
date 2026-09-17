using System.Diagnostics;
using System.Text.Json;
using DotnetDiagnostics.Core.Networking;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class NetworkingCorrelationLiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    [InlineData("net10.0")]
    public async Task ConcurrentPaths_MatchMeasuredClientScope(string framework)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(55));
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
                ArgumentList = { target }, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false,
            },
        };
        Assert.True(process.Start());
        var errors = process.StandardError.ReadToEndAsync(token);
        var tlsMeasurements = new List<double>();
        bool? trackerEnabled = null;
        Task<NetworkingSnapshot>? activeCapture = null;
        try
        {
            output.WriteLine(await ReadLine());
            await Workload("baseline");
            Assert.False(trackerEnabled);
            for (var session = 0; session < 2; session++)
            {
                activeCapture = new EventPipeNetworkingCollector().CollectAsync(process.Id, TimeSpan.FromSeconds(4),
                    cancellationToken: token);
                await process.StandardInput.WriteLineAsync("observe");
                Assert.Equal("OBSERVING", await ReadLine());
                var measured = await Workload($"session-{session}");
                var snapshot = await activeCapture;
                activeCapture = null;
                output.WriteLine(JsonSerializer.Serialize(snapshot));
                Assert.Equal(6, snapshot.HttpRequestsStarted);
                Assert.Equal(6, snapshot.HttpRequestsStopped);
                Assert.Equal(6, snapshot.ByOperation.Sum(g => g.Count));
                Assert.True(trackerEnabled);
                Assert.NotNull(snapshot.Correlation);
                Assert.Equal(6, snapshot.Correlation.Http.Paired);
                Assert.False(snapshot.Correlation.Http.HasLimitations);
                Assert.Equal(2, snapshot.TlsHandshakesStarted);
                Assert.Equal(2, snapshot.TlsHandshakesStopped);
                Assert.Equal(0, snapshot.TlsHandshakesFailed);
                if (snapshot.Correlation.Tls.AmbiguousStarts > 0)
                {
                    Assert.Equal(2, snapshot.Correlation.Tls.AmbiguousStarts);
                    Assert.Equal(0, snapshot.Correlation.Tls.Paired);
                    Assert.Equal(TimeSpan.Zero, snapshot.TlsMax);
                    Assert.Contains(snapshot.Notes, n => n.Contains("TLS correlation is limited", StringComparison.Ordinal));
                }
                else
                {
                    Assert.Equal(2, snapshot.Correlation.Tls.Paired);
                    Assert.InRange(Math.Abs(snapshot.TlsMax.TotalMilliseconds - tlsMeasurements.Max()),
                        0, 25 + tlsMeasurements.Max() * 0.10);
                }
                foreach (var (path, milliseconds) in measured)
                {
                    var group = Assert.Single(snapshot.ByOperation, g => g.Path == path);
                    Assert.Equal(1, group.Count);
                    // Both scopes end at response headers; allow scheduling around the EventSource callbacks,
                    // not configured server delays, to bound the independent Stopwatch comparison.
                    Assert.InRange(Math.Abs(group.TotalDuration.TotalMilliseconds - milliseconds), 0, 25 + milliseconds * 0.10);
                }
            }
            await Workload("after");
            Assert.True(trackerEnabled);
            await process.StandardInput.WriteLineAsync("quit");
            await process.WaitForExitAsync(token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (activeCapture is not null)
            {
                try { await activeCapture; }
                catch (Exception ex) { output.WriteLine($"Capture cleanup: {ex.GetType().Name}: {ex.Message}"); }
            }
            output.WriteLine($"Owned target {process.Id} exited: {process.HasExited}");
        }

        async Task<string> ReadLine()
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            Assert.NotNull(line);
            return line;
        }

        async Task<Dictionary<string, double>> Workload(string phase)
        {
            await process.StandardInput.WriteLineAsync(phase);
            tlsMeasurements.Clear();
            trackerEnabled = null;
            var measurements = new Dictionary<string, double>(StringComparer.Ordinal);
            while (await ReadLine() is { } line && line != "DONE")
            {
                output.WriteLine(line);
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("Path", out var path))
                    measurements.Add(path.GetString()!, json.RootElement.GetProperty("ElapsedMs").GetDouble());
                if (json.RootElement.TryGetProperty("TlsSide", out _))
                    tlsMeasurements.Add(json.RootElement.GetProperty("ElapsedMs").GetDouble());
                if (json.RootElement.TryGetProperty("ActivityTrackingEnabled", out var enabled))
                    trackerEnabled = enabled.GetBoolean();
            }
            Assert.Equal(6, measurements.Count);
            Assert.Equal(2, tlsMeasurements.Count);
            return measurements;
        }
    }
}
