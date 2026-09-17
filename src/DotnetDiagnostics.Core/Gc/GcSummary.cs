namespace DotnetDiagnostics.Core.Gc;

/// <summary>Statistics for a single GC generation across the sample window.</summary>
public sealed record GenerationStats(int Generation, int Count);

/// <summary>A completed collection. Legacy PauseDuration is collection elapsed, NOT runtime suspension.</summary>
public sealed record GcEvent(
    DateTimeOffset Timestamp,
    int Generation,
    string Reason,
    string Type,
    TimeSpan PauseDuration,
    int? ClrInstanceId = null,
    uint? CollectionCount = null)
{
    public TimeSpan CollectionElapsedDuration => PauseDuration;
}

/// <summary>
/// A per-collection <c>GCHeapStats</c> sample: per-generation heap sizes, promoted bytes,
/// pinned-object and GC-handle counts, and finalization survivors. Emitted once per GC on
/// CoreCLR / R2R / NativeAOT. <see cref="PohSizeBytes"/> / <see cref="PohPromotedBytes"/> are
/// populated only by the V2 event (pinned object heap) and are 0 on runtimes that emit V1.
/// </summary>
public sealed record GcHeapStatsSample(
    DateTimeOffset Timestamp,
    long Gen0SizeBytes,
    long Gen1SizeBytes,
    long Gen2SizeBytes,
    long LohSizeBytes,
    long PohSizeBytes,
    long TotalHeapSizeBytes,
    long TotalPromotedBytes,
    long Gen2PromotedBytes,
    long PohPromotedBytes,
    long FinalizationPromotedBytes,
    long FinalizationPromotedCount,
    long PinnedObjectCount,
    long GcHandleCount);

/// <summary>
/// Aggregates over valid observed collection pairs. Legacy pause-named fields are collection
/// elapsed; corrected nullable suspension measurements and quality live in Suspension.
/// Detail caps do not affect aggregates; transport/pairing/capture limitations still apply.
/// </summary>
public sealed record GcSummary(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int TotalCollections,
    TimeSpan TotalPauseTime,
    TimeSpan MaxPauseTime,
    IReadOnlyList<GenerationStats> Generations,
    IReadOnlyList<GcEvent> Events,
    IReadOnlyList<GcHeapStatsSample>? HeapStats = null,
    int DroppedEvents = 0,
    int DroppedHeapStats = 0,
    GcSuspensionEvidence? Suspension = null)
{
    public string PauseMeasurementStatus => Suspension?.Status ?? "legacy-unknown";
    public TimeSpan? RequestedDuration { get; init; }
    public TimeSpan CollectionElapsedTime => TotalPauseTime;
    [System.Text.Json.Serialization.JsonIgnore]
    public string MeasurementSummary => Suspension is { IsAuthoritative: true } evidence
        ? $"{TotalCollections} observed completed collection(s); fully-suspended GC-related phase {evidence.TotalSuspensionTime?.TotalMilliseconds:F2}ms, max {evidence.MaxSuspensionTime?.TotalMilliseconds:F2}ms. Measurement v2 ({evidence.Boundaries}); retained intervals {evidence.Intervals.Count}, dropped {evidence.DroppedIntervals}; lifetime compatibility {evidence.LifetimeCompatibility}. {string.Join(", ", evidence.Limitations.Select(p => $"{p.Key}={p.Value}"))}"
        : $"{TotalCollections} observed completed collection(s); GC suspension unavailable ({PauseMeasurementStatus}). Collection elapsed is not pause. {string.Join(", ", Suspension?.Limitations.Select(p => $"{p.Key}={p.Value}") ?? [])}";

    public Evidence.EvidenceQuality GetQuality()
    {
        if (Suspension is null) return Evidence.EvidenceQuality.LegacyUnknown;
        var limitations = Suspension.Limitations.Select(pair => new Evidence.EvidenceLimitation(
            pair.Key.Contains("transport", StringComparison.Ordinal) ? Evidence.EvidenceLimitationCategory.DetectedTransportLoss
                : pair.Key.Contains("cap-", StringComparison.Ordinal) ? Evidence.EvidenceLimitationCategory.CollectorEviction
                : Evidence.EvidenceLimitationCategory.CaptureWindow,
            pair.Key, pair.Value, pair.Key)).ToList();
        if (!Suspension.IsAuthoritative)
            limitations.Add(new(Evidence.EvidenceLimitationCategory.MechanismUnavailable, "gc-suspension", null, PauseMeasurementStatus));
        if (Suspension.DroppedIntervals > 0)
            limitations.Add(new(Evidence.EvidenceLimitationCategory.CollectorEviction, "gc-pause-details", Suspension.DroppedIntervals,
                "Validated aggregate retained; pause detail is a prefix."));
        var support = limitations.Count == 0 ? Evidence.EvidenceConclusionSupport.Supported : Evidence.EvidenceConclusionSupport.Inconclusive;
        return new(Evidence.EvidenceQuality.SchemaV1, limitations,
            new(Suspension.IsAuthoritative && Suspension.ObservedIntervals > 0
                ? Evidence.EvidenceConclusionSupport.Supported
                : Evidence.EvidenceConclusionSupport.NotEstablished, support, support));
    }
}
