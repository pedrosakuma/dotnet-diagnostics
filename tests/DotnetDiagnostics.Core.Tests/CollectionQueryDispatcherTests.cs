using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.ThreadPool;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public class CollectionQueryDispatcherTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    [Fact]
    public void Counters_SummaryView_ReturnsAllCounters()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), new List<CounterValue>
        {
            new("System.Runtime", "cpu-usage", "CPU", 12.5, CounterKind.Mean, "%"),
            new("System.Runtime", "gc-heap-size", "Heap", 100, CounterKind.Mean, "MB"),
        },
        new List<MeterInstrumentValue>
        {
            new("MyMeter", "orders.total", null, "Counter", new Dictionary<string, string?>(), 7, 2, null),
        },
        ["note"]);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, null, snap, 50);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.View.Should().Be("summary");
        var payload = outcome.Result.Payload.Should().BeOfType<CountersSummaryView>().Subject;
        payload.Counters.Should().HaveCount(2);
        payload.MeterCount.Should().Be(1);
        payload.Meters.Should().ContainSingle();
        payload.Notes.Should().ContainSingle("note");
    }

    [Fact]
    public void Counters_ByProviderView_GroupsByProvider()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), new List<CounterValue>
        {
            new("System.Runtime", "cpu-usage", "CPU", 12.5, CounterKind.Mean),
            new("Microsoft-AspNetCore-Server-Kestrel", "current-connections", "Conns", 3, CounterKind.Mean),
        },
        Array.Empty<MeterInstrumentValue>(),
        Array.Empty<string>());

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "byProvider", snap, 50);

        var payload = outcome.Result!.Payload.Should().BeOfType<CountersByProviderView>().Subject;
        payload.Providers.Should().HaveCount(2);
    }

    [Fact]
    public void Exceptions_SummaryView_TopsByTypeWithinTopN()
    {
        var snap = new ExceptionSnapshot(42, At, TimeSpan.FromSeconds(10), 30,
            new List<ExceptionCount>
            {
                new("System.FormatException", 20),
                new("System.InvalidOperationException", 8),
                new("System.NullReferenceException", 2),
            },
            new List<ManagedExceptionEvent>())
        { RecentCap = 100 };

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.ExceptionSnapshot, "summary", snap, 2);

        var payload = outcome.Result!.Payload.Should().BeOfType<ExceptionByTypeView>().Subject;
        payload.ByType.Should().HaveCount(2);
        payload.TotalExceptions.Should().Be(30);
    }

    [Fact]
    public void Exceptions_RecentView_TruncatesAndReportsCap()
    {
        var recent = Enumerable.Range(0, 25)
            .Select(i => new ManagedExceptionEvent(At.AddSeconds(i), "T", "msg", "0x1", 1))
            .ToList();
        var snap = new ExceptionSnapshot(42, At, TimeSpan.FromSeconds(10), 25,
            new List<ExceptionCount> { new("T", 25) }, recent) { RecentCap = 100 };

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.ExceptionSnapshot, "recent", snap, 5);

        var payload = outcome.Result!.Payload.Should().BeOfType<ExceptionRecentView>().Subject;
        payload.Returned.Should().Be(5);
        payload.RecentCap.Should().Be(100);
    }

    [Fact]
    public void CrashGuard_StackView_ReturnsFinalExceptionStack()
    {
        var final = new CrashGuardExceptionEvent(
            At,
            "System.InvalidOperationException",
            "fatal",
            "0x80131509",
            7,
            "ExceptionThrown_V1",
            IsUnhandled: true,
            new[] { "at BadCodeSample.Program.Crash()" });
        var snap = new CrashGuardSnapshot(
            42,
            At,
            TimeSpan.FromSeconds(2),
            ProcessExited: true,
            ExitCode: 134,
            UnhandledExceptionObserved: true,
            TotalExceptions: 1,
            ByType: new List<ExceptionCount> { new("System.InvalidOperationException", 1) },
            Exceptions: new List<CrashGuardExceptionEvent> { final },
            FinalException: final,
            Notes: Array.Empty<string>())
        { RecentCap = 100 };

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.CrashGuardSnapshot, "stack", snap, 5);

        outcome.Result.Should().NotBeNull();
        var payload = outcome.Result!.Payload.Should().BeOfType<CrashGuardStackView>().Subject;
        payload.FinalException.Should().Be(final);
        payload.ManagedStack.Should().ContainSingle().Which.Should().Contain("BadCodeSample");
    }

    [Fact]
    public void Gc_PauseHistogram_BucketsBoundariesCorrectly()
    {
        // One event per intended bucket: 0.5ms, 5ms, 50ms, 500ms, 1500ms.
        var events = new[] { 0.5, 5, 50, 500, 1500 }
            .Select(ms => new GcEvent(At, 0, "AllocSmall", "Background", TimeSpan.FromMilliseconds(ms)))
            .ToList();
        var g = new GcSummary(42, At, TimeSpan.FromSeconds(5), events.Count,
            TimeSpan.FromMilliseconds(events.Sum(e => e.PauseDuration.TotalMilliseconds)),
            TimeSpan.FromMilliseconds(1500),
            new List<GenerationStats> { new(0, 5) },
            events);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "pauseHistogram", g, 50);

        var payload = outcome.Result!.Payload.Should().BeOfType<GcPauseHistogramView>().Subject;
        payload.Buckets.Select(b => b.Count).Should().Equal(1, 1, 1, 1, 1);
    }

    // gen0 @+0ms (2ms), background gen2 @+10ms (50ms), gen1 @+30ms (5ms), gen2 @+40ms (100ms).
    // Deliberately enqueued out of chronological order to exercise the timeline sort.
    private static GcSummary ScrambledGc()
    {
        var events = new List<GcEvent>
        {
            new(At.AddMilliseconds(40), 2, "AllocLarge", "NonConcurrentGC", TimeSpan.FromMilliseconds(100)),
            new(At.AddMilliseconds(0), 0, "AllocSmall", "NonConcurrentGC", TimeSpan.FromMilliseconds(2)),
            new(At.AddMilliseconds(10), 2, "AllocSmall", "BackgroundGC", TimeSpan.FromMilliseconds(50)),
            new(At.AddMilliseconds(30), 1, "Induced", "NonConcurrentGC", TimeSpan.FromMilliseconds(5)),
        };
        return new GcSummary(42, At, TimeSpan.FromSeconds(5), events.Count,
            TimeSpan.FromMilliseconds(157), TimeSpan.FromMilliseconds(100),
            new List<GenerationStats> { new(0, 1), new(1, 1), new(2, 2) },
            events);
    }

    private static GcSummary EmptyGc() =>
        new(42, At, TimeSpan.FromSeconds(5), 0, TimeSpan.Zero, TimeSpan.Zero,
            new List<GenerationStats>(), new List<GcEvent>());

    [Fact]
    public void Gc_Timeline_OrdersByStartAndComputesGaps()
    {
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "timeline", ScrambledGc(), 50);

        var payload = outcome.Result!.Payload.Should().BeOfType<GcTimelineView>().Subject;
        payload.Returned.Should().Be(4);
        payload.Entries.Select(e => e.Index).Should().Equal(0, 1, 2, 3);
        payload.Entries.Select(e => e.Generation).Should().Equal(0, 2, 1, 2);
        payload.Entries.Select(e => e.GapSincePreviousStart.TotalMilliseconds)
            .Should().Equal(0, 10, 20, 10);
    }

    [Fact]
    public void Gc_Timeline_CapsToTopN()
    {
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "timeline", ScrambledGc(), 2);

        var payload = outcome.Result!.Payload.Should().BeOfType<GcTimelineView>().Subject;
        payload.Returned.Should().Be(2);
        payload.Entries.Select(e => e.Index).Should().Equal(0, 1); // earliest two by start time
    }

    [Fact]
    public void Gc_LongestPauses_RanksByPauseDescending()
    {
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "longestPauses", ScrambledGc(), 2);

        var payload = outcome.Result!.Payload.Should().BeOfType<GcLongestPausesView>().Subject;
        payload.Returned.Should().Be(2);
        payload.Pauses.Select(p => p.PauseDuration.TotalMilliseconds).Should().Equal(100, 50);
        payload.Pauses.Select(p => p.Index).Should().Equal(3, 1); // timeline indices retained
    }

    [Fact]
    public void Gc_ByGeneration_BucketsBackgroundSeparatelyWithStats()
    {
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "byGeneration", ScrambledGc(), 50);

        var payload = outcome.Result!.Payload.Should().BeOfType<GcByGenerationView>().Subject;
        payload.Generations.Select(s => s.Bucket).Should().Equal("gen0", "gen1", "gen2", "background");

        var gen2 = payload.Generations.Single(s => s.Bucket == "gen2");
        gen2.Count.Should().Be(1); // background gen2 excluded
        gen2.MaxPause.Should().Be(TimeSpan.FromMilliseconds(100));
        gen2.MeanPause.Should().Be(TimeSpan.FromMilliseconds(100));

        var background = payload.Generations.Single(s => s.Bucket == "background");
        background.Count.Should().Be(1);
        background.TotalPause.Should().Be(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void Gc_ByGeneration_AggregatesMeanAndTotalAcrossEvents()
    {
        var events = new List<GcEvent>
        {
            new(At.AddMilliseconds(0), 0, "AllocSmall", "NonConcurrentGC", TimeSpan.FromMilliseconds(2)),
            new(At.AddMilliseconds(5), 0, "AllocSmall", "NonConcurrentGC", TimeSpan.FromMilliseconds(8)),
        };
        var g = new GcSummary(42, At, TimeSpan.FromSeconds(5), 2,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(8),
            new List<GenerationStats> { new(0, 2) }, events);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "byGeneration", g, 50);

        var gen0 = outcome.Result!.Payload.Should().BeOfType<GcByGenerationView>().Subject
            .Generations.Single(s => s.Bucket == "gen0");
        gen0.Count.Should().Be(2);
        gen0.TotalPause.Should().Be(TimeSpan.FromMilliseconds(10));
        gen0.MeanPause.Should().Be(TimeSpan.FromMilliseconds(5));
        gen0.MaxPause.Should().Be(TimeSpan.FromMilliseconds(8));
    }

    [Fact]
    public void Gc_NewViews_HandleEmptyEvents()
    {
        var empty = EmptyGc();

        CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "timeline", empty, 50)
            .Result!.Payload.Should().BeOfType<GcTimelineView>().Subject.Entries.Should().BeEmpty();
        CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "longestPauses", empty, 50)
            .Result!.Payload.Should().BeOfType<GcLongestPausesView>().Subject.Pauses.Should().BeEmpty();
        CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "byGeneration", empty, 50)
            .Result!.Payload.Should().BeOfType<GcByGenerationView>().Subject.Generations.Should().BeEmpty();

        var heapStats = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "heap-stats", empty, 50)
            .Result!.Payload.Should().BeOfType<GcHeapStatsView>().Subject;
        heapStats.Samples.Should().BeEmpty();
        heapStats.SampleCount.Should().Be(0);
        heapStats.Trend.Should().BeNull("a trend needs at least two samples");
    }

    [Fact]
    public void Gc_HeapStats_ReturnsChronologicalSamplesAndFirstToLastTrend()
    {
        // Samples deliberately out of chronological order to exercise the sort + first/last selection.
        var samples = new List<GcHeapStatsSample>
        {
            new(At.AddMilliseconds(200), 0, 0, 30_000, 5_000, 1_000, 36_000, 0, 0, 0, 0, 0, 40, 120),
            new(At.AddMilliseconds(0), 0, 0, 10_000, 2_000, 500, 12_500, 0, 0, 0, 0, 0, 10, 100),
            new(At.AddMilliseconds(100), 0, 0, 20_000, 3_000, 800, 23_800, 0, 0, 0, 0, 0, 25, 110),
        };
        var g = new GcSummary(42, At, TimeSpan.FromSeconds(5), 0, TimeSpan.Zero, TimeSpan.Zero,
            new List<GenerationStats>(), new List<GcEvent>(), samples);

        var payload = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "heap-stats", g, 50)
            .Result!.Payload.Should().BeOfType<GcHeapStatsView>().Subject;

        payload.SampleCount.Should().Be(3);
        payload.Returned.Should().Be(3);
        payload.Samples.Select(s => s.Gen2SizeBytes).Should().Equal(10_000, 20_000, 30_000);

        payload.Trend.Should().NotBeNull();
        payload.Trend!.Gen2DeltaBytes.Should().Be(20_000);
        payload.Trend.LohDeltaBytes.Should().Be(3_000);
        payload.Trend.PohDeltaBytes.Should().Be(500);
        payload.Trend.TotalHeapDeltaBytes.Should().Be(23_500);
        payload.Trend.PinnedObjectCountDelta.Should().Be(30);
        payload.Trend.GcHandleCountDelta.Should().Be(20);
    }

    [Fact]
    public void Gc_HeapStats_CapsSamplesToTopN()
    {
        var samples = Enumerable.Range(0, 5)
            .Select(i => new GcHeapStatsSample(At.AddMilliseconds(i * 10), 0, 0, i * 1_000, 0, 0, i * 1_000, 0, 0, 0, 0, 0, i, i))
            .ToList();
        var g = new GcSummary(42, At, TimeSpan.FromSeconds(5), 0, TimeSpan.Zero, TimeSpan.Zero,
            new List<GenerationStats>(), new List<GcEvent>(), samples);

        var payload = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "heap-stats", g, 2)
            .Result!.Payload.Should().BeOfType<GcHeapStatsView>().Subject;

        payload.SampleCount.Should().Be(5);
        payload.Returned.Should().Be(2);
        payload.Trend.Should().NotBeNull("the trend spans all retained samples, not just the returned slice");
        payload.Trend!.Gen2DeltaBytes.Should().Be(4_000);
    }

    [Fact]
    public void Gc_ViewsFor_IncludesNewDrilldownViews()
    {
        CollectionQueryDispatcher.ViewsFor(CollectionHandleKinds.GcEvents)
            .Should().Contain(new[] { "timeline", "longestPauses", "byGeneration", "heap-stats" });
    }

    [Fact]
    public void EventSource_ByEventNameView_OrdersByCount()
    {
        var events = new List<CapturedEvent>
        {
            new(At, "P", "Start", "Info", new Dictionary<string,string>()),
            new(At, "P", "Stop",  "Info", new Dictionary<string,string>()),
            new(At, "P", "Stop",  "Info", new Dictionary<string,string>()),
            new(At, "P", "Stop",  "Info", new Dictionary<string,string>()),
        };
        var cap = new EventSourceCapture(42, "P", At, TimeSpan.FromSeconds(5), events.Count, events);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.EventSource, "byEventName", cap, 10);

        var payload = outcome.Result!.Payload.Should().BeOfType<EventSourceByEventNameView>().Subject;
        payload.ByEventName[0].EventName.Should().Be("Stop");
        payload.ByEventName[0].Count.Should().Be(3);
        payload.CapturedCount.Should().Be(4);
        payload.Truncated.Should().BeFalse();
    }

    [Fact]
    public void EventSource_ByEventNameView_FlagsTruncationWhenTotalExceedsCaptured()
    {
        // Simulate a collector that observed 1000 events but only stored the first 200
        // because maxEvents=200. ByEventName must surface that mismatch so the LLM doesn't
        // present partial aggregates as if they represented the whole window.
        var captured = Enumerable.Range(0, 200)
            .Select(i => new CapturedEvent(At, "P", i < 50 ? "Start" : "Heartbeat", "Info",
                new Dictionary<string, string>()))
            .ToList();
        var cap = new EventSourceCapture(42, "P", At, TimeSpan.FromSeconds(5), TotalEvents: 1000, captured);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.EventSource, "byEventName", cap, 10);

        var payload = outcome.Result!.Payload.Should().BeOfType<EventSourceByEventNameView>().Subject;
        payload.TotalEvents.Should().Be(1000);
        payload.CapturedCount.Should().Be(200);
        payload.Truncated.Should().BeTrue("collector dropped tail events; the LLM must see that");
    }

    [Fact]
    public void Activities_SummaryView_ReportsTruncation_AndTopGroups()
    {
        var capture = new ActivityCapture(
            ProcessId: 42,
            SourceFilters: new[] { "Demo.*" },
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(5),
            TotalActivities: 5,
            CompletedActivities: 4,
            Activities:
            [
                new CapturedActivity("Demo.Service", "GET /a", "1", null, "trace-1", "span-1", null, At, At.AddMilliseconds(12), TimeSpan.FromMilliseconds(12), new Dictionary<string, string>()),
                new CapturedActivity("Demo.Service", "GET /a", "2", null, "trace-1", "span-2", null, At.AddMilliseconds(20), At.AddMilliseconds(40), TimeSpan.FromMilliseconds(20), new Dictionary<string, string>()),
                new CapturedActivity("Demo.Service", "GET /b", "3", "1", "trace-1", "span-3", "span-1", At.AddMilliseconds(25), At.AddMilliseconds(55), TimeSpan.FromMilliseconds(30), new Dictionary<string, string>()),
            ],
            BySource:
            [
                new ActivitySourceSummary("Demo.Service", 3, 3, 20, 30),
            ],
            ByOperation:
            [
                new ActivityOperationSummary("Demo.Service", "GET /a", 2, 2, 16, 20),
                new ActivityOperationSummary("Demo.Service", "GET /b", 1, 1, 30, 30),
            ]);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Activities, "summary", capture, 1);

        var payload = outcome.Result!.Payload.Should().BeOfType<ActivitiesSummaryView>().Subject;
        payload.TotalActivities.Should().Be(5);
        payload.CapturedCount.Should().Be(3);
        payload.Truncated.Should().BeTrue();
        payload.BySource.Should().ContainSingle();
        payload.ByOperation.Should().ContainSingle();
        payload.ByOperation[0].OperationName.Should().Be("GET /a");
    }

    [Fact]
    public void Activities_ActivitiesView_TruncatesRawList()
    {
        var capture = new ActivityCapture(
            ProcessId: 42,
            SourceFilters: null,
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(5),
            TotalActivities: 2,
            CompletedActivities: 2,
            Activities:
            [
                new CapturedActivity("Demo.Service", "outer", "1", null, "trace-1", "span-1", null, At, At.AddMilliseconds(12), TimeSpan.FromMilliseconds(12), new Dictionary<string, string>()),
                new CapturedActivity("Demo.Service", "inner", "2", "1", "trace-1", "span-2", "span-1", At.AddMilliseconds(2), At.AddMilliseconds(6), TimeSpan.FromMilliseconds(4), new Dictionary<string, string>()),
            ],
            BySource:
            [
                new ActivitySourceSummary("Demo.Service", 2, 2, 8, 12),
            ],
            ByOperation:
            [
                new ActivityOperationSummary("Demo.Service", "outer", 1, 1, 12, 12),
                new ActivityOperationSummary("Demo.Service", "inner", 1, 1, 4, 4),
            ]);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Activities, "activities", capture, 1);

        var payload = outcome.Result!.Payload.Should().BeOfType<ActivitiesListView>().Subject;
        payload.Returned.Should().Be(1);
        payload.Activities[0].OperationName.Should().Be("outer");
    }

    [Fact]
    public void Logs_SummaryAndErrorsViews_RenderExpectedSlices()
    {
        var snapshot = new LogSnapshot(
            ProcessId: 42,
            CategoryFilters: new[] { "BadCodeSample.LogSpam" },
            MinimumLevel: "Information",
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(5),
            TotalEvents: 4,
            EventsByLevelTrace: 0,
            EventsByLevelDebug: 0,
            EventsByLevelInformation: 1,
            EventsByLevelWarning: 2,
            EventsByLevelError: 1,
            EventsByLevelCritical: 0,
            ByCategory:
            [
                new LogCategoryGroup("BadCodeSample.LogSpam", 4, 1, 3),
            ],
            Recent:
            [
                new LogEntry(At, "Information", "BadCodeSample.LogSpam", 1, null, "info", null, null, null),
                new LogEntry(At.AddSeconds(1), "Warning", "BadCodeSample.LogSpam", 2, null, "warn-1", null, null, new Dictionary<string, string> { ["Password"] = "<redacted:sensitive>" }),
                new LogEntry(At.AddSeconds(2), "Warning", "BadCodeSample.LogSpam", 3, null, "warn-2", null, null, null),
                new LogEntry(At.AddSeconds(3), "Error", "BadCodeSample.LogSpam", 4, "Boom", "error", "System.InvalidOperationException", "boom", null),
            ],
            Truncated: false,
            Notes: new[] { "note" });

        var summaryOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.LogSnapshot, "summary", snapshot, 10);
        var summary = summaryOutcome.Result!.Payload.Should().BeOfType<LogSummaryView>().Subject;
        summary.TotalEvents.Should().Be(4);
        summary.Counts.Warning.Should().Be(2);
        summary.ByCategory.Should().ContainSingle();

        var errorsOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.LogSnapshot, "errors", snapshot, 10);
        var errors = errorsOutcome.Result!.Payload.Should().BeOfType<LogErrorsView>().Subject;
        errors.Returned.Should().Be(3);
        errors.Errors.Should().OnlyContain(entry =>
            entry.Level == "Warning" || entry.Level == "Error" || entry.Level == "Critical");
        errors.Errors.Should().Contain(entry => entry.ExceptionType == "System.InvalidOperationException");
    }

    [Fact]
    public void Jit_SummaryAndReJitViews_RenderExpectedSlices()
    {
        var snapshot = new JitSnapshot(
            ProcessId: 42,
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(5),
            JitStartCount: 4,
            CompletedCompilations: 4,
            UniqueMethods: 3,
            Distribution: new JitTierDistribution(Tier0: 3, Tier1: 1, ReadyToRun: 0, R2RHit: 2, R2RMissThenJit: 1),
            R2RLookupCount: 3,
            ReJitCount: 1,
            OsrCount: 1,
            IlMapCount: 2,
            Tier1Percent: 25,
            R2RHitRatePercent: 66.7,
            HealthCheck: "25% of completed methods reached Tier1; R2R hit rate 67%.",
            Methods:
            [
                new JitMethodSummary("BadCodeSample", "JitPressureDynamicMethod0001", "(Int32)", "BadCodeSample.JitPressureDynamicMethod0001(Int32)", 12.5, 1, "QuickJitted", 1, 0, 0, 0, 0, true),
                new JitMethodSummary("BadCodeSample", "JitPressureDynamicMethod0002", "(Int32)", "BadCodeSample.JitPressureDynamicMethod0002(Int32)", 10.1, 1, "OptimizedTier1OSR", 0, 1, 0, 1, 1, true),
                new JitMethodSummary("BadCodeSample", "JitPressureDynamicMethod0003", "(Int32)", "BadCodeSample.JitPressureDynamicMethod0003(Int32)", 6.2, 2, "QuickJitted", 2, 0, 0, 0, 0, false),
            ],
            Notes: new[] { "note" });

        var summaryOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.JitSnapshot, "summary", snapshot, 2);
        var summary = summaryOutcome.Result!.Payload.Should().BeOfType<JitSummaryView>().Subject;
        summary.TopMethods.Should().HaveCount(2);
        summary.Distribution.Tier0.Should().Be(3);
        summary.R2RLookupCount.Should().Be(3);

        var rejitOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.JitSnapshot, "reJIT", snapshot, 10);
        var rejit = rejitOutcome.Result!.Payload.Should().BeOfType<JitReJitView>().Subject;
        rejit.ReJitCount.Should().Be(1);
        rejit.OsrCount.Should().Be(1);
        rejit.Methods.Should().ContainSingle(method => method.OsrCount == 1);
    }

    [Fact]
    public void ThreadPool_SummaryAndHillClimbingViews_RenderExpectedSlices()
    {
        var snapshot = new ThreadPoolEventSnapshot(
            ProcessId: 42,
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(6),
            WorkerThreadTimeline:
            [
                new ThreadPoolCountBucket(At, 4),
                new ThreadPoolCountBucket(At.AddSeconds(1), 9),
            ],
            IocpThreadTimeline:
            [
                new ThreadPoolCountBucket(At, 1),
            ],
            HillClimbing:
            [
                new ThreadPoolHillClimbingSample(At, "Warmup", 4, 6, 10),
                new ThreadPoolHillClimbingSample(At.AddSeconds(1), "Starvation", 6, 9, 25),
            ],
            WorkItemOrigins:
            [
                new ThreadPoolWorkItemOrigin("BadCodeSample.ThreadPoolStarve", 7),
                new ThreadPoolWorkItemOrigin("System.Threading.Tasks.Task", 2),
            ],
            EffectiveSettings: new ThreadPoolEffectiveSettings(1, 32767, 1, 1000),
            TotalEnqueueEvents: 9,
            TotalDequeueEvents: 4,
            Notes: ["note"]);

        var summaryOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.ThreadPoolSnapshot, "summary", snapshot, 1);
        var summary = summaryOutcome.Result!.Payload.Should().BeOfType<ThreadPoolSummaryView>().Subject;
        summary.PeakWorkerThreadCount.Should().Be(9);
        summary.StarvationAdjustments.Should().Be(1);
        summary.TopWorkItemOrigins.Should().ContainSingle();

        var hillOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.ThreadPoolSnapshot, "hillClimbing", snapshot, 1);
        var hill = hillOutcome.Result!.Payload.Should().BeOfType<ThreadPoolHillClimbingView>().Subject;
        hill.Returned.Should().Be(1);
        hill.Samples[0].Reason.Should().Be("Warmup");
    }

    [Fact]
    public void Contention_ByCallSiteAndByOwnerViews_RenderExpectedSlices()
    {
        var snapshot = new ContentionSnapshot(
            ProcessId: 42,
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(5),
            TotalEvents: 3,
            DistinctMonitors: 2,
            TotalContentionDuration: TimeSpan.FromMilliseconds(57),
            P50ContentionDuration: TimeSpan.FromMilliseconds(15),
            P95ContentionDuration: TimeSpan.FromMilliseconds(30),
            MaxContentionDuration: TimeSpan.FromMilliseconds(30),
            Events:
            [
                new ContentionEventSample(At, At.AddMilliseconds(30), TimeSpan.FromMilliseconds(30), 9, 17, 101, 5001, "BadCodeSample.LockStorm", "BadCodeSample"),
                new ContentionEventSample(At.AddMilliseconds(40), At.AddMilliseconds(55), TimeSpan.FromMilliseconds(15), 10, 17, 101, 5001, "BadCodeSample.LockStorm", "BadCodeSample"),
                new ContentionEventSample(At.AddMilliseconds(60), At.AddMilliseconds(72), TimeSpan.FromMilliseconds(12), 11, 23, 202, 5002, "System.Threading.Monitor.Enter", "System.Private.CoreLib"),
            ],
            Notes: ["note"]);

        var summaryOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.ContentionSnapshot, "summary", snapshot, 10);
        var summary = summaryOutcome.Result!.Payload.Should().BeOfType<ContentionSummaryView>().Subject;
        summary.TotalEvents.Should().Be(3);
        summary.ContendedMonitorCount.Should().Be(2);
        summary.P95ContentionDuration.Should().Be(TimeSpan.FromMilliseconds(30));

        var byCallSiteOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.ContentionSnapshot, "byCallSite", snapshot, 10);
        var byCallSite = byCallSiteOutcome.Result!.Payload.Should().BeOfType<ContentionByCallSiteView>().Subject;
        byCallSite.Returned.Should().Be(2);
        byCallSite.CallSites[0].CallSiteMethod.Should().Be("BadCodeSample.LockStorm");
        byCallSite.CallSites[0].DistinctOwnerThreads.Should().Be(1);
        byCallSite.CallSites[0].TotalContentionDuration.Should().Be(TimeSpan.FromMilliseconds(45));

        var byOwnerOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.ContentionSnapshot, "byOwner", snapshot, 10);
        var byOwner = byOwnerOutcome.Result!.Payload.Should().BeOfType<ContentionByOwnerView>().Subject;
        byOwner.Returned.Should().Be(2);
        byOwner.Owners[0].OwnerManagedThreadId.Should().Be(17);
        byOwner.Owners[0].DistinctMonitors.Should().Be(1);
        byOwner.Owners[0].TotalContentionDuration.Should().Be(TimeSpan.FromMilliseconds(45));
    }

    [Fact]
    public void Db_SummaryAndNPlusOneViews_RenderExpectedSlices()
    {
        var snapshot = new DbSnapshot(
            ProcessId: 42,
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(5),
            TotalCommands: 15,
            ByCommand:
            [
                new DbCommandAggregate(
                    "HASH1",
                    "SELECT * FROM widgets WHERE id = <redacted:literal>",
                    "Data Source=badcode-db;Password=<redacted:sensitive>",
                    ["Microsoft.EntityFrameworkCore"],
                    15,
                    150,
                    20,
                    18,
                    At,
                    At.AddSeconds(2)),
            ],
            NPlusOne:
            [
                new DbNPlusOneIncident(
                    "scope-1",
                    "HASH1",
                    "SELECT * FROM widgets WHERE id = <redacted:literal>",
                    "Data Source=badcode-db;Password=<redacted:sensitive>",
                    ["Microsoft.EntityFrameworkCore"],
                    15,
                    At,
                    At.AddSeconds(2)),
            ],
            ConnectionPool:
            [
                new DbConnectionPoolStats(
                    "Microsoft.Data.SqlClient.EventSource",
                    4,
                    6,
                    8,
                    10,
                    1,
                    ["Observed active-hard-connections=4"]),
            ],
            Notes: ["note"]);

        var summaryOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.DbSnapshot, "summary", snapshot, 10);
        var summary = summaryOutcome.Result!.Payload.Should().BeOfType<DbSummaryView>().Subject;
        summary.TotalCommands.Should().Be(15);
        summary.NPlusOneCount.Should().Be(1);
        summary.TopCommands.Should().ContainSingle();

        var nPlusOneOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.DbSnapshot, "n+1", snapshot, 10);
        var nPlusOne = nPlusOneOutcome.Result!.Payload.Should().BeOfType<DbNPlusOneView>().Subject;
        nPlusOne.TotalIncidents.Should().Be(1);
        nPlusOne.Incidents.Should().ContainSingle(incident => incident.Count == 15);

        var poolOutcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.DbSnapshot, "connectionPool", snapshot, 10);
        var pool = poolOutcome.Result!.Payload.Should().BeOfType<DbConnectionPoolView>().Subject;
        pool.PoolExhaustedCount.Should().Be(1);
        pool.ConnectionPool.Should().ContainSingle(stats => stats.Provider == "Microsoft.Data.SqlClient.EventSource");
    }

    [Fact]
    public void UnknownKind_ReturnsUnknownKind()
    {
        var outcome = CollectionQueryDispatcher.Dispatch("not-a-real-kind", "summary", new object(), 50);
        outcome.UnknownKind.Should().Be("not-a-real-kind");
        outcome.Result.Should().BeNull();
    }

    [Fact]
    public void UnknownView_ReturnsAllowedViews()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), Array.Empty<CounterValue>(), Array.Empty<MeterInstrumentValue>(), Array.Empty<string>());
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "bogus", snap, 50);

        outcome.UnknownView.Should().Be("bogus");
        outcome.AllowedViews.Should().Contain(new[] { "summary", "byProvider" });
    }

    [Fact]
    public void InvalidTopN_ReturnsInvalidArgument()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), Array.Empty<CounterValue>(), Array.Empty<MeterInstrumentValue>(), Array.Empty<string>());
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "summary", snap, 0);

        outcome.InvalidArgument.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ViewNames_AreCaseInsensitive()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), Array.Empty<CounterValue>(), Array.Empty<MeterInstrumentValue>(), Array.Empty<string>());
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "BYPROVIDER", snap, 50);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.Payload.Should().BeOfType<CountersByProviderView>();
    }

    [Fact]
    public void Activities_GcOverlay_CorrelatesOverlappingEvents()
    {
        var baseTime = DateTimeOffset.UtcNow;

        // Activity: 100ms-600ms (500ms duration)
        var activities = new ActivityCapture(
            ProcessId: 42,
            SourceFilters: null,
            StartedAt: baseTime,
            Duration: TimeSpan.FromSeconds(1),
            TotalActivities: 1,
            CompletedActivities: 1,
            Activities: new[]
            {
                new CapturedActivity(
                    SourceName: "TestSource",
                    OperationName: "TestOp",
                    Id: "a1",
                    ParentId: null,
                    TraceId: "trace1",
                    SpanId: "span1",
                    ParentSpanId: null,
                    StartedAt: baseTime.AddMilliseconds(100),
                    StoppedAt: baseTime.AddMilliseconds(600),
                    Duration: TimeSpan.FromMilliseconds(500),
                    Tags: new Dictionary<string, string>())
            },
            BySource: Array.Empty<ActivitySourceSummary>(),
            ByOperation: Array.Empty<ActivityOperationSummary>());

        // GC: 200ms-350ms (150ms pause, fully inside activity window)
        var gcSummary = new GcSummary(
            ProcessId: 42,
            StartedAt: baseTime,
            Duration: TimeSpan.FromSeconds(1),
            TotalCollections: 1,
            TotalPauseTime: TimeSpan.FromMilliseconds(150),
            MaxPauseTime: TimeSpan.FromMilliseconds(150),
            Generations: new[] { new GenerationStats(2, 1) },
            Events: new[]
            {
                new GcEvent(
                    Timestamp: baseTime.AddMilliseconds(200),
                    Generation: 2,
                    Reason: "AllocSmall",
                    Type: "NonConcurrentGC",
                    PauseDuration: TimeSpan.FromMilliseconds(150))
            });

        var outcome = CollectionQueryDispatcher.Dispatch(
            CollectionHandleKinds.Activities, "gc-overlay", activities, 50, gcSummary);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.View.Should().Be("gc-overlay");
        var payload = outcome.Result.Payload.Should().BeOfType<GcOverlayResult>().Subject;

        payload.ImpactedCount.Should().Be(1);
        payload.ImpactedActivities.Should().HaveCount(1);

        var impacted = payload.ImpactedActivities[0];
        impacted.OperationName.Should().Be("TestOp");
        impacted.DurationMs.Should().Be(500);
        impacted.GcPauseMs.Should().Be(150);
        impacted.GcPausePercent.Should().Be(30); // 150ms of 500ms = 30%
        impacted.GcEvents.Should().HaveCount(1);
        impacted.GcEvents[0].Generation.Should().Be(2);
    }

    [Fact]
    public void Activities_GcOverlay_RequiresGcHandle()
    {
        var activities = new ActivityCapture(
            ProcessId: 42,
            SourceFilters: null,
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(1),
            TotalActivities: 0,
            CompletedActivities: 0,
            Activities: Array.Empty<CapturedActivity>(),
            BySource: Array.Empty<ActivitySourceSummary>(),
            ByOperation: Array.Empty<ActivityOperationSummary>());

        // No correlateArtifact provided
        var outcome = CollectionQueryDispatcher.Dispatch(
            CollectionHandleKinds.Activities, "gc-overlay", activities, 50, correlateArtifact: null);

        outcome.Result.Should().BeNull();
        outcome.InvalidArgument.Should().Contain("gcHandle");
    }

    [Fact]
    public void Activities_GcOverlay_PartialOverlap_CalculatesCorrectly()
    {
        var baseTime = DateTimeOffset.UtcNow;

        // Activity: 100ms-400ms (300ms duration)
        var activities = new ActivityCapture(
            ProcessId: 42,
            SourceFilters: null,
            StartedAt: baseTime,
            Duration: TimeSpan.FromSeconds(1),
            TotalActivities: 1,
            CompletedActivities: 1,
            Activities: new[]
            {
                new CapturedActivity(
                    SourceName: "TestSource",
                    OperationName: "TestOp",
                    Id: "a1",
                    ParentId: null,
                    TraceId: null,
                    SpanId: null,
                    ParentSpanId: null,
                    StartedAt: baseTime.AddMilliseconds(100),
                    StoppedAt: baseTime.AddMilliseconds(400),
                    Duration: TimeSpan.FromMilliseconds(300),
                    Tags: new Dictionary<string, string>())
            },
            BySource: Array.Empty<ActivitySourceSummary>(),
            ByOperation: Array.Empty<ActivityOperationSummary>());

        // GC: 300ms-500ms (200ms pause, but only 100ms overlaps with activity)
        var gcSummary = new GcSummary(
            ProcessId: 42,
            StartedAt: baseTime,
            Duration: TimeSpan.FromSeconds(1),
            TotalCollections: 1,
            TotalPauseTime: TimeSpan.FromMilliseconds(200),
            MaxPauseTime: TimeSpan.FromMilliseconds(200),
            Generations: new[] { new GenerationStats(1, 1) },
            Events: new[]
            {
                new GcEvent(
                    Timestamp: baseTime.AddMilliseconds(300),
                    Generation: 1,
                    Reason: "AllocSmall",
                    Type: "NonConcurrentGC",
                    PauseDuration: TimeSpan.FromMilliseconds(200))
            });

        var outcome = CollectionQueryDispatcher.Dispatch(
            CollectionHandleKinds.Activities, "gc-overlay", activities, 50, gcSummary);

        var payload = outcome.Result!.Payload.Should().BeOfType<GcOverlayResult>().Subject;
        var impacted = payload.ImpactedActivities[0];

        // Only 100ms of the GC pause overlapped with the activity (300-400ms)
        impacted.GcPauseMs.Should().Be(100);
        impacted.GcPausePercent.Should().BeApproximately(33.33, 0.1); // 100ms of 300ms ≈ 33.33%
    }
    private static InFlightRequestSnapshot SampleInFlight() => new(
        ProcessId: 7,
        StartedAt: At,
        Duration: TimeSpan.FromSeconds(2),
        RequestsStarted: 3,
        RequestsCompleted: 1,
        InFlightCount: 2,
        LongRunningCount: 1,
        LongRunningThresholdMs: 1000,
        OldestElapsedMs: 5000,
        Requests: new[]
        {
            new InFlightRequest("trace-old", "span-old", "GET", "/slow-hang", At.AddSeconds(-5), 5000, IsLongRunning: true),
            new InFlightRequest("trace-new", "span-new", "POST", "/orders", At.AddMilliseconds(-200), 200, IsLongRunning: false),
        },
        Notes: new[] { "note" });

    [Fact]
    public void InFlightRequests_SummaryView_ReturnsHeadlineCountsAndRequests()
    {
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.InFlightRequests, null, SampleInFlight(), 50);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.View.Should().Be("summary");
        var payload = outcome.Result.Payload.Should().BeOfType<InFlightRequestsSummaryView>().Subject;
        payload.InFlightCount.Should().Be(2);
        payload.LongRunningCount.Should().Be(1);
        payload.OldestElapsedMs.Should().Be(5000);
        payload.Requests.Should().HaveCount(2);
        payload.Requests[0].Path.Should().Be("/slow-hang");
    }

    [Fact]
    public void InFlightRequests_RequestsView_IsCappedByTopN()
    {
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.InFlightRequests, "requests", SampleInFlight(), 1);

        var payload = outcome.Result!.Payload.Should().BeOfType<InFlightRequestsListView>().Subject;
        payload.InFlightCount.Should().Be(2);
        payload.Returned.Should().Be(1);
        payload.Requests.Should().ContainSingle();
        payload.Requests[0].Path.Should().Be("/slow-hang");
    }

    [Fact]
    public void InFlightRequests_LongRunningView_FiltersToLongRunners()
    {
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.InFlightRequests, "longRunning", SampleInFlight(), 50);

        var payload = outcome.Result!.Payload.Should().BeOfType<InFlightRequestsLongRunningView>().Subject;
        payload.LongRunningCount.Should().Be(1);
        payload.Requests.Should().ContainSingle();
        payload.Requests[0].IsLongRunning.Should().BeTrue();
        payload.Requests[0].Path.Should().Be("/slow-hang");
    }

    [Fact]
    public void InFlightRequests_UnknownView_ReportsAllowedViews()
    {
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.InFlightRequests, "nope", SampleInFlight(), 50);

        outcome.Result.Should().BeNull();
        outcome.UnknownView.Should().Be("nope");
        outcome.AllowedViews.Should().BeEquivalentTo(new[] { "summary", "requests", "longRunning" });
    }
}
