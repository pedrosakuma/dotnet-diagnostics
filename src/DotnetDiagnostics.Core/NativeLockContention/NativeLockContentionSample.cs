using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.NativeLockContention;

/// <summary>
/// Compact summary of a native-lock-contention sampling window, safe to return inline to the MCP
/// client. The full caller→callee tree lives in the companion <see cref="CpuSampleTraceArtifact"/>
/// (retrieved via the issued handle and walked with <c>query_snapshot(view="call-tree")</c>).
/// </summary>
/// <param name="ProcessId">Target pid.</param>
/// <param name="StartedAt">Wall-clock start of the sampling window.</param>
/// <param name="Duration">Configured (not measured) window length.</param>
/// <param name="TotalSampledLockCalls">
/// Number of recorded mutex-call samples. With <c>samplePeriod &gt; 1</c> this is a sampled subset
/// of the real call count, not the total — and it counts <b>calls into pthread_mutex_lock /
/// pthread_mutex_unlock, not confirmed blocking waits</b>. An uncontended fast-path acquisition
/// (resolved with a single CAS, no kernel futex syscall) is indistinguishable from a genuinely
/// blocked one at this uprobe; treat a high count as "this call site touches the mutex a lot", and
/// corroborate with <c>collect_sample(kind="off_cpu")</c> to confirm the thread actually blocked.
/// </param>
/// <param name="TopContendedCallSites">Up to topN call-stack frames ranked by inclusive sampled mutex-call hits.</param>
/// <param name="ProbedFunctions">The libc mutex symbols actually uprobed (pthread_mutex_lock, pthread_mutex_unlock).</param>
/// <param name="LibcPath">The resolved libc shared object the uprobes were attached to (target-namespace path).</param>
/// <param name="SamplePeriod">perf sample period used: one recorded callchain per this many mutex-call hits.</param>
/// <param name="SymbolSource">Aggregate symbol-resolution quality across all frames.</param>
/// <param name="Notes">Best-effort caveats (overhead, partial probes, no samples, wait-vs-call-count, etc.) the LLM can disclose.</param>
/// <param name="ContentionEvidence">Honest evidence classification for this sample. Native-lock uprobes report lock activity only.</param>
public sealed record NativeLockContentionSample(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalSampledLockCalls,
    IReadOnlyList<Hotspot> TopContendedCallSites,
    IReadOnlyList<string> ProbedFunctions,
    string LibcPath,
    long SamplePeriod,
    string SymbolSource,
    IReadOnlyList<string>? Notes = null,
    NativeContentionEvidence? ContentionEvidence = null);

/// <summary>
/// Pair returned by <see cref="INativeLockContentionSampler"/>: the lightweight summary plus the
/// trace artifact handed to the handle store. The artifact is a <see cref="CpuSampleTraceArtifact"/>
/// so the existing <c>query_snapshot(view="call-tree")</c> drilldown resolves it without a
/// dedicated artifact type (mirrors the native-allocation sampler's issue #279 §2 precedent: reuse
/// the shared call-tree pipeline).
/// </summary>
public sealed record NativeLockContentionSampleResult(NativeLockContentionSample Summary, CpuSampleTraceArtifact Artifact);
