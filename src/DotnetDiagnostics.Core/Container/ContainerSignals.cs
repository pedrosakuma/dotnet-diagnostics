namespace DotnetDiagnostics.Core.Container;

/// <summary>cgroup v1 vs v2 vs none-detected.</summary>
public enum CgroupVersion
{
    None = 0,
    V1 = 1,
    V2 = 2,
}

/// <summary>
/// Kernel-side view of the target process's container/cgroup envelope. Closes the most common
/// blind spot in Kubernetes triage: "app is slow but runtime CPU counters look fine" — usually
/// <see cref="ContainerCpuSignals.ThrottlePercent"/> &gt; 0 (CPU throttling). All sub-records are
/// nullable so partial reads (e.g. PSI unavailable on older kernels, no memory limit) degrade
/// gracefully instead of failing the whole tool.
/// </summary>
public sealed record ContainerSignals(
    int ProcessId,
    DateTimeOffset CollectedAt,
    bool InContainer,
    CgroupVersion CgroupVersion,
    string? CgroupPath,
    ContainerCpuSignals? Cpu,
    ContainerMemorySignals? Memory,
    ContainerPressureSignals? Pressure,
    ContainerPidsSignals? Pids,
    int? OomScore,
    IReadOnlyList<string> Notes);

/// <summary>cgroup CPU stat snapshot.</summary>
/// <param name="UsageUsec">Cumulative CPU usage in microseconds (cpu.stat <c>usage_usec</c>).</param>
/// <param name="NrPeriods">Total scheduling periods observed (cpu.stat <c>nr_periods</c>).</param>
/// <param name="NrThrottled">Periods in which the cgroup was throttled (cpu.stat <c>nr_throttled</c>).</param>
/// <param name="ThrottledUsec">Cumulative throttled time in microseconds (cpu.stat <c>throttled_usec</c>).</param>
/// <param name="ThrottlePercent">Fraction of periods in which the cgroup hit its quota, in [0,100].
/// Null when no quota is configured (<c>cpu.max</c> is <c>max</c>) — throttling is impossible.
/// The canonical "am I CPU-throttled" signal in K8s.</param>
/// <param name="QuotaCores">Effective CPU quota expressed in cores (quota_us / period_us).
/// Null when unlimited.</param>
public sealed record ContainerCpuSignals(
    long UsageUsec,
    long NrPeriods,
    long NrThrottled,
    long ThrottledUsec,
    double? ThrottlePercent,
    double? QuotaCores);

/// <summary>cgroup memory headline: current vs limit and OOM event count.</summary>
/// <param name="CurrentBytes">Current charged memory (memory.current).</param>
/// <param name="MaxBytes">Hard memory limit (memory.max). Null when unlimited.</param>
/// <param name="HighBytes">Soft memory limit (memory.high). Null when unset.</param>
/// <param name="UsageFraction">Fraction of the memory limit currently in use, in [0,1]. Null when unlimited.</param>
/// <param name="OomKillCount">Cumulative OOM kills observed (memory.events <c>oom_kill</c>).</param>
/// <param name="MaxHitCount">Cumulative times the cgroup hit its limit (memory.events <c>max</c>).</param>
public sealed record ContainerMemorySignals(
    long CurrentBytes,
    long? MaxBytes,
    long? HighBytes,
    double? UsageFraction,
    long OomKillCount,
    long MaxHitCount);

/// <summary>Pressure Stall Information (PSI) avg10 values, percent of time. Null when the
/// kernel lacks PSI (older kernels) or the cgroup file is unreadable.</summary>
public sealed record ContainerPressureSignals(
    double? CpuSomeAvg10,
    double? MemSomeAvg10,
    double? MemFullAvg10,
    double? IoSomeAvg10,
    double? IoFullAvg10);

/// <summary>Process count vs the cgroup's pids.max.</summary>
public sealed record ContainerPidsSignals(
    long Current,
    long? Max);
