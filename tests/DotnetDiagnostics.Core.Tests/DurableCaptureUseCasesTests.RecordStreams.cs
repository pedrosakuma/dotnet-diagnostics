using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests
{
    [Fact]
    public async Task SnapshotAvailabilityDoesNotManufactureAnEmptyRecordStream()
    {
        var service = Service();
        var result = await service.CaptureAsync("snapshot only", "counters", Owner,
            _ => Task.FromResult(DiagnosticResult.Ok(Snapshot, "done")));
        var artifact = Assert.Single(result.Capture!.Artifacts);
        var opened = await service.OpenAsync(result.Capture.CaptureId, artifact.ArtifactId, Owner);
        Assert.False(opened.RecordStreamAvailable);
        Assert.False(opened.RecordStream!.Available);
        Assert.DoesNotContain("records", opened.SupportedViews);
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.QueryRecordsAsync(result.Capture.CaptureId, new(artifact.ArtifactId), Owner));
        Assert.Equal(CaptureErrorCode.UnsupportedFormat, error.Code);
        await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.AuthorizeViewAsync(opened.Handle.Id, "records", Owner));
    }

    [Fact]
    public async Task ExplicitSourceReportCanDeclareARealEmptyStreamWithoutInventingKnownLoss()
    {
        var service = Service();
        var result = await service.CaptureAsync("zero events", "counters", Owner, _ =>
        {
            CaptureRecordingContext.Current!.ReportSourceLoss("EventPipe", null);
            var handle = _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1));
            return Task.FromResult(DiagnosticResult.OkWithHandle(Snapshot, "done", handle.Id, handle.ExpiresAt));
        });
        var artifact = Assert.Single(result.Capture!.Artifacts);
        var reopened = await service.OpenAsync(result.Capture.CaptureId, artifact.ArtifactId, Owner);
        Assert.True(reopened.RecordStreamAvailable);
        Assert.Null(reopened.RecordStream!.SourceRejected);
        Assert.Null(reopened.RecordStream.Sources["EventPipe"]);
        Assert.Null(result.Capture.Quality.SourceRejected);
        Assert.Empty((await service.QueryRecordsAsync(result.Capture.CaptureId, new(artifact.ArtifactId), Owner)).Records);
        await service.AuthorizeViewAsync(result.Handle!, "records", Owner);
        await service.AuthorizeViewAsync(reopened.Handle.Id, "records", Owner);
    }

    [Fact]
    public async Task SourceGranularityAndAdmissionCountsSurviveStandaloneReopen()
    {
        var service = Service();
        var result = await service.CaptureAsync("sources", "counters", Owner, _ =>
        {
            var sink = CaptureRecordingContext.Current!;
            sink.ReportSourceLoss("same", 2);
            sink.ReportSourceLoss("same", 3);
            sink.ReportSourceLoss("other", null);
            Assert.True(sink.TryAppend(new("source", At, null, null, [])));
            return Task.FromResult(DiagnosticResult.Ok(Snapshot, "done"));
        });
        var artifact = Assert.Single(result.Capture!.Artifacts);
        var opened = await service.OpenAsync(result.Capture.CaptureId, artifact.ArtifactId, Owner);
        Assert.Equal(5, opened.RecordStream!.Sources["same"]);
        Assert.Null(opened.RecordStream.Sources["other"]);
        Assert.Null(opened.RecordStream.SourceRejected);
        Assert.Equal(1, opened.RecordStream.Offered);
        Assert.Equal(1, opened.RecordStream.Accepted);
        Assert.True(opened.RecordStreamAvailable);
    }

    [Fact]
    public async Task InterruptedEmptyStreamMetadataRecoversWithoutPretendingToHaveTypedSnapshot()
    {
        var service = Service();
        var result = await service.CaptureAsync<CounterSnapshot>("interrupted", "counters", Owner, _ =>
        {
            CaptureRecordingContext.Current!.ReportSourceLoss("EventPipe", null);
            throw new OperationCanceledException();
        });
        var recovered = await service.RecoverAsync(result.Capture!.CaptureId, Owner);
        var artifact = Assert.Single(recovered.Artifacts);
        Assert.Empty((await service.QueryRecordsAsync(recovered.CaptureId, new(artifact.ArtifactId), Owner)).Records);
        Assert.Equal(["records"], await service.DescribeArtifactViewsAsync(recovered.CaptureId, artifact.ArtifactId, Owner));
        Assert.Null(_handles.TryGetLatestByKind("counters"));
        await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.OpenAsync(recovered.CaptureId, artifact.ArtifactId, Owner));
    }

    [Fact]
    public async Task RegistrationFreeViewDescriptionsStillCheckOwnerAndDeletion()
    {
        var service = Service();
        var result = await service.CaptureAsync("snapshot", "counters", Owner,
            _ => Task.FromResult(DiagnosticResult.Ok(Snapshot, "done")));
        var info = result.Capture!;
        var artifact = Assert.Single(info.Artifacts);
        Assert.NotEmpty(await service.DescribeArtifactViewsAsync(info.CaptureId, artifact.ArtifactId, Owner));
        Assert.Null(_handles.TryGetLatestByKind("counters"));
        var forbidden = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.DescribeArtifactViewsAsync(info.CaptureId, artifact.ArtifactId, new("bob")));
        Assert.Equal(CaptureErrorCode.Forbidden, forbidden.Code);
        await service.DeleteAsync(info.CaptureId, Owner);
        await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.DescribeArtifactViewsAsync(info.CaptureId, artifact.ArtifactId, Owner));
    }

    [Fact]
    public void SnapshotMetadataRejectsInconsistentCountsAndOversizedPayload()
    {
        var options = new CaptureStoreOptions();
        var inner = CaptureArtifactCodec.Encode("counters", Snapshot, options.MaxSnapshotBytes);
        var stream = new DurableCaptureRecordStreamInfo(false, 1, 1, null, new Dictionary<string, long?>(), 0);
        var bytes = DurableCaptureSnapshotMetadata.Encode("counters", CaptureArtifactCodec.FormatVersion,
            inner, stream, options.MaxSnapshotBytes);
        Assert.Throws<InvalidDataException>(() => DurableCaptureSnapshotMetadata.Decode("counters",
            new(DurableCaptureSnapshotMetadata.Version, bytes), options.MaxSnapshotBytes));
        var valid = stream with { Available = true };
        bytes = DurableCaptureSnapshotMetadata.Encode("counters", CaptureArtifactCodec.FormatVersion,
            inner, valid, options.MaxSnapshotBytes);
        Assert.Throws<InvalidDataException>(() => DurableCaptureSnapshotMetadata.Decode("counters",
            new(DurableCaptureSnapshotMetadata.Version, bytes), bytes.Length - 1));
        var decoded = DurableCaptureSnapshotMetadata.Decode("counters",
            new(DurableCaptureSnapshotMetadata.Version, bytes), options.MaxSnapshotBytes);
        Assert.Equal(inner, decoded.Snapshot.Utf8Json.ToArray());
        Assert.True(decoded.Stream.Available);
    }
}
