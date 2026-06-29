namespace DotnetDiagnostics.Core.ReplicaCounters;

/// <summary>
/// The headline EventCounter readings collected from a single replica (Pod) during the
/// simultaneous fan-out window. Exactly one of these is produced per attached Pod that
/// returned a counter snapshot; <see cref="Values"/> is keyed by the canonical metric token
/// (<c>cpu</c>, <c>gc-heap-size</c>, <c>threadpool-queue</c>) so the analyzer can compare
/// like-for-like across replicas regardless of provider/name ordering.
/// </summary>
public sealed record ReplicaCounterReading(
    string PodName,
    int ProcessId,
    IReadOnlyDictionary<string, double> Values);

/// <summary>
/// Dispersion of one metric across every replica that reported it. <see cref="Spread"/> is the
/// absolute max−min and <see cref="RelativeSpread"/> normalizes it by the mean so a 10 MB skew on
/// a 20 MB heap reads as "large" while the same 10 MB on a 4 GB heap reads as "noise". The
/// outlier pick uses the per-metric z-score (max deviation / stddev).
/// </summary>
public sealed record ReplicaMetricDispersion(
    string Metric,
    double Min,
    double Max,
    double Mean,
    double StdDev,
    double Spread,
    double RelativeSpread,
    string? MinPod,
    string? MaxPod);

/// <summary>
/// Consolidated cross-replica counter skew: which replica is the outlier on gc-heap-size / cpu /
/// threadpool-queue across every attached Pod, captured live and simultaneously in one fan-out.
/// </summary>
/// <remarks>
/// Distinct from <c>compare_to_baseline</c> which contrasts pre-collected serial snapshots; this is
/// a best-effort same-window view of currently-attached replicas. Pods that failed collection are
/// reported by the orchestrator layer, not here — this engine is pure.
/// </remarks>
public sealed record ReplicaCounterSkew(
    int PodCount,
    IReadOnlyList<ReplicaCounterReading> Replicas,
    IReadOnlyList<ReplicaMetricDispersion> Metrics,
    string? OutlierPod,
    double OutlierScore,
    IReadOnlyList<string> Warnings);
