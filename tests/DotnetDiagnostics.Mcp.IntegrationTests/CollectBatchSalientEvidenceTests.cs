using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.NativeLockContention;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class CollectBatchSalientEvidenceTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 7, 24, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Apply_LohAllocBatch_SurfacesBoundedInlineEvidenceAndLabelsGen2Scopes()
    {
        var store = new MemoryDiagnosticHandleStore();
        var counters = LohCounterSnapshot();
        var gc = Gen2GcSummary();
        var countersHandle = store.Register(123, CollectionHandleKinds.Counters, counters, TimeSpan.FromMinutes(10));
        var gcHandle = store.Register(123, CollectionHandleKinds.GcEvents, gc, TimeSpan.FromMinutes(10));
        var report = BatchReport(countersHandle, gcHandle);

        var projected = CollectBatchSalientEvidence.Apply(report, store);

        var inline = DeserializeCounters(projected);
        inline.Counters.Should().HaveCountLessThanOrEqualTo(CollectBatchSalientEvidence.MaxInlineCounters);
        inline.Counters.Select(static counter => counter.Name).Should().Contain(
            ["loh-size", "gc-fragmentation", "gen-2-size", "gen-2-gc-count", "time-in-gc"]);
        inline.Counters.Should().NotContain(static counter => counter.Name.StartsWith("noise-", StringComparison.Ordinal));

        projected.Gen2Evidence.Should().BeEquivalentTo(new
        {
            EventCounterIntervalDelta = (double?)1,
            EventCounterIntervalSeconds = 1,
            MeterRatePerSecond = (double?)5,
            MeterProcessCumulative = (double?)97,
            GcCollectorWindowCount = 32,
            GcCollectorWindowSeconds = 6,
        });
        projected.Gen2Evidence!.Explanation.Should().Contain("not interchangeable");
    }

    [Fact]
    public void Apply_LohAllocBatch_PreservesFullCounterDrilldownParity()
    {
        var store = new MemoryDiagnosticHandleStore();
        var counters = LohCounterSnapshot();
        var gc = Gen2GcSummary();
        var countersHandle = store.Register(123, CollectionHandleKinds.Counters, counters, TimeSpan.FromMinutes(10));
        var gcHandle = store.Register(123, CollectionHandleKinds.GcEvents, gc, TimeSpan.FromMinutes(10));

        var projected = CollectBatchSalientEvidence.Apply(BatchReport(countersHandle, gcHandle), store);

        projected.Results.Single(static result => result.Kind == "counters").Handle.Should().Be(countersHandle.Id);
        var lookup = store.TryGet<CounterSnapshot>(countersHandle.Id);
        lookup.Should().BeSameAs(counters);

        var drilldown = CollectionQueryDispatcher.Dispatch(
            CollectionHandleKinds.Counters,
            "byProvider",
            lookup!,
            topN: 50);
        var byProvider = drilldown.Result!.Payload.Should().BeOfType<CountersByProviderView>().Subject;
        var drilldownCounters = byProvider.Providers.SelectMany(static provider => provider.Counters).ToList();
        var inlineCounters = DeserializeCounters(projected).Counters;

        drilldownCounters.Should().HaveCount(counters.Counters.Count);
        drilldownCounters.Should().Contain(static counter => counter.Name == "noise-24");
        foreach (var inlineCounter in inlineCounters)
        {
            drilldownCounters.Should().ContainEquivalentOf(inlineCounter);
        }
    }

    private static CollectBatchReport BatchReport(DiagnosticHandle counters, DiagnosticHandle gc)
        => new(
            ProcessId: 123,
            DurationSeconds: 6,
            Results:
            [
                new CollectBatchEntryResult(
                    "collect_events",
                    "counters",
                    "counter summary",
                    JsonSerializer.SerializeToElement(new CollectEventsEnvelope("counters")),
                    counters.Id,
                    counters.ExpiresAt,
                    Error: null),
                new CollectBatchEntryResult(
                    "collect_events",
                    "gc",
                    "gc summary",
                    JsonSerializer.SerializeToElement(new CollectEventsEnvelope("gc")),
                    gc.Id,
                    gc.ExpiresAt,
                    Error: null),
            ]);

    private static CounterSnapshot DeserializeCounters(CollectBatchReport report)
    {
        var data = report.Results.Single(static result => result.Kind == "counters").Data!.Value;
        return data.GetProperty("counters").Deserialize<CounterSnapshot>(McpJsonUtilities.DefaultOptions)!;
    }

    private static CounterSnapshot LohCounterSnapshot()
    {
        var counters = new List<CounterValue>
        {
            Counter("cpu-usage", 42, CounterKind.Mean, "%"),
            Counter("working-set", 512_000_000, CounterKind.Mean, "B"),
            Counter("gc-heap-size", 256_000_000, CounterKind.Mean, "B"),
            Counter("gen-2-gc-count", 1, CounterKind.Sum),
            Counter("time-in-gc", 15, CounterKind.Mean, "%"),
            Counter("alloc-rate", 80_000_000, CounterKind.Sum, "B / 1 sec"),
            Counter("threadpool-thread-count", 12, CounterKind.Mean),
            Counter("threadpool-queue-length", 0, CounterKind.Mean),
            Counter("active-timer-count", 4, CounterKind.Mean),
            Counter("exception-count", 0, CounterKind.Sum),
            Counter("monitor-lock-contention-count", 0, CounterKind.Sum),
            Counter("gen-2-size", 180_000_000, CounterKind.Mean, "B"),
            Counter("loh-size", 3_299_280, CounterKind.Mean, "B"),
            Counter("gc-fragmentation", 64.5, CounterKind.Mean, "%"),
        };
        counters.AddRange(Enumerable.Range(0, 25)
            .Select(index => Counter($"noise-{index}", index, CounterKind.Mean)));

        return new CounterSnapshot(
            ProcessId: 123,
            StartedAt: StartedAt,
            Duration: TimeSpan.FromSeconds(6),
            Counters: counters,
            Meters:
            [
                new MeterInstrumentValue(
                    "System.Runtime",
                    "dotnet.gc.collections",
                    "{collection}",
                    "Counter",
                    new Dictionary<string, string?> { ["gc.heap.generation"] = "gen2" },
                    LastValue: 97,
                    Rate: 5,
                    Histogram: null),
            ],
            Notes: Array.Empty<string>());
    }

    private static GcSummary Gen2GcSummary()
        => new(
            ProcessId: 123,
            StartedAt: StartedAt,
            Duration: TimeSpan.FromSeconds(6),
            TotalCollections: 32,
            TotalPauseTime: TimeSpan.FromSeconds(1.311),
            MaxPauseTime: TimeSpan.FromMilliseconds(80),
            Generations: [new GenerationStats(2, 32)],
            Events: Array.Empty<GcEvent>());

    // --- ApplyNativeContentionEvidence (issue #855) ----------------------------------------------

    [Fact]
    public void ApplyNativeContentionEvidence_BothSucceed_MergesEvidenceWithoutElevatingLevel()
    {
        var store = new MemoryDiagnosticHandleStore();
        var lockHandle = RegisterNativeLockSample(store, sampledLockCalls: 200);
        var offCpuHandle = RegisterOffCpuSnapshot(store, ConfirmedBlockingEvidence());
        var report = NativeContentionBatchReport(lockHandle, offCpuHandle);

        var projected = CollectBatchSalientEvidence.ApplyNativeContentionEvidence(report, store);

        projected.NativeContentionEvidence.Should().NotBeNull();
        projected.NativeContentionEvidence!.Level.Should().Be(NativeContentionEvidenceLevels.ConfirmedBlocking);
        projected.NativeContentionEvidence.SampledLockCallCount.Should().Be(200);
        projected.NativeContentionEvidence.ClosedNativeSyncSpanCount.Should().Be(3);
    }

    [Fact]
    public void ApplyNativeContentionEvidence_UncontendedWorkload_StaysActivityOnly()
    {
        var store = new MemoryDiagnosticHandleStore();
        var lockHandle = RegisterNativeLockSample(store, sampledLockCalls: 900);
        var offCpuHandle = RegisterOffCpuSnapshot(store, NoneEvidence());
        var report = NativeContentionBatchReport(lockHandle, offCpuHandle);

        var projected = CollectBatchSalientEvidence.ApplyNativeContentionEvidence(report, store);

        projected.NativeContentionEvidence.Should().NotBeNull();
        projected.NativeContentionEvidence!.Level.Should().Be(
            NativeContentionEvidenceLevels.None,
            "heavy sampled mutex-call activity alone must never be mislabeled as blocking");
    }

    [Fact]
    public void ApplyNativeContentionEvidence_NativeLockEntryFailed_KeepsOffCpuEvidence_PartialSuccess()
    {
        var store = new MemoryDiagnosticHandleStore();
        var offCpuHandle = RegisterOffCpuSnapshot(store, ConfirmedBlockingEvidence());
        var report = new CollectBatchReport(
            ProcessId: 123,
            DurationSeconds: 6,
            Results:
            [
                new CollectBatchEntryResult(
                    "collect_sample", "native-lock-contention", "failed: perf permission denied",
                    Data: null, Handle: null, HandleExpiresAt: null,
                    Error: new DiagnosticError("PermissionDenied", "perf permission denied", null)),
                new CollectBatchEntryResult(
                    "collect_sample", "off_cpu", "off-cpu summary",
                    JsonSerializer.SerializeToElement(new object()), offCpuHandle.Id, offCpuHandle.ExpiresAt, Error: null),
            ]);

        var projected = CollectBatchSalientEvidence.ApplyNativeContentionEvidence(report, store);

        projected.NativeContentionEvidence.Should().NotBeNull("partial success must still surface the successful collector's evidence");
        projected.NativeContentionEvidence!.Level.Should().Be(NativeContentionEvidenceLevels.ConfirmedBlocking);
        projected.NativeContentionEvidence.SampledLockCallCount.Should().Be(0);
        projected.NativeContentionEvidence.Summary.Should().Contain("native-lock-contention did not run (or failed)");
        projected.Results.Single(r => r.Kind == "native-lock-contention").Error.Should().NotBeNull();
    }

    [Fact]
    public void ApplyNativeContentionEvidence_OffCpuEntryTimedOut_KeepsNativeLockEvidence_PartialSuccess()
    {
        var store = new MemoryDiagnosticHandleStore();
        var lockHandle = RegisterNativeLockSample(store, sampledLockCalls: 77);
        var report = new CollectBatchReport(
            ProcessId: 123,
            DurationSeconds: 6,
            Results:
            [
                new CollectBatchEntryResult(
                    "collect_sample", "native-lock-contention", "native-lock summary",
                    JsonSerializer.SerializeToElement(new object()), lockHandle.Id, lockHandle.ExpiresAt, Error: null),
                new CollectBatchEntryResult(
                    "collect_sample", "off_cpu", "failed: bounded perf subprocess timed out",
                    Data: null, Handle: null, HandleExpiresAt: null,
                    Error: new DiagnosticError("CaptureTimeout", "bounded perf subprocess timed out", null)),
            ]);

        var projected = CollectBatchSalientEvidence.ApplyNativeContentionEvidence(report, store);

        projected.NativeContentionEvidence.Should().NotBeNull("partial success must still surface the successful collector's evidence");
        projected.NativeContentionEvidence!.Level.Should().Be(NativeContentionEvidenceLevels.Activity);
        projected.NativeContentionEvidence.SampledLockCallCount.Should().Be(77);
        projected.NativeContentionEvidence.Summary.Should().Contain("off_cpu did not run (or failed)");
    }

    [Fact]
    public void ApplyNativeContentionEvidence_BothEntriesFailed_LeavesNativeContentionEvidenceNull()
    {
        var report = new CollectBatchReport(
            ProcessId: 123,
            DurationSeconds: 6,
            Results:
            [
                new CollectBatchEntryResult(
                    "collect_sample", "native-lock-contention", "failed: cancelled",
                    Data: null, Handle: null, HandleExpiresAt: null,
                    Error: new DiagnosticError("CollectorFailed", "cancelled", null)),
                new CollectBatchEntryResult(
                    "collect_sample", "off_cpu", "failed: cancelled",
                    Data: null, Handle: null, HandleExpiresAt: null,
                    Error: new DiagnosticError("CollectorFailed", "cancelled", null)),
            ]);

        var projected = CollectBatchSalientEvidence.ApplyNativeContentionEvidence(report, new MemoryDiagnosticHandleStore());

        projected.NativeContentionEvidence.Should().BeNull(
            "both entries failed (e.g. cancellation/timeout of the whole batch) — the per-entry Error already explains why");
    }

    [Fact]
    public void ApplyNativeContentionEvidence_NeitherKindRequested_IsNoOp()
    {
        var store = new MemoryDiagnosticHandleStore();
        var counters = LohCounterSnapshot();
        var countersHandle = store.Register(123, CollectionHandleKinds.Counters, counters, TimeSpan.FromMinutes(10));
        var report = new CollectBatchReport(
            ProcessId: 123,
            DurationSeconds: 6,
            Results:
            [
                new CollectBatchEntryResult(
                    "collect_events", "counters", "counter summary",
                    JsonSerializer.SerializeToElement(new CollectEventsEnvelope("counters")),
                    countersHandle.Id, countersHandle.ExpiresAt, Error: null),
            ]);

        var projected = CollectBatchSalientEvidence.ApplyNativeContentionEvidence(report, store);

        projected.NativeContentionEvidence.Should().BeNull();
    }

    private static NativeContentionEvidence ConfirmedBlockingEvidence()
        => new(
            NativeContentionEvidenceLevels.ConfirmedBlocking,
            "closed futex span(s) confirm the thread blocked in the kernel.",
            NativeSyncSpanCount: 3,
            ClosedNativeSyncSpanCount: 3,
            NativeSyncOffCpuMicros: 45_000,
            ClosedNativeSyncOffCpuMicros: 45_000);

    private static NativeContentionEvidence NoneEvidence()
        => new(NativeContentionEvidenceLevels.None, "No native synchronization off-CPU evidence observed in this window.");

    private static DiagnosticHandle RegisterNativeLockSample(MemoryDiagnosticHandleStore store, long sampledLockCalls)
    {
        var trace = new CpuSampleTraceArtifact(123, StartedAt, TimeSpan.FromSeconds(6), sampledLockCalls, new CallTreeNode(new SampledFrame("root", "root"), 0, 0, []));
        var sample = new NativeLockContentionSample(
            123,
            StartedAt,
            TimeSpan.FromSeconds(6),
            sampledLockCalls,
            TopContendedCallSites: [],
            ProbedFunctions: ["pthread_mutex_lock", "pthread_mutex_unlock"],
            LibcPath: "/lib/x86_64-linux-gnu/libc.so.6",
            SamplePeriod: 5000,
            SymbolSource: "PdbResolved",
            ContentionEvidence: new NativeContentionEvidence(
                NativeContentionEvidenceLevels.Activity,
                "sampled pthread mutex entry points are lock activity only.",
                SampledLockCallCount: sampledLockCalls));
        return store.Register(
            123,
            "native-lock-contention-sample",
            new NativeLockContentionArtifact(sample, trace),
            TimeSpan.FromMinutes(10));
    }

    private static DiagnosticHandle RegisterOffCpuSnapshot(MemoryDiagnosticHandleStore store, NativeContentionEvidence evidence)
    {
        var artifact = new OffCpuSnapshotArtifact(
            123,
            StartedAt,
            TimeSpan.FromSeconds(6),
            TotalOffCpuMicros: 45_000,
            SchedSwitches: 10,
            Stacks: [],
            Threads: [],
            SymbolSource: "PdbResolved",
            NativeContentionEvidence: evidence);
        return store.Register(123, "off-cpu-snapshot", artifact, TimeSpan.FromMinutes(10));
    }

    private static CollectBatchReport NativeContentionBatchReport(DiagnosticHandle lockHandle, DiagnosticHandle offCpuHandle)
        => new(
            ProcessId: 123,
            DurationSeconds: 6,
            Results:
            [
                new CollectBatchEntryResult(
                    "collect_sample", "native-lock-contention", "native-lock summary",
                    JsonSerializer.SerializeToElement(new object()), lockHandle.Id, lockHandle.ExpiresAt, Error: null),
                new CollectBatchEntryResult(
                    "collect_sample", "off_cpu", "off-cpu summary",
                    JsonSerializer.SerializeToElement(new object()), offCpuHandle.Id, offCpuHandle.ExpiresAt, Error: null),
            ]);

    private static CounterValue Counter(
        string name,
        double value,
        CounterKind kind,
        string? unit = null)
        => new("System.Runtime", name, name, value, kind, unit);
}
