using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests
{
    [Fact]
    public async Task SequentialReplayPacesTinyQueueAndPreservesExactFieldsAndAccounting()
    {
        var options = new CaptureStoreOptions { QueueRecords = 1, QueueBytes = 1024, BatchRecords = 1 };
        var service = Service(options);
        var result = await service.CaptureAsync("replay", "counters", Owner, async token =>
        {
            var sink = Assert.IsAssignableFrom<IReplayCaptureObservationSink>(CaptureRecordingContext.Current);
            for (var i = 0; i < 512; i++)
                Assert.True(await sink.AppendReplayAsync(new("replay", At, long.MaxValue, Rich,
                [
                    CaptureObservationField.Int64("index", i),
                    CaptureObservationField.Int64("exact", long.MaxValue),
                    CaptureObservationField.String("text", Rich),
                    CaptureObservationField.Null("missing"),
                    CaptureObservationField.Bool("enabled", false),
                    CaptureObservationField.Double("number", 0.125),
                ]), token));
            sink.ReportSourceLoss("completed-trace", 0);
            return DiagnosticResult.Ok(Snapshot, "replayed");
        });
        Assert.False(result.IsError, result.Error?.Message);
        var info = result.Capture!;
        Assert.Equal(512, info.Quality.Offered);
        Assert.Equal(512, info.Quality.Persisted);
        Assert.Equal(0, info.Quality.QueueRejected);
        Assert.Equal(0, info.Quality.RecordRejected);
        var artifact = Assert.Single(info.Artifacts);
        var opened = await service.OpenAsync(info.CaptureId, artifact.ArtifactId, Owner);
        Assert.Equal(512, opened.RecordStream!.Offered);
        Assert.Equal(512, opened.RecordStream.Accepted);
        var page = await service.QueryRecordsAsync(info.CaptureId, new(artifact.ArtifactId, PageSize: 1000), Owner);
        Assert.Equal(512, page.Records.Count);
        var last = page.Records[^1].Record;
        Assert.Equal(long.MaxValue, last.ThreadId);
        Assert.Equal(511, last.Fields![0].Int64Value);
        Assert.Equal(long.MaxValue, last.Fields[1].Int64Value);
        Assert.Equal(Rich, last.Fields[2].StringValue);
        Assert.Equal(CaptureFieldKind.Null, last.Fields[3].Kind);
        Assert.False(last.Fields[4].BooleanValue);
        Assert.Equal(0.125, last.Fields[5].DoubleValue);
    }

    [Fact]
    public async Task ReplayHardRejectionsCountOnceWithoutEnumeratingOversizedFieldsOrRetrying()
    {
        var service = Service();
        var result = await service.CaptureAsync("invalid replay", "counters", Owner, async token =>
        {
            var sink = Assert.IsAssignableFrom<IReplayCaptureObservationSink>(CaptureRecordingContext.Current);
            Assert.False(await sink.AppendReplayAsync(new("bad", At, null, null, new HugeFields()), token));
            Assert.False(await sink.AppendReplayAsync(new("bad", At, null, null,
                [CaptureObservationField.String("oversized", new string('x', 65_536))]), token));
            return DiagnosticResult.Ok(Snapshot, "rejections retained");
        });
        Assert.False(result.IsError, result.Error?.Message);
        Assert.Equal(2, result.Capture!.Quality.Offered);
        Assert.Equal(2, result.Capture.Quality.RecordRejected);
        Assert.Equal(0, result.Capture.Quality.QueueRejected);
        Assert.Equal(0, result.Capture.Quality.Persisted);
        var artifact = Assert.Single(result.Capture.Artifacts);
        var opened = await service.OpenAsync(result.Capture.CaptureId, artifact.ArtifactId, Owner);
        Assert.Equal(2, opened.RecordStream!.Offered);
        Assert.Equal(0, opened.RecordStream.Accepted);
    }

    [Fact]
    public async Task CancelledReplayUnwindsCaptureWithoutPretendingTheOfferPersisted()
    {
        var service = Service();
        using var cancellation = new CancellationTokenSource();
        var result = await service.CaptureAsync("cancel replay", "counters", Owner, async token =>
        {
            var sink = Assert.IsAssignableFrom<IReplayCaptureObservationSink>(CaptureRecordingContext.Current);
            Assert.True(await sink.AppendReplayAsync(new("replay", At, null, null, []), token));
            await cancellation.CancelAsync();
            await sink.AppendReplayAsync(new("replay", At, null, null, []), token);
            return DiagnosticResult.Ok(Snapshot, "must not succeed");
        }, cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        Assert.Equal(1, result.Capture.Quality.Offered);
        Assert.Equal(1, result.Capture.Quality.Persisted);
        Assert.Null(CaptureRecordingContext.Current);
    }
}
