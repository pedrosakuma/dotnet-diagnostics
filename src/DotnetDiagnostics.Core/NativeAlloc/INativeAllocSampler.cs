namespace DotnetDiagnostics.Core.NativeAlloc;

/// <summary>
/// Attributes <b>native (unmanaged) allocations</b> to a call site by uprobing the C library
/// allocator (<c>malloc</c> / <c>calloc</c> / <c>realloc</c>) with the kernel <c>perf</c>
/// profiler and DWARF stack unwinding. The companion to <c>EventPipeAllocationSampler</c>
/// (managed <c>GCAllocationTick</c>) — the managed sampler only sees the GC heap, this one sees
/// allocations the runtime makes outside it (P/Invoke, native libraries, the runtime itself).
/// </summary>
/// <remarks>
/// <para>Hotspot-only first cut (issue #279): the result answers "which call stacks call the
/// allocator most often" — it is NOT alloc/free retention matching, so it cannot by itself
/// prove a leak. Counts are <b>sampled allocation call hits</b>, not bytes.</para>
/// <para>Linux only. Requires the <c>perf</c> binary plus permission to create a dynamic uprobe
/// (typically <c>CAP_SYS_ADMIN</c> / tracefs write access — strictly more than the
/// <c>CAP_PERFMON</c> that off-CPU sampling needs). Failures surface as a structured
/// <c>PermissionDenied</c> envelope with the perf stderr passed through.</para>
/// </remarks>
public interface INativeAllocSampler
{
    /// <summary>
    /// True when the implementation can run on the current host. Cheap probe — checks the OS and
    /// resolves a working <c>perf</c> binary. Does NOT verify uprobe-creation privilege; that
    /// surfaces as a <c>PermissionDenied</c> at the first <c>perf probe</c> attempt.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Uprobes the target's libc allocator for <paramref name="duration"/> and returns the merged
    /// native allocation call tree plus a compact summary.
    /// </summary>
    /// <param name="processId">Target pid.</param>
    /// <param name="duration">Sampling window. Must be (0, 5 minutes].</param>
    /// <param name="topN">Max allocator hotspots returned in the summary; the full call tree is retained in the artifact.</param>
    /// <param name="samplePeriod">
    /// perf sample period — record one callchain per <paramref name="samplePeriod"/> allocator
    /// hits. Higher values reduce DWARF-unwind overhead and perf.data size at the cost of
    /// resolution. Must be &gt;= 1. Note: this throttles the <i>recorded</i> samples, it does NOT
    /// remove the per-call uprobe trap cost on allocator-hot workloads.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<NativeAllocSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        long samplePeriod = 1000,
        CancellationToken cancellationToken = default);
}
