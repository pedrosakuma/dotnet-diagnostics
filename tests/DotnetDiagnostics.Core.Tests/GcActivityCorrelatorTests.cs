using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class GcActivityCorrelatorTests
{
    [Fact]
    public void Correlate_KeepsOnlyTopNImpactedActivities_WhileTrackingFullTotals()
    {
        var at = DateTimeOffset.UtcNow;
        var activities = new ActivityCapture(
            ProcessId: 42,
            SourceFilters: null,
            StartedAt: at,
            Duration: TimeSpan.FromSeconds(1),
            TotalActivities: 3,
            CompletedActivities: 3,
            Activities:
            [
                new CapturedActivity("Svc", "A", "a", null, null, null, null, at, at.AddMilliseconds(200), TimeSpan.FromMilliseconds(200), new Dictionary<string, string>()),
                new CapturedActivity("Svc", "B", "b", null, null, null, null, at.AddMilliseconds(50), at.AddMilliseconds(450), TimeSpan.FromMilliseconds(400), new Dictionary<string, string>()),
                new CapturedActivity("Svc", "C", "c", null, null, null, null, at.AddMilliseconds(500), at.AddMilliseconds(900), TimeSpan.FromMilliseconds(400), new Dictionary<string, string>()),
            ],
            BySource: Array.Empty<ActivitySourceSummary>(),
            ByOperation: Array.Empty<ActivityOperationSummary>());

        var gcSummary = new GcSummary(
            ProcessId: 42,
            StartedAt: at,
            Duration: TimeSpan.FromSeconds(1),
            TotalCollections: 3,
            TotalPauseTime: TimeSpan.FromMilliseconds(300),
            MaxPauseTime: TimeSpan.FromMilliseconds(150),
            Generations: [new GenerationStats(2, 3)],
            Events:
            [
                new GcEvent(at.AddMilliseconds(520), 2, "AllocSmall", "NonConcurrentGC", TimeSpan.FromMilliseconds(120)),
                new GcEvent(at.AddMilliseconds(20), 2, "AllocSmall", "NonConcurrentGC", TimeSpan.FromMilliseconds(150)),
                new GcEvent(at.AddMilliseconds(250), 2, "AllocSmall", "NonConcurrentGC", TimeSpan.FromMilliseconds(30)),
            ]);

        var overlay = GcActivityCorrelator.Correlate(activities, gcSummary, topN: 2);

        overlay.ImpactedCount.Should().Be(3);
        overlay.ReturnedCount.Should().Be(2);
        overlay.TotalGcOverlapMs.Should().Be(420);
        overlay.ImpactedActivities.Select(static activity => activity.OperationName)
            .Should()
            .Equal("A", "B");
        overlay.CorrelationTruncated.Should().BeFalse();
        overlay.CorrelationScope.Should().Be("full-window");
        overlay.CorrelationValuesAreLowerBounds.Should().BeFalse();
        overlay.ImpactedActivities.Should().OnlyContain(static activity => !activity.GcPauseIsLowerBound);
    }

    [Fact]
    public void Correlate_WhenGcEventsExceededRetentionCap_LabelsPrefixValuesAsLowerBounds()
    {
        var at = new DateTimeOffset(2026, 7, 24, 22, 30, 0, TimeSpan.Zero);
        var activities = new ActivityCapture(
            ProcessId: 42,
            SourceFilters: null,
            StartedAt: at,
            Duration: TimeSpan.FromSeconds(2),
            TotalActivities: 2,
            CompletedActivities: 2,
            Activities:
            [
                new CapturedActivity(
                    "Svc", "RetainedOverlap", "a", null, null, null, null,
                    at, at.AddMilliseconds(100), TimeSpan.FromMilliseconds(100),
                    new Dictionary<string, string>()),
                new CapturedActivity(
                    "Svc", "PotentialDroppedOverlap", "b", null, null, null, null,
                    at.AddSeconds(1), at.AddMilliseconds(1100), TimeSpan.FromMilliseconds(100),
                    new Dictionary<string, string>()),
            ],
            BySource: Array.Empty<ActivitySourceSummary>(),
            ByOperation: Array.Empty<ActivityOperationSummary>());
        var retainedEvents = Enumerable.Range(0, 200)
            .Select(index => new GcEvent(
                at.AddTicks(index),
                2,
                "AllocSmall",
                "NonConcurrentGC",
                TimeSpan.FromTicks(1)))
            .ToList();
        var gcSummary = new GcSummary(
            ProcessId: 42,
            StartedAt: at,
            Duration: TimeSpan.FromSeconds(2),
            TotalCollections: 250,
            TotalPauseTime: TimeSpan.FromTicks(250),
            MaxPauseTime: TimeSpan.FromTicks(1),
            Generations: [new GenerationStats(2, 250)],
            Events: retainedEvents,
            DroppedEvents: 50);

        var outcome = CollectionQueryDispatcher.Dispatch(
            CollectionHandleKinds.Activities,
            "gc-overlay",
            activities,
            topN: 10,
            correlateArtifact: gcSummary);
        var overlay = outcome.Result!.Payload.Should().BeOfType<GcOverlayResult>().Subject;

        overlay.TotalGcCollections.Should().Be(250);
        overlay.TotalGcPauseMs.Should().Be(TimeSpan.FromTicks(250).TotalMilliseconds);
        overlay.RetainedGcEvents.Should().Be(200);
        overlay.DroppedGcEvents.Should().Be(50);
        overlay.CorrelationTruncated.Should().BeTrue();
        overlay.CorrelationScope.Should().Be("retained-prefix");
        overlay.CorrelationValuesAreLowerBounds.Should().BeTrue();
        overlay.ImpactedCount.Should().Be(1);
        overlay.ImpactedActivities.Should().ContainSingle()
            .Which.GcPauseIsLowerBound.Should().BeTrue();
    }

    [Fact]
    public void BuildView_NotesWhenTimerAddressTrackingFallsBackToApproximateCounts()
    {
        var aggregation = new ClrMdTaskTimerAnalyzer.RawTaskTimerAggregation(maxTrackedTimerAddresses: 1);
        aggregation.TryTrackTimerAddress(0x1000).Should().BeTrue();
        aggregation.TryTrackTimerAddress(0x1000).Should().BeFalse();
        aggregation.TryTrackTimerAddress(0x2000).Should().BeTrue();
        aggregation.TimerAddressTrackingTruncated.Should().BeTrue();
        aggregation.TotalTimers = 2;
        aggregation.TimersByCallback[new ClrMdTaskTimerAnalyzer.TimerCallbackKey(
            "System.Threading.TimerQueueTimer",
            null,
            "Demo.Timer",
            "Tick",
            null,
            null,
            0,
            false)] = new ClrMdTaskTimerAnalyzer.RawTimerCallbackStat(
            new ClrMdTaskTimerAnalyzer.TimerCallbackKey(
                "System.Threading.TimerQueueTimer",
                null,
                "Demo.Timer",
                "Tick",
                null,
                null,
                0,
                false),
            method: null)
        {
            Count = 2,
        };

        var view = ClrMdTaskTimerAnalyzer.BuildView(
            aggregation,
            topN: 5,
            buildTypeIdentity: static _ => null,
            tryReadMvid: static _ => null);

        view.Notes.Should().Contain(note => note.Contains("de-duplication hit its safety cap", StringComparison.Ordinal));
    }
}
