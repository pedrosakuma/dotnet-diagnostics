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
