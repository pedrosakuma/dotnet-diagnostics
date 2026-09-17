using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class GcProgressEvidenceTests
{
    private static DateTimeOffset T(long ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);

    private static async Task<(GcProgressEvidence Evidence, GcSummary Summary)> Capture(
        long progressQpc = 150, long clockShift = 0, bool acknowledged = true, int status = 0,
        bool omitProgress = false, bool reverseCallbacks = false, long armedQpc = 10)
    {
        var evidence = new GcProgressEvidence();
        evidence.Marker(2, 0, armedQpc, 11);
        evidence.Marker(3, 0, 20, 11, 0);
        if (acknowledged) await evidence.AcknowledgeArmAsync(0, CancellationToken.None);
        evidence.Marker(4, 0, 50, 22);
        // Real collection pairing, not a second implementation of the matcher.
        var state = new GcCaptureState(10);
        state.CollectionBegin(7, 42, 2, T(100 + clockShift), 2, "Induced", "BackgroundGC");
        state.SuspendBegin(7, 2, 1, T(101 + clockShift), 1, 42);
        state.Boundary(7, 2, 1, T(102 + clockShift), 0);
        state.Boundary(7, 2, 1, T(103 + clockShift), 1);
        state.Boundary(7, 2, 1, T(104 + clockShift), 2);
        state.CollectionEnd(7, 42, 1, T(200 + clockShift));
        var suspension = state.Finish(T(clockShift), T(400 + clockShift), null, 0, "normal-stop");
        suspension.IsAuthoritative.Should().BeTrue();
        var summary = new GcSummary(123, T(clockShift), TimeSpan.FromMilliseconds(400), 1,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), [], state.Collections.Events.ToArray(),
            Suspension: suspension);
        if (reverseCallbacks) evidence.Stop(7, 42, 200, T(200 + clockShift));
        evidence.Start(7, 42, "BackgroundGC", 100, T(100 + clockShift));
        evidence.Marker(5, 0, 120, 22);
        if (!reverseCallbacks) evidence.Stop(7, 42, 200, T(200 + clockShift));
        evidence.Marker(6, 0, 300, 22, 2, status);
        // Progress can be delivered after completion; balance is evaluated after stream drain.
        if (!omitProgress) evidence.Marker(3, 0, progressQpc, 11, 1);
        return (evidence, summary);
    }

    [Fact]
    public async Task PreArmedWitnessProvesProgressEvenWhenPostReturnObserverIsLate()
    {
        var (evidence, summary) = await Capture();
        evidence.Evaluate(summary, 0).ProvesProgress.Should().BeTrue();
        evidence.Evaluate(summary, 0).Collections.Single().Inside.Should().Be(1);
        // A first post-return heartbeat at QPC 250 would miss the actual [100,200) collection.
        var (late, lateSummary) = await Capture(progressQpc: 250);
        late.Evaluate(lateSummary, 0).ProvesProgress.Should().BeFalse();
    }

    [Theory]
    [InlineData(-1000, 150, true)]
    [InlineData(1000, 150, true)]
    [InlineData(100, 250, false)]
    [InlineData(-100, 50, false)]
    public async Task IndependentWallClockOffsetsCannotChangeQpcContainment(long shift, long progress, bool expected)
    {
        var (evidence, summary) = await Capture(progressQpc: progress, clockShift: shift);
        var collection = summary.Events.Single();
        var mixedClockMatch = T(progress) > collection.Timestamp &&
            T(progress) < collection.Timestamp + collection.CollectionElapsedDuration;
        mixedClockMatch.Should().Be(!expected, "the historical mixed-clock comparison misclassifies these facts");
        evidence.Evaluate(summary, 0).ProvesProgress.Should().Be(expected);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(250)]
    public async Task BoundariesAndOutsideSamplesNeverProveProgress(long progress)
    {
        var (evidence, summary) = await Capture(progressQpc: progress);
        evidence.Evaluate(summary, 0).ProvesProgress.Should().BeFalse();
    }

    [Fact]
    public async Task CallbackArrivalOrderDoesNotDetermineOccurrenceOrder()
    {
        var (evidence, summary) = await Capture(reverseCallbacks: true);
        evidence.Evaluate(summary, 0).ProvesProgress.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, 0, false, 10)]
    [InlineData(true, 1, false, 10)]
    [InlineData(true, 2, false, 10)]
    [InlineData(true, 0, true, 10)]
    [InlineData(true, 0, false, 110)]
    public async Task UnacknowledgedDeadlineCapLossOrLateArmingCannotPass(bool ack, int status, bool omit, long armed)
    {
        var (evidence, summary) = await Capture(acknowledged: ack, status: status, omitProgress: omit, armedQpc: armed);
        evidence.Evaluate(summary, 0).ProvesProgress.Should().BeFalse();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("clr")]
    [InlineData("count")]
    [InlineData("start")]
    [InlineData("elapsed")]
    [InlineData("type")]
    [InlineData("loss")]
    [InlineData("completion")]
    public async Task MissingOrMismatchedPublishedEvidenceCannotPass(string change)
    {
        var (evidence, summary) = await Capture();
        var row = summary.Events.Single();
        summary = change switch
        {
            "missing" => summary with { Events = [] },
            "clr" => summary with { Events = [row with { ClrInstanceId = 8 }] },
            "count" => summary with { Events = [row with { CollectionCount = 43 }] },
            "start" => summary with { Events = [row with { Timestamp = row.Timestamp.AddTicks(1) }] },
            "elapsed" => summary with { Events = [row with { PauseDuration = TimeSpan.FromMilliseconds(101) }] },
            "type" => summary with { Events = [row with { Type = "NonConcurrentGC" }] },
            "completion" => summary with { Suspension = null },
            _ => summary,
        };
        evidence.Evaluate(summary, change == "loss" ? 1 : 0).ProvesProgress.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidRequestProgressCapAndDuplicateSequenceAreFailClosed()
    {
        foreach (var kind in new[] { "request", "cap", "duplicate" })
        {
            var (evidence, summary) = await Capture();
            if (kind == "request") evidence.Marker(2, GcProgressEvidence.MaxRequests, 10, 11);
            else evidence.Marker(3, 0, 151, 11, kind == "cap" ? GcProgressEvidence.MaxSamples : 1);
            evidence.Evaluate(summary, 0).ProvesProgress.Should().BeFalse();
        }
    }

    [Fact]
    public async Task MissingArmCanBeCancelledWithoutClaimingReadiness()
    {
        var evidence = new GcProgressEvidence();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var action = () => evidence.AcknowledgeArmAsync(0, cancelled.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }
}
