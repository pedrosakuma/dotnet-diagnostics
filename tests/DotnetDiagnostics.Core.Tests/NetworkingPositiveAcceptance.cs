using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

internal static class NetworkingPositiveAcceptance
{
    internal static async Task RunAsync(string framework, ITestOutputHelper output)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(55));
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
                ArgumentList = { target }, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false,
            },
        };
        Assert.True(process.Start());
        var errors = process.StandardError.ReadToEndAsync();
        var tlsMeasurements = new List<double>();
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal);
        bool? trackerEnabled = null;
        Task<DiagnosticResult<NetworkingSnapshot>>? activeCapture = null;
        try
        {
            output.WriteLine(await ReadLine());
            await Workload("baseline");
            Assert.False(trackerEnabled);
            for (var session = 0; session < 2; session++)
            {
                var handles = new MemoryDiagnosticHandleStore();
                activeCapture = DiagnosticTools.CollectNetworking(new EventPipeNetworkingCollector(),
                    new Resolver(process.Id), handles, process.Id, durationSeconds: 4,
                    depth: SamplingDepth.Detail, cancellationToken: captureCancellation.Token);
                await process.StandardInput.WriteLineAsync("observe");
                Assert.Equal("OBSERVING", await ReadLine());
                var measured = await Workload($"session-{session}");
                var collected = await activeCapture;
                activeCapture = null;
                Assert.Null(collected.Error);
                Assert.False(string.IsNullOrWhiteSpace(collected.Handle));
                var snapshot = Assert.IsType<NetworkingSnapshot>(collected.Data);
                output.WriteLine(JsonSerializer.Serialize(snapshot));
                Assert.Same(snapshot, handles.TryGet<NetworkingSnapshot>(collected.Handle!));
                Assert.Equal(6, snapshot.HttpRequestsStarted);
                Assert.Equal(6, snapshot.HttpRequestsStopped);
                Assert.Equal(0, snapshot.HttpRequestsFailed);
                Assert.Equal(6, snapshot.ByOperation.Sum(g => g.Count));
                Assert.True(trackerEnabled);
                Assert.NotNull(snapshot.Correlation);
                Assert.Equal(6, snapshot.Correlation.Http.Paired);
                Assert.Equal(2, snapshot.Correlation.Http.LatencyPopulationVersion);
                Assert.Equal(0, snapshot.Correlation.Http.PairedFailed);
                Assert.Equal(6, snapshot.Correlation.Http.PairedWithoutFailure);
                Assert.Equal(6, snapshot.Correlation.Http.PercentileSamples);
                Assert.False(snapshot.Correlation.Http.HasLimitations);
                Assert.Equal("measured", snapshot.LatencyAvailability["http"]);
                Assert.True(snapshot.HttpRequestP95 > TimeSpan.Zero);
                var quality = Assert.IsType<NetworkingCaptureQuality>(snapshot.CaptureQuality);
                Assert.Equal("normal", quality.Completion);
                Assert.Equal(0, quality.EventsLost);
                Assert.Equal(0, quality.ParseErrors);
                Assert.False(quality.HasLimitations);
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
                    Assert.Equal(1, group.PercentileSamples);
                    Assert.Equal(hosts[path], group.Host);
                    Assert.Equal("measured", group.LatencyAvailability);
                    Assert.True(group.TotalDuration > TimeSpan.Zero);
                    // Both scopes end at response headers; allow scheduling around the EventSource callbacks,
                    // not configured server delays, to bound the independent Stopwatch comparison.
                    Assert.InRange(Math.Abs(group.TotalDuration.TotalMilliseconds - milliseconds), 0, 25 + milliseconds * 0.10);
                }
                await AssertDrilldowns(handles, collected.Handle!, snapshot, token);
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
            await captureCancellation.CancelAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (activeCapture is not null)
            {
                try { await activeCapture; }
                catch (Exception ex) { output.WriteLine($"Capture cleanup: {ex.GetType().Name}: {ex.Message}"); }
            }
            output.WriteLine($"Owned target {process.Id} exited: {process.HasExited}; stderr={await errors}");
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
            hosts.Clear();
            trackerEnabled = null;
            var measurements = new Dictionary<string, double>(StringComparer.Ordinal);
            var intervals = new Dictionary<string, (long Start, long Stop)>(StringComparer.Ordinal);
            while (await ReadLine() is { } line && line != "DONE")
            {
                output.WriteLine(line);
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("Path", out var path))
                {
                    measurements.Add(path.GetString()!, json.RootElement.GetProperty("ElapsedMs").GetDouble());
                    hosts.Add(path.GetString()!, json.RootElement.GetProperty("Host").GetString()!);
                    intervals.Add(path.GetString()!, (json.RootElement.GetProperty("StartedTimestamp").GetInt64(),
                        json.RootElement.GetProperty("StoppedTimestamp").GetInt64()));
                }
                if (json.RootElement.TryGetProperty("TlsSide", out _))
                    tlsMeasurements.Add(json.RootElement.GetProperty("ElapsedMs").GetDouble());
                if (json.RootElement.TryGetProperty("ActivityTrackingEnabled", out var enabled))
                    trackerEnabled = enabled.GetBoolean();
            }
            Assert.Equal(6, measurements.Count);
            Assert.Equal(new[] { "/fast", "/medium", "/serial-fast", "/serial-medium", "/serial-slow", "/slow" },
                measurements.Keys.Order(StringComparer.Ordinal));
            Assert.All(measurements.Values, milliseconds => Assert.True(milliseconds > 0));
            Assert.All(intervals.Values, interval => Assert.True(interval.Stop > interval.Start));
            var concurrent = new[] { intervals["/fast"], intervals["/medium"], intervals["/slow"] };
            Assert.True(concurrent.Max(interval => interval.Start) < concurrent.Min(interval => interval.Stop),
                $"Concurrent requests did not overlap: {JsonSerializer.Serialize(measurements)}");
            // The oracle must distinguish the concurrent paths even on a busy host.
            // If scheduling erases the contrast, fail the fixture rather than accept wrong pairing.
            foreach (var (faster, slower) in new[] { ("/fast", "/medium"), ("/medium", "/slow") })
                Assert.True(measurements[slower] - measurements[faster]
                    > 50 + (measurements[slower] + measurements[faster]) * 0.10,
                    $"Timing witnesses overlap: {JsonSerializer.Serialize(measurements)}");
            Assert.Equal(2, tlsMeasurements.Count);
            return measurements;
        }
    }

    private static async Task AssertDrilldowns(
        MemoryDiagnosticHandleStore handles, string handle, NetworkingSnapshot snapshot, CancellationToken token)
    {
        foreach (var view in new[] { "summary", "byOperation", "queue", "tls", "dns" })
        {
            var drilled = await QuerySnapshotTool.QuerySnapshot(
                handles, new ClrMdDumpInspector(), new SensitiveDataRedactor(),
                new SensitiveValueGate(null), new RootPrincipalAccessor(),
                new DotnetDiagnostics.Core.Symbols.ClrMdNativeAddressResolver(),
                new DotnetDiagnostics.Core.Threads.ClrMdFrameVariableResolver(),
                handle, view: view, topN: 25, cancellationToken: token);
            Assert.Null(drilled.Error);
            var query = Assert.IsType<CollectionQueryResult>(drilled.Data);
            Assert.Equal(CollectionHandleKinds.NetworkingSnapshot, query.Kind);
            Assert.Equal(view, query.View);
            var json = JsonSerializer.SerializeToElement(query.Payload);
            Assert.Equal(JsonSerializer.Serialize(snapshot.Correlation), json.GetProperty("Correlation").GetRawText());
            Assert.Equal(JsonSerializer.Serialize(snapshot.CaptureQuality), json.GetProperty("CaptureQuality").GetRawText());
            Assert.Equal(JsonSerializer.Serialize(snapshot.LatencyAvailability), json.GetProperty("LatencyAvailability").GetRawText());
            switch (query.Payload)
            {
                case NetworkingSummaryView summary:
                    Assert.Equal(6, summary.HttpRequestsStarted);
                    Assert.Equal(6, summary.HttpRequestsStopped);
                    Assert.Equal(snapshot.HttpRequestP95, summary.HttpRequestP95);
                    break;
                case NetworkingByOperationView operations:
                    Assert.Equal(6, operations.TotalOperations);
                    Assert.Equal(snapshot.ByOperation, operations.ByOperation);
                    break;
                case NetworkingQueueView queue:
                    Assert.True(queue.RequestsLeftQueue > 0);
                    Assert.Equal(snapshot.Correlation!.Http.QueueSamples, queue.RequestsLeftQueue);
                    Assert.Equal(snapshot.TimeInQueueP95, queue.TimeInQueueP95);
                    break;
                case NetworkingTlsView tls:
                    Assert.Equal(2, tls.Started);
                    Assert.Equal(2, tls.Stopped);
                    Assert.Equal(snapshot.TlsP95, tls.P95);
                    break;
                case NetworkingDnsView dns:
                    Assert.Equal(0, dns.Started);
                    Assert.Equal("not-observed", dns.LatencyAvailability["dns"]);
                    break;
                default:
                    Assert.Fail($"Unexpected networking view: {query.Payload.GetType().Name}");
                    break;
            }
        }
    }

    private sealed class Resolver(int processId) : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(new(processId, RuntimeFlavor.CoreClr, true, true, false), null));
    }

    private sealed class RootPrincipalAccessor : IPrincipalAccessor
    {
        public BearerPrincipal? Current { get; } = new("test-root", ImmutableHashSet.Create(BearerPrincipal.RootScope));
    }
}
