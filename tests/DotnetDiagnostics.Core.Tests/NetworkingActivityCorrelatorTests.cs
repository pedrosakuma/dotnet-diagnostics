using DotnetDiagnostics.Core.Networking;

namespace DotnetDiagnostics.Core.Tests;

public sealed class NetworkingActivityCorrelatorTests
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UnixEpoch;

    [Fact]
    public void DuplicateNonemptyIdentity_DiscardsBothAndAllLateStops()
    {
        var pairs = new NetworkingActivityCorrelator<string>();
        var id = Guid.NewGuid();
        pairs.Start(id, StartTime, "/slow");
        pairs.Start(id, StartTime.AddMilliseconds(1), "/fast");
        Assert.False(pairs.Stop(id, StartTime.AddMilliseconds(80), out _, out _));
        Assert.False(pairs.Stop(id, StartTime.AddMilliseconds(450), out _, out _));
        pairs.Start(id, StartTime.AddMinutes(10), "/reused");
        Assert.False(pairs.Stop(id, StartTime.AddMinutes(10.1), out _, out _));
        var unique = Guid.NewGuid();
        pairs.Start(unique, StartTime.AddMinutes(11), "/unique");
        Assert.True(pairs.Stop(unique, StartTime.AddMinutes(11).AddMilliseconds(123), out var path, out var elapsed));
        Assert.Equal("/unique", path);
        Assert.Equal(TimeSpan.FromMilliseconds(123), elapsed);
        Assert.Equal(3, pairs.Snapshot().AmbiguousStarts);
        AssertPartition(pairs);
    }

    [Fact]
    public void EmptyIdentity_IsNeverPaired()
    {
        var pairs = new NetworkingActivityCorrelator<string>();
        pairs.Start(Guid.Empty, StartTime, "/a");
        pairs.Start(Guid.Empty, StartTime, "/b");
        Assert.False(pairs.Stop(Guid.Empty, StartTime.AddSeconds(1), out _, out _));
        Assert.Equal(2, pairs.Snapshot().EmptyStarts);
        Assert.Equal(1, pairs.Snapshot().EmptyStops);
        Assert.Equal(0, pairs.RememberedCount);
        AssertPartition(pairs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiryAndEviction_RetireIdentityForWholeCapture(bool expire)
    {
        var pairs = new NetworkingActivityCorrelator<string>(maxPending: 1);
        var old = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        pairs.Start(old, StartTime, "/old");
        var next = expire ? StartTime.AddMinutes(2) : StartTime.AddSeconds(1);
        pairs.Start(fresh, next, "/fresh");
        pairs.Start(old, next, "/unsafe-reuse");
        Assert.False(pairs.Stop(old, next.AddSeconds(1), out _, out _));
        Assert.True(pairs.Stop(fresh, next.AddSeconds(1), out var path, out _));
        Assert.Equal("/fresh", path);
        Assert.Equal(expire ? 1 : 0, pairs.Snapshot().Expired);
        Assert.Equal(expire ? 0 : 1, pairs.Snapshot().Evicted);
        AssertPartition(pairs);
    }

    [Fact]
    public void IdentityCap_FailsClosedIncludingAlreadyPendingAndNeverRecovers()
    {
        var pairs = new NetworkingActivityCorrelator<string>(maxPending: 2, maxIdentities: 2);
        var first = Guid.NewGuid();
        pairs.Start(first, StartTime, "/a");
        pairs.Start(Guid.NewGuid(), StartTime, "/b");
        pairs.Start(Guid.NewGuid(), StartTime, "/cap");
        Assert.False(pairs.Stop(first, StartTime.AddSeconds(1), out _, out _));
        for (var i = 0; i < 100; i++)
        {
            var id = Guid.NewGuid();
            pairs.Start(id, StartTime.AddHours(1), "/suppressed");
            Assert.False(pairs.Stop(id, StartTime.AddHours(1), out _, out _));
        }
        Assert.True(pairs.Snapshot().IdentityCapacityReached);
        Assert.Equal(103, pairs.Snapshot().CapacitySuppressedStarts);
        Assert.Equal(0, pairs.PendingCount);
        Assert.Equal(2, pairs.RememberedCount);
        AssertPartition(pairs);
    }

    [Fact]
    public void OrphanStop_AndFailure_AndCompletedIdentity_CannotConsumeLaterStart()
    {
        var pairs = new NetworkingActivityCorrelator<string>();
        var orphan = Guid.NewGuid();
        Assert.False(pairs.Fail(Guid.NewGuid(), StartTime));
        Assert.Equal(1, pairs.Snapshot().UnmatchedFailures);
        Assert.False(pairs.Stop(orphan, StartTime, out _, out _));
        pairs.Start(orphan, StartTime, "/late");
        Assert.False(pairs.Stop(orphan, StartTime, out _, out _));
        var failed = Guid.NewGuid();
        pairs.Start(failed, StartTime, "/failure-population-remains-separate");
        Assert.True(pairs.Fail(failed, StartTime));
        pairs.Start(failed, StartTime, "/reuse-after-failure");
        Assert.False(pairs.Stop(failed, StartTime, out _, out _));
        var completed = Guid.NewGuid();
        pairs.Start(completed, StartTime, "/ok");
        Assert.True(pairs.Stop(completed, StartTime, out _, out _));
        pairs.Start(completed, StartTime, "/reuse-after-completion");
        Assert.False(pairs.Stop(completed, StartTime, out _, out _));
        Assert.Equal(1, pairs.Snapshot().FailureDiscarded);
        AssertPartition(pairs);
    }

    [Fact]
    public void UnmatchedStopCanSaturateHistory_WithoutManufacturingRecovery()
    {
        var pairs = new NetworkingActivityCorrelator<int>(maxPending: 1, maxIdentities: 1);
        var id = Guid.NewGuid();
        pairs.Start(id, StartTime, 1);
        Assert.False(pairs.Stop(Guid.NewGuid(), StartTime, out _, out _));
        Assert.False(pairs.Stop(id, StartTime, out _, out _));
        Assert.True(pairs.Snapshot().IdentityCapacityReached);
        Assert.Equal(1, pairs.Snapshot().CapacitySuppressedStarts);
        AssertPartition(pairs);
    }

    [Fact]
    public void NegativeTimestamp_IsNotClampedIntoAValidLatency()
    {
        var pairs = new NetworkingActivityCorrelator<string>();
        var id = Guid.NewGuid();
        pairs.Start(id, StartTime, "/clock");
        Assert.False(pairs.Stop(id, StartTime.AddTicks(-1), out _, out _));
        Assert.Equal(1, pairs.Snapshot().InvalidTimestampStops);
        AssertPartition(pairs);
    }

    [Fact]
    public void ProductionPendingLimitAndTtl_AreInsertionBounded()
    {
        var pairs = new NetworkingActivityCorrelator<int>();
        for (var i = 0; i < 4097; i++)
            pairs.Start(Guid.NewGuid(), StartTime, i);
        Assert.Equal(4096, pairs.PendingCount);
        Assert.Equal(1, pairs.Snapshot().Evicted);
        pairs.Start(Guid.NewGuid(), StartTime.AddMinutes(2), 4098);
        Assert.Equal(4096, pairs.Snapshot().Expired);
        Assert.Equal(1, pairs.Snapshot().Unfinished);
        AssertPartition(pairs);
    }

    private static void AssertPartition<T>(NetworkingActivityCorrelator<T> pairs)
    {
        var counts = pairs.Snapshot();
        Assert.Equal(counts.Started, counts.Paired + counts.EmptyStarts + counts.AmbiguousStarts
            + counts.Expired + counts.Evicted + counts.FailureDiscarded + counts.Unfinished
            + counts.CapacitySuppressedStarts);
    }
}
