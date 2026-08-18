using DotnetDiagnostics.Core.NativeLockContention;

namespace DotnetDiagnostics.Core.OffCpu;

/// <summary>
/// Compact summary of an off-CPU sampling window, safe to return inline to the MCP client.
/// The heavy per-stack and per-thread data lives in <see cref="OffCpuSnapshotArtifact"/>,
/// retrieved via the issued handle.
/// </summary>
/// <param name="ProcessId">Target pid.</param>
/// <param name="StartedAt">Wall-clock start of the sampling window.</param>
/// <param name="Duration">Configured (not measured) window length.</param>
/// <param name="TotalOffCpuMicros">Sum of off-CPU time across every thread of the target.</param>
/// <param name="DistinctThreads">Number of distinct kernel TIDs that went off-CPU at least once.</param>
/// <param name="TopBlockingStacks">Up to topN stacks ranked by inclusive off-CPU microseconds.</param>
/// <param name="SchedSwitches">Total <c>sched_switch</c> events attributed to the target (sanity check the LLM can use to confirm capture density).</param>
/// <param name="SymbolSource">Resolution quality across all frames (mirrors the on-CPU sampler's flag so the LLM can reason about kernel-vs-user symbol coverage).</param>
/// <param name="CensoredSpans">Number of off-CPU spans whose IN event was never seen before capture ended; <see cref="TotalOffCpuMicros"/> includes their lower-bound contribution.</param>
/// <param name="CensoredOffCpuMicros">Subset of <see cref="TotalOffCpuMicros"/> attributable to censored spans (truncated at capture end).</param>
/// <param name="Notes">Best-effort warnings (size caps hit, late-attribution, partial TID set, etc.) so the LLM can disclose data-quality caveats.</param>
/// <param name="NativeContentionEvidence">Aggregated native synchronization evidence derived from syscall-correlated off-CPU spans.</param>
public sealed record OffCpuSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalOffCpuMicros,
    int DistinctThreads,
    IReadOnlyList<OffCpuStackHotspot> TopBlockingStacks,
    long SchedSwitches,
    string SymbolSource,
    long CensoredSpans = 0,
    long CensoredOffCpuMicros = 0,
    IReadOnlyList<string>? Notes = null,
    NativeContentionEvidence? NativeContentionEvidence = null);

/// <summary>A blocking stack ranked by the total micros spent off-CPU below it.</summary>
/// <param name="LeafFrame">Innermost frame in the blocking stack (module!method, or just method for kernel frames).</param>
/// <param name="OffCpuMicros">Total off-CPU microseconds attributed to this stack group.</param>
/// <param name="OccurrenceCount">Number of distinct off-CPU spans folded into this stack group.</param>
/// <param name="DominantState">Most common <c>PrevState</c> (Linux <c>S/D/R/...</c> character, or Windows <c>KWAIT_REASON</c> name) across the group's spans.</param>
/// <param name="Stack">Full root→leaf stack for this group.</param>
/// <param name="SyscallBreakdown">
/// Syscall/wait-reason attribution for this stack group, ranked by total off-CPU micros
/// attributed to each label (issue #829). Aggregated per stack group rather than per individual
/// span — cheaper to compute/return and the issue's own design note calls a per-stack rollup
/// ("this hot off-CPU stack is 80% futex, 20% read") "probably sufficient" for root-causing.
/// <c>null</c> when no span in this stack group could be correlated to a syscall/wait-reason
/// (e.g. Linux: no matching perf.data <c>raw_syscalls</c> event; Windows: correlation is
/// best-effort, see <c>EtwOffCpuSampler</c>).
/// </param>
/// <param name="NativeContentionEvidence">Per-stack native synchronization evidence; null only for older/manual artifacts that did not classify it.</param>
public sealed record OffCpuStackHotspot(
    string LeafFrame,
    long OffCpuMicros,
    long OccurrenceCount,
    string DominantState,
    IReadOnlyList<OffCpuFrame> Stack,
    IReadOnlyList<OffCpuSyscallAttribution>? SyscallBreakdown = null,
    NativeContentionEvidence? NativeContentionEvidence = null);

/// <summary>
/// One syscall/wait-reason label's share of a stack group's off-CPU time (issue #829).
/// <see cref="Name"/> is a real syscall name on Linux (e.g. <c>futex</c>, <c>epoll_wait</c>,
/// <c>read</c>) resolved from the raw syscall number via <see cref="SyscallTable"/>. On Windows,
/// <see cref="Name"/> is either a specific <c>FileIO:*</c> / <c>TcpIp:*</c> label when a
/// correlated kernel File/Network ETW event was found near the block point, or a normalized
/// wait-reason bucket (<c>Network</c>, <c>Disk</c>, <c>Sync</c>, <c>Sleep</c>, <c>Other</c>)
/// derived from the coarser <c>KWAIT_REASON</c> enum when no such event correlates — see
/// <c>EtwOffCpuSampler</c>'s remarks for the full platform-asymmetry rationale.
/// </summary>
public sealed record OffCpuSyscallAttribution(string Name, long Count, long Micros);

/// <summary>A single resolved stack frame (kernel or user, demangled when possible).
/// <para><see cref="Identity"/> is populated for managed frames where the backend could
/// reconstruct the canonical <c>(ModuleVersionId, MetadataToken)</c> handoff key
/// (Slice 2c managed↔kernel stack merge) — null for native/kernel frames and for managed
/// frames whose module path or MVID could not be resolved on the diagnostics box.</para></summary>
public sealed record OffCpuFrame(
    string Module,
    string Method,
    DotnetDiagnostics.Core.Memory.MethodIdentity? Identity = null);

/// <summary>
/// Full off-CPU data set retained behind a handle for drill-down queries. Keeps the per-thread
/// view (which the summary intentionally omits) and the raw stack-keyed aggregation so the LLM
/// can ask "which thread blocked the longest?" or "what does this specific stack look like?"
/// without re-running <c>perf record</c>.
/// </summary>
public sealed record OffCpuSnapshotArtifact(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalOffCpuMicros,
    long SchedSwitches,
    IReadOnlyList<OffCpuStackHotspot> Stacks,
    IReadOnlyList<OffCpuThreadView> Threads,
    string SymbolSource,
    long CensoredSpans = 0,
    long CensoredOffCpuMicros = 0,
    IReadOnlyList<string>? Notes = null,
    NativeContentionEvidence? NativeContentionEvidence = null);

/// <summary>Per-thread off-CPU rollup ranked by total micros blocked.</summary>
public sealed record OffCpuThreadView(
    int Tid,
    string ThreadName,
    long OffCpuMicros,
    long SwitchCount,
    string TopBlockingLeaf);

/// <summary>Pair returned by <see cref="IOffCpuSampler"/>: lightweight summary plus the artifact for the handle store.</summary>
public sealed record OffCpuSampleResult(OffCpuSnapshot Summary, OffCpuSnapshotArtifact Artifact);

/// <summary>
/// Discriminated off-CPU view returned by <c>query_snapshot</c>. Exactly one of
/// <see cref="Stacks"/>, <see cref="Threads"/>, <see cref="Stack"/> is non-null depending on the
/// requested <see cref="View"/> ("topStacks" | "byThread" | "stack").
/// </summary>
public sealed record OffCpuQueryView(
    string View,
    int ProcessId,
    long TotalOffCpuMicros,
    IReadOnlyList<OffCpuStackHotspot>? Stacks,
    IReadOnlyList<OffCpuThreadView>? Threads,
    OffCpuStackHotspot? Stack,
    NativeContentionEvidence? NativeContentionEvidence = null);
