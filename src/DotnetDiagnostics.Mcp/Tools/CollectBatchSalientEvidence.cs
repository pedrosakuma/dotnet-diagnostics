using System.Text.Json;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using ModelContextProtocol;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// Adds bounded cross-collector evidence to a completed batch without changing the full artifacts
/// retained behind each entry's handle.
/// </summary>
internal static class CollectBatchSalientEvidence
{
    internal const int MaxInlineCounters = 18;
    internal const int CounterIntervalSeconds = 1;

    /// <summary>
    /// Top-N cap for the allocation half of <see cref="CollectBatchInvestigationDigest"/> — matches
    /// <see cref="CpuSampleQueryDispatcher.CompactTopN"/>'s "first page" framing on the CPU side.
    /// </summary>
    internal const int CompactAllocationTopN = CpuSampleQueryDispatcher.CompactTopN;

    private static readonly HashSet<(string Provider, string Name)> LohGcCounters =
    [
        ("System.Runtime", "gen-2-size"),
        ("System.Runtime", "loh-size"),
        ("System.Runtime", "gc-fragmentation"),
    ];

    internal static CollectBatchReport Apply(
        CollectBatchReport report,
        IDiagnosticHandleStore handles)
    {
        var countersIndex = FindEntry(report.Results, CollectBatchTool.ToolCollectEvents, "counters");
        var gcIndex = FindEntry(report.Results, CollectBatchTool.ToolCollectEvents, "gc");
        if (countersIndex < 0 || gcIndex < 0)
        {
            return report;
        }

        var countersEntry = report.Results[countersIndex];
        var gcEntry = report.Results[gcIndex];
        if (countersEntry.Error is not null ||
            gcEntry.Error is not null ||
            countersEntry.Handle is null ||
            gcEntry.Handle is null)
        {
            return report;
        }

        var counters = handles.TryGet<CounterSnapshot>(countersEntry.Handle);
        var gc = handles.TryGet<GcSummary>(gcEntry.Handle);
        if (counters is null || gc is null)
        {
            return report;
        }

        var gen2WindowCount = gc.Generations
            .Where(static generation => generation.Generation == 2)
            .Sum(static generation => generation.Count);
        var gen2Counter = counters.Counters.FirstOrDefault(static counter =>
            counter.Provider == "System.Runtime" &&
            counter.Name == "gen-2-gc-count" &&
            counter.Kind == CounterKind.Sum);
        var gen2Meter = counters.Meters.FirstOrDefault(IsGen2CollectionMeter);

        var evidence = new CollectBatchGen2Evidence(
            EventCounterIntervalDelta: gen2Counter?.Value,
            EventCounterIntervalSeconds: CounterIntervalSeconds,
            MeterRatePerSecond: gen2Meter?.Rate,
            MeterProcessCumulative: gen2Meter?.LastValue,
            GcCollectorWindowCount: gen2WindowCount,
            GcCollectorWindowSeconds: report.DurationSeconds,
            Explanation:
                "EventCounterIntervalDelta is the last reporting-interval increment; " +
                "MeterRatePerSecond is a rate; MeterProcessCumulative is the process-lifetime " +
                "Meter value; GcCollectorWindowCount counts GC events observed only during this batch window. " +
                "These values are not interchangeable.");

        if (gen2WindowCount <= 0)
        {
            return report with { Gen2Evidence = evidence };
        }

        var headlineCounters = HeadlineCounters.FilterCounters(counters.Counters).ToHashSet();
        var selectedCounters = counters.Counters
            .Where(counter =>
                headlineCounters.Contains(counter) ||
                LohGcCounters.Contains((counter.Provider, counter.Name)))
            .Take(MaxInlineCounters)
            .ToList();
        var selectedMeters = HeadlineCounters.FilterMeters(counters.Meters);
        var addedLohGcCounters = selectedCounters.Count(counter =>
            LohGcCounters.Contains((counter.Provider, counter.Name)));
        var notes = counters.Notes
            .Concat(
            [
                $"BatchSalientSelection: paired GC collection observed {gen2WindowCount} Gen2 collection(s); " +
                $"showing at most {MaxInlineCounters} headline/LOH/GC counters inline while the handle retains all {counters.Counters.Count}.",
                "BatchGen2Scopes: see gen2Evidence; EventCounter interval delta, Meter rate/process-cumulative value, " +
                "and GC collector-window count are not interchangeable.",
            ])
            .ToList();
        var inlineSnapshot = counters with
        {
            Counters = selectedCounters,
            Meters = selectedMeters,
            Notes = notes,
            FirstCounters = null,
        };
        var envelope = new CollectEventsEnvelope("counters", Counters: inlineSnapshot);
        var enrichedEntry = countersEntry with
        {
            Summary =
                $"Captured {counters.Counters.Count} counter(s) and {counters.Meters.Count} meter series over " +
                $"{report.DurationSeconds}s — paired GC observed {gen2WindowCount} Gen2 collection(s); showing " +
                $"{selectedCounters.Count} bounded salient counter(s), including {addedLohGcCounters} LOH/GC-specific " +
                $"counter(s), while the handle retains all.",
            Data = JsonSerializer.SerializeToElement(envelope, McpJsonUtilities.DefaultOptions),
        };

        var results = report.Results.ToArray();
        results[countersIndex] = enrichedEntry;
        return report with { Results = results, Gen2Evidence = evidence };
    }

