using System.Text.Json;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
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

    private static CounterValue Counter(
        string name,
        double value,
        CounterKind kind,
        string? unit = null)
        => new("System.Runtime", name, name, value, kind, unit);
}
