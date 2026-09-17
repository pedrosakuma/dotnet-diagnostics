namespace DotnetDiagnostics.Core.Gc;

/// <summary>A GC-related fully-suspended phase, not collection elapsed or per-thread lost time.</summary>
public sealed record GcSuspensionInterval(
    DateTimeOffset StartedAt,
    DateTimeOffset StoppedAt,
    int ClrInstanceId,
    int ThreadId,
    int Reason,
    uint GcCountAtSuspend,
    TimeSpan AcquisitionDuration,
    TimeSpan? RestartDuration = null)
{
    public TimeSpan Duration => StoppedAt - StartedAt;
}

/// <summary>
/// Version 2 pause contract: GCSuspendEEStop to GCRestartEEStart, reasons 1 and 6 only.
/// Legacy GcSummary pause-named fields remain collection elapsed for source compatibility.
/// Missing evidence means unknown, never measured zero. Timing with unlocalized stream loss is unavailable.
/// </summary>
public sealed record GcSuspensionEvidence(
    string Status,
    DateTimeOffset ObservationStart,
    DateTimeOffset ObservationEnd,
    DateTimeOffset? ProcessStartedAt,
    string Completion,
    TimeSpan? TotalSuspensionTime,
    TimeSpan? MaxSuspensionTime,
    long ObservedIntervals,
    long DroppedIntervals,
    IReadOnlyList<GcSuspensionInterval> Intervals,
    IReadOnlyDictionary<string, long> Limitations)
{
    public int MeasurementVersion { get; init; } = 2;
    public string Boundaries { get; init; } = "GCSuspendEEStop-to-GCRestartEEStart";
    public string LifetimeCompatibility => ProcessStartedAt.HasValue ? "available" : "unknown";
    public IReadOnlyList<int> IgnoredReasons { get; init; } = [];
    public long ObservedCollectionPairs { get; init; }
    public int OutputOmittedIntervals { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAuthoritative => MeasurementVersion == 2 && Boundaries == "GCSuspendEEStop-to-GCRestartEEStart"
        && Status == "no-detected-loss";
}
