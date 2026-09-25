using System.Collections;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Hosting;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "durable-usecases-tests", Guid.NewGuid().ToString("N"));
    private static readonly CaptureAccess Owner = new("alice");
    private static readonly DateTimeOffset At = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private const string Rich = "exact\u0000é🙂\r\n\"\\漢字";
    private readonly MemoryDiagnosticHandleStore _handles = new();
    private static CounterSnapshot Snapshot => new(42, At, TimeSpan.FromSeconds(3),
        [new("System.Runtime", "working-set", Rich, 123, CounterKind.Mean, "bytes")], [], []);
    private SqliteCaptureStore Store(CaptureStoreOptions? options = null) => new(new RootProvider(_root), options);
    private DurableCaptureUseCases Service(CaptureStoreOptions? options = null, IDiagnosticHandleStore? handles = null)
        => new(Store(options), handles ?? _handles, options ?? new());

    [Fact]
    public async Task FullRoundTripPreservesEnvelopeScalarsAndOfflineHandleLifetime()
    {
        var service = Service();
        DiagnosticResult<CounterSnapshot>? original = null;
        var result = await service.CaptureAsync("capture", "counters", Owner, async _ =>
        {
            var sink = Assert.IsType<SqliteCaptureObservationSink>(CaptureRecordingContext.Current);
            Assert.True(sink.TryAppend(new("counter-source", At, long.MaxValue, Rich,
            [
                CaptureObservationField.String("text", Rich),
                CaptureObservationField.Int64("large", long.MaxValue),
                CaptureObservationField.Double("number", 0.125),
                CaptureObservationField.Bool("enabled", false),
                CaptureObservationField.Null("missing"),
            ])));
            sink.ReportSourceLoss("session", 2);
            await Task.Yield();
            sink.ReportSourceLoss("session", 3);
            var handle = _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1),
                producingTool: "collect_events");
            // Default interface implementations and delegating stores may announce twice.
            sink.ArtifactRegistered(handle, Snapshot);
            return original = DiagnosticResult.OkWithHandle(Snapshot, Rich, handle.Id, handle.ExpiresAt,
                new NextActionHint("query_snapshot", "inspect"));
        });
        Assert.Null(CaptureRecordingContext.Current);
        Assert.Equal(original, result with { Capture = null });
        var info = Assert.IsType<CaptureInfo>(result.Capture);
        var artifact = Assert.Single(info.Artifacts);
        Assert.Equal(CaptureState.Sealed, info.State);
        Assert.Equal(5, info.Quality.SourceRejected);
        Assert.Equal(1, info.Quality.Offered);
        Assert.Equal(1, info.Quality.Persisted);
        _handles.Invalidate(result.Handle!);

        var freshHandles = new MemoryDiagnosticHandleStore();
        var fresh = Service(handles: freshHandles);
        var open = await fresh.OpenAsync(info.CaptureId, artifact.ArtifactId, Owner);
        Assert.NotEqual(result.Handle, open.Handle.Id);
        Assert.Equal(HandleOrigin.Imported, open.Handle.Origin);
        Assert.Equal("collect_events", open.Handle.ProducingTool);
        Assert.Equal("Live", open.Artifact.Provenance!.OriginalHandleOrigin);
        Assert.Equal(42, open.Artifact.Provenance.ProcessId);
        Assert.Equal(open.Artifact, fresh.LookupBinding(open.Handle.Id)!.Artifact);
        Assert.NotNull(freshHandles.TryGet<CounterSnapshot>(open.Handle.Id));
        Assert.Equal(0, freshHandles.InvalidateForProcess(open.Handle.ProcessId));
        Assert.NotEmpty(open.SupportedViews);
        var page = await fresh.QueryRecordsAsync(info.CaptureId, new(artifact.ArtifactId), Owner);
        var row = Assert.Single(page.Records).Record;
        Assert.Equal("counter-source", row.Category);
        Assert.Equal(Rich, row.Name);
        Assert.Equal(At, row.Timestamp);
        Assert.Equal(long.MaxValue, row.ThreadId);
        Assert.Null(row.NumericValue);
        Assert.Equal(Rich, row.Fields![0].StringValue);
        Assert.Equal(long.MaxValue, row.Fields[1].Int64Value);
        Assert.Equal(0.125, row.Fields[2].DoubleValue);
        Assert.False(row.Fields[3].BooleanValue);
        Assert.Equal(CaptureFieldKind.Null, row.Fields[4].Kind);
        await fresh.AuthorizeViewAsync(open.Handle.Id, open.SupportedViews[0], Owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceUnknownIsStickyAndAbsenceIsNotZero(bool report)
    {
        var result = await Service().CaptureAsync("capture", "counters", Owner, _ =>
        {
            if (report)
            {
                var sink = CaptureRecordingContext.Current!;
                sink.ReportSourceLoss("same", 2);
                sink.ReportSourceLoss("same", null);
                sink.ReportSourceLoss("same", 0);
            }
            return Task.FromResult(DiagnosticResult.Ok(Snapshot, "done"));
        });
        Assert.Null(result.Capture!.Quality.SourceRejected);
        Assert.False(result.Capture.Quality.IsComplete);
    }

    [Fact]
    public async Task OwnerDeletionAndOfflineViewChecksApplyToEveryQuery()
    {
        var service = Service();
        var result = await service.CaptureAsync("capture", "counters", Owner,
            _ => Task.FromResult(DiagnosticResult.Ok(Snapshot, "done")));
        var info = result.Capture!;
        var open = await service.OpenAsync(info.CaptureId, Assert.Single(info.Artifacts).ArtifactId, Owner);
        var foreign = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.AuthorizeHandleAsync(open.Handle.Id, new("bob")));
        Assert.Equal(CaptureErrorCode.Forbidden, foreign.Code);
        var badView = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.AuthorizeViewAsync(open.Handle.Id, "objects", Owner));
        Assert.Equal(CaptureErrorCode.Forbidden, badView.Code);
        await service.DeleteAsync(info.CaptureId, Owner);
        Assert.NotNull(_handles.TryGetWithKind(open.Handle.Id)); // Materialized, but no longer authorized.
        await Assert.ThrowsAsync<CaptureStoreException>(() => service.AuthorizeHandleAsync(open.Handle.Id, Owner));
        _handles.Invalidate(open.Handle.Id);
        Assert.Null(service.LookupBinding(open.Handle.Id));
    }

    [Fact]
    public async Task OriginalProducerHandlesRecheckOwnershipDeletionAndOfflineViews()
    {
        var service = Service();
        var result = await service.CaptureAsync("original", "counters", Owner, _ =>
        {
            var handle = _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1));
            return Task.FromResult(DiagnosticResult.OkWithHandle(Snapshot, "done", handle.Id, handle.ExpiresAt));
        });
        var binding = Assert.IsType<DurableCaptureHandleBinding>(service.LookupBinding(result.Handle!));
        Assert.Equal(result.Capture!.CaptureId, binding.CaptureId);
        await service.AuthorizeViewAsync(result.Handle!, binding.SupportedViews[0], Owner);
        await Assert.ThrowsAsync<CaptureStoreException>(() => service.AuthorizeHandleAsync(result.Handle!, new("bob")));
        await Assert.ThrowsAsync<CaptureStoreException>(() => service.AuthorizeViewAsync(result.Handle!, "objects", Owner));
        await service.DeleteAsync(result.Capture.CaptureId, Owner);
        Assert.NotNull(_handles.TryGetWithKind(result.Handle!));
        await Assert.ThrowsAsync<CaptureStoreException>(() => service.AuthorizeHandleAsync(result.Handle!, Owner));
    }

    [Fact]
    public async Task EveryChildAndOverBudgetHandleRemainsBoundAndFailsClosed()
    {
        var service = Service(new() { MaxArtifacts = 1 });
        var ids = new List<string>();
        var shared = Snapshot;
        var result = await service.CaptureAsync("children", "counters", Owner, _ =>
        {
            for (var i = 0; i < 5; i++)
                ids.Add(_handles.RegisterWithMetadata(42, "counters", shared, TimeSpan.FromMinutes(1)).Id);
            return Task.FromResult(DiagnosticResult.Ok(shared, "children"));
        });
        Assert.True(result.IsError);
        foreach (var id in ids)
        {
            Assert.NotNull(_handles.TryGetWithKind(id));
            Assert.NotNull(service.LookupBinding(id));
            await Assert.ThrowsAsync<CaptureStoreException>(() => service.AuthorizeHandleAsync(id, Owner));
        }
    }

    [Fact]
    public void SharedSnapshotBindingAliasCapacityFailsClosedWithoutDroppingKnownBindings()
    {
        var handles = new MemoryDiagnosticHandleStore(maxEntries: DiagnosticHandleStoreOptions.MaxAllowedEntries);
        var bindings = new DurableCaptureBindings(handles);
        var shared = Snapshot;
        var binding = new DurableCaptureHandleBinding("capture", "artifact", ["view"])
        {
            Artifact = new("artifact", "counters", "test"),
        };
        for (var i = 0; i < DiagnosticHandleStoreOptions.MaxAllowedEntries + 2; i++)
        {
            var handle = handles.Register(42, "counters", shared, TimeSpan.FromMinutes(1));
            bindings.Set(handle, shared, binding);
            var retained = Assert.IsType<DurableCaptureHandleBinding>(bindings.Lookup(handle.Id));
            if (i < DiagnosticHandleStoreOptions.MaxAllowedEntries) Assert.Same(binding, retained);
            else
            {
                Assert.Null(retained.Artifact);
                Assert.Empty(retained.SupportedViews);
            }
        }
    }

    [Theory]
    [InlineData("heap-snapshot")]
    [InlineData("live")]
    [InlineData("dump")]
    [InlineData("gcdump")]
    [InlineData("gc-dump")]
    public async Task LiveHeapPathsAreProvenanceAndNeverEnableLiveViews(string kind)
    {
        var service = Service();
        var heap = new HeapSnapshotArtifact(HeapSnapshotOrigin.Live, 42, At, TimeSpan.FromSeconds(1),
            new("CoreCLR", "10.0.0", "X64", false, 1), new(1024, 0, 0, 1024, 0, 0, 1024), [], [])
        { DumpFilePath = "/unavailable/provenance-only.dmp" };
        var result = await service.CaptureAsync("heap", kind, Owner,
            _ => Task.FromResult(DiagnosticResult.Ok(heap, "done")));
        Assert.False(result.IsError, result.Error?.Message);
        Assert.Equal("heap-snapshot", Assert.Single(result.Capture!.Artifacts).Kind);
        var open = await service.OpenAsync(result.Capture!.CaptureId, result.Capture.Artifacts[0].ArtifactId, Owner);
        Assert.Equal(["top-types", "records"], open.SupportedViews);
        foreach (var view in new[] { "objects", "gcroot", "object", "roots", "strings" })
            await Assert.ThrowsAsync<CaptureStoreException>(() => service.AuthorizeViewAsync(open.Handle.Id, view, Owner));
    }

    [Fact]
    public async Task ConcurrentInvocationsKeepObservationsAndLossSeparate()
    {
        var service = Service();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = service.CaptureAsync("first", "counters", Owner, async _ =>
        {
            var sink = CaptureRecordingContext.Current!;
            entered.SetResult();
            await Task.Delay(50);
            Assert.Same(sink, CaptureRecordingContext.Current);
            Assert.True(sink.TryAppend(new("first", null, null, null, [])));
            sink.ReportSourceLoss("same", 1);
            return DiagnosticResult.Ok(Snapshot, "first");
        });
        await entered.Task;
        var second = service.CaptureAsync("second", "counters", Owner, _ =>
        {
            Assert.True(CaptureRecordingContext.Current!.TryAppend(new("second", null, null, null, [])));
            CaptureRecordingContext.Current.ReportSourceLoss("same", 2);
            return Task.FromResult(DiagnosticResult.Ok(Snapshot, "second"));
        });
        foreach (var result in await Task.WhenAll(first, second))
        {
            var info = result.Capture!;
            var record = Assert.Single((await service.QueryRecordsAsync(info.CaptureId,
                new(info.Artifacts[0].ArtifactId), Owner)).Records);
            Assert.Equal(result.Summary, record.Record.Category);
            Assert.Equal(result.Summary == "first" ? 1 : 2, info.Quality.SourceRejected);
        }
        Assert.Null(CaptureRecordingContext.Current);
    }

    [Fact]
    public async Task StructuredErrorPreservesFieldsAndRequiresExplicitRecovery()
    {
        var service = Service();
        var error = DiagnosticResult.Fail<CounterSnapshot>("error", new("TargetFailed", "original"),
            new NextActionHint("retry", "reason")) with { Data = Snapshot, Handle = "unavailable", Cancelled = true };
        var result = await service.CaptureAsync("failed", "counters", Owner, _ =>
        {
            CaptureRecordingContext.Current!.TryAppend(new("partial", null, null, null, []));
            return Task.FromResult(error);
        });
        Assert.Equal(error, result with { Capture = null });
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        var artifact = result.Capture.Artifacts[0];
        var failure = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.QueryRecordsAsync(result.Capture.CaptureId, new(artifact.ArtifactId), Owner));
        Assert.Equal(CaptureErrorCode.Incomplete, failure.Code);
        Assert.Single((await service.ListAsync(Owner)).Captures);
        var recovered = await service.RecoverAsync(result.Capture.CaptureId, Owner);
        Assert.NotEqual(result.Capture.CaptureId, recovered.CaptureId);
        Assert.Equal(result.Capture.CaptureId, recovered.DerivedFrom);
        Assert.Single((await service.QueryRecordsAsync(recovered.CaptureId, new(recovered.Artifacts[0].ArtifactId), Owner)).Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrownCollectorsReturnInterruptedCaptureAndUnwindScope(bool cancel)
    {
        var result = await Service().CaptureAsync<CounterSnapshot>("failed", "counters", Owner, _ =>
        {
            Assert.NotNull(CaptureRecordingContext.Current);
            if (cancel) throw new OperationCanceledException();
            throw new InvalidOperationException("collector failed");
        });
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        Assert.Equal(cancel, result.Cancelled);
        Assert.Equal(!cancel, result.IsError);
        Assert.Null(CaptureRecordingContext.Current);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("batch")]
    public async Task UnknownKindsAndAggregateObjectsAreNeverEmptySuccessfulCaptures(string kind)
    {
        var result = await Service().CaptureAsync("unsupported", kind, Owner,
            _ => Task.FromResult(DiagnosticResult.Ok(new object(), "done")));
        Assert.True(result.IsError);
        Assert.Equal("CapturePersistenceFailed", result.Error!.Kind);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
    }

    [Fact]
    public async Task SnapshotFailureDoesNotTurnCollectionSuccessIntoPersistenceSuccess()
    {
        var result = await Service(new() { MaxSnapshotBytes = 32 }).CaptureAsync("oversized", "counters", Owner,
            _ => Task.FromResult(DiagnosticResult.Ok(Snapshot, "done")));
        Assert.True(result.IsError);
        Assert.Same(Snapshot.GetType(), result.Data!.GetType());
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
    }

    [Fact]
    public async Task HugeFieldCountRejectsWithoutEnumerationAndCountsOffer()
    {
        var result = await Service().CaptureAsync("fields", "counters", Owner, _ =>
        {
            Assert.False(CaptureRecordingContext.Current!.TryAppend(new("bad", null, null, null, new HugeFields())));
            return Task.FromResult(DiagnosticResult.Ok(Snapshot, "done"));
        });
        Assert.Equal(1, result.Capture!.Quality.Offered);
        Assert.Equal(1, result.Capture.Quality.RecordRejected);
        Assert.Equal(0, result.Capture.Quality.Persisted);
    }

    [Theory]
    [MemberData(nameof(CaptureArtifactCodecTests.Snapshots), MemberType = typeof(CaptureArtifactCodecTests))]
    public async Task EveryAllowlistedFamilyCanReopenThroughSharedOrchestration(string kind, object snapshot)
    {
        var service = Service();
        var result = await service.CaptureAsync("family", kind, Owner, _ =>
        {
            var handle = _handles.RegisterWithMetadata(42, kind, snapshot, TimeSpan.FromMinutes(1),
                producingTool: "test-collector");
            return Task.FromResult(DiagnosticResult.OkWithHandle(snapshot, "done", handle.Id, handle.ExpiresAt));
        });
        Assert.False(result.IsError, result.Error?.Message);
        var info = result.Capture!;
        var artifact = Assert.Single(info.Artifacts);
        var views = await service.DescribeArtifactViewsAsync(info.CaptureId, artifact.ArtifactId, Owner);
        Assert.Equal(result.Handle, _handles.TryGetLatestByKind(kind)!.Id);
        var open = await service.OpenAsync(info.CaptureId, artifact.ArtifactId, Owner);
        Assert.Equal(snapshot.GetType(), _handles.TryGetWithKind(open.Handle.Id)!.Value.Artifact.GetType());
        var snapshotViews = CaptureArtifactCodec.GetSupportedSnapshotViews(kind, snapshot);
        Assert.Equal(SnapshotObservationProjection.Supports(kind) ? [.. snapshotViews, "records"] : snapshotViews, open.SupportedViews);
        Assert.Equal(views, open.SupportedViews);
        if (SnapshotObservationProjection.Supports(kind)) Assert.True(info.Quality.Offered > 0);
        else Assert.Equal(0, info.Quality.Offered);
    }

    [Fact]
    public async Task CompositeChildrenAreRetainedButNeverPresentedAsCompleteAggregate()
    {
        var service = Service();
        var result = await service.CaptureAsync("sweep", "counters", Owner, _ =>
        {
            _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1));
            _handles.RegisterWithMetadata(43, "counters", Snapshot with { ProcessId = 43 }, TimeSpan.FromMinutes(1));
            return Task.FromResult(DiagnosticResult.Ok(new object(), "sweep"));
        });
        Assert.True(result.IsError);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        Assert.Equal(3, result.Capture.Artifacts.Count);
        var recovered = await service.RecoverAsync(result.Capture.CaptureId, Owner);
        foreach (var child in recovered.Artifacts.Where(a => a.Name != "sweep"))
            Assert.NotNull(await service.OpenAsync(recovered.CaptureId, child.ArtifactId, Owner));
    }

    [Fact]
    public async Task ArtifactOverflowIsExplicitAndDuplicateAnnouncementsDoNotConsumeBudget()
    {
        var service = Service(new() { MaxArtifacts = 1 });
        var result = await service.CaptureAsync("duplicate", "counters", Owner, _ =>
        {
            var handle = _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1));
            for (var i = 0; i < 100; i++)
                CaptureRecordingContext.Current!.ArtifactRegistered(handle, Snapshot);
            return Task.FromResult(DiagnosticResult.Ok(Snapshot, "done"));
        });
        Assert.False(result.IsError);
        Assert.Single(result.Capture!.Artifacts);
        var overflow = await service.CaptureAsync("overflow", "counters", Owner, _ =>
        {
            for (var i = 0; i < 10; i++)
                _handles.RegisterWithMetadata(i, "counters", Snapshot, TimeSpan.FromMinutes(1));
            return Task.FromResult(DiagnosticResult.Ok(Snapshot, "done"));
        });
        Assert.True(overflow.IsError);
        Assert.Contains("MaxArtifacts", overflow.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(CaptureState.Interrupted, overflow.Capture!.State);
    }

    [Fact]
    public async Task ConcurrentLossReportsAreSummedEvenWhenSessionsShareAName()
    {
        var result = await Service().CaptureAsync("loss", "counters", Owner, _ =>
        {
            var sink = CaptureRecordingContext.Current!;
            Parallel.For(0, 1000, _ => sink.ReportSourceLoss("shared-session-name", 2));
            return Task.FromResult(DiagnosticResult.Ok(Snapshot, "done"));
        });
        Assert.Equal(2000, result.Capture!.Quality.SourceRejected);
    }

    [Fact]
    public async Task ConstructionRegistrationAndNonUseCreateNoCaptureFiles()
    {
        var services = new ServiceCollection();
        services.AddDiagnosticCoreServices(new SecurityOptions());
        services.AddSingleton<IArtifactRootProvider>(new RootProvider(_root));
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<DurableCaptureUseCases>();
        Assert.False(Directory.Exists(_root));
        Assert.Empty((await service.ListAsync(Owner)).Captures);
        Assert.False(Directory.Exists(_root));
        Assert.Null(DiagnosticResult.Ok(Snapshot, "ephemeral").Capture);
    }

    [Theory]
    [InlineData(2, "{}")]
    [InlineData(1, "{\"kind\":\"counters\",\"snapshot\":{}}")]
    public async Task MalformedAndUnsupportedVersionCannotRegisterHandles(int version, string json)
    {
        var store = Store();
        await using var writer = await store.CreateAsync(new("invalid"), Owner);
        var id = writer.AddArtifact("counters", "invalid");
        writer.SetSnapshot(id, version, System.Text.Encoding.UTF8.GetBytes(json));
        var info = await writer.CompleteAsync();
        var ex = await Assert.ThrowsAsync<CaptureStoreException>(() => Service().OpenAsync(info.CaptureId, id, Owner));
        Assert.Equal(CaptureErrorCode.UnsupportedFormat, ex.Code);
        Assert.Null(_handles.TryGetLatestByKind("counters"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record RootProvider(string Root) : IArtifactRootProvider;

    private sealed class HugeFields : IReadOnlyList<CaptureObservationField>
    {
        public int Count => int.MaxValue;
        public CaptureObservationField this[int index] => throw new InvalidOperationException("Must not enumerate");
        public IEnumerator<CaptureObservationField> GetEnumerator() => throw new InvalidOperationException("Must not enumerate");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
