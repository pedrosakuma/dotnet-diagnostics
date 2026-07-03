namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>A single resolved frame within a CPU sample stack.</summary>
public sealed record SampledFrame(string Module, string Method);

/// <summary>A hotspot is a frame ranked by how often it appeared in CPU samples.</summary>
public sealed record Hotspot(
    SampledFrame Frame,
    long InclusiveSamples,
    long ExclusiveSamples,
    DotnetDiagnostics.Core.Memory.MethodIdentity? Identity = null);

/// <summary>Aggregated CPU sample over a window.</summary>
public sealed record CpuSample(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalSamples,
    IReadOnlyList<Hotspot> TopHotspots)
{
    /// <summary>
    /// Aggregate symbol-resolution quality of <see cref="TopHotspots"/>. Always populated for
    /// NativeAOT samples by <c>PerfNativeAotCpuSampler</c>; <c>null</c> for CoreCLR samples
    /// since the EventPipe path resolves managed methods via TraceEvent and the concept does
    /// not apply uniformly. See #29 / #35 — surfacing this avoids forcing the consumer to
    /// drill into the trace artifact just to know whether demangling succeeded.
    /// </summary>
    public NativeAotSymbolDemangler.SymbolSource? SymbolSource { get; init; }

    /// <summary>
    /// The single hottest method by <b>self-time</b> (exclusive samples) across the whole merged call
    /// tree — computed before <see cref="TopHotspots"/> is capped by inclusive rank, so it is the true
    /// global self-time leader even when it falls outside the inclusive top-N. This is what a CPU
    /// investigation should lead with: in most server workloads the inclusive top is the invariant
    /// ThreadPool/dispatch roots, while self-time points at where cycles are actually burned.
    /// <c>null</c> when no dominant on-CPU leaf exists (wait-bound / unresolved capture) or the
    /// sampler does not compute it (the consumer then falls back to the inclusive-ranked hotspots).
    /// </summary>
    public Hotspot? TopSelfTime { get; init; }
}
