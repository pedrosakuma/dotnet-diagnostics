using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.NativeAlloc;

/// <summary>
/// Compact summary of a native-allocation sampling window, safe to return inline to the MCP
/// client. The full caller→callee tree lives in the companion <see cref="CpuSampleTraceArtifact"/>
/// (retrieved via the issued handle and walked with <c>query_snapshot(view="call-tree")</c>).
/// </summary>
/// <param name="ProcessId">Target pid.</param>
/// <param name="StartedAt">Wall-clock start of the sampling window.</param>
/// <param name="Duration">Configured (not measured) window length.</param>
/// <param name="TotalSampledAllocations">
/// Number of recorded allocator-call samples. With <c>samplePeriod &gt; 1</c> this is a sampled
/// subset of the real allocation count, not the total — and it counts <b>calls, not bytes</b>.
/// </param>
/// <param name="TopAllocators">Up to topN call-stack frames ranked by inclusive sampled allocator hits.</param>
/// <param name="ProbedFunctions">The libc allocator symbols actually uprobed (e.g. malloc, calloc, realloc).</param>
/// <param name="LibcPath">The resolved libc shared object the uprobes were attached to (target-namespace path).</param>
/// <param name="SamplePeriod">perf sample period used: one recorded callchain per this many allocator hits.</param>
/// <param name="SymbolSource">Aggregate symbol-resolution quality across all frames.</param>
/// <param name="Notes">Best-effort caveats (overhead, partial probes, no samples, etc.) the LLM can disclose.</param>
public sealed record NativeAllocSample(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalSampledAllocations,
    IReadOnlyList<Hotspot> TopAllocators,
    IReadOnlyList<string> ProbedFunctions,
    string LibcPath,
    long SamplePeriod,
    string SymbolSource,
    IReadOnlyList<string>? Notes = null);

/// <summary>
/// Pair returned by <see cref="INativeAllocSampler"/>: the lightweight summary plus the trace
/// artifact handed to the handle store. The artifact is a <see cref="CpuSampleTraceArtifact"/> so
/// the existing <c>query_snapshot(view="call-tree")</c> drilldown resolves it without a dedicated
/// artifact type (issue #279 §2: reuse the shared call-tree pipeline).
/// </summary>
public sealed record NativeAllocSampleResult(NativeAllocSample Summary, CpuSampleTraceArtifact Artifact);
