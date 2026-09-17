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
        pairs.Start(failed, StartTime, "/failed");
        Assert.True(pairs.Fail(failed, StartTime));
        pairs.Start(failed, StartTime, "/reuse-after-failure");
        Assert.False(pairs.Stop(failed, StartTime, out _, out _));
        var completed = Guid.NewGuid();
        pairs.Start(completed, StartTime, "/ok");
        Assert.True(pairs.Stop(completed, StartTime, out _, out _));
        pairs.Start(completed, StartTime, "/reuse-after-completion");
        Assert.False(pairs.Stop(completed, StartTime, out _, out _));
        Assert.Equal(0, pairs.Snapshot().FailureDiscarded);
        Assert.Equal(4, pairs.Snapshot().AmbiguousStarts);
        AssertPartition(pairs);
    }

    [Fact]
    public void FailureIsNotTerminal_RepeatsDoNotDuplicateCompletionOrRenewTtl()
    {
        var pairs = new NetworkingActivityCorrelator<string>();
        var id = Guid.NewGuid();
        pairs.Start(id, StartTime, "/failed");
        Assert.True(pairs.Fail(id, StartTime.AddMilliseconds(100)));
        Assert.True(pairs.Fail(id, StartTime.AddMilliseconds(200)));
        Assert.Equal(1, pairs.PendingCount);
        Assert.Equal(1, pairs.Snapshot().UnfinishedFailed);
        Assert.Equal(0, pairs.Snapshot().Paired);
        Assert.True(pairs.Stop(id, StartTime.AddMilliseconds(255), out var path, out var elapsed, out var failed));
        Assert.Equal("/failed", path);
        Assert.Equal(TimeSpan.FromMilliseconds(255), elapsed);
        Assert.True(failed);
        Assert.False(pairs.Stop(id, StartTime.AddMilliseconds(300), out _, out _));
        Assert.False(pairs.Fail(id, StartTime.AddMilliseconds(301)));
        var counts = pairs.Snapshot();
        Assert.Equal(2, counts.LatencyPopulationVersion);
        Assert.Equal(1, counts.PairedFailed);
        Assert.Equal(0, counts.PairedWithoutFailure);
        Assert.Equal(2, counts.MatchedFailureEvents);
        Assert.Equal(1, counts.RepeatedFailureEvents);
        Assert.Equal(1, counts.UnmatchedFailures);
        AssertPartition(pairs);
    }

    [Theory]
    [InlineData("missing-stop")]
    [InlineData("expiry")]
    [InlineData("eviction")]
    [InlineData("duplicate")]
    [InlineData("capacity")]
    [InlineData("empty")]
    [InlineData("negative-failure")]
    [InlineData("backwards-failure")]
    [InlineData("stop-before-failure")]
    public void FailedIncompleteOrAmbiguousLifecycles_NeverInventSamples(string scenario)
    {
        var pairs = new NetworkingActivityCorrelator<string>(maxPending: 1, maxIdentities: scenario == "capacity" ? 1 : 16);
        var id = scenario == "empty" ? Guid.Empty : Guid.NewGuid();
        pairs.Start(id, StartTime, "/failed");
        pairs.Fail(id, StartTime.AddMilliseconds(scenario == "negative-failure" ? -1 : 100));
        switch (scenario)
        {
            case "missing-stop":
                Assert.Equal(1, pairs.Snapshot().UnfinishedFailed);
                break;
            case "expiry":
                Assert.True(pairs.Fail(id, StartTime.AddSeconds(119)));
                Assert.False(pairs.Stop(id, StartTime.AddMinutes(2), out _, out _));
                Assert.Equal(1, pairs.Snapshot().Expired);
                break;
            case "eviction":
            case "capacity":
                pairs.Start(Guid.NewGuid(), StartTime.AddSeconds(1), "/new");
                Assert.False(pairs.Stop(id, StartTime.AddSeconds(2), out _, out _));
                break;
            case "duplicate":
                pairs.Start(id, StartTime.AddMilliseconds(150), "/reused");
                Assert.False(pairs.Stop(id, StartTime.AddSeconds(1), out _, out _));
                Assert.Equal(2, pairs.Snapshot().AmbiguousStarts);
                break;
            case "backwards-failure":
                Assert.False(pairs.Fail(id, StartTime.AddMilliseconds(50)));
                Assert.False(pairs.Stop(id, StartTime.AddSeconds(1), out _, out _));
                Assert.Equal(1, pairs.Snapshot().InvalidTimestampFailures);
                break;
            default:
                Assert.False(pairs.Stop(id, StartTime.AddMilliseconds(50), out _, out _));
                break;
        }
        Assert.Equal(0, pairs.Snapshot().Paired);
        Assert.Equal(0, pairs.Snapshot().PairedFailed);
        Assert.True(pairs.Snapshot().HasLimitations);
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