    /// <summary>
    /// Populates <see cref="CollectBatchReport.InvestigationDigest"/> (issue #825) when the batch
    /// includes <c>collect_sample(kind="cpu")</c> and/or <c>collect_sample(kind="allocation")</c>
    /// with a resolved handle. Each half is independent: a cpu-only batch yields CPU fields with
    /// allocation fields left <see langword="null"/>, and vice versa. Reuses
    /// <see cref="CpuSampleQueryDispatcher.RenderTriage"/> against the same call-tree artifact
    /// <c>query_snapshot(view="triage")</c> would resolve, so the ranking/wait-category/hot-path
    /// logic is not duplicated.
    /// </summary>
    internal static CollectBatchReport ApplyInvestigationDigest(
        CollectBatchReport report,
        IDiagnosticHandleStore handles)
    {
        IReadOnlyList<MethodSampleStat>? topCpuSelfTime = null;
        IReadOnlyList<CpuWaitCategoryStat>? topCpuWaitCategories = null;
        HotPathFrame? hotPathLeaf = null;
        int? hotPathDepth = null;
        IReadOnlyList<AllocatedType>? topAllocationTypes = null;
        IReadOnlyList<AllocationSite>? topAllocationCallsites = null;

        var cpuIndex = FindEntry(report.Results, CollectBatchTool.ToolCollectSample, "cpu");
        if (cpuIndex >= 0)
        {
            var cpuEntry = report.Results[cpuIndex];
            if (cpuEntry.Error is null && cpuEntry.Handle is not null)
            {
                var trace = handles.TryGet<CpuSampleTraceArtifact>(cpuEntry.Handle);
                if (trace is not null)
                {
                    var triage = CpuSampleQueryDispatcher.RenderTriage(
                        trace,
                        cpuEntry.Handle,
                        CpuSampleQueryDispatcher.CompactTopN,
                        CpuSampleQueryDispatcher.DefaultHotPathThresholdPercent);
                    if (triage.Data is not null)
                    {
                        topCpuSelfTime = triage.Data.TopBusyMethods;
                        topCpuWaitCategories = triage.Data.TopWaitCategories;
                        hotPathLeaf = triage.Data.HotPathLeaf;
                        hotPathDepth = triage.Data.HotPathDepth;
                    }
                }
            }
        }

        var allocationIndex = FindEntry(report.Results, CollectBatchTool.ToolCollectSample, "allocation");
        if (allocationIndex >= 0)
        {
            var allocationEntry = report.Results[allocationIndex];
            if (allocationEntry.Error is null && allocationEntry.Handle is not null)
            {
                var artifact = handles.TryGet<AllocationSampleArtifact>(allocationEntry.Handle);
                if (artifact is not null)
                {
                    topAllocationTypes = artifact.Summary.TopByBytes.Take(CompactAllocationTopN).ToList();
                    topAllocationCallsites = artifact.Summary.TopBySite.Take(CompactAllocationTopN).ToList();
                }
            }
        }

        if (topCpuSelfTime is null && topCpuWaitCategories is null && hotPathLeaf is null &&
            topAllocationTypes is null && topAllocationCallsites is null)
        {
            return report;
        }

        return report with
        {
            InvestigationDigest = new CollectBatchInvestigationDigest(
                topCpuSelfTime,
                topCpuWaitCategories,
                hotPathLeaf,
                hotPathDepth,
                topAllocationTypes,
                topAllocationCallsites),
        };
    }

    private static int FindEntry(
        IReadOnlyList<CollectBatchEntryResult> results,
        string tool,
        string kind)
    {
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Tool == tool && results[i].Kind == kind)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsGen2CollectionMeter(MeterInstrumentValue meter)
    {
        if (!string.Equals(meter.Instrument, "dotnet.gc.collections", StringComparison.Ordinal))
        {
            return false;
        }

        return meter.Tags.Any(static tag =>
            (tag.Key == "gc.heap.generation" || tag.Key == "generation") &&
            (string.Equals(tag.Value, "gen2", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(tag.Value, "2", StringComparison.Ordinal)));
    }
}
