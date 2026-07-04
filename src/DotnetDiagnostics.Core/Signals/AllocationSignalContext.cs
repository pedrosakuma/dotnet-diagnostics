using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Already-collected allocation-sample data a <see cref="ISignalProvider{TContext}"/> groups over.
/// Both ranked lists are byte-weighted (the allocation analogue of CPU self-time): <see cref="ByType"/>
/// answers <i>what</i> was allocated, <see cref="BySite"/> answers <i>where</i> (which call site).
/// Both are already ranked descending by <see cref="AllocatedType.TotalBytes"/> /
/// <see cref="AllocationSite.TotalBytes"/> by the sampler, so providers only need to read the head.
/// </summary>
/// <param name="TotalBytes">Total bytes attributed across the sampling window.</param>
/// <param name="ByType">Top allocated types ranked by total bytes.</param>
/// <param name="BySite">Top allocation call sites (leaf frame) ranked by total bytes. May be empty when no allocation stacks were captured.</param>
/// <param name="HandleId">Drill-down handle the sample was registered under, referenced by every bucket.</param>
public sealed record AllocationSignalContext(
    long TotalBytes,
    IReadOnlyList<AllocatedType> ByType,
    IReadOnlyList<AllocationSite> BySite,
    string HandleId);
