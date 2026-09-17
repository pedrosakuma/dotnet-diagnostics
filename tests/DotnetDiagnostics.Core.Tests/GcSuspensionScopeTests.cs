using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class GcSuspensionScopeTests
{
    private static DateTimeOffset At(int milliseconds) => DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds);

    [Fact]
    public void BlockingCollectionElapsedCanBeStrictlyInsideFullySuspendedInterval()
    {
        var state = new GcCaptureState(4);
        state.SuspendBegin(1, 10, 1, At(0), reason: 1, count: 11);
        state.Boundary(1, 10, 1, At(10), phase: 0); // SuspendEEStop: managed threads are suspended.
        state.CollectionBegin(1, 12, 2, At(20), 2, "Induced", "NonConcurrentGC");
        state.CollectionEnd(1, 12, 1, At(40));
        state.Boundary(1, 10, 1, At(50), phase: 1); // RestartEEStart: fully-suspended phase ends.
        state.Boundary(1, 10, 1, At(60), phase: 2);

        var evidence = state.Finish(At(0), At(100), At(-1000));
        evidence.IsAuthoritative.Should().BeTrue();
        evidence.Limitations.Should().BeEmpty();
        evidence.ObservedIntervals.Should().Be(1);
        evidence.DroppedIntervals.Should().Be(0);
        var interval = evidence.Intervals.Should().ContainSingle().Subject;
        interval.StartedAt.Should().Be(At(10));
        interval.StoppedAt.Should().Be(At(50));
        interval.AcquisitionDuration.Should().Be(TimeSpan.FromMilliseconds(10));
        interval.RestartDuration.Should().Be(TimeSpan.FromMilliseconds(10));
        interval.GcCountAtSuspend.Should().Be(11);
        var collection = state.Collections.Events.Should().ContainSingle().Subject;
        collection.CollectionCount.Should().Be(12);
        collection.Timestamp.Should().Be(At(20));
        (collection.Timestamp + collection.CollectionElapsedDuration).Should().Be(At(40));
        state.Collections.TotalPauseTime.Should().Be(TimeSpan.FromMilliseconds(20));
        evidence.TotalSuspensionTime.Should().Be(TimeSpan.FromMilliseconds(40));
        evidence.TotalSuspensionTime.Should().BeGreaterThan(state.Collections.TotalPauseTime,
            "GCStart/GCStop are collection boundaries, not suspend/restart boundaries");
    }

    [Fact]
    public void PreparationSuspensionNeedsNoCompletedCollectionForPositiveMeasuredTime()
    {
        var state = new GcCaptureState(4);
        // Runtime heap-count preparation can suspend/restart without emitting any collection pair.
        state.SuspendBegin(1, 10, 1, At(10), reason: 6, count: 99);
        state.Boundary(1, 10, 1, At(20), phase: 0);
        state.Boundary(1, 10, 1, At(50), phase: 1);
        state.Boundary(1, 10, 1, At(60), phase: 2);

        var evidence = state.Finish(At(0), At(100), At(-1000));
        evidence.IsAuthoritative.Should().BeTrue();
        evidence.Limitations.Should().BeEmpty();
        evidence.ObservedCollectionPairs.Should().Be(0);
        state.Collections.Events.Should().BeEmpty();
        state.Collections.TotalPauseTime.Should().Be(TimeSpan.Zero);
        var interval = evidence.Intervals.Should().ContainSingle().Subject;
        interval.Reason.Should().Be(6);
        interval.GcCountAtSuspend.Should().Be(99);
        interval.StartedAt.Should().Be(At(20));
        interval.StoppedAt.Should().Be(At(50));
        evidence.TotalSuspensionTime.Should().Be(TimeSpan.FromMilliseconds(30));
        evidence.TotalSuspensionTime.Should().BeGreaterThan(state.Collections.TotalPauseTime);
    }
}
