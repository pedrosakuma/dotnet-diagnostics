using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Findings;

/// <summary>
/// Runs the registered CPU-sample <see cref="IFindingProvider{TContext}"/>s and returns a ranked,
/// capped set of <see cref="Finding"/>s. This is the single aggregation entry point used by both the
/// <c>collect_sample</c> tool (inline, from the <see cref="CpuSample"/> summary) and the findings
/// Resource (from a stored <see cref="CpuSampleTraceArtifact"/>), so both surface identical findings.
/// </summary>
public static class CpuSampleFindings
{
    private static readonly IReadOnlyList<IFindingProvider<CpuFindingContext>> Providers =
        new IFindingProvider<CpuFindingContext>[]
        {
            new RegexBacktrackingFindingProvider(),
            new CultureAwareStringFindingProvider(),
        };

    /// <summary>Detects findings from the compact CPU-sample summary (the inline collection path).</summary>
    public static IReadOnlyList<Finding> Detect(CpuSample sample, string handleId)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return Detect(new CpuFindingContext(sample.TotalSamples, sample.TopHotspots, handleId, sample.TopSelfTime));
    }

    /// <summary>
    /// Detects findings from a stored trace artifact (the Resource path). Hotspots are re-derived
    /// from the full merged call tree — the same ranking <c>query_snapshot(view="top-methods")</c>
    /// uses — so no data is lost to the inline top-N cap.
    /// </summary>
    public static IReadOnlyList<Finding> Detect(CpuSampleTraceArtifact artifact, string handleId)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var hotspots = CpuSampleAnalytics
            .RankMethods(artifact.Root, artifact.TotalSamples, byInclusive: true)
            .Select(m => new Hotspot(new SampledFrame(m.Module, m.Method), m.InclusiveSamples, m.ExclusiveSamples, m.Identity))
            .ToArray();

        // Global self-time leader (uncapped), mirroring what EventPipeCpuSampler sets on the summary.
        var selfRanked = CpuSampleAnalytics.RankMethods(artifact.Root, artifact.TotalSamples, byInclusive: false);
        var topSelfTime = selfRanked.Count > 0 && selfRanked[0].ExclusiveSamples > 0
            ? new Hotspot(new SampledFrame(selfRanked[0].Module, selfRanked[0].Method), selfRanked[0].InclusiveSamples, selfRanked[0].ExclusiveSamples, selfRanked[0].Identity)
            : null;

        return Detect(new CpuFindingContext(artifact.TotalSamples, hotspots, handleId, topSelfTime));
    }

    /// <summary>Runs every registered provider over the context and ranks the union.</summary>
    public static IReadOnlyList<Finding> Detect(CpuFindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return FindingRanker.Rank(Providers.SelectMany(p => p.Detect(context)));
    }
}
