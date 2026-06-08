using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Comparison;
using DotnetDiagnosticsMcp.Core.Contention;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.ThreadPool;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class ComparableProjectorTests
{
    private static DatasTuningEvent Tuning(ulong gcIndex, int newHeapCount, float medianTcp) => new(
        Timestamp: new DateTimeOffset(2026, 1, 1, 0, 0, (int)gcIndex, TimeSpan.Zero),
        NewHeapCount: newHeapCount,
        MaxHeapCount: 16,
        MinHeapCount: 1,
        GcIndex: gcIndex,
        TotalSohStableSize: 10_000_000,
        MedianThroughputCostPercent: medianTcp,
        TcpToConsider: medianTcp,
        CurrentAroundTargetAccumulation: 0,
        RecordedTcpCount: 3,
        RecordedTcpSlope: 0,
        NumGcsSinceLastChange: 5,
        AggFactor: 1,
        ChangeDecision: 0,
        AdjustmentReason: 0,
        HeapCountChangeFreqFactor: 1,
        HeapCountFreqReason: 0,
        AdjustMetric: 0);

    private static DatasSampleEvent Sample(ulong gcIndex, uint gen0Budget, ulong soh) => new(
        Timestamp: new DateTimeOffset(2026, 1, 1, 0, 0, (int)gcIndex, TimeSpan.Zero),
        GcIndex: gcIndex,
        ElapsedBetweenGcsUs: 1000,
        GcPauseTimeUs: 50,
        SohMslWaitUs: 0,
        UohMslWaitUs: 0,
        TotalSohStableSize: soh,
        Gen0BudgetPerHeap: gen0Budget);

    [Fact]
    public void GcDatasProjector_EmitsHeadlineMetrics_AndNoRows()
    {
        var snapshot = new GcDatasSnapshot(
            ProcessId: 777,
            StartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Duration: TimeSpan.FromSeconds(10),
            Samples: new[] { Sample(1, 12 * 1024 * 1024, 20_000_000), Sample(2, 12 * 1024 * 1024, 20_000_000) },
            TuningEvents: new[] { Tuning(1, 4, 3.0f), Tuning(2, 8, 5.0f) },
            FullGcTuningEvents: Array.Empty<DatasFullGcTuningEvent>(),
            ParseStats: new DatasParseStats(0, 0, 0));

        var snap = new GcDatasComparableProjector().Project(snapshot, "baseline");

        snap.Kind.Should().Be("gc-datas");
        snap.Label.Should().Be("baseline");
        snap.ProcessId.Should().Be(777);
        snap.Rows.Should().BeEmpty();

        var byName = snap.Metrics.ToDictionary(m => m.Definition.Name);
        byName.Should().ContainKey("heapCountChanges");
        byName["heapCountChanges"].Value.Should().Be(1);
        byName["heapCountChanges"].Definition.BetterDirection.Should().Be(BetterDirection.Lower);
        byName.Should().ContainKey("meanMedianThroughputCostPercent");
        byName["meanMedianThroughputCostPercent"].Value.Should().Be(4.0);
        byName.Should().ContainKey("minHeapCount");
        byName.Should().ContainKey("maxHeapCount");
    }

    [Fact]
    public void GcDatasProjector_EmptySnapshot_SkipsNullMetrics()
    {
        var snapshot = new GcDatasSnapshot(
            ProcessId: 1,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(1),
            Samples: Array.Empty<DatasSampleEvent>(),
            TuningEvents: Array.Empty<DatasTuningEvent>(),
            FullGcTuningEvents: Array.Empty<DatasFullGcTuningEvent>(),
            ParseStats: new DatasParseStats(0, 0, 0));

        var snap = new GcDatasComparableProjector().Project(snapshot, "empty");

        var names = snap.Metrics.Select(m => m.Definition.Name).ToHashSet();
        names.Should().NotContain("meanMedianThroughputCostPercent");
        names.Should().NotContain("minHeapCount");
        // Pure counts are always present.
        names.Should().Contain("heapCountChanges");
        names.Should().Contain("sampleCount");
    }

    [Fact]
    public void CountersProjector_FlattensCountersAndMeters()
    {
        var snapshot = new CounterSnapshot(
            ProcessId: 9,
            StartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Duration: TimeSpan.FromSeconds(5),
            Counters: new[]
            {
                new CounterValue("System.Runtime", "cpu-usage", "CPU Usage", 42.5, CounterKind.Mean, "%"),
                new CounterValue("System.Runtime", "gen-0-gc-count", "Gen 0 GC Count", 7, CounterKind.Sum, "count"),
            },
            Meters: new[]
            {
                new MeterInstrumentValue(
                    "MyApp", "request-duration", "ms", "Histogram",
                    new Dictionary<string, string?> { ["route"] = "/api" },
                    LastValue: null, Rate: null,
                    Histogram: new HistogramSnapshot(100, 1234, 5, 9, 11)),
            },
            Notes: Array.Empty<string>());

        var snap = new CountersComparableProjector().Project(snapshot, "after");

        snap.Kind.Should().Be("counters");
        snap.Rows.Should().BeEmpty();

        var names = snap.Metrics.Select(m => m.Definition.Name).ToList();
        names.Should().Contain("counter:System.Runtime/cpu-usage");
        names.Should().Contain("counter:System.Runtime/gen-0-gc-count");
        names.Should().Contain("meter:MyApp/request-duration[route=/api]/p50");
        names.Should().Contain("meter:MyApp/request-duration[route=/api]/p99");

        var byName = snap.Metrics.ToDictionary(m => m.Definition.Name);
        byName["counter:System.Runtime/gen-0-gc-count"].Definition.Aggregation.Should().Be(MetricAggregation.Total);
        byName["counter:System.Runtime/cpu-usage"].Definition.Aggregation.Should().Be(MetricAggregation.Point);
    }

    [Fact]
    public void CountersProjector_RateMeter_EmitsBothPointAndRate()
    {
        var snapshot = new CounterSnapshot(
            ProcessId: 9,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            Counters: Array.Empty<CounterValue>(),
            Meters: new[]
            {
                new MeterInstrumentValue(
                    "MyApp", "requests", "count", "Counter",
                    new Dictionary<string, string?>(),
                    LastValue: 500, Rate: 100, Histogram: null),
            },
            Notes: Array.Empty<string>());

        var snap = new CountersComparableProjector().Project(snapshot, "after");
        var byName = snap.Metrics.ToDictionary(m => m.Definition.Name);

        byName.Should().ContainKey("meter:MyApp/requests");
        byName["meter:MyApp/requests"].Value.Should().Be(500);
        byName["meter:MyApp/requests"].Definition.Aggregation.Should().Be(MetricAggregation.Point);
        byName.Should().ContainKey("meter:MyApp/requests/rate");
        byName["meter:MyApp/requests/rate"].Value.Should().Be(100);
        byName["meter:MyApp/requests/rate"].Definition.Aggregation.Should().Be(MetricAggregation.Rate);
    }

    [Fact]
    public void CountersProjector_AmbiguousTagSets_StayDistinct()
    {
        MeterInstrumentValue Meter(IReadOnlyDictionary<string, string?> tags) => new(
            "M", "i", null, "Gauge", tags, LastValue: 1, Rate: null, Histogram: null);

        var snapshot = new CounterSnapshot(
            ProcessId: 1,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(1),
            Counters: Array.Empty<CounterValue>(),
            Meters: new[]
            {
                Meter(new Dictionary<string, string?> { ["a"] = "b,c=d" }),
                Meter(new Dictionary<string, string?> { ["a"] = "b", ["c"] = "d" }),
            },
            Notes: Array.Empty<string>());

        var snap = new CountersComparableProjector().Project(snapshot, "x");

        // Two genuinely different tag sets must not collapse into one overwriting metric.
        snap.Metrics.Should().HaveCount(2);
        snap.Metrics.Select(m => m.Definition.Name).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void GcEventsProjector_EmitsPauseAndGenerationMetrics()
    {
        var snapshot = new GcSummary(
            ProcessId: 42,
            StartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Duration: TimeSpan.FromSeconds(10),
            TotalCollections: 3,
            TotalPauseTime: TimeSpan.FromMilliseconds(25),
            MaxPauseTime: TimeSpan.FromMilliseconds(12),
            Generations: new[] { new GenerationStats(0, 2), new GenerationStats(2, 1) },
            Events: new[]
            {
                new GcEvent(DateTimeOffset.UtcNow, 0, "AllocSmall", "NonConcurrent", TimeSpan.FromMilliseconds(5)),
                new GcEvent(DateTimeOffset.UtcNow, 2, "Induced", "Blocking", TimeSpan.FromMilliseconds(12)),
            });

        var snap = new GcEventsComparableProjector().Project(snapshot, "after");

        snap.Kind.Should().Be("gc-events");
        snap.Label.Should().Be("after");
        snap.ProcessId.Should().Be(42);
        snap.Rows.Should().BeEmpty();

        var byName = snap.Metrics.ToDictionary(m => m.Definition.Name);
        byName["totalCollections"].Value.Should().Be(3);
        byName["totalCollections"].Definition.BetterDirection.Should().Be(BetterDirection.Lower);
        byName["totalPauseTimeMs"].Value.Should().Be(25);
        byName["pauseTimePercent"].Value.Should().Be(0.25);
        byName["maxPauseTimeMs"].Definition.Role.Should().Be(MetricRole.Secondary);
        byName["gen0Collections"].Value.Should().Be(2);
        byName["gen1Collections"].Value.Should().Be(0);
        byName["gen2Collections"].Value.Should().Be(1);
    }

    [Fact]
    public void HeapProjector_UsesTotalBytesAsLowerBetterPrimary_AndTypeKeys()
    {
        var mvid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var baseline = new HeapSnapshotComparableProjector().Project(HeapSnapshotForProjector("System.Byte[]", "System.Private.CoreLib.dll", 100, 1, mvid, 0x02000042), "baseline");
        var current = new HeapSnapshotComparableProjector().Project(HeapSnapshotForProjector("System.Byte[]", "System.Private.CoreLib.dll", 150, 2, mvid, 0x02000042), "current");

        var row = baseline.Rows.Should().ContainSingle().Subject;
        row.Key.ExactId.Should().Be("11111111-1111-1111-1111-111111111111:0x02000042");
        row.Key.StableId.Should().Be("System.Private.CoreLib.dll!System.Byte[]");
        var primary = row.Metrics.Single(m => m.Definition.Role == MetricRole.Primary);
        primary.Definition.Name.Should().Be("totalBytes");
        primary.Definition.BetterDirection.Should().Be(BetterDirection.Lower);
        row.Metrics.Should().Contain(m => m.Definition.Name == "instanceCount" && m.Definition.Role == MetricRole.Secondary);

        SnapshotDiffer.Compare(new[] { baseline, current }).Verdict.Should().Be("regression");
        SnapshotDiffer.Compare(new[] { current, baseline }).Verdict.Should().Be("improvement");
    }

    [Fact]
    public void CpuProjector_UsesExclusivePercentAsLowerBetterPrimary_AndMethodKeys()
    {
        var mvid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var identity = new MethodIdentity("DoWork", 0, "MyApp.dll", ModuleVersionId: mvid, MetadataToken: 0x06000042, TypeFullName: "MyApp.Worker");
        var baseline = new CpuSampleComparableProjector().Project(CpuTraceForProjector("MyApp.dll", "MyApp.Worker.DoWork", identity, 10, totalSamples: 100), "baseline");
        var current = new CpuSampleComparableProjector().Project(CpuTraceForProjector("MyApp.dll", "MyApp.Worker.DoWork", identity, 20, totalSamples: 100), "current");

        var row = baseline.Rows.Should().ContainSingle().Subject;
        row.Key.ExactId.Should().Be("22222222-2222-2222-2222-222222222222:0x06000042");
        row.Key.StableId.Should().Be("MyApp.dll!MyApp.Worker.DoWork");
        var primary = row.Metrics.Single(m => m.Definition.Role == MetricRole.Primary);
        primary.Definition.Name.Should().Be("exclusivePercent");
        primary.Definition.Aggregation.Should().Be(MetricAggregation.Percent);
        primary.Definition.NormalizedBy.Should().Be(MetricNormalization.SampleCount);
        primary.Definition.BetterDirection.Should().Be(BetterDirection.Lower);
        primary.Value.Should().Be(10);

        SnapshotDiffer.Compare(new[] { baseline, current }).Verdict.Should().Be("regression");
        SnapshotDiffer.Compare(new[] { current, baseline }).Verdict.Should().Be("improvement");
    }

    [Fact]
    public void NativeAllocProjector_UsesExclusivePercentAsLowerBetterPrimary_WithNativeKind()
    {
        var baseline = new NativeAllocSampleComparableProjector().Project(CpuTraceForProjector("libnative.so", "malloc", identity: null, exclusiveSamples: 5, totalSamples: 100), "baseline");
        var current = new NativeAllocSampleComparableProjector().Project(CpuTraceForProjector("libnative.so", "malloc", identity: null, exclusiveSamples: 20, totalSamples: 100), "current");

        baseline.Kind.Should().Be("native-alloc-sample");
        var row = baseline.Rows.Should().ContainSingle().Subject;
        row.Key.Kind.Should().Be("native-alloc-sample");
        row.Key.StableId.Should().Be("libnative.so!malloc");
        var primary = row.Metrics.Single(m => m.Definition.Role == MetricRole.Primary);
        primary.Definition.Name.Should().Be("exclusivePercent");
        primary.Definition.BetterDirection.Should().Be(BetterDirection.Lower);
        primary.Value.Should().Be(5);

        SnapshotDiffer.Compare(new[] { baseline, current }).Verdict.Should().Be("regression");
        SnapshotDiffer.Compare(new[] { current, baseline }).Verdict.Should().Be("improvement");
    }

    [Fact]
    public void AllocationProjector_UsesBytesPerSecondAsLowerBetterPrimary_AndTypeKeys()
    {
        var mvid = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var baseline = new AllocationSampleComparableProjector().Project(AllocationArtifactForProjector("System.String", "System.Private.CoreLib.dll", 4_000, 40, 4, mvid, 0x02000043), "baseline");
        var current = new AllocationSampleComparableProjector().Project(AllocationArtifactForProjector("System.String", "System.Private.CoreLib.dll", 8_000, 80, 4, mvid, 0x02000043), "current");

        var row = baseline.Rows.Should().ContainSingle().Subject;
        row.Key.ExactId.Should().Be("33333333-3333-3333-3333-333333333333:0x02000043");
        row.Key.StableId.Should().Be("System.Private.CoreLib.dll!System.String");
        var primary = row.Metrics.Single(m => m.Definition.Role == MetricRole.Primary);
        primary.Definition.Name.Should().Be("bytesPerSecond");
        primary.Definition.Aggregation.Should().Be(MetricAggregation.Rate);
        primary.Definition.NormalizedBy.Should().Be(MetricNormalization.DurationSeconds);
        primary.Definition.BetterDirection.Should().Be(BetterDirection.Lower);
        primary.Value.Should().Be(1_000);

        SnapshotDiffer.Compare(new[] { baseline, current }).Verdict.Should().Be("regression");
        SnapshotDiffer.Compare(new[] { current, baseline }).Verdict.Should().Be("improvement");
    }

    [Fact]
    public void ContentionProjector_GroupsRowsByCallSite_AndDrivesKeySetVerdicts()
    {
        var projector = new ContentionComparableProjector();
        var baseline = projector.Project(ContentionSnapshot(("MyApp.dll", "MyApp.Locking.Slow", 10, 2)), "baseline");
        var regression = projector.Project(ContentionSnapshot(("MyApp.dll", "MyApp.Locking.Slow", 30, 3)), "regression");
        var improvement = projector.Project(ContentionSnapshot(("MyApp.dll", "MyApp.Locking.Slow", 3, 1)), "improvement");

        baseline.Kind.Should().Be(CollectionHandleKinds.ContentionSnapshot);
        baseline.Rows.Should().ContainSingle();
        var row = baseline.Rows.Single();
        row.Key.Kind.Should().Be("contention-callsite");
        row.Key.Module.Should().Be("MyApp.dll");
        row.Key.MethodName.Should().Be("MyApp.Locking.Slow");
        row.Metrics[0].Definition.Name.Should().Be("totalContentionDurationMs");
        row.Metrics[0].Definition.Role.Should().Be(MetricRole.Primary);
        row.Metrics[0].Definition.BetterDirection.Should().Be(BetterDirection.Lower);
        row.Metrics[0].Value.Should().Be(10);

        SnapshotDiffer.Compare(new[] { baseline, regression }).Verdict.Should().Be("regression");
        SnapshotDiffer.Compare(new[] { baseline, improvement }).Verdict.Should().Be("improvement");
    }

    [Fact]
    public void ThreadPoolProjector_EmitsScalarMetricsOnly_WithDirections()
    {
        var snapshot = new ThreadPoolEventSnapshot(
            ProcessId: 11,
            StartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Duration: TimeSpan.FromSeconds(5),
            WorkerThreadTimeline:
            [
                new ThreadPoolCountBucket(DateTimeOffset.UtcNow, 2),
                new ThreadPoolCountBucket(DateTimeOffset.UtcNow, 8),
                new ThreadPoolCountBucket(DateTimeOffset.UtcNow, 6),
            ],
            IocpThreadTimeline: [new ThreadPoolCountBucket(DateTimeOffset.UtcNow, 1)],
            HillClimbing:
            [
                new ThreadPoolHillClimbingSample(DateTimeOffset.UtcNow, "Warmup", 1, 2, 100),
                new ThreadPoolHillClimbingSample(DateTimeOffset.UtcNow, "Starvation", 2, 4, 90),
            ],
            WorkItemOrigins: [new ThreadPoolWorkItemOrigin("MyApp.Queue.Work", 7)],
            EffectiveSettings: new ThreadPoolEffectiveSettings(1, 100, 1, 100),
            TotalEnqueueEvents: 12,
            TotalDequeueEvents: 5,
            Notes: Array.Empty<string>());

        var snap = new ThreadPoolComparableProjector().Project(snapshot, "after");

        snap.Kind.Should().Be(CollectionHandleKinds.ThreadPoolSnapshot);
        snap.Rows.Should().BeEmpty();
        var byName = snap.Metrics.ToDictionary(m => m.Definition.Name);
        byName["starvationAdjustments"].Value.Should().Be(1);
        byName["starvationAdjustments"].Definition.Role.Should().Be(MetricRole.Primary);
        byName["starvationAdjustments"].Definition.BetterDirection.Should().Be(BetterDirection.Lower);
        byName["pendingWorkItemsEstimate"].Value.Should().Be(7);
        byName["peakWorkerThreadCount"].Value.Should().Be(8);
        byName["peakWorkerThreadCount"].Definition.Role.Should().Be(MetricRole.Primary);
        byName["latestWorkerThreadCount"].Value.Should().Be(6);
        byName["workItemOriginCount"].Definition.Role.Should().Be(MetricRole.Context);
    }

    private static CpuSampleTraceArtifact CpuTraceForProjector(
        string module,
        string method,
        MethodIdentity? identity,
        long exclusiveSamples,
        long totalSamples)
    {
        var symbol = new SymbolRef(module, method);
        var identities = identity is null
            ? null
            : new Dictionary<SymbolRef, MethodIdentity> { [symbol] = identity };
        return new CpuSampleTraceArtifact(
            123,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(5),
            totalSamples,
            new CallTreeNode(
                new SampledFrame(string.Empty, "<root>"),
                totalSamples,
                0,
                [new CallTreeNode(new SampledFrame(module, method), exclusiveSamples, exclusiveSamples, Array.Empty<CallTreeNode>())]),
            MethodIdentities: identities);
    }

    private static HeapSnapshotArtifact HeapSnapshotForProjector(
        string typeName,
        string moduleName,
        long bytes,
        long instances,
        Guid mvid,
        int metadataToken)
    {
        var stats = new[]
        {
            new TypeStat(
                typeName,
                moduleName,
                instances,
                bytes,
                TotalBytesPercent: 0,
                Identity: new TypeIdentity(typeName)
                {
                    ModuleName = moduleName,
                    ModuleVersionId = mvid,
                    MetadataToken = metadataToken,
                }),
        };

        return new HeapSnapshotArtifact(
            HeapSnapshotOrigin.Live,
            ProcessId: 123,
            CapturedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            WalkDuration: TimeSpan.FromMilliseconds(10),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
            TopTypesByBytes: stats,
            TopTypesByInstances: stats);
    }

    private static AllocationSampleArtifact AllocationArtifactForProjector(
        string typeName,
        string moduleName,
        long totalBytes,
        long totalEvents,
        int seconds,
        Guid mvid,
        int metadataToken)
    {
        var identity = new TypeIdentity(typeName)
        {
            ModuleName = moduleName,
            ModuleVersionId = mvid,
            MetadataToken = metadataToken,
        };
        var types = new[] { new AllocatedType(typeName, totalBytes, totalEvents, HeapKind.Small, identity) };
        var summary = new AllocationSample(
            ProcessId: 123,
            StartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Duration: TimeSpan.FromSeconds(seconds),
            TotalEvents: totalEvents,
            TotalBytes: totalBytes,
            TopByBytes: types,
            TopByCount: types);
        var trace = new CpuSampleTraceArtifact(
            123,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(seconds),
            totalEvents,
            new CallTreeNode(new SampledFrame(string.Empty, "<root>"), totalEvents, 0, Array.Empty<CallTreeNode>()));
        return new AllocationSampleArtifact(summary, trace);
    }

    private static ContentionSnapshot ContentionSnapshot(params (string module, string method, double durationMs, ulong lockId)[] events)
        => new(
            ProcessId: 42,
            StartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Duration: TimeSpan.FromSeconds(5),
            TotalEvents: events.Length,
            DistinctMonitors: events.Select(static item => item.lockId).Distinct().Count(),
            TotalContentionDuration: TimeSpan.FromMilliseconds(events.Sum(static item => item.durationMs)),
            P50ContentionDuration: TimeSpan.Zero,
            P95ContentionDuration: TimeSpan.Zero,
            MaxContentionDuration: TimeSpan.FromMilliseconds(events.Max(static item => item.durationMs)),
            Events: events.Select(static item => new ContentionEventSample(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMilliseconds(item.durationMs),
                TimeSpan.FromMilliseconds(item.durationMs),
                ContendingThreadId: 1,
                OwnerManagedThreadId: 2,
                LockId: item.lockId,
                AssociatedObjectId: 0,
                CallSiteMethod: item.method,
                CallSiteModule: item.module)).ToArray(),
            Notes: Array.Empty<string>());
}
