namespace DotnetDiagnostics.Core.CpuEfficiency;

/// <summary>
/// Aggregate, whole-window CPU microarchitecture-efficiency snapshot for a live process —
/// answers "is this CPU-bound process executing efficiently, or stalled?" via IPC, cache/branch/TLB
/// miss rates, stall-cycle breakdown, page faults, and scheduler noise. This is deliberately an
/// AGGREGATE counter (one number per metric for the whole capture window), not per-method/per-frame
/// attribution — see <c>collect_sample(kind="cpu")</c> for the latter.
/// </summary>
/// <remarks>
/// <para>
/// Every metric is nullable because PMU availability differs by CPU vendor, virtualization, and
/// platform: Intel vs. AMD expose different raw counters (this sampler prefers the kernel/perf
/// generic aliases — <c>cache-misses</c>, <c>branch-misses</c>, <c>stalled-cycles-frontend</c> — which
/// are already vendor-normalized in most cases), and many cloud VMs / CI runners expose no vPMU to
/// the guest at all. A null field means "not available on this host", surfaced alongside an entry in
/// <see cref="Notes"/> naming which metric/event was unavailable and why — never a hard failure for
/// the whole call just because one metric is missing.
/// </para>
/// <para>
/// <see cref="StalledCyclesFrontendRate"/> / <see cref="StalledCyclesBackendRate"/> and
/// <see cref="TlbMissRate"/> are Linux-only in this first cut: <c>perf stat</c> exposes them as
/// standard software-normalized events, but Windows ETW's <c>PMCProfile</c> keyword has no commonly
/// available profile source for stalled-cycle or TLB-miss classification (see the design discussion
/// in issue #828). They stay null on Windows with an explanatory note rather than attempting a
/// vendor-specific raw MSR encoding.
/// </para>
/// </remarks>
/// <param name="ProcessId">Target pid.</param>
/// <param name="StartedAt">Wall-clock start of the counting window.</param>
/// <param name="Duration">Configured (not measured) window length.</param>
/// <param name="Backend">Collection backend identifier: <c>perf-stat</c> (Linux) or <c>etw-pmc</c> (Windows).</param>
/// <param name="Instructions">Retired instruction count over the window, when available.</param>
/// <param name="Cycles">CPU cycle count over the window, when available.</param>
/// <param name="InstructionsPerCycle">Instructions / cycles — the headline "is this CPU-bound work efficient" number.</param>
/// <param name="CacheReferences">Last-level-cache access count, when available.</param>
/// <param name="CacheMisses">Last-level-cache miss count, when available.</param>
/// <param name="CacheMissRate">CacheMisses / CacheReferences, when both are available.</param>
/// <param name="BranchInstructions">Retired branch instruction count, when available.</param>
/// <param name="BranchMisses">Mispredicted branch count, when available.</param>
/// <param name="BranchMissRate">BranchMisses / BranchInstructions, when both are available.</param>
/// <param name="StalledCyclesFrontend">Cycles stalled waiting on instruction fetch/decode (Linux only).</param>
/// <param name="StalledCyclesFrontendRate">StalledCyclesFrontend / Cycles, when both are available.</param>
/// <param name="StalledCyclesBackend">Cycles stalled waiting on execution/data dependencies (Linux only).</param>
/// <param name="StalledCyclesBackendRate">StalledCyclesBackend / Cycles, when both are available.</param>
/// <param name="DTlbMisses">Data-TLB load-miss count, when available.</param>
/// <param name="ITlbMisses">Instruction-TLB load-miss count, when available.</param>
/// <param name="TlbMissRate">(DTlbMisses + ITlbMisses) / Instructions, when the numerator and instructions are both available.</param>
/// <param name="PageFaults">Page-fault count over the window, when available.</param>
/// <param name="ContextSwitches">Voluntary + involuntary context-switch count for the target's threads, when available.</param>
/// <param name="CpuMigrations">Count of times a target thread was rescheduled onto a different CPU core, when available.</param>
/// <param name="Notes">Per-metric/per-platform degradation notes (unsupported event, permission denied, vPMU absent, etc.).</param>
public sealed record CpuEfficiencySample(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string Backend,
    long? Instructions = null,
    long? Cycles = null,
    double? InstructionsPerCycle = null,
    long? CacheReferences = null,
    long? CacheMisses = null,
    double? CacheMissRate = null,
    long? BranchInstructions = null,
    long? BranchMisses = null,
    double? BranchMissRate = null,
    long? StalledCyclesFrontend = null,
    double? StalledCyclesFrontendRate = null,
    long? StalledCyclesBackend = null,
    double? StalledCyclesBackendRate = null,
    long? DTlbMisses = null,
    long? ITlbMisses = null,
    double? TlbMissRate = null,
    long? PageFaults = null,
    long? ContextSwitches = null,
    long? CpuMigrations = null,
    IReadOnlyList<string>? Notes = null);
