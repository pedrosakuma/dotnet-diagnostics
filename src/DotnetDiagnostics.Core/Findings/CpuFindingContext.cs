using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Findings;

/// <summary>
/// Normalized input for CPU-sample <see cref="IFindingProvider{TContext}"/>s. Built from either the
/// compact <see cref="CpuSample"/> summary (inline, at collection time) or a stored
/// <see cref="CpuSampleTraceArtifact"/> (when the findings Resource is read for a handle), so the
/// same detectors run identically on both paths.
/// </summary>
/// <param name="TotalSamples">Total samples captured in the window.</param>
/// <param name="Hotspots">Per-method aggregated hotspots (inclusive + exclusive attribution).</param>
/// <param name="HandleId">Drill-down handle the samples were registered under, referenced by evidence.</param>
/// <param name="TopSelfTime">
/// The global self-time (exclusive) leader across the whole tree (see #512/#513), if any. Carried
/// separately from <paramref name="Hotspots"/> because the inline hotspot list is capped by inclusive
/// rank, so a deep hot leaf can fall outside it — detectors that key off self-time should consult this.
/// </param>
public sealed record CpuFindingContext(
    long TotalSamples,
    IReadOnlyList<Hotspot> Hotspots,
    string HandleId,
    Hotspot? TopSelfTime = null);
