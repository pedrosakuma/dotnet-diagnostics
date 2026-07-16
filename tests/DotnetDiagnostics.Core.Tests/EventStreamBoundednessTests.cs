using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.ThreadPool;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class EventStreamBoundednessTests
{
    [Fact]
    public void InFlightRequestTracker_RetainsOldestRequestsWithinCap()
    {
        var tracker = new EventPipeInFlightRequestCollector.OldestPendingRequestTracker(capacity: 2);
        var startedAt = DateTimeOffset.UtcNow;

        tracker.Track("oldest", new EventPipeInFlightRequestCollector.PendingRequest("trace-1", null, "/oldest", "GET", startedAt));
        tracker.Track("newest", new EventPipeInFlightRequestCollector.PendingRequest("trace-2", null, "/newest", "GET", startedAt.AddSeconds(2)));
        tracker.Track("middle", new EventPipeInFlightRequestCollector.PendingRequest("trace-3", null, "/middle", "GET", startedAt.AddSeconds(1)));

        tracker.DroppedCount.Should().Be(1);
        tracker.GetPending()
            .Select(static request => request.Path)
            .Should()
            .BeEquivalentTo(["/oldest", "/middle"]);
    }

    [Fact]
    public void RequestsNowSnapshotQueue_ReportsOverflowAsIncompleteLowerBound()
    {
        var queue = new RequestsNowCollector.SnapshotCaptureQueue(capacity: 2);

        queue.TryWrite(new RequestsNowCollector.SnapshotCaptureRequest("first", 1)).Should().BeTrue();
        queue.TryWrite(new RequestsNowCollector.SnapshotCaptureRequest("second", 2)).Should().BeTrue();
        queue.TryWrite(new RequestsNowCollector.SnapshotCaptureRequest("dropped", 3)).Should().BeFalse();

        queue.DroppedCount.Should().Be(1);
        queue.BuildNotes().Should().ContainSingle()
            .Which.Should().ContainAll(
                "Dropped 1",
                "SnapshotQueueCapacity=2",
                "incomplete lower bounds");
    }

    [Fact]
    public void RequestsNowSnapshotQueue_DoesNotReportTruncationWithinCapacity()
    {
        var queue = new RequestsNowCollector.SnapshotCaptureQueue(capacity: 2);

        queue.TryWrite(new RequestsNowCollector.SnapshotCaptureRequest("first", 1)).Should().BeTrue();
        queue.TryWrite(new RequestsNowCollector.SnapshotCaptureRequest("second", 2)).Should().BeTrue();

        queue.DroppedCount.Should().Be(0);
        queue.BuildNotes().Should().BeEmpty();
    }

    [Fact]
    public void TopContentionEvents_RetainsLongestDurationsWithinCap()
    {
        var tracker = new EventPipeContentionCollector.TopContentionEvents(capacity: 2);

        tracker.Add(new ContentionEventSample(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10), 1, null, 1, 1, "A", "A"));
        tracker.Add(new ContentionEventSample(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(30), 2, null, 2, 2, "B", "B"));
        tracker.Add(new ContentionEventSample(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(20), 3, null, 3, 3, "C", "C"));

        tracker.DroppedCount.Should().Be(1);
        tracker.GetOrdered().Select(static item => item.Duration.TotalMilliseconds).Should().Equal(30, 20);
    }

    [Fact]
    public void DbAggregationState_UsesOverflowBucketWhenDistinctCommandsExceedCap()
    {
        var state = new DbEventAggregationState();
        var startedAt = DateTimeOffset.UtcNow;

        for (var i = 0; i < DbEventAggregationState.MaxTrackedCommandAggregates + 3; i++)
        {
            state.CompleteCommand(
                new PendingCommand(
                    "provider",
                    $"key-{i}",
                    $"hash-{i}",
                    $"select {i}",
                    $"server=tenant-{i}",
                    "scope",
                    startedAt),
                startedAt.AddMilliseconds(i + 1),
                i + 1);
        }

        var snapshot = state.BuildSnapshot(42, startedAt, TimeSpan.FromSeconds(1));

        snapshot.ByCommand.Should().HaveCount(DbEventAggregationState.MaxTrackedCommandAggregates + 1);
        snapshot.ByCommand.Should().Contain(static aggregate => aggregate.CommandTextHash == DbEventAggregationState.OverflowCommandTextHash);
        snapshot.Notes.Should().Contain(note => note.Contains("overflow bucket", StringComparison.Ordinal));
    }

    [Fact]
    public void DbAggregationState_BoundsPendingCommandsAndExpiresStaleEntries()
    {
        var state = new DbEventAggregationState();
        var startedAt = DateTimeOffset.UtcNow;

        state.SetPendingCommand(
            "expired",
            new PendingCommand("provider", "expired", "hash-expired", "select 1", "server=one", "scope", startedAt));

        state.SetPendingCommand(
            "fresh",
            new PendingCommand("provider", "fresh", "hash-fresh", "select 2", "server=two", "scope", startedAt.AddMinutes(3)));

        for (var i = 0; i < DbEventAggregationState.MaxTrackedPendingCommands + 5; i++)
        {
            state.SetPendingCommand(
                $"pending-{i}",
                new PendingCommand("provider", $"pending-{i}", $"hash-{i}", $"select {i}", $"server={i}", "scope", startedAt.AddMinutes(3).AddMilliseconds(i)));
        }

        state.TrackedPendingCommandCount.Should().Be(DbEventAggregationState.MaxTrackedPendingCommands);

        var snapshot = state.BuildSnapshot(42, startedAt, TimeSpan.FromMinutes(4));

        snapshot.Notes.Should().Contain(note => note.Contains("Expired", StringComparison.Ordinal));
        snapshot.Notes.Should().Contain(note => note.Contains("Evicted", StringComparison.Ordinal));
    }

    [Fact]
    public void FixedCapacityQueue_DropsOldestItemsWhenCapacityExceeded()
    {
        var queue = new EventPipeThreadPoolCollector.FixedCapacityQueue<int>(capacity: 2);

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);

        queue.DroppedCount.Should().Be(1);
        queue.Items.Should().Equal(2, 3);
    }
}
