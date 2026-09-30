using System.Threading.Channels;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using static DotnetDiagnostics.Core.Tests.SamplerCaptureObservationTests;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SamplerCaptureReplayObservationTests
{
    [Fact]
    public async Task CpuReplay_PacesBusyWriterWithoutTryingLiveAdmissionOrSkippingSamples()
    {
        var sink = new BoundedReplaySink();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var interpreted = 0;
        var replay = ReplayAsync(200, sink, () => interpreted++, deadline.Token);
        await sink.SecondOffer.Task.WaitAsync(deadline.Token);
        Assert.False(replay.IsCompleted);
        Assert.Equal(2, interpreted);
        Assert.Equal(0, sink.LiveOffers);
        Assert.Equal(2, sink.ReplayOffers);

        for (var i = 0; i < 200; i++)
        {
            var row = await sink.Rows.Reader.ReadAsync(deadline.Token);
            Assert.Equal(i, row.ThreadId);
            Assert.Null(row.Timestamp);
            Assert.Equal("sample.cpu.eventpipe", row.Category);
            Assert.Equal("类型.Method", row.Name);
            Assert.Equal(i / 1000.0, Field(row, "sourceSeconds").Number);
            Assert.Equal(1, Field(row, "weight").Integer);
            Assert.Equal("trace-relative-seconds", Field(row, "sourceClock").Text);
            Assert.False(Field(row, "stackTruncated").Boolean);
        }

        Assert.Equal(200, await replay);
        Assert.Equal(200, sink.ReplayOffers);
        Assert.Equal(0, sink.LiveOffers);
        Assert.False(sink.Rows.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CpuReplay_CancellationInterruptsBusyAdmissionWithoutReadingAheadOrClaimingSuccess()
    {
        var sink = new BoundedReplaySink();
        using var cancellation = new CancellationTokenSource();
        var interpreted = 0;
        var replay = ReplayAsync(200, sink, () => interpreted++, cancellation.Token);
        await sink.SecondOffer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, interpreted);
        Assert.Equal(2, sink.ReplayOffers);
        Assert.Equal(0, sink.LiveOffers);
        Assert.True(sink.Rows.Reader.TryRead(out var first));
        Assert.NotNull(first);
        Assert.Equal(0, first.ThreadId);
        Assert.False(sink.Rows.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CpuReplay_PreCancelledTokenDoesNotOfferAnObservation()
    {
        var sink = new BoundedReplaySink();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await EventPipeCpuSampler.EmitReplayObservationAsync(sink, 1, 1, [], cancellation.Token));
        Assert.Equal(0, sink.ReplayOffers);
        Assert.Equal(0, sink.LiveOffers);
    }

    [Fact]
    public async Task CpuReplay_HardRejectionIsReturnedOnce_WithoutRetryOrLiveFallback()
    {
        var sink = new RejectingReplaySink();
        var admitted = await EventPipeCpuSampler.EmitReplayObservationAsync(sink, 1, 1, [], CancellationToken.None);
        Assert.False(admitted);
        Assert.Equal(1, sink.ReplayOffers);
        Assert.Equal(0, sink.LiveOffers);
    }

    [Fact]
    public async Task CpuReplay_LegacySinkRemainsNonblockingAndReturnsItsAdmissionResult()
    {
        var sink = new ObservationTestSink { Accept = false };
        Assert.False(await EventPipeCpuSampler.EmitReplayObservationAsync(sink, 17, 123.5, [], CancellationToken.None));
        var row = Assert.Single(sink.Rows);
        Assert.Equal("[]", Field(row, "stack").Text);
        Assert.Equal(.1235, Field(row, "sourceSeconds").Number);
    }

    [Fact]
    public void LiveSample_DoesNotInvokeAwaitableAdmissionEvenWithReplayCapableSink()
    {
        var sink = new RejectingReplaySink();
        SamplerObservationProjection.Sample(sink, "sample.off-cpu", "unavailable", null, 1, []);
        Assert.Equal(1, sink.LiveOffers);
        Assert.Equal(0, sink.ReplayOffers);
    }

    private static async Task<int> ReplayAsync(int count, ICaptureObservationSink sink, Action interpreting,
        CancellationToken cancellationToken)
    {
        var admitted = 0;
        for (var i = 0; i < count; i++)
        {
            interpreting();
            // This is the same copied-frame emission boundary called by the TraceLog replay,
            // without requiring a live process or a retained native trace in the fixture.
            if (await EventPipeCpuSampler.EmitReplayObservationAsync(sink, i, i,
                [("模块!类型.Method", "模块", "类型.Method")], cancellationToken))
                admitted++;
        }
        return admitted;
    }

    private sealed class BoundedReplaySink : ICaptureObservationSink, IReplayCaptureObservationSink
    {
        internal Channel<CaptureObservation> Rows { get; } = Channel.CreateBounded<CaptureObservation>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        internal TaskCompletionSource SecondOffer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReplayOffers { get; private set; }
        internal int LiveOffers { get; private set; }

        public async ValueTask<bool> AppendReplayAsync(CaptureObservation observation, CancellationToken cancellationToken)
        {
            ReplayOffers++;
            if (ReplayOffers == 2) SecondOffer.TrySetResult();
            await Rows.Writer.WriteAsync(observation, cancellationToken);
            return true;
        }

        public bool TryAppend(CaptureObservation observation) { LiveOffers++; return false; }
        public void ReportSourceLoss(string source, long? count) { }
        public void ArtifactRegistered(DiagnosticHandle handle, object artifact) { }
    }

    private sealed class RejectingReplaySink : ICaptureObservationSink, IReplayCaptureObservationSink
    {
        internal int ReplayOffers { get; private set; }
        internal int LiveOffers { get; private set; }

        public ValueTask<bool> AppendReplayAsync(CaptureObservation observation, CancellationToken cancellationToken)
        {
            ReplayOffers++;
            return ValueTask.FromResult(false);
        }

        public bool TryAppend(CaptureObservation observation) { LiveOffers++; return false; }
        public void ReportSourceLoss(string source, long? count) { }
        public void ArtifactRegistered(DiagnosticHandle handle, object artifact) { }
    }
}
