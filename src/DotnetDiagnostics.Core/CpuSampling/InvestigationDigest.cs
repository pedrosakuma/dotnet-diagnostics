namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// A "first page" cross-collector summary bundling the top CPU self-time hotspots, top CPU
/// wait/noise categories, dominant hot-path leaf, and top allocation types/call sites — the same
/// evidence an operator would otherwise gather from two or more separate drill-down round trips
/// against a <c>cpu-sample</c> and/or <c>allocation-sample</c> artifact, bundled into one. Each
/// half is populated independently by <see cref="InvestigationDigestBuilder.Build"/> — supplying
/// only a CPU trace yields CPU fields with allocation fields left <see langword="null"/>, and vice
/// versa.
/// </summary>
/// <remarks>
/// Originated in issue #825 as <c>CollectBatchInvestigationDigest</c> (MCP <c>collect_batch</c>
/// tool only). Factored into <c>DotnetDiagnostics.Core</c> by issue #827 so the standalone CLI
/// (<c>session</c> REPL) and the BenchmarkDotNet diagnoser's exported report can reuse the same
/// ranking/gating logic instead of re-implementing it.
/// </remarks>
public sealed record InvestigationDigest(
    IReadOnlyList<MethodSampleStat>? TopCpuSelfTime,
    IReadOnlyList<CpuWaitCategoryStat>? TopCpuWaitCategories,
    HotPathFrame? HotPathLeaf,
    int? HotPathDepth,
    IReadOnlyList<AllocatedType>? TopAllocationTypes,
    IReadOnlyList<AllocationSite>? TopAllocationCallsites);

/// <summary>
/// Builds <see cref="InvestigationDigest"/> from an already-collected CPU trace and/or allocation
/// summary — no handle-store lookup, no MCP/CLI/BenchmarkDotNet awareness. Each caller is
/// responsible for resolving its own artifacts (an MCP handle, a CLI session's latest handle of a
/// kind, or a BenchmarkDotNet in-process capture) before calling <see cref="Build"/>.
/// </summary>
public static class InvestigationDigestBuilder
{
    /// <summary>
    /// Renders the digest. <paramref name="cpuTrace"/> and <paramref name="allocationSummary"/> are
    /// each optional and independent: a CPU-only call populates only the CPU fields, an
    /// allocation-only call populates only the allocation fields, and supplying neither returns
    /// <see langword="null"/> rather than an all-null placeholder record.
    /// </summary>
    /// <param name="cpuTrace">The merged call-tree artifact behind a <c>cpu-sample</c> handle, or
    /// <see langword="null"/> when no CPU sample is available.</param>
    /// <param name="allocationSummary">The compact allocation summary behind an
    /// <c>allocation-sample</c> handle, or <see langword="null"/> when no allocation sample is
    /// available.</param>
    /// <param name="topN">Row cap applied to every ranked list — defaults to
    /// <see cref="CpuSampleQueryDispatcher.CompactTopN"/>, the same "first page" cap
    /// <c>collect_batch</c> uses.</param>
    /// <param name="hotPathThresholdPercent">Threshold passed through to
    /// <see cref="CpuSampleQueryDispatcher.RenderTriage"/> — defaults to
    /// <see cref="CpuSampleQueryDispatcher.DefaultHotPathThresholdPercent"/>.</param>
    public static InvestigationDigest? Build(
        CpuSampleTraceArtifact? cpuTrace,
        AllocationSample? allocationSummary,
        int topN = CpuSampleQueryDispatcher.CompactTopN,
        double hotPathThresholdPercent = CpuSampleQueryDispatcher.DefaultHotPathThresholdPercent)
    {
        IReadOnlyList<MethodSampleStat>? topCpuSelfTime = null;
        IReadOnlyList<CpuWaitCategoryStat>? topCpuWaitCategories = null;
        HotPathFrame? hotPathLeaf = null;
        int? hotPathDepth = null;

        if (cpuTrace is not null)
        {
            var triage = CpuSampleQueryDispatcher.RenderTriage(
                cpuTrace,
                handle: string.Empty,
                topN,
                hotPathThresholdPercent);
            if (triage.Data is not null)
            {
                topCpuSelfTime = triage.Data.TopBusyMethods;
                topCpuWaitCategories = triage.Data.TopWaitCategories;
                hotPathLeaf = triage.Data.HotPathLeaf;
                hotPathDepth = triage.Data.HotPathDepth;
            }
        }

        IReadOnlyList<AllocatedType>? topAllocationTypes = null;
        IReadOnlyList<AllocationSite>? topAllocationCallsites = null;
        if (allocationSummary is not null)
        {
            topAllocationTypes = allocationSummary.TopByBytes.Take(topN).ToList();
            topAllocationCallsites = allocationSummary.TopBySite.Take(topN).ToList();
        }

        if (topCpuSelfTime is null && topCpuWaitCategories is null && hotPathLeaf is null &&
            topAllocationTypes is null && topAllocationCallsites is null)
        {
            return null;
        }

        return new InvestigationDigest(
            topCpuSelfTime,
            topCpuWaitCategories,
            hotPathLeaf,
            hotPathDepth,
            topAllocationTypes,
            topAllocationCallsites);
    }
}
