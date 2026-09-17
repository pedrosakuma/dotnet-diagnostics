using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class GcCaptureStateTests
{
    private static DateTimeOffset T(int ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);
    private static GcSuspensionEvidence Finish(GcCaptureState state, long lost = 0, string completion = "normal-stop") =>
        state.Finish(T(0), T(1000), null, lost, completion);
    private static void Pause(GcCaptureState state, int reason = 1, int thread = 10, int start = 10)
    {
        state.SuspendBegin(1, thread, 1, T(start), reason, 999);
        state.Boundary(1, thread, 1, T(start + 100), 0);
        state.Boundary(1, thread, 1, T(start + 110), 1);
        state.Boundary(1, thread, 1, T(start + 200), 2);
    }

    [Fact]
    public void BackgroundElapsed_IsSeparateFromShortPrimaryPhaseAndLongTails()
    {
        var state = new GcCaptureState(10);
        state.CollectionBegin(1, 20, 2, T(0), 2, "Induced", "BackgroundGC");
        Pause(state, 6);
        state.CollectionBegin(1, 21, 2, T(220), 0, "Induced", "ForegroundGC");
        Pause(state, start: 250);
        state.CollectionEnd(1, 21, 1, T(500));
        state.CollectionEnd(1, 20, 1, T(900));
        var result = Finish(state);
        result.TotalSuspensionTime.Should().Be(TimeSpan.FromMilliseconds(20));
        result.Intervals[0].AcquisitionDuration.Should().Be(TimeSpan.FromMilliseconds(100));
        result.Intervals[0].RestartDuration.Should().Be(TimeSpan.FromMilliseconds(90));
        result.Intervals[0].Duration.Should().Be(TimeSpan.FromMilliseconds(10));
        state.Collections.TotalPauseTime.Should().Be(TimeSpan.FromMilliseconds(1180));
        result.Intervals[0].GcCountAtSuspend.Should().Be(999);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    [InlineData(7, 0)]
    [InlineData(8, 0)]
    [InlineData(900, 0)]
    public void OnlyGcAndPrepReasonsAreIncluded(int reason, int expected)
    {
        var state = new GcCaptureState(10);
        Pause(state, reason);
        Finish(state).ObservedIntervals.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MissingPrimaryBoundary_NeverInventsAPause(int missing)
    {
        var state = new GcCaptureState(10);
        if (missing != 0) state.SuspendBegin(1, 10, 1, T(0), 1, 0);
        if (missing != 1) state.Boundary(1, 10, 1, T(10), 0);
        if (missing != 2) state.Boundary(1, 10, 1, T(20), 1);
        var result = Finish(state);
        result.Intervals.Should().BeEmpty();
        result.TotalSuspensionTime.Should().BeNull();
    }

    [Fact]
    public void MissingOptionalRestartTail_PreservesValidPrimary()
    {
        var state = new GcCaptureState(10);
        state.SuspendBegin(1, 10, 1, T(0), 1, 0);
        state.Boundary(1, 10, 1, T(10), 0);
        state.Boundary(1, 10, 1, T(20), 1);
        var result = Finish(state);
        result.TotalSuspensionTime.Should().Be(TimeSpan.FromMilliseconds(10));
        result.Limitations.Should().ContainKey("missing-restart-stop");
        result.Intervals.Single().RestartDuration.Should().BeNull();
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(1, 11)]
    public void IdentityMismatch_NeverPairsAcrossRuntimeOrThread(int clr, int thread)
    {
        var state = new GcCaptureState(10);
        state.SuspendBegin(1, 10, 1, T(0), 1, 0);
        state.Boundary(clr, thread, 1, T(10), 0);
        state.Boundary(clr, thread, 1, T(20), 1);
        Finish(state).TotalSuspensionTime.Should().BeNull();
    }

    [Fact]
    public void DetailCap_DoesNotLoseValidatedAggregate()
    {
        var state = new GcCaptureState(1);
        Pause(state);
        Pause(state, start: 300);
        var result = Finish(state);
        result.Intervals.Should().HaveCount(1);
        result.DroppedIntervals.Should().Be(1);
        result.TotalSuspensionTime.Should().Be(TimeSpan.FromMilliseconds(20));
    }

    [Theory]
    [InlineData(1, "normal-stop")]
    [InlineData(0, "processing-failure")]
    [InlineData(0, "early-exit")]
    [InlineData(0, "cancelled")]
    public void FinalDrainLossOrIncompleteProcessing_InvalidatesAuthoritativeTiming(long lost, string completion)
    {
        var state = new GcCaptureState(10);
        Pause(state);
        var result = Finish(state, lost, completion);
        result.Intervals.Should().ContainSingle();
        result.TotalSuspensionTime.Should().BeNull();
        result.Status.Should().Be("unreliable");
    }

    [Fact]
    public void EveryPendingCap_IsReportedAtInsertion()
    {
        var state = new GcCaptureState(1);
        for (uint i = 0; i < 300; i++) state.CollectionBegin(1, i, 2, T(0), 2, "test", "BackgroundGC");
        for (var i = 1; i <= 150; i++) state.SuspendBegin(1, i, 1, T(0), 1, 0);
        for (var i = 2; i <= 18; i++) state.CollectionBegin(i, 0, 2, T(0), 0, "test", "test");
        var result = Finish(state);
        result.Limitations["pending-collection-cap-256"].Should().Be(59);
        result.Limitations["suspension-state-cap-128"].Should().Be(22);
        result.Limitations["runtime-identity-cap-16"].Should().Be(2);
    }

    [Fact]
    public void CollectionCountsAreUnsignedAndClrScoped_AndDetailCapDoesNotAffectPauses()
    {
        var state = new GcCaptureState(1);
        state.CollectionBegin(1, uint.MaxValue, 2, T(0), 2, "test", "BackgroundGC");
        state.CollectionBegin(2, uint.MaxValue, 2, T(5), 0, "test", "ForegroundGC");
        state.CollectionEnd(2, uint.MaxValue, 1, T(10));
        state.CollectionEnd(1, uint.MaxValue, 1, T(900));
        state.Collections.TotalCollections.Should().Be(2);
        state.Collections.DroppedEvents.Should().Be(1);
        state.Collections.Events.Single().ClrInstanceId.Should().Be(2);
        state.Collections.Events.Single().CollectionCount.Should().Be(uint.MaxValue);
        Finish(state).Status.Should().Be("unreliable", "multiple runtimes cannot establish process-wide suspension");
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("regression")]
    [InlineData("retry")]
    [InlineData("legacy")]
    public void AmbiguousPairingIsNeverALowerBound(string error)
    {
        var state = new GcCaptureState(10);
        state.SuspendBegin(1, 10, error == "legacy" ? 0 : 1, T(100), 1, 0);
        if (error == "duplicate") state.SuspendBegin(1, 10, 1, T(101), 1, 0);
        if (error == "retry") state.Boundary(1, 10, 1, T(105), 1);
        state.Boundary(1, 10, 1, T(error == "regression" ? 99 : 110), 0);
        state.Boundary(1, 10, 1, T(120), 1);
        Finish(state).TotalSuspensionTime.Should().BeNull();
    }

    [Fact]
    public void NonGcBeginDoesNotInheritOldGcReason_UnknownNumbersRemainObservable()
    {
        var state = new GcCaptureState(10);
        state.SuspendBegin(1, 10, 1, T(0), 1, 0);
        Pause(state, 123);
        var result = Finish(state);
        result.Intervals.Should().BeEmpty();
        result.IgnoredReasons.Should().Equal(123);
    }

    [Fact]
    public void IntervalAndIgnoredReasonHardCapsRejectOrAccountAtInsertion()
    {
        var invalid = () => new GcCaptureState(GcCaptureState.MaxRetainedIntervals + 1);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        var state = new GcCaptureState(1);
        for (var reason = 100; reason < 120; reason++) Pause(state, reason, start: 0);
        var result = Finish(state);
        result.IgnoredReasons.Should().HaveCount(16);
        result.Limitations["ignored-reason-cap-16"].Should().Be(4);
    }
}
