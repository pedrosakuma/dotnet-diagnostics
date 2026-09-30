using System.Text;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class SamplerCaptureStackBudgetLiveTests(ITestOutputHelper output)
{
    [Fact(Timeout = 90_000)]
    public async Task EightSecondCpuReplay_PreservesEverySampleWithinOriginalLogicalAndSnapshotBudgets()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "cpu-stack-budget-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new CaptureStoreOptions();
            var store = new SqliteCaptureStore(new RootProvider(root), options);
            await using var writer = await store.CreateAsync(new("CPU stack budget"), new("cpu-budget-test"));
            var artifactId = writer.AddArtifact("cpu-sample", "CPU");
            var observer = new MeasuringSink(new SqliteCaptureObservationSink(writer, artifactId, "cpu-sample", "CPU", options));
            await using var sample = await LiveSampleProcess.StartPublishedAsync("CoreClrSample", new LiveSampleOptions
            {
                WaitForHttpReady = true, ReadinessPath = "/weatherforecast", DiagnosticTimeout = TimeSpan.FromSeconds(30),
            });
            using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
            using var scope = CaptureRecordingContext.Enter(observer);
            var capture = new EventPipeCpuSampler().SampleAsync(sample.ProcessId, TimeSpan.FromSeconds(8), topN: 10);
            var load = DriveAsync(http);
            await Task.WhenAll(capture, load);
            var result = await capture;
            var snapshot = CaptureArtifactCodec.Encode("cpu-sample", result.Artifact, options.MaxSnapshotBytes);
            var metrics = writer.GetMetrics();
            output.WriteLine($"CPU_BUDGET samples={result.Artifact.TotalSamples} offered={metrics.Quality.Offered} " +
                $"accepted={metrics.Quality.Accepted} recordRejected={metrics.Quality.RecordRejected} queueRejected={metrics.Quality.QueueRejected} " +
                $"storageRejected={metrics.Quality.StorageRejected} logicalBytes={metrics.LogicalBytes} snapshotBytes={snapshot.Length} " +
                $"offeredLogicalBytes={observer.OfferedLogicalBytes} offeredStackLogicalBytes={observer.StackLogicalBytes} " +
                $"stackRows={observer.StackRows} distinctStacks={observer.DistinctStacks} distinctStackBytes={observer.DistinctStackBytes} " +
                $"sourceRows={observer.SourceRows}");
            Assert.Equal(result.Artifact.TotalSamples, observer.SourceRows);
            Assert.Equal(0, metrics.Quality.StorageRejected);
            Assert.Equal(0, metrics.Quality.RecordRejected);
            Assert.Equal(0, metrics.Quality.QueueRejected);
            Assert.True(metrics.LogicalBytes + snapshot.Length < options.MaxLogicalBytes);
            writer.SetSnapshot(artifactId, CaptureArtifactCodec.FormatVersion, snapshot);
            writer.SetSourceRejected(observer.SourceLoss);
            var info = await writer.CompleteAsync();
            Assert.Equal(info.Quality.Offered, info.Quality.Persisted);
            Assert.Null(result.Artifact.TracePath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task DriveAsync(HttpClient http)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 6; i++)
        {
            using var response = await http.GetAsync("/cpu-burn?ms=400");
            response.EnsureSuccessStatusCode();
            await Task.Delay(100);
        }
    }

    private sealed record RootProvider(string Root) : IArtifactRootProvider;

    private sealed class MeasuringSink(IReplayCaptureObservationSink inner) : IReplayCaptureObservationSink
    {
        private readonly HashSet<string> _stacks = new(StringComparer.Ordinal);
        internal long OfferedLogicalBytes { get; private set; }
        internal long StackLogicalBytes { get; private set; }
        internal long StackRows { get; private set; }
        internal long DistinctStackBytes { get; private set; }
        internal int DistinctStacks => _stacks.Count;
        internal long? SourceLoss { get; private set; }
        internal long SourceRows { get; private set; }

        public ValueTask<bool> AppendReplayAsync(CaptureObservation observation, CancellationToken cancellationToken = default)
        {
            if (observation.Fields.Any(f => f.Name == "sourceOccurrence" && f.Boolean)) SourceRows++;
            OfferedLogicalBytes += 128 + TextBytes(observation.Category) + TextBytes(observation.Name);
            foreach (var field in observation.Fields)
            {
                OfferedLogicalBytes += 64 + TextBytes(field.Name) + TextBytes(field.Text);
                if (field.Name != "stack" || field.Text is not { } stack) continue;
                StackRows++;
                StackLogicalBytes += TextBytes(stack);
                if (_stacks.Count < 4096 && DistinctStackBytes + TextBytes(stack) <= 8 * 1024 * 1024 && _stacks.Add(stack))
                    DistinctStackBytes += TextBytes(stack);
            }
            return inner.AppendReplayAsync(observation, cancellationToken);
        }

        private static long TextBytes(string? value) => value is null ? 0 : 24 + value.Length * 2L + Encoding.UTF8.GetByteCount(value);
        public bool TryAppend(CaptureObservation observation) => inner.TryAppend(observation);
        public void ReportSourceLoss(string source, long? count)
        {
            SourceLoss = count;
            inner.ReportSourceLoss(source, count);
        }
        public void ArtifactRegistered(DiagnosticHandle handle, object artifact) => inner.ArtifactRegistered(handle, artifact);
    }
}
