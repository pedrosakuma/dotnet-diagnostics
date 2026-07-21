using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Runs the registered CPU-sample <see cref="ISignalProvider{TContext}"/>s and returns a ranked,
/// capped set of <see cref="SignalGroup"/>s — the salient "vector" the engine forwards instead of the
/// full raw payload. Single aggregation entry point used by both the <c>collect_sample</c> tool
/// (inline, from the <see cref="CpuSample"/> summary) and the signals Resource (from a stored
/// <see cref="CpuSampleTraceArtifact"/>). The Resource path re-derives the full self-time ranking, so
/// its groupings are richer than the inline path's (which is limited to the uncapped self-time leader).
/// </summary>
public static class CpuSampleSignals
{
    private static readonly IReadOnlyList<ISignalProvider<CpuSignalContext>> Providers =
        new ISignalProvider<CpuSignalContext>[]
        {
            new CpuSelfTimeConcentrationProvider(),
            new CpuSelfTimeByNamespaceProvider(),
        };

    /// <summary>Derives signals from the compact CPU-sample summary (the inline collection path).</summary>
    public static IReadOnlyList<SignalGroup> Detect(CpuSample sample, string handleId)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return Detect(new CpuSignalContext(sample.TotalSamples, sample.TopHotspots, handleId, sample.TopSelfTime, null, sample.SelfSamples, sample.TopRunningSelfTime));
    }

    /// <summary>
    /// Derives signals from a stored trace artifact (the Resource path). The full per-method self-time
    /// ranking is re-derived from the merged call tree — the same ranking
    /// <c>query_snapshot(view="top-methods", rankBy="exclusive")</c> uses — so nothing is lost to the
    /// inline top-N cap and the namespace roll-up is faithful.
    /// </summary>
    public static IReadOnlyList<SignalGroup> Detect(CpuSampleTraceArtifact artifact, string handleId)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var selfRanked = CpuSampleAnalytics.RankMethods(artifact.Root, artifact.TotalSamples, byInclusive: false);
        var hotspots = CpuSampleAnalytics
            .RankMethods(artifact.Root, artifact.TotalSamples, byInclusive: true)
            .Select(m => new Hotspot(new SampledFrame(m.Module, m.Method), m.InclusiveSamples, m.ExclusiveSamples, m.Identity)
            {
                SelfSamples = m.SelfSamples,
            })
            .ToArray();

        var topSelfTime = CpuSampleAnalytics.TopSelfTime(artifact.Root, artifact.TotalSamples);

        return Detect(new CpuSignalContext(
            artifact.TotalSamples,
            hotspots,
            handleId,
            topSelfTime,
            selfRanked,
            artifact.SelfSamples,
            CpuSampleAnalytics.TopRunningSelfTime(artifact.Root, artifact.TotalSamples)));
    }

    /// <summary>Runs every registered provider over the context and ranks the union by salience.</summary>
    public static IReadOnlyList<SignalGroup> Detect(CpuSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SignalRanker.Rank(Providers.SelectMany(p => p.Detect(context)));
    }
}
