using DotnetDiagnostics.Core.Evidence;

namespace DotnetDiagnostics.Core.ThreadPool;

public interface IThreadPoolCollector
{
    Task<ThreadPoolEventSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}

public sealed record ThreadPoolEventSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<ThreadPoolCountBucket> WorkerThreadTimeline,
    IReadOnlyList<ThreadPoolCountBucket> IocpThreadTimeline,
    IReadOnlyList<ThreadPoolHillClimbingSample> HillClimbing,
    IReadOnlyList<ThreadPoolWorkItemOrigin> WorkItemOrigins,
    ThreadPoolEffectiveSettings? EffectiveSettings,
    int TotalEnqueueEvents,
    int TotalDequeueEvents,
    IReadOnlyList<string> Notes,
    ThreadPoolEvidenceSummary? Evidence = null,
    EvidenceQuality? Quality = null);

public sealed record ThreadPoolCountBucket(
    DateTimeOffset Timestamp,
    int Count,
    string? CountProvenance = null);

public sealed record ThreadPoolHillClimbingSample(
    DateTimeOffset Timestamp,
    string Reason,
    int? OldCount,
    int? NewCount,
    double? Throughput,
    string? ReasonProvenance = null,
    string? OldCountProvenance = null,
    string? NewCountProvenance = null);

public sealed record ThreadPoolEvidenceSummary(
    int? HillClimbingEvents,
    int? ConfirmedStarvationAdjustments,
    int? ConfirmedCooperativeBlockingAdjustments,
    bool? HasCompleteRuntimeReasonEvidence);

public sealed record ThreadPoolWorkItemOrigin(string Method, int Count);

public sealed record ThreadPoolEffectiveSettings(
    int WorkerMinThreads,
    int WorkerMaxThreads,
    int IocpMinThreads,
    int IocpMaxThreads);

public static class ThreadPoolEvidence
{
    public const string RuntimeObserved = "runtime-observed";
    public const string RuntimeUnrecognized = "runtime-unrecognized";
    public const string Missing = "missing";
    public const string CarriedForward = "carried-forward";
    public const string InferredFromDelta = "inferred-from-delta";
    public const string InferredFromNeighbor = "inferred-from-neighbor";

    public static bool IsConfirmedReason(ThreadPoolHillClimbingSample sample, string reason)
        => string.Equals(sample.Reason, reason, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sample.ReasonProvenance, RuntimeObserved, StringComparison.Ordinal);

    public static ThreadPoolEvidenceSummary Summarize(IReadOnlyList<ThreadPoolHillClimbingSample> samples)
        => new(
            samples.Count,
            samples.Count(static sample => IsConfirmedReason(sample, "Starvation")),
            samples.Count(static sample => IsConfirmedReason(sample, "CooperativeBlocking")),
            samples.Count > 0
                && samples.All(static sample => string.Equals(sample.ReasonProvenance, RuntimeObserved, StringComparison.Ordinal)));

    public static ThreadPoolEvidenceSummary? GetSummary(ThreadPoolEventSnapshot snapshot)
        => snapshot.Evidence
            ?? (snapshot.HillClimbing.Count > 0
                && snapshot.HillClimbing.All(static sample => sample.ReasonProvenance is not null)
                    ? Summarize(snapshot.HillClimbing)
                    : null);

    public static EvidenceQuality GetQuality(ThreadPoolEventSnapshot snapshot)
        => snapshot.Quality ?? EvidenceQuality.LegacyUnknown;

    internal static EvidenceQuality BuildQuality(
        ThreadPoolEvidenceSummary evidence,
        long? detectedTransportLossEvents,
        int processingFailures,
        int workerTimelineEvictions,
        int iocpTimelineEvictions,
        int hillClimbingEvictions,
        int inferredValues,
        bool effectiveSettingsAvailable,
        bool workItemOriginsObservable)
    {
        var limitations = new List<EvidenceLimitation>(9);
        if (detectedTransportLossEvents is > 0)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.DetectedTransportLoss,
                "eventpipe",
                detectedTransportLossEvents,
                "EventPipe reported lost events; absence and exhaustive counts may omit runtime events."));
        }
        else if (detectedTransportLossEvents is null)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.MechanismUnobservable,
                "eventpipe-loss",
                null,
                "The EventPipe lost-event count was unavailable; transport loss cannot be excluded."));
        }
        if (processingFailures > 0)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.ProcessingFailure,
                "eventpipe-parser",
                processingFailures,
                "Event processing ended with an error after partial evidence may have been retained."));
        }
        AddEviction(limitations, "worker-timeline", workerTimelineEvictions);
        AddEviction(limitations, "iocp-timeline", iocpTimelineEvictions);
        AddEviction(limitations, "hill-climbing", hillClimbingEvictions);
        if (inferredValues > 0)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.Inference,
                "thread-counts",
                inferredValues,
                "Some thread-count values were carried forward or inferred; adjustment reasons were not inferred."));
        }
        if (evidence.HillClimbingEvents == 0)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.CaptureWindow,
                "hill-climbing",
                null,
                "No hill-climbing event was observed in this capture window; absence does not establish a healthy control."));
        }
        if (!effectiveSettingsAvailable)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.MechanismUnavailable,
                "effective-settings",
                null,
                "Effective ThreadPool minimum and maximum settings were unavailable from this EventPipe capture."));
        }
        if (!workItemOriginsObservable)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.MechanismUnobservable,
                "work-item-origins",
                null,
                "The live EventPipe stream did not expose managed call stacks for work-item origin attribution."));
        }

        var degradedCounts = detectedTransportLossEvents is null or > 0
            || processingFailures > 0
            || hillClimbingEvictions > 0
            || evidence.HillClimbingEvents == 0
            || evidence.HasCompleteRuntimeReasonEvidence != true;
        var positive = evidence.ConfirmedStarvationAdjustments > 0
            || evidence.ConfirmedCooperativeBlockingAdjustments > 0
                ? EvidenceConclusionSupport.Supported
                : EvidenceConclusionSupport.NotEstablished;
        var exhaustive = degradedCounts
            ? EvidenceConclusionSupport.Inconclusive
            : EvidenceConclusionSupport.Supported;

        return new EvidenceQuality(
            EvidenceQuality.SchemaV1,
            limitations,
            new EvidenceConclusionPolicy(positive, exhaustive, exhaustive));
    }

    public static EvidenceQuality WithProjection(
        EvidenceQuality? quality,
        string scope,
        long omittedCount,
        string detail)
    {
        var source = quality ?? EvidenceQuality.LegacyUnknown;
        if (omittedCount <= 0)
        {
            return source;
        }

        var limitations = source.Limitations
            .Where(l => l.Category != EvidenceLimitationCategory.OutputProjection
                || !string.Equals(l.Scope, scope, StringComparison.Ordinal))
            .Append(new EvidenceLimitation(
                EvidenceLimitationCategory.OutputProjection,
                scope,
                omittedCount,
                detail))
            .ToArray();
        return source with { Limitations = limitations };
    }

    private static void AddEviction(List<EvidenceLimitation> limitations, string scope, int count)
    {
        if (count > 0)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.CollectorEviction,
                scope,
                count,
                "Older retained detail was evicted at the collector's fixed in-memory limit."));
        }
    }

}