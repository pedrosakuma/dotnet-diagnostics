using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests
{
    [Theory]
    [InlineData("heap-snapshot", true)]
    [InlineData("heap-snapshot", false)]
    [InlineData("thread-snapshot", true)]
    [InlineData("thread-snapshot", false)]
    [InlineData("requests-now", true)]
    [InlineData("requests-now", false)]
    [InlineData("cpu-efficiency-sample", true)]
    [InlineData("cpu-efficiency-sample", false)]
    public async Task PointInTimeSnapshotsEmitIndexedDerivedRowsAfterCollection(string kind, bool register)
    {
        var snapshot = CaptureArtifactCodecTests.Snapshots().Single(row => (string)row[0] == kind)[1];
        var original = CaptureArtifactCodec.Encode(kind, snapshot, 4 * 1024 * 1024);
        var service = Service();
        var result = await service.CaptureAsync("point-in-time", kind, Owner, _ =>
        {
            if (!register) return Task.FromResult(DiagnosticResult.Ok(snapshot, "original"));
            var handle = _handles.RegisterWithMetadata(42, kind, snapshot, TimeSpan.FromMinutes(1));
            CaptureRecordingContext.Current!.ArtifactRegistered(handle, snapshot);
            return Task.FromResult(DiagnosticResult.OkWithHandle(snapshot, "original", handle.Id, handle.ExpiresAt));
        });
        Assert.False(result.IsError, result.Error?.Message);
        Assert.Same(snapshot, result.Data);
        Assert.Equal(original, CaptureArtifactCodec.Encode(kind, snapshot, original.Length));
        var capture = result.Capture!;
        Assert.True(capture.Quality.Persisted > 0);
        Assert.Equal(capture.Quality.Offered, capture.Quality.Persisted);
        Assert.Null(capture.Quality.SourceRejected);
        Assert.False(capture.Quality.IsComplete);
        var artifact = Assert.Single(capture.Artifacts);
        var fresh = Service();
        var reopened = await fresh.OpenAsync(capture.CaptureId, artifact.ArtifactId, Owner);
        Assert.True(reopened.RecordStreamAvailable);
        Assert.Contains("records", reopened.SupportedViews);
        Assert.Null(reopened.RecordStream!.Sources["snapshot-derived"]);
        var rows = (await fresh.QueryRecordsAsync(capture.CaptureId, new(artifact.ArtifactId, PageSize: 1000), Owner)).Records;
        Assert.Equal(capture.Quality.Persisted, rows.Count);
        Assert.Single(rows, row => row.Record.Category == $"snapshot.{kind}.metadata");
        Assert.All(rows, row =>
        {
            Assert.StartsWith("snapshot.", row.Record.Category, StringComparison.Ordinal);
            Assert.False(row.Record.Fields!.Single(field => field.Name == "sourceOccurrence").BooleanValue);
            Assert.True(row.Record.Fields!.Single(field => field.Name == "derivedRetainedRow").BooleanValue);
        });
        if (kind is "cpu-efficiency-sample" or "requests-now")
            Assert.Equal(["records"], reopened.SupportedViews);
    }

    [Fact]
    public async Task DerivedRowsRespectHardRecordBoundsAndPreserveOriginalSnapshotAndLossEvidence()
    {
        var snapshot = CaptureArtifactCodecTests.Snapshots().Single(row => (string)row[0] == "heap-snapshot")[1];
        var service = Service(new() { MaxRecordBytes = 128 });
        var result = await service.CaptureAsync("bounded heap rows", "heap-snapshot", Owner,
            _ => Task.FromResult(DiagnosticResult.Ok(snapshot, "heap")));
        Assert.False(result.IsError, result.Error?.Message);
        Assert.Same(snapshot, result.Data);
        Assert.True(result.Capture!.Quality.Offered > 0);
        Assert.Equal(result.Capture.Quality.Offered, result.Capture.Quality.RecordRejected);
        Assert.Equal(0, result.Capture.Quality.Persisted);
        Assert.False(result.Capture.Quality.IsComplete);
        var artifact = Assert.Single(result.Capture.Artifacts);
        var opened = await service.OpenAsync(result.Capture.CaptureId, artifact.ArtifactId, Owner);
        Assert.Contains("top-types", opened.SupportedViews);
        Assert.Equal(0, opened.RecordStream!.Accepted);
    }

    [Fact]
    public async Task RejectedChildSnapshotIsNeverProjectedIntoRootOrAdmittedSibling()
    {
        var snapshot = CaptureArtifactCodecTests.Snapshots().Single(row => (string)row[0] == "heap-snapshot")[1];
        var service = Service(new() { MaxArtifacts = 1 });
        var result = await service.CaptureAsync("no child capacity", "heap-snapshot", Owner, async _ =>
            await service.RunChildAsync("heap-snapshot", "rejected heap", _ =>
                Task.FromResult(DiagnosticResult.Ok(snapshot, "original rejected heap"))));
        Assert.True(result.IsError);
        Assert.Same(snapshot, result.Data);
        Assert.Equal(0, result.Capture!.Quality.Offered);
        Assert.Equal(0, result.Capture.Quality.Persisted);
        Assert.Equal(CaptureState.Interrupted, result.Capture.State);
    }
}
