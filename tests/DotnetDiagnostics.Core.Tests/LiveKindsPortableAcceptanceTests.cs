using System.Collections.Immutable;
using System.Security.Cryptography;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Symbols;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class LiveKindsPortableAcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private const string AcceptanceVariable = "DOTNET_DIAGNOSTICS_LIVE_KINDS_PORTABLE_ACCEPTANCE";
    private static readonly CaptureAccess Owner = new("live-kinds-portable-acceptance");
    private static readonly AuthorizePortableImport AllowImport = static (_, _, _, _) => ValueTask.CompletedTask;
    private static readonly string[] PrincipalScopes =
    [
        BearerPrincipal.RootScope,
        "read-counters",
        "eventpipe",
        "heap-read",
        "ptrace",
        "sensitive-heap-read",
        "module-bytes-read",
        "investigation-export"
    ];

    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, "live-kinds-portable-acceptance", Guid.NewGuid().ToString("N"));
    private readonly CaptureStoreOptions _options = new();

    [LiveKindsPortableAcceptanceFact(Timeout = 180_000)]
    [Trait("Category", "HistoricalAcceptance")]
    [Trait("Category", "PortableImportNative")]
    public Task LiveCpuCountersHeapThreadsAndBatch_SurvivePortableTransferAndSourceDeletion() =>
        HistoricalAcceptanceDiagnostics.RunAsync(CoreAsync, output.WriteLine);

    private async Task CoreAsync()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable(AcceptanceVariable));
        var worker = CreateWorker();
        var source = CreateContext("source");
        var sourceCaptures = new Dictionary<string, CaptureInfo>(StringComparer.Ordinal);
        var selections = new List<CaptureExportSelection>();

        LiveSampleProcess sample;
        try
        {
            sample = await LiveSampleProcess.StartPublishedAsync("CoreClrSample",
                HistoricalComparisonAcceptanceTests.OwnedSampleOptions());
        }
        catch (SkipException exception)
        {
            throw new InvalidOperationException("Authorized live-kind acceptance requires the published CoreClrSample.", exception);
        }

        await using (sample)
        {
            using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
            for (var index = 0; index < 2; index++)
            {
                var batch = await CaptureBatchAsync(source, sample.ProcessId, http, index);
                AddSelection($"batch-{index}", batch);
                var heap = await CaptureHeapAsync(source, sample.ProcessId, $"heap-{index}");
                AddSelection($"heap-{index}", heap);
            }

            var threads = await CaptureThreadsAsync(source, sample.ProcessId);
            AddSelection("threads", threads);
        }

        var bundlePath = Path.Combine(_root, "transfer", "live-kinds.ddcapture");
        Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
        var exportOperation = Key();
        await using (var bundle = File.Create(bundlePath))
        {
            var export = await PortableService("source").ExportAsync(new(exportOperation, selections), bundle, Owner);
            Assert.True(export.ArchiveBytes > 0);
        }

        var archive = await File.ReadAllBytesAsync(bundlePath);
        var destination = PortableService("destination", worker);
        PortableImportResult importResult;
        await using (var stream = File.OpenRead(bundlePath))
        {
            var importOperation = Key();
            importResult = await destination.ImportAsync(
                new(importOperation, archive.Length, Hash(archive)), stream, Owner, AllowImport);
            Assert.True(importResult.Complete);
            Assert.Equal(selections.Count, importResult.Entries.Count);
            Assert.All(importResult.Entries, entry => Assert.Equal(PortableEntryState.Published, entry.State));
        }

        Directory.Delete(Path.Combine(_root, "source"), recursive: true);

        var imported = importResult.Entries.ToDictionary(entry => entry.Mapping!.Label!, entry => entry.Mapping!, StringComparer.Ordinal);
        await AssertImportedCapturesHaveFreshIdsAndOriginAsync(imported, sourceCaptures);

        var batch0 = await AssertImportedBatchAsync(imported["batch-0"]);
        var batch1 = await AssertImportedBatchAsync(imported["batch-1"]);
        var heap0 = await AssertImportedSnapshotAsync<HeapSnapshotArtifact>(imported["heap-0"], HeapInspectionUseCases.HeapSnapshotKind, "top-types");
        var heap1 = await AssertImportedSnapshotAsync<HeapSnapshotArtifact>(imported["heap-1"], HeapInspectionUseCases.HeapSnapshotKind, "top-types");
        await AssertImportedSnapshotAsync<ThreadSnapshotArtifact>(imported["threads"], SamplerUseCases.ThreadSnapshotKind, "threads-summary");

        await AssertCompareWorksAsync("cpu", batch0.Cpu, batch1.Cpu, requireMetric: true);
        await AssertCompareWorksAsync("counters", batch0.Counters, batch1.Counters, requireMetric: true);
        await AssertCompareWorksAsync("heap", heap0, heap1, requireMetric: true);
        output.WriteLine("bundle={0}; bytes={1}; imported={2}; sourceDeleted=true", bundlePath, archive.Length, imported.Count);

        void AddSelection(string label, CaptureInfo capture)
        {
            sourceCaptures.Add(label, capture);
            selections.Add(new(capture.CaptureId, label));
        }
    }

    private async Task<CaptureInfo> CaptureBatchAsync(Context context, int processId, HttpClient http, int index)
    {
        var collect = context.UseCases.CaptureAsync("collect_batch", "batch", Owner, async ct =>
            {
                var cpu = context.UseCases.RunChildAsync("cpu-sample", "collect_sample:cpu",
                    token => CollectSampleTool.CollectSample(
                        new EventPipeCpuSampler(), null!, null!, null!, null!, null!, null!, context.Handles,
                        new FixedResolver(processId), new SymbolServerAllowlist(new()), new(), Principal(),
                        NullLoggerFactory.Instance, kind: "cpu", processId: processId, durationSeconds: 6,
                        topN: 10, resolveSourceLines: false, cancellationToken: token), ct);
                var counters = context.UseCases.RunChildAsync("counters", "collect_events:counters",
                    token => CollectEventsTool.CollectEvents(
                        counterCollector: new EventPipeCounterCollector(),
                        exceptionCollector: null!,
                        crashGuardCollector: null!,
                        gcCollector: null!,
                        gcDatasCollector: null!,
                        activityCollector: null!,
                        eventSourceCollector: null!,
                        eventCatalogCollector: null!,
                        logCollector: null!,
                        jitCollector: null!,
                        threadPoolCollector: null!,
                        contentionCollector: null!,
                        dbCollector: null!,
                        kestrelCollector: null!,
                        networkingCollector: null!,
                        inFlightRequestCollector: null!,
                        startupCollector: null!,
                        processResourcesCollector: null!,
                        gatedCaptureCollector: null!,
                        cpuSampler: new EventPipeCpuSampler(),
                        threadSnapshotInspector: null!,
                        dumpInspector: null!,
                        processDumper: null!,
                        resolver: new FixedResolver(processId),
                        handles: context.Handles,
                        allowlist: new EventSourceAllowlist(new()),
                        sensitiveGate: new SensitiveValueGate(new()),
                        principalAccessor: Principal(),
                        securityOptions: new(),
                        loggerFactory: NullLoggerFactory.Instance,
                        kind: "counters",
                        processId: processId,
                        durationSeconds: 6,
                        providers: ["System.Runtime"],
                        intervalSeconds: 1,
                        cancellationToken: token), ct);
                await DriveCpuAsync(http);
                var cpuResult = await cpu;
                var counterResult = await counters;
                Assert.Null(cpuResult.Error);
                Assert.Null(counterResult.Error);
                return DiagnosticResult.Ok(new CollectBatchReport(processId, 6,
                [
                    new("collect_sample", "cpu", cpuResult.Summary, null, cpuResult.Handle, cpuResult.HandleExpiresAt, cpuResult.Error),
                    new("collect_events", "counters", counterResult.Summary, null, counterResult.Handle, counterResult.HandleExpiresAt, counterResult.Error)
                ]), $"live batch {index}");
            }, CancellationToken.None);
        var result = await collect;
        Assert.Null(result.Error);
        var capture = Assert.IsType<CaptureInfo>(result.Capture);
        Assert.Equal(CaptureState.Sealed, capture.State);
        Assert.Contains(capture.Artifacts, artifact => artifact.Kind == "batch");
        Assert.Contains(capture.Artifacts, artifact => artifact.Kind == "cpu-sample");
        Assert.Contains(capture.Artifacts, artifact => artifact.Kind == "counters");
        return capture;
    }

    private async Task<CaptureInfo> CaptureHeapAsync(Context context, int processId, string name)
    {
        var result = await context.UseCases.CaptureAsync(name, HeapInspectionUseCases.HeapSnapshotKind, Owner, async ct =>
        {
            var artifact = await new ClrMdDumpInspector().InspectLiveAsync(
                processId, new DumpInspectionOptions(TopTypes: 25), ct);
            var handle = context.Handles.RegisterWithMetadata(processId, HeapInspectionUseCases.HeapSnapshotKind,
                artifact, TimeSpan.FromMinutes(10), evictWhenProcessExits: false, origin: HandleOrigin.Live,
                producingTool: "inspect_heap");
            return DiagnosticResult.OkWithHandle(artifact, "heap", handle.Id, handle.ExpiresAt);
        });
        Assert.Null(result.Error);
        return Assert.IsType<CaptureInfo>(result.Capture);
    }

    private async Task<CaptureInfo> CaptureThreadsAsync(Context context, int processId)
    {
        var result = await context.UseCases.CaptureAsync("threads", SamplerUseCases.ThreadSnapshotKind, Owner, async ct =>
        {
            var artifact = await new ClrMdThreadSnapshotInspector().InspectLiveAsync(
                processId, new ThreadSnapshotOptions(MaxFramesPerThread: 32), ct);
            Assert.NotEmpty(artifact.Threads);
            var handle = context.Handles.RegisterWithMetadata(processId, SamplerUseCases.ThreadSnapshotKind,
                artifact, TimeSpan.FromMinutes(10), evictWhenProcessExits: false, origin: HandleOrigin.Live,
                producingTool: "collect_thread_snapshot");
            return DiagnosticResult.OkWithHandle(artifact, "threads", handle.Id, handle.ExpiresAt);
        });
        Assert.Null(result.Error);
        return Assert.IsType<CaptureInfo>(result.Capture);
    }

    private async Task<(HistoricalCaptureReference Cpu, HistoricalCaptureReference Counters)> AssertImportedBatchAsync(
        PortableEntryMapping mapping)
    {
        using var reader = await Store("destination").OpenAsync(mapping.LocalCaptureId, Owner);
        var parent = Assert.Single(reader.Info.Artifacts, artifact => artifact.Kind == "batch");
        var cpu = Assert.Single(reader.Info.Artifacts, artifact => artifact.Kind == "cpu-sample");
        var counters = Assert.Single(reader.Info.Artifacts, artifact => artifact.Kind == "counters");
        var opened = await DurableService("destination").OpenAsync(mapping.LocalCaptureId, parent.ArtifactId, Owner);
        Assert.NotNull(opened.Composition);
        Assert.Contains(opened.Composition!.Children, child => child.ArtifactId == cpu.ArtifactId && child.ParentArtifactId == parent.ArtifactId);
        Assert.Contains(opened.Composition.Children, child => child.ArtifactId == counters.ArtifactId && child.ParentArtifactId == parent.ArtifactId);
        Assert.Equal(0, reader.Info.Quality.RecordRejected);
        Assert.Equal(0, reader.Info.Quality.StorageRejected);
        AssertCpuStackRecordsResolve(reader, cpu.ArtifactId);
        Assert.NotEmpty(reader.Query(new(counters.ArtifactId, PageSize: 1000)).Records);
        await OpenImportedAsync<CpuSampleTraceArtifact>(mapping.LocalCaptureId, cpu.ArtifactId, "call-tree");
        await OpenImportedAsync<CounterSnapshot>(mapping.LocalCaptureId, counters.ArtifactId, "summary");
        return (new(mapping.LocalCaptureId, cpu.ArtifactId), new(mapping.LocalCaptureId, counters.ArtifactId));
    }

    private async Task<HistoricalCaptureReference> AssertImportedSnapshotAsync<T>(
        PortableEntryMapping mapping, string kind, string view) where T : class
    {
        using var reader = await Store("destination").OpenAsync(mapping.LocalCaptureId, Owner);
        var artifact = Assert.Single(reader.Info.Artifacts, item => item.Kind == kind);
        var page = reader.Query(new(artifact.ArtifactId, PageSize: 1000));
        Assert.NotEmpty(page.Records);
        Assert.Equal(0, reader.Info.Quality.RecordRejected);
        Assert.Equal(0, reader.Info.Quality.StorageRejected);
        await OpenImportedAsync<T>(mapping.LocalCaptureId, artifact.ArtifactId, view);
        return new(mapping.LocalCaptureId, artifact.ArtifactId);
    }

    private async Task OpenImportedAsync<T>(string captureId, string artifactId, string view) where T : class
    {
        var handles = new MemoryDiagnosticHandleStore();
        var useCases = new DurableCaptureUseCases(Store("destination"), handles, _options);
        var opened = await useCases.OpenAsync(captureId, artifactId, Owner);
        Assert.Equal(HandleOrigin.Imported, opened.Handle.Origin);
        Assert.Contains(view, opened.SupportedViews);
        Assert.IsType<T>(handles.TryGetWithKind(opened.Handle.Id)!.Value.Artifact);
        await useCases.AuthorizeViewAsync(opened.Handle.Id, view, Owner);
        var query = await QuerySnapshotTool.QuerySnapshot(
            handles,
            new NoopDumpInspector(),
            new SensitiveDataRedactor(new()),
            new SensitiveValueGate(new()),
            new(),
            Principal(),
            new ClrMdNativeAddressResolver(),
            new ThrowingFrameVariableResolver(),
            opened.Handle.Id,
            view,
            cancellationToken: CancellationToken.None);
        Assert.Null(query.Error);
        Assert.NotNull(query.Data);
    }

    private async Task AssertCompareWorksAsync(
        string label, HistoricalCaptureReference left, HistoricalCaptureReference right, bool requireMetric)
    {
        var result = await DurableService("destination").CompareHistoricalAsync(
            new(left, right), Owner, static (_, _, _, _) => ValueTask.CompletedTask);
        Assert.Equal("qualified", result.Compatibility.Status);
        if (requireMetric) Assert.NotEmpty(result.Metrics);
        Assert.NotNull(result.Left.ClaimedPortableSource);
        Assert.NotNull(result.Right.ClaimedPortableSource);
        output.WriteLine("compare {0}: status={1}; metrics={2}", label, result.Compatibility.Status, result.Metrics.Count);
    }

    private static void AssertCpuStackRecordsResolve(CaptureReader reader, string artifactId)
    {
        var records = reader.Query(new(artifactId, PageSize: 1000)).Records;
        var definitions = records.Where(row => row.Record.Category == "definition.cpu-stack.v1").ToArray();
        var samples = records.Where(row => row.Record.Category == "sample.cpu.eventpipe.stack-ref.v1").ToArray();
        Assert.NotEmpty(definitions);
        Assert.NotEmpty(samples);
        var names = definitions.Select(row => row.Record.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(samples, row => Assert.Contains(row.Record.Name, names));
    }

    private async Task AssertImportedCapturesHaveFreshIdsAndOriginAsync(
        IReadOnlyDictionary<string, PortableEntryMapping> imported,
        IReadOnlyDictionary<string, CaptureInfo> sourceCaptures)
    {
        Assert.Equal(sourceCaptures.Count, imported.Count);
        foreach (var (label, mapping) in imported)
        {
            var source = sourceCaptures[label];
            Assert.NotEqual(source.CaptureId, mapping.LocalCaptureId);
            Assert.Equal(source.CaptureId, mapping.SourceCaptureId);
            Assert.Equal(source.Artifacts.Count, mapping.Artifacts.Count);
            using var reader = await Store("destination").OpenAsync(mapping.LocalCaptureId, Owner);
            Assert.Equal(source.CaptureId, reader.Info.PortableSource!.Origin.CaptureId);
            Assert.Equal(Owner.OwnerId, reader.Info.OwnerId);
            Assert.Equal(source.Quality, reader.Info.Quality);
            foreach (var artifact in source.Artifacts)
            {
                var importedArtifact = mapping.Artifacts.Single(item => item.EntryArtifactId == artifact.ArtifactId);
                Assert.NotEqual(artifact.ArtifactId, importedArtifact.LocalArtifactId);
                var local = reader.Info.Artifacts.Single(item => item.ArtifactId == importedArtifact.LocalArtifactId);
                Assert.Equal(artifact.ArtifactId, local.SourceArtifactId);
                Assert.Equal(artifact.Kind, local.Kind);
                Assert.Equal(artifact.Provenance?.ProducingTool, local.Provenance?.ProducingTool);
                Assert.Equal(artifact.Provenance?.OriginalHandleOrigin, local.Provenance?.OriginalHandleOrigin);
            }
        }
    }

    private static async Task DriveCpuAsync(HttpClient client)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        for (var i = 0; i < 8; i++)
        {
            using var response = await client.GetAsync("/cpu-burn?ms=350");
            response.EnsureSuccessStatusCode();
        }
    }

    private Context CreateContext(string name)
    {
        var store = Store(name);
        var handles = new MemoryDiagnosticHandleStore();
        var useCases = new DurableCaptureUseCases(store, handles, _options);
        return new(store, handles, useCases);
    }

    private SqliteCaptureStore Store(string name) => new(new RootProvider(Path.Combine(_root, name)), _options);
    private DurableCaptureUseCases DurableService(string name) => new(Store(name), new MemoryDiagnosticHandleStore(), _options);
    private PortableCaptureUseCases PortableService(string name, PortableCaptureImportWorker? worker = null) =>
        new(Store(name), static (_, _) => ValueTask.CompletedTask, importWorker: worker);

    private static PortableCaptureImportWorker CreateWorker()
    {
        var executable = Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_IMPORT_WORKER")
            ?? throw new InvalidOperationException("DOTNET_DIAGNOSTICS_IMPORT_WORKER is required.");
        var sqlite = Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_SQLITE_LIBRARY")
            ?? throw new InvalidOperationException("DOTNET_DIAGNOSTICS_SQLITE_LIBRARY is required.");
        var worker = new PortableCaptureImportWorker(executable, sqlite);
        worker.Validate();
        return worker;
    }

    private static IPrincipalAccessor Principal() => new StubPrincipal(new BearerPrincipal(
        "live-kinds-portable-acceptance", ImmutableHashSet.Create(PrincipalScopes), Owner.OwnerId));
    private static PortableOperationKey Key() => new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record RootProvider(string Root) : IArtifactRootProvider;
    private sealed record Context(SqliteCaptureStore Store, MemoryDiagnosticHandleStore Handles,
        DurableCaptureUseCases UseCases);
    private sealed record StubPrincipal(BearerPrincipal? Current) : IPrincipalAccessor;

    private sealed class NoopDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(
            string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HeapSnapshotArtifact> InspectLiveAsync(
            int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HeapObjectInspection> InspectObjectAsync(
            HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HeapGcRootInspection> InspectGcRootAsync(
            HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(
            HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingFrameVariableResolver : IFrameVariableResolver
    {
        public Task<FrameVariablesResult> ResolveAsync(
            ThreadSnapshotArtifact artifact, int managedThreadId, bool includeSensitiveValues,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedResolver(int processId) : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessContextResolution(
                new ProcessContext(requestedProcessId ?? processId, RuntimeFlavor.CoreClr,
                    CanSampleCpu: true, CanCollectGcDump: true, AutoResolved: false), null));
    }

    private sealed class LiveKindsPortableAcceptanceFactAttribute : FactAttribute
    {
        public LiveKindsPortableAcceptanceFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(AcceptanceVariable) != "1" ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_IMPORT_WORKER")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_SQLITE_LIBRARY")))
                Skip = "Requires a separately authorized live/native portable-kind acceptance slot.";
        }
    }
}
