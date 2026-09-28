namespace DotnetDiagnostics.TestSupport.OperatingEnvelope;

public sealed record OperatingEnvelopeRequestAccounting(
    int Planned,
    int Offered,
    int Admitted,
    int Rejected,
    int Unknown,
    int NotOffered,
    double MeasurementWindowMilliseconds,
    IReadOnlyList<double> LatencyMilliseconds)
{
    public double? ThroughputPerSecond => MeasurementWindowMilliseconds > 0
        ? Admitted * 1000d / MeasurementWindowMilliseconds
        : null;

    public double? LatencyMinimumMilliseconds => LatencyMilliseconds.Count == 0
        ? null
        : LatencyMilliseconds.Min();

    public double? LatencyMedianMilliseconds => Percentile(0.5);
    public double? LatencyP95Milliseconds => Percentile(0.95);
    public double? LatencyMaximumMilliseconds => LatencyMilliseconds.Count == 0
        ? null
        : LatencyMilliseconds.Max();

    public OperatingEnvelopeRequestAccounting Validate()
    {
        if (Planned < 0 || Offered < 0 || Admitted < 0 || Rejected < 0 || Unknown < 0 ||
            NotOffered < 0 || MeasurementWindowMilliseconds < 0 ||
            Offered != Admitted + Rejected + Unknown ||
            Planned != Offered + NotOffered ||
            LatencyMilliseconds.Count > Planned ||
            LatencyMilliseconds.Any(value => !double.IsFinite(value) || value < 0))
        {
            throw new ArgumentException("Request accounting populations are inconsistent or out of bounds.");
        }

        return this;
    }

    private double? Percentile(double percentile)
    {
        if (LatencyMilliseconds.Count == 0)
        {
            return null;
        }

        var ordered = LatencyMilliseconds.Order().ToArray();
        var index = Math.Max(0, (int)Math.Ceiling(percentile * ordered.Length) - 1);
        return ordered[index];
    }
}

public sealed record OperatingEnvelopeCaptureAccounting(
    long? Offered,
    long? Accepted,
    long? Persisted,
    long? RecordRejected,
    long? QueueRejected,
    long? StorageRejected,
    long? Pending,
    long? Unknown,
    long? SourceRejected,
    bool UnknownTail,
    string AccountingScope)
{
    public long? Rejected =>
        RecordRejected is { } records && QueueRejected is { } queued && StorageRejected is { } storage
            ? records + queued + storage
            : null;

    public static OperatingEnvelopeCaptureAccounting Ephemeral(string scope) =>
        new(null, null, 0, null, null, null, null, null, null, true, scope);

    public static OperatingEnvelopeCaptureAccounting FromDurableQuality(
        long offered,
        long accepted,
        long persisted,
        long recordRejected,
        long queueRejected,
        long storageRejected,
        long pending,
        long? sourceRejected,
        bool unknownTail,
        string scope)
    {
        var accounted = persisted + recordRejected + queueRejected + storageRejected + pending;
        long? unknown = !unknownTail && offered >= accounted ? offered - accounted : null;
        return new(offered, accepted, persisted, recordRejected, queueRejected, storageRejected,
            pending, unknown, sourceRejected, unknownTail, scope);
    }
}

public sealed record OperatingEnvelopeTargetResources(
    int ProcessId,
    double? CpuMilliseconds,
    long? PeakWorkingSetBytes,
    IReadOnlyList<string> Notes);

public sealed record OperatingEnvelopeTargetRequests(
    int ProcessId,
    OperatingEnvelopeRequestAccounting Accounting);

public sealed record OperatingEnvelopeDiagnosticResources(
    double? CpuMilliseconds,
    long? PeakWorkingSetBytes,
    long? AllocatedBytes,
    long? Gen0Collections,
    long? Gen1Collections,
    long? Gen2Collections,
    IReadOnlyList<string> Notes);

public sealed record OperatingEnvelopeArtifactMeasurement(
    string Kind,
    bool CollectionSucceeded,
    string? CaptureId,
    string? CaptureState,
    OperatingEnvelopeCaptureAccounting Accounting,
    long? RecordCount,
    long? StoreBytes,
    IReadOnlyList<OperatingEnvelopeArtifactHash> StoreFiles,
    double CollectorElapsedMilliseconds,
    double? StoreEncodeAndSealMilliseconds,
    double? QueryOpenMilliseconds,
    double? QueryMilliseconds,
    long? QueriedRecords,
    IReadOnlyList<string> LossAndCapNotes,
    string? Error,
    IReadOnlyDictionary<string, double?>? TargetGcCounters = null,
    double? CollectorDrainMilliseconds = null,
    double? QueryCloseMilliseconds = null,
    double? EvidenceHashMilliseconds = null);

public sealed record OperatingEnvelopeTrialResult(
    OperatingEnvelopeTrialPlan Plan,
    string ConfigurationHash,
    OperatingEnvelopeTrialOutcome Outcome,
    OperatingEnvelopeStopOutcome StopOutcome,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    double WarmupMilliseconds,
    OperatingEnvelopeRequestAccounting WarmupRequests,
    OperatingEnvelopeRequestAccounting Requests,
    IReadOnlyList<OperatingEnvelopeTargetRequests> TargetRequests,
    IReadOnlyList<OperatingEnvelopeTargetResources> Targets,
    OperatingEnvelopeDiagnosticResources DiagnosticProcess,
    IReadOnlyList<OperatingEnvelopeArtifactMeasurement> Artifacts,
    double CleanupMilliseconds,
    bool CleanupSucceeded,
    IReadOnlyList<string> CleanupErrors,
    IReadOnlyList<string> Notes,
    string? Error);

public sealed record OperatingEnvelopePairResult(
    string PairId,
    OperatingEnvelopePopulation Population,
    bool IsValid,
    IReadOnlyList<string> InvalidReasons,
    IReadOnlyList<string> TrialIds);

public static class OperatingEnvelopePairValidation
{
    public static OperatingEnvelopePairResult Validate(
        string pairId,
        OperatingEnvelopePopulation population,
        IReadOnlyList<OperatingEnvelopeTrialResult> trials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairId);
        ArgumentNullException.ThrowIfNull(trials);
        var reasons = new List<string>();
        var pair = trials.Where(trial => trial.Plan.PairId == pairId)
            .OrderBy(trial => trial.Plan.OrderInPair)
            .ToArray();

        if (pair.Length != 2)
        {
            reasons.Add($"Expected two outcomes for the pair; found {pair.Length}.");
        }

        if (pair.Any(trial => trial.Plan.Population != population))
        {
            reasons.Add("Pair members do not use the same workload population.");
        }

        if (pair.Select(trial => trial.Plan.StorageMode).Distinct().Count() != 2)
        {
            reasons.Add("Pair does not contain exactly one ephemeral and one durable trial.");
        }

        if (pair.Any(trial => trial.Outcome != OperatingEnvelopeTrialOutcome.Completed ||
                              trial.StopOutcome != OperatingEnvelopeStopOutcome.None ||
                              trial.Artifacts.Any(artifact => !artifact.CollectionSucceeded)))
        {
            reasons.Add("At least one trial did not produce a completed collection outcome.");
        }

        if (pair.Select(trial => trial.ConfigurationHash).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            reasons.Add("Pair members were run with different configuration hashes.");
        }

        return new(pairId, population, reasons.Count == 0, reasons.AsReadOnly(),
            pair.Select(trial => trial.Plan.TrialId).ToArray());
    }
}
