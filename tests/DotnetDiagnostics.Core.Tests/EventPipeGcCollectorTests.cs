using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class EventPipeGcCollectorTests
{
    [Fact]
    public void Aggregation_SaturatesLegacyCountsAndElapsedWithoutOverflow()
    {
        var state = new GcCaptureState(1);
        var aggregation = state.Collections;
        typeof(GcEventAggregation).GetProperty(nameof(GcEventAggregation.ObservedCollections))!
            .SetValue(aggregation, long.MaxValue - 1);
        aggregation.Add(new(DateTimeOffset.UnixEpoch, 0, "test", "test", TimeSpan.MaxValue));
        aggregation.Add(new(DateTimeOffset.UnixEpoch, 0, "test", "test", TimeSpan.FromTicks(1)));
        aggregation.ObservedCollections.Should().Be(long.MaxValue);
        aggregation.TotalCollections.Should().Be(int.MaxValue);
        aggregation.DroppedEvents.Should().Be(int.MaxValue);
        aggregation.TotalPauseTime.Should().Be(TimeSpan.MaxValue);
        state.Finish(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), null)
            .Limitations.Should().ContainKey("legacy-collection-count-saturated");
    }

    [Fact]
    public void Aggregation_PreservesExactTotalsAfterRawEventCap()
    {
        var aggregation = new GcEventAggregation(maxEvents: 200);
        var startedAt = new DateTimeOffset(2026, 7, 24, 22, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 250; i++)
        {
            aggregation.Add(new GcEvent(
                startedAt.AddMilliseconds(i),
                Generation: i % 3,
                Reason: "AllocSmall",
                Type: "NonConcurrentGC",
                PauseDuration: TimeSpan.FromTicks(i + 1)));
        }

        aggregation.TotalCollections.Should().Be(250);
        aggregation.Events.Should().HaveCount(200);
        aggregation.DroppedEvents.Should().Be(50);
        aggregation.TotalPauseTime.Should().Be(TimeSpan.FromTicks(31_375));
        aggregation.MaxPauseTime.Should().Be(TimeSpan.FromTicks(250));
        aggregation.Generations.Should().Equal(
            new GenerationStats(0, 84),
            new GenerationStats(1, 83),
            new GenerationStats(2, 83));
    }
}
