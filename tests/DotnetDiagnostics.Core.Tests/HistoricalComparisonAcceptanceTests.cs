using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.UseCases;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class HistoricalComparisonAcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "historical-acceptance", Guid.NewGuid().ToString("N"));
    private static readonly CaptureAccess Owner = new("historical-acceptance");
    private sealed record RootProvider(string Root) : IArtifactRootProvider;
    private SqliteCaptureStore Store(string name) => new(new RootProvider(Path.Combine(_root, name)));
    private DurableCaptureUseCases Service(string name) => new(Store(name), new MemoryDiagnosticHandleStore(), new());
    private static PortableOperationKey Key() => new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

    [HistoricalAcceptanceFact]
    [Trait("Category", "HistoricalAcceptance")]
    public Task OwnedWorkloadsCompareOnlyAfterBothTargetsExit()
    {
        var diagnostics = new HttpReadinessDiagnostics();
        return HistoricalAcceptanceDiagnostics.RunAsync(() => OwnedWorkloadsCore(diagnostics), output.WriteLine, diagnostics);
    }

    internal static LiveSampleOptions OwnedSampleOptions(HttpReadinessDiagnostics? diagnostics = null) =>
        new() { WaitForHttpReady = true, ReadinessPath = "/weatherforecast", HttpDiagnostics = diagnostics };

    private async Task OwnedWorkloadsCore(HttpReadinessDiagnostics diagnostics)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_HISTORICAL_ACCEPTANCE"));
        var captures = new List<HistoricalCaptureReference>();
        var snapshots = new List<CounterSnapshot>();
        for (var side = 0; side < 2; side++)
        {
            LiveSampleProcess sample;
            try { sample = await LiveSampleProcess.StartPublishedAsync("CoreClrSample", OwnedSampleOptions(diagnostics)); }
            catch (SkipException exception) { throw new InvalidOperationException("Authorized acceptance requires the real sample.", exception); }
            await using (sample)
            {
                using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
                var collection = new EventPipeCounterCollector().CollectAsync(sample.ProcessId, TimeSpan.FromSeconds(8),
                    providers: ["System.Runtime"], intervalSeconds: 1);
                if (side == 1)
                {
                    await Task.Delay(1500);
                    using var load = await http.GetAsync("/cpu-burn?ms=4000");
                    load.EnsureSuccessStatusCode();
                }
                var snapshot = await collection;
                Assert.NotEmpty(snapshot.Counters);
                snapshots.Add(snapshot);
                captures.Add(await Persist("owned", snapshot));
                output.WriteLine("side={0}; pid={1}; retainedCounters={2}", side, sample.ProcessId, snapshot.Counters.Count);
            }
        }
        var result = await Service("owned").CompareHistoricalAsync(new(captures[0], captures[1]), Owner,
            static (_, _, _, _) => ValueTask.CompletedTask);
        Assert.Equal("qualified", result.Compatibility.Status);
        Assert.Contains(result.Metrics, metric => metric.AbsoluteDelta is not null);
        foreach (var metric in result.Metrics.Where(m => m.Key.EndsWith("/value", StringComparison.Ordinal) && m.AbsoluteDelta is not null))
            Assert.Equal(metric.RightValue - metric.LeftValue, metric.AbsoluteDelta);
        Assert.Equal(snapshots[0].StartedAt, result.Left.WindowStart);
        Assert.Equal(snapshots[1].StartedAt, result.Right.WindowStart);
        output.WriteLine("targetsDisposed=true; metrics={0}; result={1}", result.Metrics.Count,
            JsonSerializer.Serialize(result, HistoricalComparisonJsonContext.Default.HistoricalComparisonResult));
    }

    [HistoricalAcceptanceFact]
    [Trait("Category", "HistoricalAcceptance")]
    [Trait("Category", "PortableImportNative")]
    public Task DifferentBundlesCompareAfterSourceDeletionAndDestinationReopen() =>
        HistoricalAcceptanceDiagnostics.RunAsync(ImportedComparisonCore, output.WriteLine);

    private async Task ImportedComparisonCore()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_HISTORICAL_ACCEPTANCE"));
        var worker = new PortableCaptureImportWorker(
            Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_IMPORT_WORKER") ?? throw new InvalidOperationException("Explicit worker required."),
            Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_SQLITE_LIBRARY") ?? throw new InvalidOperationException("Explicit SQLite library required."));
        worker.Validate();
        var local = new List<HistoricalCaptureReference>();
        var bundleIds = new List<string>();
        foreach (var value in new[] { 10d, 15d })
        {
            var reference = await Persist("source", new(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(6),
                [new("Provider", "retained", "retained", value, CounterKind.Mean, "bytes")], [], []));
            using var bytes = new MemoryStream();
            var export = await new PortableCaptureUseCases(Store("source"), static (_, _) => ValueTask.CompletedTask)
                .ExportAsync(new(Key(), [new(reference.CaptureId, "label")]), bytes, Owner);
            bundleIds.Add(export.BundleId);
            var archive = bytes.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var import = await new PortableCaptureUseCases(Store("destination"), static (_, _) => ValueTask.CompletedTask,
                importWorker: worker).ImportAsync(new(Key(), archive.Length, hash), new MemoryStream(archive, false),
                Owner, static (_, _, _, _) => ValueTask.CompletedTask);
            Assert.True(import.Complete, JsonSerializer.Serialize(import));
            var map = Assert.Single(import.Entries).Mapping!;
            local.Add(new(map.LocalCaptureId, Assert.Single(map.Artifacts).LocalArtifactId));
            output.WriteLine("bundle={0}; archiveBytes={1}; sha256={2}; localCapture={3}", export.BundleId, archive.Length, hash, map.LocalCaptureId);
        }
        Assert.NotEqual(bundleIds[0], bundleIds[1]);
        Directory.Delete(Path.Combine(_root, "source"), recursive: true);
        var result = await Service("destination").CompareHistoricalAsync(new(local[0], local[1]), Owner,
            static (_, _, _, _) => ValueTask.CompletedTask);
        Assert.Equal(5m, Assert.Single(result.Metrics).AbsoluteDelta);
        Assert.NotNull(result.Left.ClaimedPortableSource);
        Assert.NotNull(result.Right.ClaimedPortableSource);
        Assert.NotEqual(result.Left.Reference.CaptureId, result.Left.ClaimedPortableSource.Origin.CaptureId);
        output.WriteLine("sourceDeleted=true; destinationReopened=true; delta=5; result={0}",
            JsonSerializer.Serialize(result, HistoricalComparisonJsonContext.Default.HistoricalComparisonResult));
    }

    private async Task<HistoricalCaptureReference> Persist(string name, CounterSnapshot snapshot)
    {
        var result = await Service(name).CaptureAsync("historical", "counters", Owner,
            _ => Task.FromResult(DiagnosticResult.Ok(snapshot, "retained")));
        Assert.Null(result.Error);
        return new(result.Capture!.CaptureId, Assert.Single(result.Capture.Artifacts).ArtifactId);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed class HistoricalAcceptanceFactAttribute : FactAttribute
    {
        public HistoricalAcceptanceFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_HISTORICAL_ACCEPTANCE") != "1")
                Skip = "Requires a separately authorized serialized live/native historical acceptance slot.";
        }
    }
}
