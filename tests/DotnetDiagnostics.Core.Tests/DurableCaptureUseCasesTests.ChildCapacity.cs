using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests
{
    [Fact]
    public async Task ChildCapacityPreservesAllCollectorResultsAndAdmittedSiblingWithoutMisroutingRejectedRecords()
    {
        var service = Service(new() { MaxArtifacts = 2 });
        var allowedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectedFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ICaptureObservationSink? rejected = null;
        var result = await service.CaptureAsync("limited group", "batch", Owner, async _ =>
        {
            var first = service.RunChildAsync("counters", "retained", async _ =>
            {
                allowedStarted.SetResult();
                await rejectedFinished.Task;
                var sink = CaptureRecordingContext.Current!;
                Assert.True(sink.TryAppend(new("retained", At, null, null, [])));
                sink.ReportSourceLoss("source", 0);
                var handle = _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1));
                return DiagnosticResult.OkWithHandle(Snapshot, "retained result", handle.Id, handle.ExpiresAt);
            });
            await allowedStarted.Task;
            var others = new List<DiagnosticResult<CounterSnapshot>>();
            for (var i = 0; i < 3; i++)
                others.Add(await service.RunChildAsync("counters", $"rejected-{i}", _ =>
                {
                    var sink = CaptureRecordingContext.Current!;
                    rejected ??= sink;
                    Assert.Same(rejected, sink);
                    Assert.Same(sink, sink.CreateChild("counters", "nested rejected"));
                    Assert.False(sink.TryAppend(new("sensitive child", At, null, null, new HugeFields())));
                    sink.ReportSourceLoss("rejected-source", 0);
                    var handle = _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1));
                    return Task.FromResult(DiagnosticResult.OkWithHandle(Snapshot,
                        "original rejected result", handle.Id, handle.ExpiresAt));
                }));
            rejectedFinished.SetResult();
            return DiagnosticResult.Ok(new[] { await first }.Concat(others).ToArray(), "all collectors completed");
        });
        Assert.Equal("CapturePersistenceFailed", result.Error!.Kind);
        Assert.Contains("MaxArtifacts", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal("all collectors completed", result.Summary);
        Assert.Equal(4, result.Data!.Length);
        Assert.All(result.Data, child => Assert.False(child.IsError));
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        Assert.Equal(2, result.Capture.Artifacts.Count);
        Assert.Equal(4, result.Capture.Quality.Offered);
        Assert.Equal(1, result.Capture.Quality.Persisted);
        Assert.Null(result.Capture.Quality.SourceRejected);
        Assert.Equal(3, result.Capture.Quality.RecordRejected + result.Capture.Quality.QueueRejected);
        Assert.NotNull(service.LookupBinding(result.Data[0].Handle!)!.Artifact);
        foreach (var child in result.Data.Skip(1))
        {
            Assert.NotNull(_handles.TryGetWithKind(child.Handle!));
            Assert.Null(service.LookupBinding(child.Handle!)!.Artifact);
            var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
                service.AuthorizeHandleAsync(child.Handle!, Owner));
            Assert.Equal(CaptureErrorCode.Incomplete, error.Code);
        }
        Assert.Null(CaptureRecordingContext.Current);
        Assert.Throws<InvalidOperationException>(() => rejected!.CreateChild("counters", "late"));
        var recovered = await service.RecoverAsync(result.Capture.CaptureId, Owner);
        var root = recovered.Artifacts.Single(a => a.Name == "limited group");
        var childArtifact = recovered.Artifacts.Single(a => a.Name == "retained");
        var opened = await service.OpenAsync(recovered.CaptureId, root.ArtifactId, Owner);
        Assert.False(opened.RecordStreamAvailable);
        var row = Assert.Single((await service.QueryRecordsAsync(recovered.CaptureId,
            new(childArtifact.ArtifactId), Owner)).Records);
        Assert.Equal("retained", row.Record.Category);
    }

    [Fact]
    public async Task ReturnedRejectedChildHandleCannotBecomeParentArtifactOrEscapeBinding()
    {
        var service = Service(new() { MaxArtifacts = 2 });
        var result = await service.CaptureAsync("limited root", "counters", Owner, async _ =>
        {
            await service.RunChildAsync("counters", "retained",
                _ => Task.FromResult(DiagnosticResult.Ok(Snapshot, "retained")));
            return await service.RunChildAsync("counters", "rejected", _ =>
            {
                var handle = _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1));
                return Task.FromResult(DiagnosticResult.OkWithHandle(Snapshot, "rejected result", handle.Id, handle.ExpiresAt));
            });
        });
        Assert.Equal("rejected result", result.Summary);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Handle);
        Assert.Equal("CapturePersistenceFailed", result.Error!.Kind);
        Assert.Null(service.LookupBinding(result.Handle!)!.Artifact);
        Assert.Equal(2, result.Capture!.Artifacts.Count);
        await Assert.ThrowsAsync<CaptureStoreException>(() => service.AuthorizeHandleAsync(result.Handle!, Owner));
    }
}
