using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed class WorkerGapDiagnosticsTests
{
    [Theory]
    [InlineData(99999)]
    [InlineData(100000)]
    public void AtOrBelowTenMillisecondsKeepsTheOriginalSampleAndExitBehavior(long delta)
    {
        var first = TimeSpan.FromTicks(400000);
        var next = first + TimeSpan.FromTicks(delta);
        var observations = new CaptureWorkerObservation(new());
        observations.Record(first, 100, TimeSpan.Zero);
        observations.CheckGap(next);
        observations.Record(next, 100, TimeSpan.Zero);
        observations.ConfirmExit(next + TimeSpan.FromMilliseconds(10));
        Assert.True(observations.Completed);
        observations.CheckGap(TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData(false, 100001)]
    [InlineData(true, 100001)]
    [InlineData(false, -1)]
    public void RejectedSampleOrExitRetainsExactLastValidTimestampAndBoundedDiagnostics(bool exit, long delta)
    {
        var first = TimeSpan.FromTicks(400000);
        var now = first + TimeSpan.FromTicks(delta);
        var observations = new CaptureWorkerObservation(new());
        observations.Record(first, 100, TimeSpan.Zero);
        var error = Assert.Throws<CaptureStoreException>(() =>
        {
            if (exit) observations.ConfirmExit(now);
            else observations.Record(now, 100, TimeSpan.Zero);
        });
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        Assert.Equal("WorkerObservationGap: worker result invalidated; no input admitted.", error.Message);
        Assert.Equal(first.Ticks, error.Data["WorkerLastValidSampleTicks"]);
        Assert.Equal(now.Ticks, error.Data["WorkerCurrentTicks"]);
        Assert.Equal(delta, error.Data["WorkerGapTicks"]);
        Assert.Equal(100000L, error.Data["WorkerGapLimitTicks"]);
        Assert.Equal("Unspecified", error.Data["WorkerProtocolPhase"]);
        Assert.Equal("Unspecified", error.Data["WorkerPollStage"]);
        Assert.Equal(6, error.Data.Count);
        Assert.False(observations.Completed);
        var again = Assert.Throws<CaptureStoreException>(() => observations.CheckGap(first + TimeSpan.FromTicks(100001)));
        Assert.Equal(first.Ticks, again.Data["WorkerLastValidSampleTicks"]);
    }

    [Theory]
    [InlineData("Sending", "AwaitCompletionGap", false)]
    [InlineData("Receiving", "Record", true)]
    [InlineData("ExitWait", "ZeroRssExitConfirmation", false)]
    public void DiagnosticSnapshotReportsOnlySuppliedTimingAndTaskObservations(string phase, string stage, bool metricFinished)
    {
        var receiver = new TaskCompletionSource();
        var observations = new CaptureWorkerObservation(new())
        {
            ProtocolPhase = phase, PollStage = stage,
            PollStartedAt = TimeSpan.FromTicks(100), LastCompletedPollDuration = TimeSpan.FromTicks(30),
            MetricsStartedAt = TimeSpan.FromTicks(120),
            MetricsFinishedAt = metricFinished ? TimeSpan.FromTicks(140) : null,
            Sender = Task.CompletedTask, Receiver = receiver.Task
        };
        observations.Record(TimeSpan.Zero, 100, TimeSpan.Zero);
        var error = Assert.Throws<CaptureStoreException>(() => observations.CheckGap(TimeSpan.FromTicks(100001)));
        Assert.Equal(phase, error.Data["WorkerProtocolPhase"]);
        Assert.Equal(stage, error.Data["WorkerPollStage"]);
        Assert.Equal(100L, error.Data["WorkerPollStartedTicks"]);
        Assert.Equal(30L, error.Data["WorkerLastCompletedPollDurationTicks"]);
        Assert.Equal(120L, error.Data["WorkerMetricsStartedTicks"]);
        Assert.Equal(metricFinished, error.Data.Contains("WorkerMetricsFinishedTicks"));
        Assert.Equal(TaskStatus.RanToCompletion.ToString(), error.Data["WorkerSenderStatus"]);
        Assert.Equal(TaskStatus.WaitingForActivation.ToString(), error.Data["WorkerReceiverStatus"]);
        Assert.Equal(metricFinished ? 12 : 11, error.Data.Count);
        receiver.SetResult();
        observations.ProtocolPhase = "FinalChecks";
        Assert.Equal(phase, error.Data["WorkerProtocolPhase"]);
        Assert.Equal(TaskStatus.WaitingForActivation.ToString(), error.Data["WorkerReceiverStatus"]);
    }

    [Fact]
    public void WallDeadlineAndCancellationStillPreemptExitReconciliationWithoutInventingGapDiagnostics()
    {
        var observations = new CaptureWorkerObservation(new() { WallTime = TimeSpan.FromMilliseconds(20) });
        observations.Record(TimeSpan.Zero, 100, TimeSpan.Zero);
        var wall = Assert.Throws<CaptureStoreException>(() => observations.ConfirmExit(TimeSpan.FromMilliseconds(21)));
        Assert.StartsWith("WorkerWallTime:", wall.Message);
        Assert.Empty(wall.Data);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => observations.WaitForConfirmedExit(
            () => TimeSpan.FromMilliseconds(21), _ => throw new InvalidOperationException("Must not probe exit"), cancelled.Token));
        Assert.False(observations.Completed);
    }
}
