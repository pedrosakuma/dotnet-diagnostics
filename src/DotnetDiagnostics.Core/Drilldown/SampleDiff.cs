using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.Drilldown;

public sealed record MethodDiffKey(SymbolRef Symbol, MethodIdentity? Identity = null);

public sealed record CpuDiffMetric(
    long ExclusiveSamples,
    long InclusiveSamples,
    double ExclusivePercent);

public sealed record HeapDiffMetric(
    long TotalBytes,
    long InstanceCount);

public sealed record AllocationDiffMetric(
    long TotalBytes,
    long AllocCount,
    double BytesPerSecond,
    double AllocCountPerSecond,
    double DurationSeconds);

public sealed record DiffRow<TKey, TMetric>(
    TKey Key,
    TMetric? Baseline,
    TMetric? Current,
    double DeltaAbs,
    double DeltaPct,
    string Direction);

public sealed record SampleDiff<TKey, TMetric>(
    string Kind,
    string BaselineHandle,
    string CurrentHandle,
    double MinDeltaPct,
    int TotalAdded,
    int TotalRemoved,
    int TotalChanged,
    IReadOnlyList<DiffRow<TKey, TMetric>> Added,
    IReadOnlyList<DiffRow<TKey, TMetric>> Removed,
    IReadOnlyList<DiffRow<TKey, TMetric>> Changed,
    string Verdict)
{
    public IReadOnlyList<string>? Notes { get; init; }
}
