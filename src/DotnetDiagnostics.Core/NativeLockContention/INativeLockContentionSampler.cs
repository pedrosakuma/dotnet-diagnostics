namespace DotnetDiagnostics.Core.NativeLockContention;

/// <summary>
/// Attributes <b>native/OS-level lock contention</b> to a call site — <c>pthread_mutex_lock</c> /
/// <c>pthread_mutex_unlock</c> calls into the C library — with a kernel <c>perf</c> uprobe and
/// DWARF stack unwinding. The companion to <c>collect_events(kind="contention")</c> (CLR
/// <c>Contention</c> EventPipe events for <c>Monitor.Enter</c> / <c>lock</c>): the managed
/// collector only sees contention the CLR itself instruments, this one sees blocking the runtime
/// never observes — a native library's own internal locking reached via P/Invoke, or a construct
/// the CLR doesn't wrap in a managed monitor.
/// </summary>
/// <remarks>
/// <para>Issue #830, narrow first cut: only <c>pthread_mutex_lock</c> / <c>pthread_mutex_unlock</c>
/// are probed. Condition variables, semaphores, and reader-writer locks are explicitly out of
/// scope — expand only if there is demonstrated future need.</para>
/// <para>Hotspot-only, call-frequency based: counts are <b>sampled call-site hits</b> on the libc
/// mutex entry points, not measured wait time. A plain uprobe on <c>pthread_mutex_lock</c> cannot
/// cheaply distinguish an uncontended fast-path acquisition (a single CAS, no syscall) from one
/// that actually blocked in the kernel futex-wait path — see <see cref="NativeLockContentionSample"/>
/// for the caveat surfaced to callers. A future enhancement could instead uprobe the
/// <c>SYS_futex</c> raw syscall tracepoint (only entered on genuine contention) for a more precise
/// wait-only signal; this first cut mirrors the native-allocation sampler's libc-uprobe mechanism
/// exactly, per the issue's own precedent.</para>
/// <para>Linux only in this release. Requires the <c>perf</c> binary plus permission to create a
/// dynamic uprobe (typically <c>CAP_SYS_ADMIN</c> / tracefs write access). See
/// <see cref="INativeLockContentionSampler"/>'s Windows counterpart for why there is no ETW
/// backend yet.</para>
/// </remarks>
public interface INativeLockContentionSampler
{
    /// <summary>
    /// True when the implementation can run on the current host. Cheap probe — checks the OS and
    /// resolves a working <c>perf</c> binary. Does NOT verify uprobe-creation privilege; that
    /// surfaces as a <c>PermissionDenied</c> at the first <c>perf probe</c> attempt.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Uprobes the target's libc <c>pthread_mutex_lock</c> / <c>pthread_mutex_unlock</c> for
    /// <paramref name="duration"/> and returns the merged native lock-contention call tree plus a
    /// compact summary.
    /// </summary>
    /// <param name="processId">Target pid.</param>
    /// <param name="duration">Sampling window. Must be (0, 5 minutes].</param>
    /// <param name="topN">Max contended call sites returned in the summary; the full call tree is retained in the artifact.</param>
    /// <param name="samplePeriod">
    /// perf sample period — record one callchain per <paramref name="samplePeriod"/> mutex-call
    /// hits. Higher values reduce DWARF-unwind overhead and perf.data size at the cost of
    /// resolution. Must be &gt;= 1. Mutex calls are typically far more frequent than allocator
    /// calls on lock-heavy workloads, so the default is higher than the native-allocation
    /// sampler's default — see the implementation for the exact value and reasoning.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<NativeLockContentionSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        long samplePeriod = 5000,
        CancellationToken cancellationToken = default);
}
