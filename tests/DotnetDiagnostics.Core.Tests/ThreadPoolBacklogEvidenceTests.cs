using System.Text.Json;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.ThreadPool;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ThreadPoolBacklogEvidenceTests
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UnixEpoch;

    [Fact]
    public void SustainedBacklog_ReportsBoundedOccupancyAndIndependentMovement()
    {
        var result = Evaluate(
            queue: [10, 12, 14, 16, 18, 20],
            workers: [4, 5, 6, 7, 8, 9],
            reason: new("Starvation", ThreadPoolEvidence.RuntimeObserved, "capture-1", StartedAt.AddSeconds(3)));

        result.Availability.Should().Be(ThreadPoolBacklogAvailability.Available);
        result.WindowsAboveLevel.Should().Be(6);
        result.ProportionAboveLevel.Should().Be(1);
        result.LongestContiguousDurationAboveLevelSeconds.Should().Be(6);
        result.DrainedAtTail.Should().BeFalse();
        result.QueueMovement.Should().Be(new ThreadPoolBacklogMovement(10, 20, 10, 15, 2));
        result.WorkerMovement.Should().Be(new ThreadPoolBacklogMovement(4, 9, 5, 6.5, 1));
        result.Reason.State.Should().Be(ThreadPoolBacklogReasonState.Known);
        result.Reason.SameCapture.Should().BeTrue();
    }

    [Fact]
    public void DrainedBurst_ReportsTailDrainWithoutAStarvationVerdict()
    {
        var result = Evaluate(
            queue: [0, 20, 20, 5, 0, 0],
            workers: [4, 5, 6, 7, 8, 9]);

        result.Availability.Should().Be(ThreadPoolBacklogAvailability.Available);
        result.WindowsAboveLevel.Should().Be(2);
        result.ProportionAboveLevel.Should().BeApproximately(2d / 6, 1e-12);
        result.LongestContiguousDurationAboveLevelSeconds.Should().Be(2);
        result.DrainedAtTail.Should().BeTrue();
    }

    [Fact]
    public void HighWorkersWithoutBacklog_LeavesQueueAndWorkerMovementIndependent()
    {
        var result = Evaluate(
            queue: [0, 0, 0, 0, 0, 0],
            workers: [30, 31, 32, 33, 34, 35]);

        result.Availability.Should().Be(ThreadPoolBacklogAvailability.Available);
        result.WindowsAboveLevel.Should().Be(0);
        result.DrainedAtTail.Should().BeFalse();
        result.QueueMovement!.Delta.Should().Be(0);
        result.WorkerMovement!.Delta.Should().Be(5);
    }

    [Fact]
    public void MissingReason_PreservesUnknownSameCaptureProvenance()
    {
        var result = Evaluate(
            queue: [10, 12, 14, 16, 18, 20],
            workers: [4, 5, 6, 7, 8, 9]);

        result.Availability.Should().Be(ThreadPoolBacklogAvailability.Available);
        result.Reason.State.Should().Be(ThreadPoolBacklogReasonState.Unknown);
        result.Reason.Provenance.Should().Be(ThreadPoolEvidence.Missing);
        result.Reason.SameCapture.Should().BeFalse();
    }

    [Fact]
    public void RuntimeReason_FromAnotherCapture_RemainsUnknown()
    {
        var result = Evaluate(
            queue: [10, 12, 14, 16, 18, 20],
            workers: [4, 5, 6, 7, 8, 9],
            reason: new(
                "Starvation",
                ThreadPoolEvidence.RuntimeObserved,
                "capture-2",
                StartedAt.AddSeconds(3)));

        result.Reason.State.Should().Be(ThreadPoolBacklogReasonState.Unknown);
        result.Reason.Value.Should().BeNull();
        result.Reason.Provenance.Should().Be(ThreadPoolEvidence.RuntimeObserved);
        result.Reason.CaptureId.Should().Be("capture-2");
        result.Reason.SameCapture.Should().BeFalse();
    }

    [Fact]
    public void HealthyControl_RemainsAvailableWithoutInventingBacklog()
    {
        var result = Evaluate(
            queue: [0, 0, 0, 0, 0, 0],
            workers: [4, 4, 4, 4, 4, 4]);

        result.Availability.Should().Be(ThreadPoolBacklogAvailability.Available);
        result.WindowsAboveLevel.Should().Be(0);
        result.ProportionAboveLevel.Should().Be(0);
        result.LongestContiguousDurationAboveLevelSeconds.Should().Be(0);
        result.QueueMovement!.Median.Should().Be(0);
        result.WorkerMovement!.Median.Should().Be(4);
    }

    [Theory]
    [InlineData(1, 0, 0, false, ThreadPoolBacklogAvailability.LossDetected, ThreadPoolBacklogAbstentionReason.EventPipeLoss)]
    [InlineData(0, 1, 0, false, ThreadPoolBacklogAvailability.LossDetected, ThreadPoolBacklogAbstentionReason.DispatchLoss)]
    [InlineData(0, 0, 1, false, ThreadPoolBacklogAvailability.UnsupportedApproximation, ThreadPoolBacklogAbstentionReason.RetentionTruncated)]
    [InlineData(0, 0, 0, true, ThreadPoolBacklogAvailability.LossDetected, ThreadPoolBacklogAbstentionReason.ProcessingFailure)]
    public void DegradedEvidence_MandatorilyAbstains(
        long eventPipeLoss,
        long dispatchLoss,
        long truncated,
        bool processingFailed,
        ThreadPoolBacklogAvailability availability,
        ThreadPoolBacklogAbstentionReason reason)
    {
        var result = Evaluate(
            queue: [10, 12, 14, 16, 18, 20],
            workers: [4, 5, 6, 7, 8, 9],
            eventPipeLoss: eventPipeLoss,
            dispatchLoss: dispatchLoss,
            truncated: truncated,
            processingFailed: processingFailed);

        result.Availability.Should().Be(availability);
        result.AbstentionReasons.Should().Contain(reason);
        result.WindowsAboveLevel.Should().BeNull();
        result.ProportionAboveLevel.Should().BeNull();
        result.LongestContiguousDurationAboveLevelSeconds.Should().BeNull();
        result.DrainedAtTail.Should().BeNull();
        result.QueueMovement.Should().BeNull();
        result.WorkerMovement.Should().BeNull();
    }

    [Fact]
    public void IrregularCadence_AndInsufficientWindows_Abstain()
    {
        var irregular = Request(
            queue: [10, 12, 14, 16],
            workers: [4, 5, 6, 7]) with
        {
            QueueWindows =
            [
                Window(0, 10),
                Window(1, 12),
                Window(3, 14),
                Window(4, 16),
            ],
        };
        var insufficient = Request(
            queue: [10, 12, 14],
            workers: [4, 5, 6]);

        var irregularResult = ThreadPoolBacklogAnalyzer.Evaluate(irregular);
        var insufficientResult = ThreadPoolBacklogAnalyzer.Evaluate(insufficient);

        irregularResult.Availability.Should().Be(ThreadPoolBacklogAvailability.UnsupportedApproximation);
        irregularResult.AbstentionReasons.Should().Contain(ThreadPoolBacklogAbstentionReason.IrregularCadence);
        insufficientResult.Availability.Should().Be(ThreadPoolBacklogAvailability.InsufficientObservations);
        insufficientResult.AbstentionReasons.Should().Contain(ThreadPoolBacklogAbstentionReason.InsufficientWindows);
    }

    [Fact]
    public void CollectorP95_RemainsUnavailableWhenAbsent_AndPayloadIsBounded()
    {
        var result = Evaluate(
            queue: [10, 12, 14, 16, 18, 20],
            workers: [4, 5, 6, 7, 8, 9]);

        result.CollectorQueueP95Median.Should().BeNull();
        result.UnavailableFields.Should().ContainSingle(field =>
            field.Field == "collectorQueueP95Median");
        JsonSerializer.SerializeToUtf8Bytes(result).Length.Should().BeLessThan(4_096);
    }

    [Fact]
    public void Accumulator_IsBoundedAndReportsTruncation()
    {
        var accumulator = new ThreadPoolBacklogAccumulator(
            "capture-1",
            StartedAt,
            new ThreadPoolBacklogProtocol(10));
        for (var index = 0; index < ThreadPoolBacklogProtocol.MaximumWindows + 1; index++)
        {
            var timestamp = StartedAt.AddSeconds(index);
            accumulator.Observe(timestamp, Counter("threadpool-queue-length", index));
            accumulator.Observe(timestamp, Counter("threadpool-thread-count", index + 4));
        }

        var result = accumulator.Complete(
            StartedAt.AddSeconds(ThreadPoolBacklogProtocol.MaximumWindows + 1),
            eventPipeEventsLost: 0,
            droppedObservations: 0);

        result.QueueWindowCount.Should().Be(ThreadPoolBacklogProtocol.MaximumWindows);
        result.WorkerWindowCount.Should().Be(ThreadPoolBacklogProtocol.MaximumWindows);
        result.Availability.Should().Be(ThreadPoolBacklogAvailability.UnsupportedApproximation);
        result.AbstentionReasons.Should().Contain(ThreadPoolBacklogAbstentionReason.RetentionTruncated);
    }

    private static ThreadPoolBacklogEvidence Evaluate(
        double[] queue,
        double[] workers,
        ThreadPoolBacklogReason? reason = null,
        long eventPipeLoss = 0,
        long dispatchLoss = 0,
        long truncated = 0,
        bool processingFailed = false)
        => ThreadPoolBacklogAnalyzer.Evaluate(Request(queue, workers, reason) with
        {
            EventPipeEventsLost = eventPipeLoss,
            DroppedObservations = dispatchLoss,
            TruncatedWindows = truncated,
            ProcessingFailed = processingFailed,
        });

    private static ThreadPoolBacklogEvidenceRequest Request(
        double[] queue,
        double[] workers,
        ThreadPoolBacklogReason? reason = null)
        => new(
            "capture-1",
            StartedAt,
            StartedAt.AddSeconds(10),
            new ThreadPoolBacklogProtocol(10),
            queue.Select((value, index) => Window(index, value)).ToArray(),
            workers.Select((value, index) => Window(index, value)).ToArray(),
            reason ?? new ThreadPoolBacklogReason(null, ThreadPoolEvidence.Missing, null, null));

    private static ThreadPoolBacklogWindow Window(int second, double value) =>
        new(StartedAt.AddSeconds(second), 1, value);

    private static CounterValue Counter(string name, double value) =>
        new("System.Runtime", name, name, value, CounterKind.Mean)
        {
            IntervalSec = 1,
        };
}
