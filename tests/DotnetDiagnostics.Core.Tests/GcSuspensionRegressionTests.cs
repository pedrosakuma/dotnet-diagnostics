using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class GcSuspensionRegressionTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Fact]
    public void ActivityCandidateLoss_IsNotCompleteEvenWithoutCollectionRowLoss()
    {
        var result = GcActivityCorrelator.Correlate(Activities(99), LegacyGc(), 10);
        result.CorrelationScope.Should().NotBe("full-window");
    }

    [Fact]
    public void LegacyCollectionElapsed_IsNotAuthoritativePause()
    {
        var result = GcActivityCorrelator.Correlate(Activities(0), LegacyGc(), 10);
        result.ImpactedActivities.Should().BeEmpty();
        result.CorrelationScope.Should().NotBe("full-window");
    }

    [Fact]
    public void NestedIntervals_AreExactlyEighty_NotSumOrClamp()
    {
        var gc = WithPauses((10, 90), (30, 70));
        var result = GcActivityCorrelator.Correlate(Activities(0), gc, 10);
        result.ImpactedActivities.Single().GcPauseMs.Should().Be(80);
        result.ImpactedActivities.Single().GcPausePercent.Should().Be(80);
        GcActivityCorrelator.Correlate(Activities(0), WithPauses((30, 70), (10, 90), (10, 90)), 1)
            .TotalGcOverlapMs.Should().Be(80);
    }

    [Fact]
    public void Union_AgreesWithIndependentIntegerGridOracle()
    {
        var random = new Random(950);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var ranges = Enumerable.Range(0, 12).Select(_ =>
            {
                var left = random.Next(-10, 110);
                return (left, left + random.Next(0, 80));
            }).ToArray();
            var expected = Enumerable.Range(0, 100).Count(i => ranges.Any(p => p.left <= i && p.Item2 > i));
            var result = GcActivityCorrelator.Correlate(Activities(0), WithPauses(ranges), 10);
            result.TotalGcOverlapMs.Should().Be(expected);
        }
    }

    [Fact]
    public void ActivityLoss_DoesNotMakeRetainedSpanPauseALowerBound()
    {
        var result = GcActivityCorrelator.Correlate(Activities(99), WithPauses((10, 90)), 1);
        result.CandidateSelection.Should().Be("incomplete");
        result.CorrelationTruncated.Should().BeTrue();
        result.ImpactedActivities.Single().GcPauseIsLowerBound.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0, 50, false, false)]
    [InlineData(99, 0, 0, true, false)]
    [InlineData(0, 10, 0, true, true)]
    [InlineData(99, 10, 50, true, true)]
    public void InputLossAxesRemainIndependent(int activitiesLost, long pausesLost, int collectionsLost,
        bool truncated, bool spanLowerBound)
    {
        var gc = WithPauses((10, 90));
        gc = gc with { DroppedEvents = collectionsLost, Suspension = gc.Suspension! with { DroppedIntervals = pausesLost } };
        var overlay = GcActivityCorrelator.Correlate(Activities(activitiesLost), gc, 1);
        overlay.CorrelationTruncated.Should().Be(truncated);
        overlay.ImpactedActivities.Single().GcPauseIsLowerBound.Should().Be(spanLowerBound);
        overlay.TotalGcOverlapMs.Should().Be(80);
    }

    [Fact]
    public void FilteredCandidatesAreNotCapLoss_AndLegacyRetentionIsUnknown()
    {
        var filtered = Activities(0) with { Retention = new("abcdef0123456789abcdef0123456789", 1, 100, 1, 1, 0, 99) };
        GcActivityCorrelator.Correlate(filtered, WithPauses((10, 90)), 1).CorrelationScope.Should().Be("full-window");
        var legacy = filtered with { Retention = null };
        GcActivityCorrelator.Correlate(legacy, WithPauses((10, 90)), 1).CandidateSelection.Should().Be("unknown");
    }

    [Fact]
    public void TwoActivitiesSharePause_WithoutGlobalDedup_AndTopNIsProjection()
    {
        var activities = Activities(0);
        activities = activities with { Activities = [activities.Activities[0], activities.Activities[0] with { Id = "b" }] };
        var result = GcActivityCorrelator.Correlate(activities, WithPauses((10, 90)), 1);
        result.TotalGcOverlapMs.Should().Be(160);
        result.ImpactedCount.Should().Be(2);
        result.OutputOmittedActivities.Should().Be(1);
        result.CorrelationTruncated.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(40)]
    public void InvalidOrZeroElapsed_DoesNotProduceHealthyZeroPercent(int endpoint)
    {
        var activities = Activities(0);
        activities = activities with { Activities = [activities.Activities[0] with
        {
            StoppedAt = At.AddMilliseconds(endpoint), Duration = TimeSpan.FromMilliseconds(endpoint == 0 ? 0 : 100),
        }] };
        var result = GcActivityCorrelator.Correlate(activities, WithPauses((10, 90)), 1);
        result.InvalidOrZeroDurationSpans.Should().Be(1);
        result.ImpactedActivities.Should().BeEmpty();
    }

    [Fact]
    public void WindowIntersectionAndTimezoneOffsetsUseUtcInstants()
    {
        var gc = WithPauses((10, 90));
        gc = gc with { Suspension = gc.Suspension! with { ObservationStart = At.AddMilliseconds(20).ToOffset(TimeSpan.FromHours(3)) } };
        var result = GcActivityCorrelator.Correlate(Activities(0), gc, 1);
        result.TotalGcOverlapMs.Should().Be(70);
        result.WindowGapSpans.Should().Be(1);
        result.ImpactedActivities.Single().GcPausePercent.Should().Be(70);
        var shift = TimeSpan.FromDays(10_000);
        var activities = Activities(0);
        activities = activities with
        {
            StartedAt = activities.StartedAt + shift,
            Activities = activities.Activities.Select(a => a with { StartedAt = a.StartedAt + shift, StoppedAt = a.StoppedAt + shift }).ToArray(),
        };
        var moved = gc with
        {
            StartedAt = gc.StartedAt + shift,
            Suspension = gc.Suspension with
            {
                ObservationStart = gc.Suspension.ObservationStart + shift,
                ObservationEnd = gc.Suspension.ObservationEnd + shift,
                Intervals = gc.Suspension.Intervals.Select(p => p with { StartedAt = p.StartedAt + shift, StoppedAt = p.StoppedAt + shift }).ToArray(),
            },
        };
        GcActivityCorrelator.Correlate(activities, moved, 1).TotalGcOverlapMs.Should().Be(result.TotalGcOverlapMs);
    }

    [Fact]
    public void IncompatiblePidLifetimeAndWindowsFailSharedDispatcher()
    {
        var gc = WithPauses((10, 90));
        var activities = Activities(0) with { ProcessStartedAt = At };
        foreach (var other in new[]
        {
            gc with { ProcessId = 43 },
            gc with { Suspension = gc.Suspension! with { ProcessStartedAt = At.AddSeconds(1) } },
            gc with { Suspension = gc.Suspension! with { ObservationStart = At.AddSeconds(1), ObservationEnd = At.AddSeconds(2) } },
        })
        {
            var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Activities, "gc-overlay", activities, 1, other);
            outcome.Result.Should().BeNull();
            outcome.InvalidArgument.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void UnreliableAndLegacyPauseNeverFeedsSignalOrComparison_AfterJsonRoundTrip()
    {
        var gc = WithPauses((10, 90));
        foreach (var snapshot in new[]
        {
            LegacyGc(),
            gc with { Suspension = gc.Suspension! with { Status = "unreliable", TotalSuspensionTime = null } },
        })
        {
            var roundTrip = System.Text.Json.JsonSerializer.Deserialize<GcSummary>(System.Text.Json.JsonSerializer.Serialize(snapshot))!;
            DotnetDiagnostics.Core.Signals.GcSignals.Detect(roundTrip, "gc")
                .Should().NotContain(s => s.Signal.Contains("suspended", StringComparison.Ordinal));
            var comparable = new DotnetDiagnostics.Core.Comparison.GcEventsComparableProjector().Project(roundTrip, "test");
            comparable.Metrics.Should().NotContain(m => m.Definition.Name.Contains("Suspended", StringComparison.Ordinal));
            comparable.Quality.Should().NotBeNull();
            comparable.Quality!.Conclusions.RetainedExplicitPositiveEvidence.Should()
                .NotBe(DotnetDiagnostics.Core.Evidence.EvidenceConclusionSupport.Supported);
            roundTrip.MeasurementSummary.Should().Contain("unavailable");
            var payload = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "longestPauses", roundTrip, 1)
                .Result!.Payload.Should().BeOfType<GcMeasurementView>().Subject;
            ((Array)payload.Data).Length.Should().Be(0);
            payload.MeasurementStatus.Should().NotBe("no-detected-loss");
        }
        var metrics = new DotnetDiagnostics.Core.Comparison.GcEventsComparableProjector().Project(gc, "test").Metrics;
        metrics.Should().Contain(m => m.Definition.Name == "fullySuspendedTimeMs.v2");
        metrics.Should().NotContain(m => m.Definition.Name == "totalPauseTimeMs");
    }

    [Fact]
    public void QueryJsonPreservesMeasurementAndQuality_WhenDetailsAreProjectedOut()
    {
        var gc = WithPauses((10, 90));
        gc = gc with { Suspension = gc.Suspension! with { DroppedIntervals = 3 } };
        var view = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "summary", gc, 1).Result!.Payload;
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var json = System.Text.Json.JsonSerializer.SerializeToElement(view, options);
        json.GetProperty("measurementVersion").GetInt32().Should().Be(2);
        json.GetProperty("evidence").GetProperty("droppedIntervals").GetInt64().Should().Be(3);
        json.GetProperty("evidence").GetProperty("totalSuspensionTime").GetString().Should().Be("00:00:00.0800000");
        json.GetProperty("evidence").GetProperty("outputOmittedIntervals").GetInt32().Should().Be(1);
    }

    [Fact]
    public void SerializedSummaryPauseOmission_IsNotCompleteCorrelationOrCollectorLoss()
    {
        var gc = WithPauses((10, 90));
        gc = gc with { Suspension = gc.Suspension! with { Intervals = [], OutputOmittedIntervals = 1 } };
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<GcSummary>(System.Text.Json.JsonSerializer.Serialize(gc))!;
        var overlay = GcActivityCorrelator.Correlate(Activities(0), roundTrip, 1);
        overlay.CorrelationScope.Should().Be("projected-pause-details");
        overlay.InputOmittedPauseIntervals.Should().Be(1);
        overlay.DroppedGcEvents.Should().Be(0);
        overlay.CorrelationTruncated.Should().BeFalse();
        overlay.CorrelationValuesAreLowerBounds.Should().BeTrue();
        overlay.TotalGcPauseMs.Should().Be(80);
        var query = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "summary", roundTrip, 1)
            .Result!.Payload.Should().BeOfType<GcMeasurementView>().Subject;
        query.Evidence!.OutputOmittedIntervals.Should().Be(1);
    }

    [Fact]
    public void LongestPauseProjectionOmissions_ExcludeTheReturnedRows()
    {
        var payload = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "longestPauses",
            WithPauses((10, 30), (50, 90)), 1).Result!.Payload.Should().BeOfType<GcMeasurementView>().Subject;
        ((GcSuspensionInterval[])payload.Data).Length.Should().Be(1);
        payload.Evidence!.OutputOmittedIntervals.Should().Be(1);
    }

    [Fact]
    public void UnreliableIntervalsRemainRawEvidence_NotMeasuredPauseOrZeroHistogram()
    {
        var gc = WithPauses((10, 90));
        gc = gc with { Suspension = gc.Suspension! with { Status = "unreliable", TotalSuspensionTime = null, MaxSuspensionTime = null } };
        var overlay = GcActivityCorrelator.Correlate(Activities(0), gc, 1);
        overlay.RetainedGcEvents.Should().Be(1);
        overlay.MeasurementStatus.Should().Be("unreliable");
        overlay.TotalGcPauseMs.Should().BeNull();
        overlay.TotalGcOverlapMs.Should().BeNull();
        overlay.ImpactedActivities.Should().BeEmpty();
        var query = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "pauseHistogram", gc, 1)
            .Result!.Payload.Should().BeOfType<GcMeasurementView>().Subject;
        query.RetainedPauseIntervals.Should().Be(1);
        query.MeasurementStatus.Should().Be("unreliable");
        ((GcPauseBucket[])query.Data).Should().BeEmpty();
    }

    internal static GcSummary WithPauses(params (int Start, int End)[] ranges) => LegacyGc() with
    {
        Suspension = new("no-detected-loss", At, At.AddMilliseconds(100), null, "normal-stop",
            TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(200), ranges.Length, 0,
            ranges.Select(p => new GcSuspensionInterval(At.AddMilliseconds(p.Start), At.AddMilliseconds(p.End),
                1, 1, 1, 0, TimeSpan.Zero)).ToArray(), new Dictionary<string, long>()),
    };

    internal static ActivityCapture Activities(int dropped) => new(
        42, null, At, TimeSpan.FromMilliseconds(100), 1 + dropped, 1 + dropped,
        [new("test", "span", "a", null, null, null, null, At, At.AddMilliseconds(100),
            TimeSpan.FromMilliseconds(100), new Dictionary<string, string>())], [], [],
        new(null, 1, 1 + dropped, 1 + dropped, 1, dropped, 0));

    internal static GcSummary LegacyGc() => new(42, At, TimeSpan.FromMilliseconds(100),
        2, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(80), [],
        [new(At.AddMilliseconds(10), 2, "test", "BackgroundGC", TimeSpan.FromMilliseconds(80)),
         new(At.AddMilliseconds(30), 0, "test", "ForegroundGC", TimeSpan.FromMilliseconds(40))]);
}
