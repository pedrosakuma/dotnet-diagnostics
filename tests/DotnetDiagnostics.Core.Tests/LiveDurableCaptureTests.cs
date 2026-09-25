using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class LiveDurableCaptureTests : IDisposable
{
    private static readonly CaptureAccess Owner = new("live-durable-test");
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, "live-capture-tests", Guid.NewGuid().ToString("N"));
    private readonly CaptureStoreOptions _options = new();

    [Fact(Timeout = 90_000)]
    public async Task Counters_PreserveIntervalsAfterTargetExitAndFreshStoreOpen()
    {
        await using var sample = await StartAsync("CoreClrSample");
        var handles = new MemoryDiagnosticHandleStore();
        var result = await CaptureAsync(
            handles, sample.ProcessId, CollectionHandleKinds.Counters,
            ct => new EventPipeCounterCollector().CollectAsync(
                sample.ProcessId, TimeSpan.FromSeconds(8),
                providers: ["System.Runtime"], intervalSeconds: 1, cancellationToken: ct));

        Assert.False(result.IsError, result.Error?.Message);
        var snapshot = Assert.IsType<CounterSnapshot>(result.Data);
        Assert.NotEmpty(snapshot.Counters);
        await sample.DisposeAsync();
        Assert.True(handles.Invalidate(result.Handle!));

        var (restored, records) = await ReadOfflineAsync<CounterSnapshot>(result, CollectionHandleKinds.Counters);
        Assert.Equal(snapshot.Counters.Count, restored.Counters.Count);
        Assert.True(records.Count > snapshot.Counters.Count,
            "Normalized occurrences must preserve intervals, not merely serialize the last-value projection.");
        Assert.Contains(records, r => r.Record.Name == "cpu-usage");
    }

    [Fact(Timeout = 90_000)]
    public async Task Activities_PreserveMoreOccurrencesThanRetainedSnapshotAfterTargetExit()
    {
        await using var sample = await StartAsync("CoreClrSample");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var handles = new MemoryDiagnosticHandleStore();
        var capture = CaptureAsync(
            handles, sample.ProcessId, CollectionHandleKinds.Activities,
            ct => new EventPipeActivityCollector().CollectAsync(
                sample.ProcessId, TimeSpan.FromSeconds(8),
                sources: ["CoreClrSample.Activities"], maxActivities: 4, cancellationToken: ct));
        var load = DriveAsync(http, "/activity?delayMs=10", 12);
        await Task.WhenAll(capture, load);
        var result = await capture;

        Assert.False(result.IsError, result.Error?.Message);
        var snapshot = Assert.IsType<ActivityCapture>(result.Data);
        Assert.NotEmpty(snapshot.Activities);
        Assert.True(snapshot.TotalActivities > snapshot.Activities.Count);
        await sample.DisposeAsync();
        handles.Invalidate(result.Handle!);

        var (restored, records) = await ReadOfflineAsync<ActivityCapture>(result, CollectionHandleKinds.Activities);
        Assert.Equal(snapshot.TotalActivities, restored.TotalActivities);
        Assert.True(records.Count > restored.Activities.Count);
        Assert.Contains(records, r => r.Record.Name == "CoreClrSample.Outer");
        Assert.Contains(records.SelectMany(r => r.Record.Fields ?? []),
            f => f.StringValue?.Contains("/activity", StringComparison.Ordinal) == true);
    }

    [Fact(Timeout = 90_000)]
    public async Task Sweep_StoresDistinctConcurrentStreamsInOneCapture()
    {
        await using var sample = await StartAsync("BadCodeSample");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var handles = new MemoryDiagnosticHandleStore();
        var useCases = new DurableCaptureUseCases(Store(), handles, _options);
        var capture = useCases.CaptureAsync("Live sweep", "sweep", Owner, ct =>
            SweepUseCase.RunSweep(
                new EventPipeCounterCollector(), new EventPipeGcCollector(),
                new EventPipeExceptionCollector(), new EventPipeThreadPoolCollector(),
                new ProcessResourcesCollector(), new FixedResolver(sample.ProcessId), handles,
                processId: sample.ProcessId, durationSeconds: 8, cancellationToken: ct));
        var load = DriveAsync(http, "/exceptions?count=20", 3);
        await Task.WhenAll(capture, load);
        var result = await capture;
        Assert.False(result.IsError, result.Error?.Message);
        var info = Assert.IsType<CaptureInfo>(result.Capture);
        Assert.Equal(CaptureState.Sealed, info.State);
        Assert.Equal(0, info.Quality.QueueRejected);
        Assert.Equal(0, info.Quality.RecordRejected);
        Assert.Equal(0, info.Quality.StorageRejected);
        Assert.Equal(0, info.Quality.SnapshotRejected);
        await sample.DisposeAsync();

        Assert.Single((await Store().ListAsync(Owner)).Captures);
        using var reader = await Store().OpenAsync(info.CaptureId, Owner);
        var counters = Assert.Single(info.Artifacts, a => a.Kind == CollectionHandleKinds.Counters);
        var exceptions = Assert.Single(info.Artifacts, a => a.Kind == CollectionHandleKinds.ExceptionSnapshot);
        Assert.Contains(info.Artifacts, a => a.Kind == CollectionHandleKinds.GcEvents);
        Assert.Contains(info.Artifacts, a => a.Kind == CollectionHandleKinds.ThreadPoolSnapshot);
        var counterRows = reader.Query(new(counters.ArtifactId, PageSize: 1000)).Records;
        var exceptionRows = reader.Query(new(exceptions.ArtifactId, PageSize: 1000)).Records;
        Assert.Contains(counterRows, r => r.Record.Name == "cpu-usage");
        Assert.DoesNotContain(counterRows, r => r.Record.Name == "System.FormatException");
        Assert.Contains(exceptionRows, r => r.Record.Name == "System.FormatException");
        Assert.DoesNotContain(exceptionRows, r => r.Record.Name == "cpu-usage");
        var parent = Assert.Single(info.Artifacts, a => a.Kind == "sweep");
        var reopened = await new DurableCaptureUseCases(Store(), new MemoryDiagnosticHandleStore(), _options)
            .OpenAsync(info.CaptureId, parent.ArtifactId, Owner);
        var metadata = Assert.IsType<DurableSweepMetadata>(reopened.Composition?.Metadata?.Sweep);
        Assert.Equal(JsonSerializer.Serialize(result.Data!.Triage), JsonSerializer.Serialize(metadata.Triage));
        Assert.Equal(JsonSerializer.Serialize(result.Data.Resource), JsonSerializer.Serialize(metadata.Resource));
        Assert.Equal(result.Data.Failures, metadata.Failures);
        Assert.All(metadata.ArtifactIds.Values.Where(id => id is not null),
            id => Assert.Contains(info.Artifacts, a => a.ArtifactId == id));
    }

    [Fact(Timeout = 90_000)]
    public async Task GcActivities_RegistersDelayedHandlesInTheirOriginalChildStreams()
    {
        await using var sample = await StartAsync("CoreClrSample");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var handles = new MemoryDiagnosticHandleStore();
        var useCases = new DurableCaptureUseCases(Store(), handles, _options);
        var capture = useCases.CaptureAsync("Live GC and activities", "gc-activities", Owner, ct =>
            GcActivitiesCaptureUseCase.CollectAsync(
                new EventPipeGcCollector(), new EventPipeActivityCollector(),
                new FixedResolver(sample.ProcessId), handles,
                new GcActivitiesCaptureOptions(DurationSeconds: 8, Sources: ["CoreClrSample.Activities"]),
                processId: sample.ProcessId, cancellationToken: ct));
        var load = DriveAsync(http, "/activity?delayMs=10&collectGc=true", 4);
        await Task.WhenAll(capture, load);
        var result = await capture;
        Assert.False(result.IsError, result.Error?.Message);
        var info = Assert.IsType<CaptureInfo>(result.Capture);
        Assert.Equal(CaptureState.Sealed, info.State);
        Assert.Equal(0, info.Quality.QueueRejected);
        Assert.Equal(0, info.Quality.RecordRejected);
        Assert.Equal(0, info.Quality.SnapshotRejected);
        await sample.DisposeAsync();

        using var reader = await Store().OpenAsync(info.CaptureId, Owner);
        var gc = Assert.Single(info.Artifacts, a => a.Kind == CollectionHandleKinds.GcEvents);
        var activities = Assert.Single(info.Artifacts, a => a.Kind == CollectionHandleKinds.Activities);
        Assert.Equal("collect_events", gc.Provenance?.ProducingTool);
        Assert.Equal("collect_events", activities.Provenance?.ProducingTool);
        var gcRows = reader.Query(new(gc.ArtifactId, PageSize: 1000)).Records;
        var activityRows = reader.Query(new(activities.ArtifactId, PageSize: 1000)).Records;
        Assert.NotEmpty(gcRows);
        Assert.Contains(activityRows, r => r.Record.Name == "CoreClrSample.Outer");
        Assert.DoesNotContain(gcRows, r => r.Record.Name == "CoreClrSample.Outer");
        Assert.NotNull(reader.ReadSnapshot(gc.ArtifactId));
        Assert.NotNull(reader.ReadSnapshot(activities.ArtifactId));
        var parent = Assert.Single(info.Artifacts, a => a.Kind == "gc-activities");
        var reopened = await new DurableCaptureUseCases(Store(), new MemoryDiagnosticHandleStore(), _options)
            .OpenAsync(info.CaptureId, parent.ArtifactId, Owner);
        var metadata = Assert.IsType<DurableGcActivitiesMetadata>(reopened.Composition?.Metadata?.GcActivities);
        Assert.Equal(result.Data!.Status, metadata.Status);
        Assert.Equal(result.Data.IntersectionStart, metadata.IntersectionStart);
        Assert.Equal(result.Data.IntersectionEnd, metadata.IntersectionEnd);
        Assert.Equal(result.Data.StartupSkewMs, metadata.StartupSkewMs);
        Assert.Equal(JsonSerializer.Serialize(result.Data.Overlay), JsonSerializer.Serialize(metadata.Overlay));
        Assert.Equal(result.Data.Notes, metadata.Notes);
        Assert.Equal(gc.ArtifactId, metadata.Gc.ArtifactId);
        Assert.Equal(activities.ArtifactId, metadata.Activities.ArtifactId);
    }

    [Fact(Timeout = 90_000)]
    public async Task Cpu_PreservesStacksAndTreeWithoutRetainedNativeTrace()
    {
        await using var sample = await StartAsync("CoreClrSample");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var handles = new MemoryDiagnosticHandleStore();
        var capture = CaptureAsync(
            handles, sample.ProcessId, "cpu-sample",
            async ct => (await new EventPipeCpuSampler().SampleAsync(
                sample.ProcessId, TimeSpan.FromSeconds(8), topN: 10, cancellationToken: ct)).Artifact,
            producingTool: "collect_sample");
        var load = DriveAsync(http, "/cpu-burn?ms=400", 6);
        await Task.WhenAll(capture, load);
        var result = await capture;

        Assert.False(result.IsError, $"{result.Error?.Message} Capture: {JsonSerializer.Serialize(result.Capture)}");
        var artifact = Assert.IsType<CpuSampleTraceArtifact>(result.Data);
        Assert.NotEmpty(artifact.Root.Children);
        Assert.Null(artifact.TracePath);
        await sample.DisposeAsync();
        handles.Invalidate(result.Handle!);

        var (restored, records) = await ReadOfflineAsync<CpuSampleTraceArtifact>(
            result, "cpu-sample", "call-tree");
        Assert.Equal(artifact.Root.Children.Count, restored.Root.Children.Count);
        Assert.Null(restored.TracePath);
        Assert.NotEmpty(records);
    }

    [Fact(Timeout = 90_000)]
    public async Task Allocation_PreservesSampledTicksAndTreeAfterTargetExit()
    {
        await using var sample = await StartAsync("CoreClrSample");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var handles = new MemoryDiagnosticHandleStore();
        var capture = CaptureAsync(
            handles, sample.ProcessId, "allocation-sample",
            async ct =>
            {
                var sampled = await new EventPipeAllocationSampler().SampleAsync(
                    sample.ProcessId, TimeSpan.FromSeconds(8), topN: 10, cancellationToken: ct);
                return new AllocationSampleArtifact(sampled.Summary, sampled.Artifact);
            },
            producingTool: "collect_sample");
        var load = DriveAsync(http, "/render?count=1000", 12);
        await Task.WhenAll(capture, load);
        var result = await capture;

        Assert.False(result.IsError, result.Error?.Message);
        var artifact = Assert.IsType<AllocationSampleArtifact>(result.Data);
        Assert.NotEmpty(artifact.TraceArtifact.Root.Children);
        await sample.DisposeAsync();
        handles.Invalidate(result.Handle!);

        var (restored, records) = await ReadOfflineAsync<AllocationSampleArtifact>(
            result, "allocation-sample", "call-tree");
        Assert.Equal(artifact.TraceArtifact.Root.Children.Count, restored.TraceArtifact.Root.Children.Count);
        Assert.NotEmpty(records);
    }

    [Fact(Timeout = 90_000)]
    public async Task Exceptions_PreserveOccurrenceHistoryBeyondRecentProjection()
    {
        await using var sample = await StartAsync("BadCodeSample");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var handles = new MemoryDiagnosticHandleStore();
        var capture = CaptureAsync(
            handles, sample.ProcessId, CollectionHandleKinds.ExceptionSnapshot,
            ct => new EventPipeExceptionCollector().CollectAsync(
                sample.ProcessId, TimeSpan.FromSeconds(8), maxRecent: 4, cancellationToken: ct));
        var load = DriveAsync(http, "/exceptions?count=20", 3);
        await Task.WhenAll(capture, load);
        var result = await capture;

        Assert.False(result.IsError, result.Error?.Message);
        var snapshot = Assert.IsType<ExceptionSnapshot>(result.Data);
        Assert.True(snapshot.TotalExceptions >= 60);
        await sample.DisposeAsync();
        handles.Invalidate(result.Handle!);

        var (restored, records) = await ReadOfflineAsync<ExceptionSnapshot>(result, CollectionHandleKinds.ExceptionSnapshot);
        Assert.Equal(snapshot.TotalExceptions, restored.TotalExceptions);
        Assert.True(records.Count >= 60);
        Assert.True(records.Count > restored.Recent.Count);
        Assert.Contains(records, r => r.Record.Name == "System.FormatException");
    }

    [Fact(Timeout = 90_000)]
    public async Task Logs_PreserveOccurrencesBeyondRingWithoutRestoringRedactedScopeValues()
    {
        await using var sample = await StartAsync("BadCodeSample");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var handles = new MemoryDiagnosticHandleStore();
        var capture = CaptureAsync(
            handles, sample.ProcessId, CollectionHandleKinds.LogSnapshot,
            ct => new EventPipeLogCollector(new SensitiveDataRedactor()).CollectAsync(
                sample.ProcessId, TimeSpan.FromSeconds(8),
                categories: ["BadCodeSample.LogSpam"], minLevel: LogLevel.Warning,
                maxEvents: 4, includeJsonPayload: true, cancellationToken: ct));
        var load = DriveAsync(http, "/log-spam?count=8&level=warning", 3);
        await Task.WhenAll(capture, load);
        var result = await capture;

        Assert.False(result.IsError, result.Error?.Message);
        var snapshot = Assert.IsType<LogSnapshot>(result.Data);
        Assert.NotEmpty(snapshot.Recent);
        Assert.True(snapshot.TotalEvents > snapshot.Recent.Count);
        await sample.DisposeAsync();
        handles.Invalidate(result.Handle!);

        var (restored, records) = await ReadOfflineAsync<LogSnapshot>(result, CollectionHandleKinds.LogSnapshot);
        Assert.Equal(snapshot.TotalEvents, restored.TotalEvents);
        Assert.True(records.Count > restored.Recent.Count);
        var strings = records.SelectMany(r => r.Record.Fields ?? [])
            .Where(f => f.Kind == CaptureFieldKind.Text).Select(f => f.StringValue!).ToArray();
        Assert.DoesNotContain(strings, value => value.Contains("super-secret", StringComparison.Ordinal));
        Assert.Contains(strings, value => value.Contains(SensitiveDataRedactor.RedactedPlaceholder, StringComparison.Ordinal));
    }

    [Fact(Timeout = 90_000)]
    public async Task Heap_PreservesIndexedSnapshotRowsAfterTargetExit()
    {
        await using var sample = await StartAsync("CoreClrSample");
        var handles = new MemoryDiagnosticHandleStore();
        var result = await CaptureAsync(
            handles, sample.ProcessId, HeapInspectionUseCases.HeapSnapshotKind,
            ct => new ClrMdDumpInspector().InspectLiveAsync(
                sample.ProcessId, new DumpInspectionOptions(TopTypes: 25), ct),
            producingTool: "inspect_heap");
        Assert.False(result.IsError, result.Error?.Message);
        await sample.DisposeAsync();
        Assert.True(handles.Invalidate(result.Handle!));

        var (_, records) = await ReadOfflineAsync<HeapSnapshotArtifact>(
            result, HeapInspectionUseCases.HeapSnapshotKind, "top-types");
        AssertDerivedRows(Assert.IsType<CaptureInfo>(result.Capture), records);
    }

    [Fact(Timeout = 90_000)]
    public async Task Threads_PreserveIndexedSnapshotRowsAfterTargetExit()
    {
        await using var sample = await StartAsync("CoreClrSample");
        var handles = new MemoryDiagnosticHandleStore();
        var result = await CaptureAsync(
            handles, sample.ProcessId, SamplerUseCases.ThreadSnapshotKind,
            ct => new ClrMdThreadSnapshotInspector().InspectLiveAsync(
                sample.ProcessId, new ThreadSnapshotOptions(MaxFramesPerThread: 32), ct),
            producingTool: "collect_thread_snapshot");
        Assert.False(result.IsError, result.Error?.Message);
        var snapshot = Assert.IsType<ThreadSnapshotArtifact>(result.Data);
        Assert.NotEmpty(snapshot.Threads);
        await sample.DisposeAsync();
        Assert.True(handles.Invalidate(result.Handle!));

        var (restored, records) = await ReadOfflineAsync<ThreadSnapshotArtifact>(
            result, SamplerUseCases.ThreadSnapshotKind, "threads-summary");
        Assert.Equal(snapshot.Threads.Count, restored.Threads.Count);
        AssertDerivedRows(Assert.IsType<CaptureInfo>(result.Capture), records);
    }

    private static void AssertDerivedRows(CaptureInfo capture, IReadOnlyList<CaptureRecordEntry> records)
    {
        Assert.NotEmpty(records);
        Assert.Null(capture.Quality.SourceRejected);
        Assert.False(capture.Quality.IsComplete);
        Assert.All(records, entry =>
        {
            Assert.StartsWith("snapshot.", entry.Record.Category);
            var fields = Assert.IsAssignableFrom<IReadOnlyList<CaptureField>>(entry.Record.Fields);
            Assert.Contains(fields, field => field.Name == "sourceOccurrence" && field.BooleanValue == false);
            Assert.Contains(fields, field => field.Name == "derivedRetainedRow" && field.BooleanValue == true);
        });
    }

    private Task<DiagnosticResult<T>> CaptureAsync<T>(
        MemoryDiagnosticHandleStore handles, int processId, string kind,
        Func<CancellationToken, Task<T>> collect, string producingTool = "collect_events") where T : class
    {
        var useCases = new DurableCaptureUseCases(Store(), handles, _options);
        return useCases.CaptureAsync(kind, kind, Owner, async ct =>
        {
            var snapshot = await collect(ct);
            var handle = handles.RegisterWithMetadata(
                processId, kind, snapshot, TimeSpan.FromMinutes(10),
                evictWhenProcessExits: false, origin: HandleOrigin.Live, producingTool: producingTool);
            return DiagnosticResult.OkWithHandle(snapshot, "Live durable smoke.", handle.Id, handle.ExpiresAt);
        });
    }

    private async Task<(T Snapshot, List<CaptureRecordEntry> Records)> ReadOfflineAsync<T>(
        DiagnosticResult<T> result, string kind, string view = "summary") where T : class
    {
        var info = Assert.IsType<CaptureInfo>(result.Capture);
        Assert.Equal(CaptureState.Sealed, info.State);
        Assert.Equal(0, info.Quality.RecordRejected);
        Assert.Equal(0, info.Quality.QueueRejected);
        Assert.Equal(0, info.Quality.StorageRejected);
        Assert.Equal(0, info.Quality.Pending);
        Assert.Equal(0, info.Quality.SnapshotRejected);
        var artifact = Assert.Single(info.Artifacts, a => a.Kind == kind);
        var provenance = Assert.IsType<CaptureArtifactProvenance>(artifact.Provenance);
        Assert.True(provenance.ProcessId > 0);
        Assert.Equal(nameof(HandleOrigin.Live), provenance.OriginalHandleOrigin);
        Assert.Contains(provenance.ProducingTool,
            new[] { "collect_events", "collect_sample", "inspect_heap", "collect_thread_snapshot" });
        var package = Path.Combine(_root, "captures", info.CaptureId);
        var before = HashFiles(package);
        var rows = new List<CaptureRecordEntry>();
        using (var reader = await Store().OpenAsync(info.CaptureId, Owner))
        {
            Assert.IsType<CaptureSnapshot>(reader.ReadSnapshot(artifact.ArtifactId));
            long after = 0;
            do
            {
                var page = reader.Query(new(artifact.ArtifactId, AfterRecordId: after, PageSize: 7));
                Assert.True(page.AccountedBytes <= _options.MaxQueryPageBytes);
                Assert.True(page.Records.Count <= 7);
                rows.AddRange(page.Records);
                if (page.NextAfterRecordId is not { } next)
                    break;
                Assert.True(next > after);
                after = next;
            } while (true);
        }

        Assert.Equal(info.Quality.Persisted, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.RecordId).Distinct().Count());
        var reopenedHandles = new MemoryDiagnosticHandleStore();
        var reopenedUseCases = new DurableCaptureUseCases(Store(), reopenedHandles, _options);
        var reopened = await reopenedUseCases.OpenAsync(info.CaptureId, artifact.ArtifactId, Owner);
        Assert.NotEqual(result.Handle, reopened.Handle.Id);
        Assert.Equal(HandleOrigin.Imported, reopened.Handle.Origin);
        Assert.Equal(provenance.ProcessId, reopened.Handle.ProcessId);
        Assert.Contains(view, reopened.SupportedViews);
        var decoded = Assert.IsType<T>(reopenedHandles.TryGetWithKind(reopened.Handle.Id)!.Value.Artifact);
        await reopenedUseCases.AuthorizeViewAsync(reopened.Handle.Id, view, Owner);
        var denied = await Assert.ThrowsAsync<CaptureStoreException>(
            () => reopenedUseCases.AuthorizeViewAsync(
                reopened.Handle.Id, view, new CaptureAccess("other-owner")));
        Assert.Equal(CaptureErrorCode.Forbidden, denied.Code);
        Assert.True(reopenedHandles.Invalidate(reopened.Handle.Id));
        Assert.Equal(before, HashFiles(package));
        Assert.DoesNotContain(Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories),
            p => p.EndsWith(".nettrace", StringComparison.Ordinal)
                || p.EndsWith("-wal", StringComparison.Ordinal)
                || p.EndsWith("-shm", StringComparison.Ordinal));
        return (decoded, rows);
    }

    private static async Task DriveAsync(HttpClient client, string path, int requests)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        for (var i = 0; i < requests; i++)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            await Task.Delay(100);
        }
    }

    private static Task<LiveSampleProcess> StartAsync(string name) =>
        LiveSampleProcess.StartPublishedAsync(name, new LiveSampleOptions
        {
            WaitForHttpReady = true,
            ReadinessPath = name == "CoreClrSample" ? "/weatherforecast" : "/",
            DiagnosticTimeout = TimeSpan.FromSeconds(30),
        });

    private SqliteCaptureStore Store() => new(new RootProvider(_root), _options);

    private static string[] HashFiles(string package) =>
        Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => $"{Path.GetRelativePath(package, path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
            .ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record RootProvider(string Root) : IArtifactRootProvider;

    private sealed class FixedResolver(int processId) : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(requestedProcessId ?? processId, RuntimeFlavor.CoreClr,
                    CanSampleCpu: true, CanCollectGcDump: true, AutoResolved: false), null));
    }
}
