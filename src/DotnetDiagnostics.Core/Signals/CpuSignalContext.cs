using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Already-collected CPU-sample data a <see cref="ISignalProvider{TContext}"/> groups over. Two
/// fidelities are carried because the inline collection path is lossy: the inline
/// <see cref="Hotspots"/> list is capped by inclusive rank, so self-time-based groupings must consult
/// <see cref="TopSelfTime"/> (the uncapped global self-time leader) and, on the Resource path,
/// <see cref="SelfTimeRanked"/> (the full per-method self-time ranking re-derived from the stored tree).
/// </summary>
/// <param name="TotalSamples">Total samples captured in the window.</param>
/// <param name="Hotspots">Per-method aggregated hotspots (inclusive-ranked, inline-capped).</param>
/// <param name="HandleId">Drill-down handle the samples were registered under, referenced by every bucket.</param>
/// <param name="TopSelfTime">The global self-time (exclusive) leader across the whole tree (see #512/#513), if any.</param>
/// <param name="SelfTimeRanked">
/// The full per-method ranking ordered by exclusive (self-time) samples, available only on the
/// Resource path (re-derived from the stored call tree). <c>null</c> on the inline path, where only
/// <see cref="TopSelfTime"/> and the inclusive-capped <see cref="Hotspots"/> are available.
/// </param>
public sealed record CpuSignalContext(
    long TotalSamples,
    IReadOnlyList<Hotspot> Hotspots,
    string HandleId,
    Hotspot? TopSelfTime = null,
    IReadOnlyList<MethodSampleStat>? SelfTimeRanked = null);
