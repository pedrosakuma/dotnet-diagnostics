using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Mcp.Resources;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class QuerySnapshotDiffToolTests
{
    [Fact]
    public async Task Diff_RejectsMixedKinds()
    {
        var store = new MemoryDiagnosticHandleStore();
        var cpuHandle = store.Register(123, "cpu-sample", CpuArtifact(1), TimeSpan.FromMinutes(10));
        var heapHandle = store.Register(123, "heap-snapshot", HeapSnapshot(("System.Byte[]", 128, 1)), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, cpuHandle.Id, heapHandle.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("Accepted pairs");
        result.Error.Message.Should().Contain("cpu-sample");
        result.Error.Message.Should().Contain("heap-snapshot");
    }

    [Fact]
    public async Task Diff_CpuHandle_ReturnsSampleDiffEnvelope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baselineHandle = store.Register(123, "cpu-sample", CpuArtifact(2), TimeSpan.FromMinutes(10));
        var currentHandle = store.Register(123, "cpu-sample", CpuArtifact(6), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, currentHandle.Id, baselineHandle.Id);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SampleDiff<MethodDiffKey, CpuDiffMetric>>().Subject;
        diff.Kind.Should().Be("cpu-sample");
        diff.Verdict.Should().Be("regression");
        diff.Changed.Should().ContainSingle(row => row.Direction == "up" && row.Key.Symbol.MethodFullName.Contains("DoWork", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diff_HeapHandle_ReturnsSampleDiffEnvelope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baselineHandle = store.Register(123, "heap-snapshot", HeapSnapshot(("System.Byte[]", 128, 1)), TimeSpan.FromMinutes(10));
        var currentHandle = store.Register(123, "heap-snapshot", HeapSnapshot(("System.Byte[]", 512, 4)), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, currentHandle.Id, baselineHandle.Id);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SampleDiff<TypeIdentity, HeapDiffMetric>>().Subject;
        diff.Kind.Should().Be("heap-snapshot");
        diff.Verdict.Should().Be("regression");
        diff.Changed.Should().ContainSingle(row => row.Key.TypeFullName == "System.Byte[]");
    }

    [Fact]
    public async Task Diff_AllocationHandle_ReturnsSampleDiffEnvelope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baselineHandle = store.Register(123, "allocation-sample", AllocationArtifact(2_000, 20, 4), TimeSpan.FromMinutes(10));
        var currentHandle = store.Register(123, "allocation-sample", AllocationArtifact(8_000, 80, 4), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, currentHandle.Id, baselineHandle.Id);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SampleDiff<TypeIdentity, AllocationDiffMetric>>().Subject;
        diff.Kind.Should().Be("allocation-sample");
        diff.Verdict.Should().Be("regression");
        diff.Changed.Should().ContainSingle(row => row.Key.TypeFullName == "System.String");
    }

    [Fact]
    public async Task Diff_NativeAllocHandle_ReturnsSampleDiffEnvelope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baselineHandle = store.Register(123, DiagnosticTools.NativeAllocHandleKind, CpuArtifact(2), TimeSpan.FromMinutes(10));
        var currentHandle = store.Register(123, DiagnosticTools.NativeAllocHandleKind, CpuArtifact(6), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, currentHandle.Id, baselineHandle.Id);

        result.Error.Should().BeNull();
        result.Data.Should().BeOfType<SampleDiff<MethodDiffKey, CpuDiffMetric>>();
    }

    [Fact]
    public async Task Diff_HeapComparisonHandles_ReturnsJourneyDiff()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(123, "heap-snapshot", HeapSnapshot(("System.Byte[]", 128, 1)), TimeSpan.FromMinutes(10));
        var second = store.Register(123, "heap-snapshot", HeapSnapshot(("System.Byte[]", 256, 2)), TimeSpan.FromMinutes(10));
        var current = store.Register(123, "heap-snapshot", HeapSnapshot(("System.Byte[]", 512, 4)), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, comparisonHandles: [first.Id, second.Id]);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SnapshotJourneyDiff>().Subject;
        diff.Kind.Should().Be("heap-snapshot");
        diff.KeyMatrix.Should().Contain(row => row.DisplayName == "System.Byte[]");
    }

    [Fact]
    public async Task Diff_CpuComparisonHandles_ReturnsJourneyDiff()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(123, "cpu-sample", CpuArtifact(2), TimeSpan.FromMinutes(10));
        var second = store.Register(123, "cpu-sample", CpuArtifact(4), TimeSpan.FromMinutes(10));
        var current = store.Register(123, "cpu-sample", CpuArtifact(6), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, comparisonHandles: [first.Id, second.Id]);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SnapshotJourneyDiff>().Subject;
        diff.Kind.Should().Be("cpu-sample");
        diff.KeyMatrix.Should().Contain(row => row.DisplayName.Contains("DoWork", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diff_NativeAllocComparisonHandles_ReturnsJourneyDiff()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(123, DiagnosticTools.NativeAllocHandleKind, CpuArtifact(2), TimeSpan.FromMinutes(10));
        var second = store.Register(123, DiagnosticTools.NativeAllocHandleKind, CpuArtifact(4), TimeSpan.FromMinutes(10));
        var current = store.Register(123, DiagnosticTools.NativeAllocHandleKind, CpuArtifact(6), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, comparisonHandles: [first.Id, second.Id]);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SnapshotJourneyDiff>().Subject;
        diff.Kind.Should().Be(DiagnosticTools.NativeAllocHandleKind);
        diff.KeyMatrix.Should().Contain(row => row.DisplayName.Contains("DoWork", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diff_AllocationComparisonHandles_ReturnsJourneyDiff()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(123, "allocation-sample", AllocationArtifact(2_000, 20, 4), TimeSpan.FromMinutes(10));
        var second = store.Register(123, "allocation-sample", AllocationArtifact(4_000, 40, 4), TimeSpan.FromMinutes(10));
        var current = store.Register(123, "allocation-sample", AllocationArtifact(8_000, 80, 4), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, comparisonHandles: [first.Id, second.Id]);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SnapshotJourneyDiff>().Subject;
        diff.Kind.Should().Be("allocation-sample");
        diff.KeyMatrix.Should().Contain(row => row.DisplayName == "System.String");
    }

    [Fact]
    public async Task Diff_GcDatasComparisonHandles_ReturnsJourneyDiff()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(123, CollectionHandleKinds.GcDatas, GcDatasSnapshot(2.0f), TimeSpan.FromMinutes(10));
        var second = store.Register(123, CollectionHandleKinds.GcDatas, GcDatasSnapshot(4.0f), TimeSpan.FromMinutes(10));
        var current = store.Register(123, CollectionHandleKinds.GcDatas, GcDatasSnapshot(7.0f), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, comparisonHandles: [first.Id, second.Id]);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SnapshotJourneyDiff>().Subject;
        diff.Kind.Should().Be(CollectionHandleKinds.GcDatas);
        diff.Labels.Should().Equal("comparison-1", "comparison-2", "current");
        diff.MetricSeries.Should().Contain(series => series.Definition.Name == "meanMedianThroughputCostPercent" && series.Values.Count == 3);
        diff.Pairwise.Should().NotBeNull();
    }

    [Fact]
    public async Task Diff_GcDatasComparisonHandles_DispersionModeReturnsDispersionVerdict()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(123, CollectionHandleKinds.GcDatas, GcDatasSnapshot(10.0f), TimeSpan.FromMinutes(10));
        var second = store.Register(123, CollectionHandleKinds.GcDatas, GcDatasSnapshot(50.0f), TimeSpan.FromMinutes(10));
        var current = store.Register(123, CollectionHandleKinds.GcDatas, GcDatasSnapshot(10.0f), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, comparisonHandles: [first.Id, second.Id], mode: "dispersion");

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SnapshotJourneyDiff>().Subject;
        diff.Mode.Should().Be(JourneyMode.Dispersion);
        diff.Verdict.Should().Be("dispersed");
        diff.Pairwise.Should().BeNull();
    }

    [Fact]
    public async Task Diff_DispersionModeRejectsLegacyPairwiseBaselineHandle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baselineHandle = store.Register(123, "cpu-sample", CpuArtifact(2), TimeSpan.FromMinutes(10));
        var currentHandle = store.Register(123, "cpu-sample", CpuArtifact(6), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, currentHandle.Id, baselineHandle.Id, mode: "dispersion");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("comparisonHandles");
        result.Error.Message.Should().Contain("cpu-sample");
    }

    [Fact]
    public async Task Diff_InvalidModeReturnsInvalidArgument()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshot(10), TimeSpan.FromMinutes(10));
        var current = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshot(25), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, baseline.Id, mode: "fleet");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("trend");
        result.Error.Message.Should().Contain("dispersion");
    }

    [Fact]
    public async Task Diff_CountersBaselineHandle_ReturnsJourneyDiffInlineWhenSmall()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshot(10), TimeSpan.FromMinutes(10));
        var current = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshot(25), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, baseline.Id);

        result.Error.Should().BeNull();
        result.Handle.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SnapshotJourneyDiff>().Subject;
        diff.Kind.Should().Be(CollectionHandleKinds.Counters);
        diff.Labels.Should().Equal("baseline", "current");
        diff.MetricSeries.Should().Contain(series => series.Definition.Name == "counter:System.Runtime/cpu-usage");
    }

    [Fact]
    public async Task Diff_CountersBaselineHandle_CompactDepthBoundsInlinePayload()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshotMany(0, 8), TimeSpan.FromMinutes(10));
        var current = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshotMany(10, 8), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, baseline.Id, topN: 2, depth: "compact");

        result.Error.Should().BeNull();
        result.Handle.Should().BeNull();
        var summary = result.Data.Should().BeOfType<JourneyDiffCompactSummary>().Subject;
        summary.Counts.MetricSeries.Should().Be(8);
        summary.MetricSeries.Should().HaveCount(2);
        summary.KeyMatrix.Should().BeEmpty();
        summary.ResourceUri.Should().BeNull();
        summary.Depth.Should().Be("compact");
    }

    [Fact]
    public async Task Diff_CountersBaselineHandle_LargeDiffReturnsCompactSummaryAndResourceHandle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshotMany(0, 700), TimeSpan.FromMinutes(10));
        var current = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshotMany(10, 700), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, baseline.Id, topN: 3);

        result.Error.Should().BeNull();
        result.Handle.Should().NotBeNullOrWhiteSpace();
        var summary = result.Data.Should().BeOfType<JourneyDiffCompactSummary>().Subject;
        summary.ResourceUri.Should().Be(JourneyDiffPresentation.ResourceUri(result.Handle!));
        summary.MetricSeries.Should().HaveCount(3);
        summary.Counts.MetricSeries.Should().Be(700);

        var retained = store.TryGet<SnapshotJourneyDiff>(result.Handle!);
        retained.Should().NotBeNull();
        retained!.MetricSeries.Should().HaveCount(700);

        var resourceJson = JourneyDiffResources.ReadDiff(store, result.Handle!);
        var resourceDiff = System.Text.Json.JsonSerializer.Deserialize(resourceJson, ComparableSnapshotJsonContext.Default.SnapshotJourneyDiff);
        resourceDiff.Should().NotBeNull();
        resourceDiff!.MetricSeries.Should().HaveCount(700);
    }

    [Fact]
    public async Task Diff_GcEventsBaselineHandle_ReturnsJourneyDiff()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, CollectionHandleKinds.GcEvents, GcSummary(totalCollections: 2, totalPauseMs: 5), TimeSpan.FromMinutes(10));
        var current = store.Register(123, CollectionHandleKinds.GcEvents, GcSummary(totalCollections: 5, totalPauseMs: 20), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, baseline.Id);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SnapshotJourneyDiff>().Subject;
        diff.Kind.Should().Be(CollectionHandleKinds.GcEvents);
        diff.Verdict.Should().Be("regression");
        diff.MetricSeries.Should().Contain(series => series.Definition.Name == "totalPauseTimeMs");
    }

    [Fact]
    public async Task Diff_ContentionComparisonHandles_ReturnsJourneyDiffWithKeySetVerdict()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(123, CollectionHandleKinds.ContentionSnapshot, ContentionSnapshot(("MyApp.dll", "MyApp.Locking.Slow", 5, 1)), TimeSpan.FromMinutes(10));
        var second = store.Register(123, CollectionHandleKinds.ContentionSnapshot, ContentionSnapshot(("MyApp.dll", "MyApp.Locking.Slow", 10, 1)), TimeSpan.FromMinutes(10));
        var current = store.Register(123, CollectionHandleKinds.ContentionSnapshot, ContentionSnapshot(("MyApp.dll", "MyApp.Locking.Slow", 20, 1)), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, comparisonHandles: [first.Id, second.Id]);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SnapshotJourneyDiff>().Subject;
        diff.Kind.Should().Be(CollectionHandleKinds.ContentionSnapshot);
        diff.Verdict.Should().Be("regression");
        diff.Labels.Should().Equal("comparison-1", "comparison-2", "current");
        diff.KeyMatrix.Should().ContainSingle(row => row.Key.Module == "MyApp.dll" && row.Key.MethodName == "MyApp.Locking.Slow");
        diff.Pairwise.Should().NotBeNull();
        diff.Pairwise!.Headline.Verdict.Should().Be("regression");
    }

    [Fact]
    public async Task Diff_ThreadPoolComparisonHandles_ReturnsJourneyDiffWithScalarVerdict()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(123, CollectionHandleKinds.ThreadPoolSnapshot, ThreadPoolSnapshot(starvationAdjustments: 0, pendingWorkItems: 0), TimeSpan.FromMinutes(10));
        var second = store.Register(123, CollectionHandleKinds.ThreadPoolSnapshot, ThreadPoolSnapshot(starvationAdjustments: 1, pendingWorkItems: 2), TimeSpan.FromMinutes(10));
        var current = store.Register(123, CollectionHandleKinds.ThreadPoolSnapshot, ThreadPoolSnapshot(starvationAdjustments: 4, pendingWorkItems: 12), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, comparisonHandles: [first.Id, second.Id]);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SnapshotJourneyDiff>().Subject;
        diff.Kind.Should().Be(CollectionHandleKinds.ThreadPoolSnapshot);
        diff.Verdict.Should().Be("regression");
        diff.KeyMatrix.Should().BeEmpty();
        diff.MetricSeries.Should().Contain(series => series.Definition.Name == "starvationAdjustments" && series.Direction == "regressed");
        diff.MetricSeries.Should().Contain(series => series.Definition.Name == "pendingWorkItemsEstimate" && series.Direction == "regressed");
        diff.Pairwise.Should().NotBeNull();
        diff.Pairwise!.Headline.Verdict.Should().Be("regression");
    }

    [Fact]
    public void CompactDispersionSummary_RanksMetricSeriesByCoefficientOfVariation()
    {
        var store = new MemoryDiagnosticHandleStore();
        var diff = SnapshotDiffer.Compare(
            new[]
            {
                MetricSnapshot("pod0", ("outlier", 10), ("monotonic", 10)),
                MetricSnapshot("pod1", ("outlier", 100), ("monotonic", 50)),
                MetricSnapshot("pod2", ("outlier", 10), ("monotonic", 90)),
            },
            JourneyMode.Dispersion);

        var result = JourneyDiffPresentation.BuildResult(
            diff,
            store,
            processId: 123,
            topN: 1,
            JourneyDiffDepth.Compact,
            "summary",
            evictWhenProcessExits: false,
            HandleOrigin.Imported);

        result.Error.Should().BeNull();
        var summary = result.Data.Should().BeOfType<JourneyDiffCompactSummary>().Subject;
        summary.MetricSeries.Should().ContainSingle();
        summary.MetricSeries[0].Definition.Name.Should().Be("outlier");
    }

    [Fact]
    public void CompactDispersionSummary_RanksKeyRowsByCoefficientOfVariation()
    {
        var store = new MemoryDiagnosticHandleStore();
        var diff = SnapshotDiffer.Compare(
            new[]
            {
                KeySnapshot("pod0", ("outlier", 10), ("monotonic", 10)),
                KeySnapshot("pod1", ("outlier", 100), ("monotonic", 50)),
                KeySnapshot("pod2", ("outlier", 10), ("monotonic", 90)),
            },
            JourneyMode.Dispersion,
            topN: 1);

        var result = JourneyDiffPresentation.BuildResult(
            diff,
            store,
            processId: 123,
            topN: 1,
            JourneyDiffDepth.Compact,
            "summary",
            evictWhenProcessExits: false,
            HandleOrigin.Imported);

        result.Error.Should().BeNull();
        var summary = result.Data.Should().BeOfType<JourneyDiffCompactSummary>().Subject;
        summary.KeyMatrix.Should().ContainSingle();
        summary.KeyMatrix[0].DisplayName.Should().Be("outlier");
    }

    [Fact]
    public async Task Diff_RejectsBaselineHandleWithComparisonHandles()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshot(10), TimeSpan.FromMinutes(10));
        var extra = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshot(12), TimeSpan.FromMinutes(10));
        var current = store.Register(123, CollectionHandleKinds.Counters, CounterSnapshot(25), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, current.Id, baseline.Id, [extra.Id]);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("cannot be combined");
    }

    private static async Task<DotnetDiagnostics.Core.DiagnosticResult<object>> QuerySnapshot(
        MemoryDiagnosticHandleStore store,
        string currentHandle,
        string? baselineHandle = null,
        string[]? comparisonHandles = null,
        int? topN = null,
        string depth = "full",
        string? mode = null)
        => await QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(null),
            new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            new DotnetDiagnostics.Core.Symbols.ClrMdNativeAddressResolver(),
            handle: currentHandle,
            view: "diff",
            topN: topN,
            baselineHandle: baselineHandle,
            comparisonHandles: comparisonHandles,
            depth: depth,
            mode: mode,
            cancellationToken: CancellationToken.None);

    private static ComparableSnapshot MetricSnapshot(string label, params (string name, double value)[] metrics)
        => new(
            ComparableSnapshot.SchemaV1,
            CollectionHandleKinds.Counters,
            label,
            DateTimeOffset.UnixEpoch,
            123,
            metrics.Select(metric => new MetricValue(new MetricDefinition(metric.name, MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Point, MetricNormalization.None, "count"), metric.value)).ToArray(),
            Array.Empty<ComparableRow>());

    private static ComparableSnapshot KeySnapshot(string label, params (string id, double value)[] rows)
        => new(
            ComparableSnapshot.SchemaV1,
            "cpu-sample",
            label,
            DateTimeOffset.UnixEpoch,
            123,
            Array.Empty<MetricValue>(),
            rows.Select(row => new ComparableRow(
                new ComparableKey("cpu-sample", row.id),
                row.id,
                [new MetricValue(new MetricDefinition("exclusivePercent", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Point, MetricNormalization.None, "%"), row.value)])).ToArray());

    private static CpuSampleTraceArtifact CpuArtifact(long exclusiveSamples)
        => new(
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            100,
            new CallTreeNode(
                new SampledFrame(string.Empty, "<root>"),
                100,
                0,
                [new CallTreeNode(new SampledFrame("MyApp.dll", "MyApp.Worker.DoWork"), exclusiveSamples, exclusiveSamples, Array.Empty<CallTreeNode>())]));

    private static HeapSnapshotArtifact HeapSnapshot(params (string typeName, long bytes, long instances)[] rows)
    {
        var stats = rows.Select(row =>
            new TypeStat(
                row.typeName,
                ModuleName: null,
                InstanceCount: row.instances,
                TotalBytes: row.bytes,
                TotalBytesPercent: 0,
                Identity: new TypeIdentity(row.typeName))).ToArray();

        return new(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: 123,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(10),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
            TopTypesByBytes: stats,
            TopTypesByInstances: stats);
    }

    private static AllocationSampleArtifact AllocationArtifact(long totalBytes, long totalEvents, int seconds)
    {
        var summary = new AllocationSample(
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(seconds),
            TotalEvents: totalEvents,
            TotalBytes: totalBytes,
            TopByBytes:
            [
                new AllocatedType("System.String", totalBytes, totalEvents, HeapKind.Small, new TypeIdentity("System.String")),
            ],
            TopByCount:
            [
                new AllocatedType("System.String", totalBytes, totalEvents, HeapKind.Small, new TypeIdentity("System.String")),
            ]);

        var trace = new CpuSampleTraceArtifact(
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(seconds),
            totalEvents,
            new CallTreeNode(new SampledFrame(string.Empty, "<root>"), totalEvents, 0, Array.Empty<CallTreeNode>()));
        return new AllocationSampleArtifact(summary, trace);
    }

    private static GcDatasSnapshot GcDatasSnapshot(float medianThroughputCostPercent)
        => new(
            ProcessId: 123,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            Samples:
            [
                new DatasSampleEvent(DateTimeOffset.UtcNow, 1, 1000, 50, 0, 0, 20_000_000, 12 * 1024 * 1024),
            ],
            TuningEvents:
            [
                new DatasTuningEvent(DateTimeOffset.UtcNow, 4, 16, 1, 1, 20_000_000, medianThroughputCostPercent, medianThroughputCostPercent, 0, 3, 0, 5, 1, 0, 0, 1, 0, 0),
            ],
            FullGcTuningEvents: Array.Empty<DatasFullGcTuningEvent>(),
            ParseStats: new DatasParseStats(0, 0, 0));

    private static CounterSnapshot CounterSnapshot(double cpuUsage)
        => new(
            ProcessId: 123,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            Counters:
            [
                new CounterValue("System.Runtime", "cpu-usage", "CPU Usage", cpuUsage, CounterKind.Mean, "%"),
            ],
            Meters: Array.Empty<MeterInstrumentValue>(),
            Notes: Array.Empty<string>());

    private static CounterSnapshot CounterSnapshotMany(double baseValue, int count)
        => new(
            ProcessId: 123,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            Counters: Enumerable.Range(0, count)
                .Select(i => new CounterValue("System.Runtime", $"counter-{i}", $"Counter {i}", baseValue + i, CounterKind.Mean, "units"))
                .ToArray(),
            Meters: Array.Empty<MeterInstrumentValue>(),
            Notes: Array.Empty<string>());

    private static GcSummary GcSummary(int totalCollections, double totalPauseMs)
        => new(
            ProcessId: 123,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalCollections: totalCollections,
            TotalPauseTime: TimeSpan.FromMilliseconds(totalPauseMs),
            MaxPauseTime: TimeSpan.FromMilliseconds(totalPauseMs / 2),
            Generations:
            [
                new GenerationStats(0, totalCollections),
            ],
            Events:
            [
                new GcEvent(DateTimeOffset.UtcNow, 0, "AllocSmall", "NonConcurrent", TimeSpan.FromMilliseconds(totalPauseMs / 2)),
            ]);

    private static ContentionSnapshot ContentionSnapshot(params (string module, string method, double durationMs, ulong lockId)[] events)
        => new(
            ProcessId: 123,
            StartedAt: DateTimeOffset.UtcNow,
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

    private static ThreadPoolEventSnapshot ThreadPoolSnapshot(int starvationAdjustments, int pendingWorkItems)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var hillClimbing = Enumerable.Range(0, starvationAdjustments)
            .Select(i => new ThreadPoolHillClimbingSample(timestamp.AddMilliseconds(i), "Starvation", i, i + 1, 100 - i))
            .ToArray();

        return new ThreadPoolEventSnapshot(
            ProcessId: 123,
            StartedAt: timestamp,
            Duration: TimeSpan.FromSeconds(5),
            WorkerThreadTimeline:
            [
                new ThreadPoolCountBucket(timestamp, 2),
                new ThreadPoolCountBucket(timestamp.AddSeconds(1), 2 + starvationAdjustments),
            ],
            IocpThreadTimeline: [new ThreadPoolCountBucket(timestamp, 1)],
            HillClimbing: hillClimbing,
            WorkItemOrigins: [new ThreadPoolWorkItemOrigin("MyApp.Queue.Work", 99)],
            EffectiveSettings: new ThreadPoolEffectiveSettings(1, 100, 1, 100),
            TotalEnqueueEvents: 100 + pendingWorkItems,
            TotalDequeueEvents: 100,
            Notes: Array.Empty<string>());
    }

    private sealed class StubDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
