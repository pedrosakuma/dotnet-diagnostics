using System.Globalization;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Session-scoped cross-collector "investigation digest" for the <c>session</c> REPL (issue #827).
/// Rather than adding a new multi-kind <c>collect</c> verb, the REPL already shares one resolved
/// process and one <see cref="IDiagnosticHandleStore"/> across back-to-back <c>collect --kind cpu</c>
/// and <c>collect --kind allocation</c> invocations; this reuses the same host-neutral
/// <see cref="InvestigationDigestBuilder"/> the MCP <c>collect_batch</c> tool calls (issue #825) to
/// print the same correlated summary once both artifacts exist for the session's target pid.
/// </summary>
internal static class CliInvestigationDigestFormatter
{
    /// <summary>Handle kind registered by <c>collect --kind cpu</c> (see <c>SamplerUseCases.CollectCpuSample</c>).</summary>
    internal const string CpuSampleKind = "cpu-sample";

    /// <summary>Handle kind registered by <c>collect --kind allocation</c> (see <c>SamplerUseCases.CollectAllocationSample</c>).</summary>
    internal const string AllocationSampleKind = "allocation-sample";

    /// <summary>
    /// Resolves the latest <c>cpu-sample</c> and <c>allocation-sample</c> handles for
    /// <paramref name="processId"/> and builds the digest when both are present. Returns
    /// <see langword="null"/> when either is missing (e.g. only one of the two kinds has been
    /// collected so far in this session) — the digest is only worth surfacing once it correlates
    /// both collectors.
    /// </summary>
    internal static InvestigationDigest? TryBuild(IDiagnosticHandleStore? store, int processId)
    {
        if (store is null)
        {
            return null;
        }

        var cpuHandle = store.TryGetLatestByKind(CpuSampleKind, processId);
        var allocationHandle = store.TryGetLatestByKind(AllocationSampleKind, processId);
        if (cpuHandle is null || allocationHandle is null)
        {
            return null;
        }

        var cpuTrace = store.TryGet<CpuSampleTraceArtifact>(cpuHandle.Id);
        var allocationSummary = store.TryGet<AllocationSampleArtifact>(allocationHandle.Id)?.Summary;
        return InvestigationDigestBuilder.Build(cpuTrace, allocationSummary);
    }

    /// <summary>Renders a compact, human-readable multi-line summary of <paramref name="digest"/>.</summary>
    internal static IReadOnlyList<string> Render(InvestigationDigest digest)
    {
        var lines = new List<string>
        {
            "  → investigation digest (cpu + allocation correlated):",
        };

        if (digest.TopCpuSelfTime is { Count: > 0 } topCpu)
        {
            lines.Add("    top cpu self-time: " + string.Join(", ", topCpu.Select(FormatMethodStat)));
        }

        if (digest.TopCpuWaitCategories is { Count: > 0 } topWait)
        {
            lines.Add("    top wait categories: " + string.Join(", ", topWait.Select(FormatWaitStat)));
        }

        if (digest.HotPathLeaf is { } leaf)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"    hot-path leaf: {leaf.Method} (depth {digest.HotPathDepth}, {leaf.InclusivePercent:N1}% inclusive)"));
        }

        if (digest.TopAllocationTypes is { Count: > 0 } topTypes)
        {
            lines.Add("    top allocation types (bytes): " + string.Join(", ", topTypes.Select(FormatAllocatedType)));
        }

        if (digest.TopAllocationCallsites is { Count: > 0 } topSites)
        {
            lines.Add("    top allocation call sites: " + string.Join(", ", topSites.Select(FormatAllocationSite)));
        }

        return lines;
    }

    private static string FormatMethodStat(MethodSampleStat stat)
        => string.Create(CultureInfo.InvariantCulture, $"{stat.Method} ({stat.ExclusivePercent:N1}%)");

    private static string FormatWaitStat(CpuWaitCategoryStat stat)
        => string.Create(CultureInfo.InvariantCulture, $"{stat.WaitReason} ({stat.ExclusivePercent:N1}%)");

    private static string FormatAllocatedType(AllocatedType type)
        => string.Create(CultureInfo.InvariantCulture, $"{type.TypeName} ({type.TotalBytes:N0} bytes)");

    private static string FormatAllocationSite(AllocationSite site)
        => string.Create(CultureInfo.InvariantCulture, $"{site.Frame.Method} ({site.TotalBytes:N0} bytes)");
}
