using System.Text.Json;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.NativeLockContention;
using DotnetDiagnostics.Core.OffCpu;
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
    /// allocation fields left <see langword="null"/>, and vice versa. Resolves the artifacts behind
    /// each handle and delegates the ranking/wait-category/hot-path computation to
    /// <see cref="InvestigationDigestBuilder"/> (issue #827) — the same host-neutral
    /// <c>DotnetDiagnostics.Core</c> logic the standalone CLI's <c>session</c> REPL and the
    /// BenchmarkDotNet diagnoser's exported report also call, so it is not triplicated.
    /// </summary>
    internal static CollectBatchReport ApplyInvestigationDigest(
        CollectBatchReport report,
        IDiagnosticHandleStore handles)
    {
        CpuSampleTraceArtifact? cpuTrace = null;
        var cpuIndex = FindEntry(report.Results, CollectBatchTool.ToolCollectSample, "cpu");
        if (cpuIndex >= 0)
        {
            var cpuEntry = report.Results[cpuIndex];
            if (cpuEntry.Error is null && cpuEntry.Handle is not null)
            {
                cpuTrace = handles.TryGet<CpuSampleTraceArtifact>(cpuEntry.Handle);
            }
        }

        AllocationSample? allocationSummary = null;
        var allocationIndex = FindEntry(report.Results, CollectBatchTool.ToolCollectSample, "allocation");
        if (allocationIndex >= 0)
        {
            var allocationEntry = report.Results[allocationIndex];
            if (allocationEntry.Error is null && allocationEntry.Handle is not null)
            {
                allocationSummary = handles.TryGet<AllocationSampleArtifact>(allocationEntry.Handle)?.Summary;
            }
        }

        // Reuses the host-neutral DotnetDiagnostics.Core.CpuSampling.InvestigationDigestBuilder
        // (issue #827) so this ranking/gating logic is not duplicated across the MCP server, the
        // standalone CLI's session REPL, and the BenchmarkDotNet diagnoser's exported report.
        var digest = InvestigationDigestBuilder.Build(cpuTrace, allocationSummary, CompactAllocationTopN);
        if (digest is null)
        {
            return report;
        }

        return report with
        {
            InvestigationDigest = new CollectBatchInvestigationDigest(
                digest.TopCpuSelfTime,
                digest.TopCpuWaitCategories,
                digest.HotPathLeaf,
                digest.HotPathDepth,
                digest.TopAllocationTypes,
                digest.TopAllocationCallsites),
        };
    }

    /// <summary>
    /// Populates <see cref="CollectBatchReport.NativeContentionEvidence"/> (issue #855) when the
    /// batch includes <c>collect_sample(kind="native-lock-contention")</c> and/or
    /// <c>collect_sample(kind="off_cpu")</c>. Resolves each entry's evidence independently via its
    /// own handle-store artifact (unaffected by <c>depth="compact"</c> elision, mirroring
    /// <see cref="Apply"/> and <see cref="ApplyInvestigationDigest"/>'s existing precedent), then
    /// delegates the merge to <see cref="NativeLockContentionUx.CorrelateBatchEvidence"/> — the
    /// same host-neutral <c>DotnetDiagnostics.Core</c> logic, so the taxonomy invariant (native-lock
    /// activity never elevates the off-CPU-derived level) lives in exactly one place. A no-op when
    /// neither entry was requested. Present-but-degraded when only one entry succeeded: the merged
    /// evidence still reflects that entry's own level, with a trailing note naming the missing/failed
    /// collector — this is the batch's explicit partial-success signal for native contention
    /// correlation, on top of the per-entry <see cref="CollectBatchEntryResult.Error"/> that already
    /// reports the failure itself.
    /// </summary>
    internal static CollectBatchReport ApplyNativeContentionEvidence(
        CollectBatchReport report,
        IDiagnosticHandleStore handles)
    {
        var lockIndex = FindEntry(report.Results, CollectBatchTool.ToolCollectSample, "native-lock-contention");
        var offCpuIndex = FindEntry(report.Results, CollectBatchTool.ToolCollectSample, "off_cpu");
        if (lockIndex < 0 && offCpuIndex < 0)
        {
            return report;
        }

        NativeContentionEvidence? lockEvidence = null;
        if (lockIndex >= 0)
        {
            var lockEntry = report.Results[lockIndex];
            if (lockEntry.Error is null && lockEntry.Handle is not null)
            {
                lockEvidence = handles.TryGet<NativeLockContentionArtifact>(lockEntry.Handle)?.Summary.ContentionEvidence;
            }
        }

        NativeContentionEvidence? offCpuEvidence = null;
        if (offCpuIndex >= 0)
        {
            var offCpuEntry = report.Results[offCpuIndex];
            if (offCpuEntry.Error is null && offCpuEntry.Handle is not null)
            {
                offCpuEvidence = handles.TryGet<OffCpuSnapshotArtifact>(offCpuEntry.Handle)?.NativeContentionEvidence;
            }
        }

        if (lockEvidence is null && offCpuEvidence is null)
        {
            // Both requested entries failed/expired, or the requested entry's evidence could not be
            // resolved (e.g. handle already evicted) — leave NativeContentionEvidence unset rather
            // than fabricate a "none" level; the per-entry Error already explains why.
            return report;
        }

        var evidence = NativeLockContentionUx.CorrelateBatchEvidence(lockEvidence, offCpuEvidence);
        return report with { NativeContentionEvidence = evidence };
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
