using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Evidence;

namespace DotnetDiagnostics.Core.Comparison;

public static class RetainedGrowthEvidenceBounds
{
    public const int MaximumAttempts = 8;
    public const int MinimumRobustPairs = 3;
    public const int BootstrapResamples = 512;
    public const double ConfidenceLevel = 0.95;
}

public static class RetainedGrowthEvidenceMethodIds
{
    public const string PairedDelta = "paired-delta";
    public const string WindowMedian = "window-median";
    public const string MedianAbsoluteDeviation = "median-absolute-deviation";
    public const string PercentileBootstrapInterval = "percentile-bootstrap-interval";
}

[JsonConverter(typeof(JsonStringEnumConverter<RetainedGrowthEvidenceAvailability>))]
public enum RetainedGrowthEvidenceAvailability
{
    [JsonStringEnumMemberName("available")]
    Available,
    [JsonStringEnumMemberName("insufficient_observations")]
    InsufficientObservations,
    [JsonStringEnumMemberName("incompatible")]
    Incompatible,
    [JsonStringEnumMemberName("loss_detected")]
    LossDetected,
    [JsonStringEnumMemberName("unsupported_approximation")]
    UnsupportedApproximation,
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<RetainedGrowthAttemptOutcome>))]
public enum RetainedGrowthAttemptOutcome
{
    [JsonStringEnumMemberName("completed")]
    Completed,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("aborted")]
    Aborted,
}

[JsonConverter(typeof(JsonStringEnumConverter<RetainedGrowthRootPathAvailability>))]
public enum RetainedGrowthRootPathAvailability
{
    [JsonStringEnumMemberName("available")]
    Available,
    [JsonStringEnumMemberName("unavailable_by_mechanism")]
    UnavailableByMechanism,
    [JsonStringEnumMemberName("not_collected")]
    NotCollected,
    [JsonStringEnumMemberName("incomplete_evidence")]
    IncompleteEvidence,
}

public sealed record RetainedGrowthEvidenceMethod(string Id, int Version);

public sealed record RetainedGrowthUnavailableField(string Field, string Reason);

public sealed record RetainedGrowthCompatibilityIdentity(
    string RuntimeName,
    string RuntimeVersion,
    string Architecture,
    string Backend,
    TypeIdentity TargetType,
    string QuantityRepresentation,
    string CaptureSettings,
    string IndependenceUnit,
    string ComparisonAxis);

public sealed record RetainedGrowthIdentityMismatch(
    string AttemptId,
    string Dimension,
    string? Expected,
    string? Actual,
    string Reason);

public sealed record RetainedGrowthEvidenceCounts(
    int Attempts,
    int SuccessfulAttempts,
    int FailedAttempts,
    int AbortedAttempts,
    int Captures,
    int CompletePairs,
    int AcceptedPairs,
    int RejectedPairs,
    int ProcessRuns,
    int AllocationObservations);

public sealed record RetainedGrowthInterval(
    double Lower,
    double Upper,
    double ConfidenceLevel,
    double ParentEstimate);

public sealed record RetainedGrowthRootPathEvidence(
    RetainedGrowthRootPathAvailability Availability,
    int CapturesWithPaths,
    int TotalCaptures,
    int? MatchingPathCount,
    string? UnavailableReason);

public sealed record RetainedGrowthObserverEffects(
    int GcDumpCaptures,
    int? ObservedGen2Collections,
    string Provenance,
    IReadOnlyList<string> Limitations);

public sealed record RetainedGrowthAllocationObservation(
    long SampledBytes,
    long SampleEvents,
    long LostOrDropped,
    string Representation = "eventpipe-gcallocationtick-sampled");

public sealed record RetainedGrowthAllocationEvidence(
    RetainedGrowthEvidenceAvailability Availability,
    RetainedGrowthEvidenceMethod Method,
    string Unit,
    double? MedianSampledBytes,
    long SampleEvents,
    long LostOrDropped,
    int ObservationCount,
    string Representation,
    IReadOnlyList<RetainedGrowthUnavailableField> UnavailableFields,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations);

public sealed record RetainedGrowthEvidenceAttempt(
    string AttemptId,
    RetainedGrowthAttemptOutcome Outcome,
    string? BaselineHandle,
    HeapSnapshotArtifact? Baseline,
    string? CurrentHandle,
    HeapSnapshotArtifact? Current)
{
    public string? FailureReason { get; init; }
    public int? ObservedGen2Collections { get; init; }
    public RetainedGrowthAllocationObservation? Allocation { get; init; }
}

public sealed record RetainedGrowthPairEvidence(
    string AttemptId,
    RetainedGrowthAttemptOutcome Outcome,
    RetainedGrowthEvidenceAvailability Availability,
    string? BaselineHandle,
    string? CurrentHandle,
    int? ProcessId,
    DateTimeOffset? BaselineCapturedAt,
    DateTimeOffset? CurrentCapturedAt,
    long? BaselineRetainedBytes,
    long? CurrentRetainedBytes,
    long? RetainedBytesDelta,
    long? BaselineRetainedInstances,
    long? CurrentRetainedInstances,
    long? RetainedInstancesDelta,
    RetainedGrowthCompatibilityIdentity? CompatibilityIdentity,
    IReadOnlyList<RetainedGrowthIdentityMismatch> CompatibilityMismatches,
    RetainedGrowthRootPathEvidence RootPathEvidence,
    RetainedGrowthObserverEffects ObserverEffects,
    IReadOnlyList<RetainedGrowthUnavailableField> UnavailableFields,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations);

public sealed record RetainedGrowthRepeatedEvidence(
    RetainedGrowthEvidenceAvailability Availability,
    RetainedGrowthEvidenceMethod EstimateMethod,
    RetainedGrowthEvidenceMethod DispersionMethod,
    RetainedGrowthEvidenceMethod IntervalMethod,
    string IndependenceUnit,
    int AcceptedPairs,
    int PositivePairedGrowthCount,
    double? MedianRetainedBytesDelta,
    double? MedianRetainedInstancesDelta,
    double? RetainedBytesDeltaMad,
    double? RetainedInstancesDeltaMad,
    RetainedGrowthInterval? RetainedBytesDeltaInterval,
    RetainedGrowthInterval? RetainedInstancesDeltaInterval,
    string? OrderedInputSha256,
    string? RetainedBytesBootstrapSeedSha256,
    string? RetainedInstancesBootstrapSeedSha256,
    int? BootstrapResamples,
    IReadOnlyList<RetainedGrowthUnavailableField> UnavailableFields,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations);

public sealed record RetainedGrowthEvidenceResult(
    string Schema,
    TypeIdentity TargetType,
    RetainedGrowthEvidenceMethod PairMethod,
    RetainedGrowthEvidenceCounts Counts,
    IReadOnlyList<RetainedGrowthPairEvidence> Pairs,
    RetainedGrowthRepeatedEvidence Repeated,
    RetainedGrowthAllocationEvidence AllocationEvidence,
    RetainedGrowthRootPathEvidence RootPathEvidence,
    RetainedGrowthObserverEffects ObserverEffects,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations)
{
    public const string SchemaV1 = "dotnet-diagnostics/retained-growth-evidence/v1";
}

public static class RetainedGrowthEvidence
{
    private const string IndependenceUnit = "process_run";
    private const string ComparisonAxis = "capture_time";
    private const string CaptureSettings = "heap-snapshot-per-type-totals";
    private const string QuantityRepresentation = "observed-retained-shallow-bytes-and-instances";
    private const string BootstrapPrng = "xoshiro256-star-star-v1";
    private const string BootstrapQuantileRule = "linear-r7-v1";

    private static readonly RetainedGrowthEvidenceMethod PairMethod =
        new(RetainedGrowthEvidenceMethodIds.PairedDelta, 1);
    private static readonly RetainedGrowthEvidenceMethod MedianMethod =
        new(RetainedGrowthEvidenceMethodIds.WindowMedian, 1);
    private static readonly RetainedGrowthEvidenceMethod MadMethod =
        new(RetainedGrowthEvidenceMethodIds.MedianAbsoluteDeviation, 1);
    private static readonly RetainedGrowthEvidenceMethod IntervalMethod =
        new(RetainedGrowthEvidenceMethodIds.PercentileBootstrapInterval, 1);

    public static RetainedGrowthEvidenceResult Build(
        TypeIdentity targetType,
        IReadOnlyList<RetainedGrowthEvidenceAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType.TypeFullName);
        if (attempts.Count is < 1 or > RetainedGrowthEvidenceBounds.MaximumAttempts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempts),
                $"Retained-growth evidence requires between 1 and {RetainedGrowthEvidenceBounds.MaximumAttempts} attempts.");
        }
        if (attempts.Any(static attempt => string.IsNullOrWhiteSpace(attempt.AttemptId)))
        {
            throw new ArgumentException("Every retained-growth attempt requires a non-empty attempt id.", nameof(attempts));
        }
        if (attempts.Select(static attempt => attempt.AttemptId).Distinct(StringComparer.Ordinal).Count() != attempts.Count)
        {
            throw new ArgumentException("Retained-growth attempt ids must be unique.", nameof(attempts));
        }

        var orderedAttempts = attempts
            .OrderBy(static attempt => attempt.AttemptId, StringComparer.Ordinal)
            .ToArray();
        var pairs = orderedAttempts.Select(attempt => BuildPair(targetType, attempt)).ToArray();
        var canonical = pairs.FirstOrDefault(static pair =>
            pair.Availability == RetainedGrowthEvidenceAvailability.Available)?.CompatibilityIdentity;
        if (canonical is not null)
        {
            pairs = pairs.Select(pair => ApplyCrossAttemptCompatibility(pair, canonical)).ToArray();
        }
        pairs = ApplyProcessRunIndependence(pairs);

        var accepted = pairs.Where(static pair =>
            pair.Availability == RetainedGrowthEvidenceAvailability.Available
            && pair.RetainedBytesDelta.HasValue
            && pair.RetainedInstancesDelta.HasValue).ToArray();
        var repeated = BuildRepeated(pairs, accepted);
        var allocation = BuildAllocationEvidence(orderedAttempts);
        var rootPaths = SummarizeRootPaths(pairs);
        var observerEffects = SummarizeObserverEffects(pairs);
        var counts = new RetainedGrowthEvidenceCounts(
            orderedAttempts.Length,
            orderedAttempts.Count(static attempt => attempt.Outcome == RetainedGrowthAttemptOutcome.Completed),
            orderedAttempts.Count(static attempt => attempt.Outcome == RetainedGrowthAttemptOutcome.Failed),
            orderedAttempts.Count(static attempt => attempt.Outcome == RetainedGrowthAttemptOutcome.Aborted),
            pairs.Sum(static pair =>
                (pair.BaselineCapturedAt.HasValue ? 1 : 0)
                + (pair.CurrentCapturedAt.HasValue ? 1 : 0)),
            pairs.Count(static pair => pair.Outcome == RetainedGrowthAttemptOutcome.Completed),
            accepted.Length,
            pairs.Length - accepted.Length,
            accepted.Length,
            orderedAttempts.Count(static attempt => attempt.Allocation is not null));
        var assumptions = new[]
        {
            "Each accepted pair represents one independently launched process run.",
            "Baseline and current captures use compatible runtime, backend, type identity, and capture semantics.",
            "Observed per-type bytes are shallow retained bytes in the heap snapshot, not transitive retained size.",
        };
        var limitations = new List<string>
        {
            "Objective retained-growth evidence does not establish a leak, regression, health state, cause, or future trajectory.",
            "Allocation evidence is sampled traffic reported separately and never contributes to retained-byte or retained-instance deltas.",
            "A zero compatible delta does not prove absence of retention outside the selected type or outside the observed capture representation.",
        };
        if (pairs.Any(static pair => pair.Outcome != RetainedGrowthAttemptOutcome.Completed))
        {
            limitations.Add("Failed and aborted attempts remain in counts and were not replaced by hidden retries.");
        }

        return new(
            RetainedGrowthEvidenceResult.SchemaV1,
            targetType,
            PairMethod,
            counts,
            pairs,
            repeated,
            allocation,
            rootPaths,
            observerEffects,
            assumptions,
            limitations);
    }

    private static RetainedGrowthPairEvidence BuildPair(
        TypeIdentity targetType,
        RetainedGrowthEvidenceAttempt attempt)
    {
        var unavailable = new List<RetainedGrowthUnavailableField>();
        var limitations = new List<string>();
        var assumptions = new[]
        {
            "The baseline/current ordering reflects the intended earlier/later comparison.",
            "The selected type identity refers to the same managed type in both captures.",
        };
        if (attempt.Outcome != RetainedGrowthAttemptOutcome.Completed)
        {
            var reason = attempt.Outcome == RetainedGrowthAttemptOutcome.Failed
                ? "attempt_failed"
                : "attempt_aborted";
            unavailable.Add(new("retainedBytesDelta", reason));
            unavailable.Add(new("retainedInstancesDelta", reason));
            if (!string.IsNullOrWhiteSpace(attempt.FailureReason))
            {
                limitations.Add(attempt.FailureReason);
            }
            return UnavailablePair(
                attempt,
                RetainedGrowthEvidenceAvailability.InsufficientObservations,
                unavailable,
                limitations,
                assumptions);
        }
        if (attempt.Baseline is null || attempt.Current is null
            || string.IsNullOrWhiteSpace(attempt.BaselineHandle)
            || string.IsNullOrWhiteSpace(attempt.CurrentHandle))
        {
            unavailable.Add(new("retainedBytesDelta", "complete_pair_required"));
            unavailable.Add(new("retainedInstancesDelta", "complete_pair_required"));
            return UnavailablePair(
                attempt,
                RetainedGrowthEvidenceAvailability.InsufficientObservations,
                unavailable,
                limitations,
                assumptions);
        }

        var baseline = attempt.Baseline;
        var current = attempt.Current;
        var rootPaths = GetRootPathEvidence(targetType, current);
        var observerEffects = PairObserverEffects(attempt, baseline, current);
        var mismatches = ValidatePairCompatibility(attempt.AttemptId, baseline, current);
        var identity = BuildIdentity(targetType, current);
        if (mismatches.Count > 0)
        {
            unavailable.Add(new("retainedBytesDelta", "capture_identity_mismatch"));
            unavailable.Add(new("retainedInstancesDelta", "capture_identity_mismatch"));
            return new(
                attempt.AttemptId,
                attempt.Outcome,
                RetainedGrowthEvidenceAvailability.Incompatible,
                attempt.BaselineHandle,
                attempt.CurrentHandle,
                current.ProcessId,
                baseline.CapturedAt,
                current.CapturedAt,
                null,
                null,
                null,
                null,
                null,
                null,
                identity,
                mismatches,
                rootPaths,
                observerEffects,
                unavailable,
                assumptions,
                limitations);
        }

        var qualityAvailability = GetQualityAvailability(baseline, current, limitations);
        if (qualityAvailability is not null)
        {
            unavailable.Add(new("retainedBytesDelta", qualityAvailability.Value.Reason));
            unavailable.Add(new("retainedInstancesDelta", qualityAvailability.Value.Reason));
            return new(
                attempt.AttemptId,
                attempt.Outcome,
                qualityAvailability.Value.Availability,
                attempt.BaselineHandle,
                attempt.CurrentHandle,
                current.ProcessId,
                baseline.CapturedAt,
                current.CapturedAt,
                null,
                null,
                null,
                null,
                null,
                null,
                identity,
                [],
                rootPaths,
                observerEffects,
                unavailable,
                assumptions,
                limitations);
        }

        var baselineValue = SelectType(targetType, baseline);
        var currentValue = SelectType(targetType, current);
        if (baselineValue.Ambiguous || currentValue.Ambiguous)
        {
            unavailable.Add(new("retainedBytesDelta", "selected_type_identity_ambiguous"));
            unavailable.Add(new("retainedInstancesDelta", "selected_type_identity_ambiguous"));
            return new(
                attempt.AttemptId,
                attempt.Outcome,
                RetainedGrowthEvidenceAvailability.Incompatible,
                attempt.BaselineHandle,
                attempt.CurrentHandle,
                current.ProcessId,
                baseline.CapturedAt,
                current.CapturedAt,
                null,
                null,
                null,
                null,
                null,
                null,
                identity,
                [],
                rootPaths,
                observerEffects,
                unavailable,
                assumptions,
                limitations);
        }

        if ((!baselineValue.Observed && !SupportsSelectedTypeAbsence(baseline))
            || (!currentValue.Observed && !SupportsSelectedTypeAbsence(current)))
        {
            unavailable.Add(new("retainedBytesDelta", "selected_type_not_observed_and_absence_not_supported"));
            unavailable.Add(new("retainedInstancesDelta", "selected_type_not_observed_and_absence_not_supported"));
            limitations.Add("A missing selected-type row was not converted to zero because bounded rankings do not establish exhaustive absence.");
            return new(
                attempt.AttemptId,
                attempt.Outcome,
                RetainedGrowthEvidenceAvailability.UnsupportedApproximation,
                attempt.BaselineHandle,
                attempt.CurrentHandle,
                current.ProcessId,
                baseline.CapturedAt,
                current.CapturedAt,
                baselineValue.Observed ? baselineValue.Bytes : null,
                currentValue.Observed ? currentValue.Bytes : null,
                null,
                baselineValue.Observed ? baselineValue.Instances : null,
                currentValue.Observed ? currentValue.Instances : null,
                null,
                identity,
                [],
                rootPaths,
                observerEffects,
                unavailable,
                assumptions,
                limitations);
        }

        var baselineBytes = baselineValue.Observed ? baselineValue.Bytes : 0;
        var currentBytes = currentValue.Observed ? currentValue.Bytes : 0;
        var baselineInstances = baselineValue.Observed ? baselineValue.Instances : 0;
        var currentInstances = currentValue.Observed ? currentValue.Instances : 0;
        long bytesDelta;
        long instancesDelta;
        try
        {
            bytesDelta = checked(currentBytes - baselineBytes);
            instancesDelta = checked(currentInstances - baselineInstances);
        }
        catch (OverflowException)
        {
            unavailable.Add(new("retainedBytesDelta", "signed_delta_overflow"));
            unavailable.Add(new("retainedInstancesDelta", "signed_delta_overflow"));
            limitations.Add("The selected retained quantities overflowed a signed Int64 delta.");
            return new(
                attempt.AttemptId,
                attempt.Outcome,
                RetainedGrowthEvidenceAvailability.Incompatible,
                attempt.BaselineHandle,
                attempt.CurrentHandle,
                current.ProcessId,
                baseline.CapturedAt,
                current.CapturedAt,
                baselineBytes,
                currentBytes,
                null,
                baselineInstances,
                currentInstances,
                null,
                identity,
                [],
                rootPaths,
                observerEffects,
                unavailable,
                assumptions,
                limitations);
        }

        return new(
            attempt.AttemptId,
            attempt.Outcome,
            RetainedGrowthEvidenceAvailability.Available,
            attempt.BaselineHandle,
            attempt.CurrentHandle,
            current.ProcessId,
            baseline.CapturedAt,
            current.CapturedAt,
            baselineBytes,
            currentBytes,
            bytesDelta,
            baselineInstances,
            currentInstances,
            instancesDelta,
            identity,
            [],
            rootPaths,
            observerEffects,
            [],
            assumptions,
            limitations);
    }

    private static RetainedGrowthPairEvidence ApplyCrossAttemptCompatibility(
        RetainedGrowthPairEvidence pair,
        RetainedGrowthCompatibilityIdentity canonical)
    {
        if (pair.Availability != RetainedGrowthEvidenceAvailability.Available
            || pair.CompatibilityIdentity is not { } actual)
        {
            return pair;
        }

        var mismatches = CompareIdentities(pair.AttemptId, canonical, actual);
        if (mismatches.Count == 0)
        {
            return pair;
        }

        return pair with
        {
            Availability = RetainedGrowthEvidenceAvailability.Incompatible,
            RetainedBytesDelta = null,
            RetainedInstancesDelta = null,
            CompatibilityMismatches = mismatches,
            UnavailableFields =
            [
                new("retainedBytesDelta", "cross_attempt_compatibility_mismatch"),
                new("retainedInstancesDelta", "cross_attempt_compatibility_mismatch"),
            ],
            Limitations = [.. pair.Limitations, "This pair was not pooled with the canonical compatible process-run identity."],
        };
    }

    private static RetainedGrowthPairEvidence[] ApplyProcessRunIndependence(
        RetainedGrowthPairEvidence[] pairs)
    {
        var processIds = new HashSet<int>();
        var result = new RetainedGrowthPairEvidence[pairs.Length];
        for (var index = 0; index < pairs.Length; index++)
        {
            var pair = pairs[index];
            if (pair.Availability != RetainedGrowthEvidenceAvailability.Available
                || pair.ProcessId is not { } processId
                || processIds.Add(processId))
            {
                result[index] = pair;
                continue;
            }

            result[index] = pair with
            {
                Availability = RetainedGrowthEvidenceAvailability.Incompatible,
                RetainedBytesDelta = null,
                RetainedInstancesDelta = null,
                CompatibilityMismatches =
                [
                    .. pair.CompatibilityMismatches,
                    new(
                        pair.AttemptId,
                        "processRun.processId",
                        "unique_across_accepted_pairs",
                        processId.ToString(CultureInfo.InvariantCulture),
                        "cross_attempt_process_id_reused"),
                ],
                UnavailableFields =
                [
                    new("retainedBytesDelta", "process_run_independence_not_established"),
                    new("retainedInstancesDelta", "process_run_independence_not_established"),
                ],
                Limitations =
                [
                    .. pair.Limitations,
                    "This pair reused a process id already accepted in the bounded window, so process-run independence was not established.",
                ],
            };
        }
        return result;
    }

    private static RetainedGrowthRepeatedEvidence BuildRepeated(
        IReadOnlyList<RetainedGrowthPairEvidence> pairs,
        RetainedGrowthPairEvidence[] accepted)
    {
        var unavailable = new List<RetainedGrowthUnavailableField>();
        var assumptions = new[]
        {
            "Accepted process runs are independent and exchangeable for the bounded median and bootstrap calculations.",
            "Whole process-run pairs, never individual heap rows or events, are the resampling unit.",
        };
        var limitations = new List<string>
        {
            "The percentile interval describes deterministic resampling variability and not systematic capture bias.",
        };
        if (pairs.Any(static pair => pair.Outcome != RetainedGrowthAttemptOutcome.Completed))
        {
            limitations.Add("Failed and aborted attempts remain in the attempt denominator and are not bootstrap inputs.");
        }

        if (accepted.Length < RetainedGrowthEvidenceBounds.MinimumRobustPairs)
        {
            const string reason = "minimum_three_compatible_process_run_pairs_not_met";
            unavailable.Add(new("medianRetainedBytesDelta", reason));
            unavailable.Add(new("medianRetainedInstancesDelta", reason));
            unavailable.Add(new("retainedBytesDeltaMad", reason));
            unavailable.Add(new("retainedInstancesDeltaMad", reason));
            unavailable.Add(new("retainedBytesDeltaInterval", reason));
            unavailable.Add(new("retainedInstancesDeltaInterval", reason));
            var primary = pairs.Any(static pair => pair.Availability == RetainedGrowthEvidenceAvailability.LossDetected)
                ? RetainedGrowthEvidenceAvailability.LossDetected
                : pairs.Any(static pair => pair.Availability == RetainedGrowthEvidenceAvailability.UnsupportedApproximation)
                    ? RetainedGrowthEvidenceAvailability.UnsupportedApproximation
                    : pairs.Any(static pair => pair.Availability == RetainedGrowthEvidenceAvailability.Incompatible)
                        ? RetainedGrowthEvidenceAvailability.Incompatible
                        : RetainedGrowthEvidenceAvailability.InsufficientObservations;
            return new(
                primary,
                MedianMethod,
                MadMethod,
                IntervalMethod,
                IndependenceUnit,
                accepted.Length,
                accepted.Count(static pair =>
                    pair.RetainedBytesDelta > 0 && pair.RetainedInstancesDelta > 0),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                unavailable,
                assumptions,
                limitations);
        }

        var bytes = accepted.Select(static pair => (double)pair.RetainedBytesDelta!.Value).ToArray();
        var instances = accepted.Select(static pair => (double)pair.RetainedInstancesDelta!.Value).ToArray();
        var orderedInputSha256 = CanonicalInputSha256(accepted);
        var bytesBootstrap = Bootstrap(bytes, orderedInputSha256, "bytes");
        var instancesBootstrap = Bootstrap(instances, orderedInputSha256, "instances");
        return new(
            RetainedGrowthEvidenceAvailability.Available,
            MedianMethod,
            MadMethod,
            IntervalMethod,
            IndependenceUnit,
            accepted.Length,
            accepted.Count(static pair =>
                pair.RetainedBytesDelta > 0 && pair.RetainedInstancesDelta > 0),
            Median(bytes),
            Median(instances),
            MedianAbsoluteDeviation(bytes),
            MedianAbsoluteDeviation(instances),
            bytesBootstrap.Interval,
            instancesBootstrap.Interval,
            orderedInputSha256,
            bytesBootstrap.SeedSha256,
            instancesBootstrap.SeedSha256,
            RetainedGrowthEvidenceBounds.BootstrapResamples,
            [],
            assumptions,
            limitations);
    }

    private static RetainedGrowthAllocationEvidence BuildAllocationEvidence(
        IReadOnlyList<RetainedGrowthEvidenceAttempt> attempts)
    {
        var observations = attempts
            .Where(static attempt => attempt.Allocation is not null)
            .Select(static attempt => attempt.Allocation!)
            .ToArray();
        var assumptions = new[]
        {
            "Sampled allocation bytes describe traffic during a bounded capture and are not retained heap bytes.",
            "Allocation observations are comparable only when their sampler configuration and workload window match.",
        };
        if (observations.Length == 0)
        {
            return new(
                RetainedGrowthEvidenceAvailability.NotApplicable,
                MedianMethod,
                "sampled-bytes",
                null,
                0,
                0,
                0,
                "not-supplied",
                [new("medianSampledBytes", "allocation_evidence_not_supplied")],
                assumptions,
                ["The heap comparison path does not synthesize allocation traffic from retained heap deltas."]);
        }

        var lost = observations.Sum(static observation => observation.LostOrDropped);
        if (lost > 0)
        {
            return new(
                RetainedGrowthEvidenceAvailability.LossDetected,
                MedianMethod,
                "sampled-bytes",
                null,
                observations.Sum(static observation => observation.SampleEvents),
                lost,
                observations.Length,
                observations.Select(static observation => observation.Representation).Distinct(StringComparer.Ordinal).Count() == 1
                    ? observations[0].Representation
                    : "mixed",
                [new("medianSampledBytes", "allocation_source_loss_detected")],
                assumptions,
                ["Allocation loss invalidates the bounded traffic estimate but does not change retained-growth evidence."]);
        }

        return new(
            RetainedGrowthEvidenceAvailability.Available,
            MedianMethod,
            "sampled-bytes",
            Median(observations.Select(static observation => (double)observation.SampledBytes)),
            observations.Sum(static observation => observation.SampleEvents),
            0,
            observations.Length,
            observations.Select(static observation => observation.Representation).Distinct(StringComparer.Ordinal).Count() == 1
                ? observations[0].Representation
                : "mixed",
            [],
            assumptions,
            ["GCAllocationTick traffic is sampled and approximate; it is reported separately from exact selected-type heap deltas."]);
    }

    private static RetainedGrowthRootPathEvidence SummarizeRootPaths(
        IReadOnlyList<RetainedGrowthPairEvidence> pairs)
    {
        var evidence = pairs.Select(static pair => pair.RootPathEvidence).ToArray();
        var totalCaptures = evidence.Sum(static item => item.TotalCaptures);
        var withPaths = evidence.Sum(static item => item.CapturesWithPaths);
        var matchingPaths = evidence
            .Where(static item => item.MatchingPathCount.HasValue)
            .Sum(static item => item.MatchingPathCount!.Value);
        var availability = evidence.All(static item =>
                item.Availability == RetainedGrowthRootPathAvailability.UnavailableByMechanism)
            ? RetainedGrowthRootPathAvailability.UnavailableByMechanism
            : evidence.All(static item =>
                item.Availability == RetainedGrowthRootPathAvailability.Available)
                ? RetainedGrowthRootPathAvailability.Available
                : evidence.All(static item =>
                    item.Availability == RetainedGrowthRootPathAvailability.NotCollected)
                    ? RetainedGrowthRootPathAvailability.NotCollected
                    : RetainedGrowthRootPathAvailability.IncompleteEvidence;
        var reason = availability switch
        {
            RetainedGrowthRootPathAvailability.Available => null,
            RetainedGrowthRootPathAvailability.UnavailableByMechanism => "selected_backend_has_no_root_or_path_graph",
            RetainedGrowthRootPathAvailability.NotCollected => "retention_paths_not_collected",
            _ => "root_or_path_evidence_incomplete",
        };
        return new(
            availability,
            withPaths,
            totalCaptures,
            availability == RetainedGrowthRootPathAvailability.Available ? matchingPaths : null,
            reason);
    }

    private static RetainedGrowthObserverEffects SummarizeObserverEffects(
        IReadOnlyList<RetainedGrowthPairEvidence> pairs)
    {
        var gcdumpCaptures = pairs.Sum(static pair => pair.ObserverEffects.GcDumpCaptures);
        var gcdumpPairs = pairs.Where(static pair => pair.ObserverEffects.GcDumpCaptures > 0).ToArray();
        int? observed = gcdumpPairs.Length > 0
            && gcdumpPairs.All(static pair => pair.ObserverEffects.ObservedGen2Collections.HasValue)
                ? gcdumpPairs.Sum(static pair => pair.ObserverEffects.ObservedGen2Collections!.Value)
                : null;
        return new(
            gcdumpCaptures,
            observed,
            observed.HasValue
                ? "caller-observed-gen2-count"
                : gcdumpCaptures > 0
                    ? "gcdump-capture-provenance"
                    : "no-gcdump-capture",
            gcdumpCaptures > 0
                ? ["Each gcdump capture induces a blocking Gen2 collection and can change the observed heap."]
                : ["ClrMD live/dump reads can suspend or observe a changing target and are not neutral measurements."]);
    }

    private static RetainedGrowthPairEvidence UnavailablePair(
        RetainedGrowthEvidenceAttempt attempt,
        RetainedGrowthEvidenceAvailability availability,
        IReadOnlyList<RetainedGrowthUnavailableField> unavailable,
        IReadOnlyList<string> limitations,
        IReadOnlyList<string> assumptions)
        => new(
            attempt.AttemptId,
            attempt.Outcome,
            availability,
            attempt.BaselineHandle,
            attempt.CurrentHandle,
            attempt.Current?.ProcessId ?? attempt.Baseline?.ProcessId,
            attempt.Baseline?.CapturedAt,
            attempt.Current?.CapturedAt,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            new(RetainedGrowthRootPathAvailability.IncompleteEvidence, 0, 0, null, "complete_pair_required"),
            PairObserverEffects(attempt, attempt.Baseline, attempt.Current),
            unavailable,
            assumptions,
            limitations);

    private static (RetainedGrowthEvidenceAvailability Availability, string Reason)? GetQualityAvailability(
        HeapSnapshotArtifact baseline,
        HeapSnapshotArtifact current,
        List<string> limitations)
    {
        foreach (var snapshot in new[] { baseline, current })
        {
            var quality = GcDumpEvidence.GetApplicableQuality(snapshot);
            if (quality is null)
            {
                continue;
            }
            foreach (var limitation in quality.Limitations)
            {
                limitations.Add($"{limitation.Scope}: {limitation.Detail}");
            }
            if (quality.Limitations.Any(static limitation =>
                    limitation.Category is EvidenceLimitationCategory.DetectedTransportLoss
                        or EvidenceLimitationCategory.ProcessingFailure))
            {
                return (RetainedGrowthEvidenceAvailability.LossDetected, "capture_loss_or_processing_failure");
            }
            if (quality.Conclusions.RetainedExplicitPositiveEvidence
                != EvidenceConclusionSupport.Supported)
            {
                return (RetainedGrowthEvidenceAvailability.UnsupportedApproximation, "retained_positive_evidence_not_supported");
            }
        }

        return null;
    }

    private static List<RetainedGrowthIdentityMismatch> ValidatePairCompatibility(
        string attemptId,
        HeapSnapshotArtifact baseline,
        HeapSnapshotArtifact current)
    {
        var mismatches = new List<RetainedGrowthIdentityMismatch>();
        AddMismatch(mismatches, attemptId, "processId",
            baseline.ProcessId.ToString(CultureInfo.InvariantCulture),
            current.ProcessId.ToString(CultureInfo.InvariantCulture),
            "baseline_and_current_must_describe_one_process_run");
        AddMismatch(mismatches, attemptId, "backend", baseline.Origin.ToString(), current.Origin.ToString(),
            "heap_snapshot_origins_must_match");
        AddMismatch(mismatches, attemptId, "runtime.name", baseline.Runtime.Name, current.Runtime.Name,
            "runtime_names_must_match");
        AddMismatch(mismatches, attemptId, "runtime.version", baseline.Runtime.Version, current.Runtime.Version,
            "runtime_versions_must_match");
        AddMismatch(mismatches, attemptId, "runtime.architecture", baseline.Runtime.Architecture, current.Runtime.Architecture,
            "runtime_architectures_must_match");
        if (current.CapturedAt < baseline.CapturedAt)
        {
            mismatches.Add(new(
                attemptId,
                "capture.chronology",
                baseline.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
                current.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
                "current_capture_precedes_baseline"));
        }
        return mismatches;
    }

    private static List<RetainedGrowthIdentityMismatch> CompareIdentities(
        string attemptId,
        RetainedGrowthCompatibilityIdentity expected,
        RetainedGrowthCompatibilityIdentity actual)
    {
        var mismatches = new List<RetainedGrowthIdentityMismatch>();
        AddMismatch(mismatches, attemptId, "runtime.name", expected.RuntimeName, actual.RuntimeName, "cross_attempt_runtime_name_mismatch");
        AddMismatch(mismatches, attemptId, "runtime.version", expected.RuntimeVersion, actual.RuntimeVersion, "cross_attempt_runtime_version_mismatch");
        AddMismatch(mismatches, attemptId, "runtime.architecture", expected.Architecture, actual.Architecture, "cross_attempt_architecture_mismatch");
        AddMismatch(mismatches, attemptId, "backend", expected.Backend, actual.Backend, "cross_attempt_backend_mismatch");
        AddMismatch(mismatches, attemptId, "target.type", IdentityKey(expected.TargetType), IdentityKey(actual.TargetType), "cross_attempt_target_type_mismatch");
        AddMismatch(mismatches, attemptId, "quantity.representation", expected.QuantityRepresentation, actual.QuantityRepresentation, "cross_attempt_quantity_mismatch");
        AddMismatch(mismatches, attemptId, "capture.settings", expected.CaptureSettings, actual.CaptureSettings, "cross_attempt_capture_settings_mismatch");
        AddMismatch(mismatches, attemptId, "independence.unit", expected.IndependenceUnit, actual.IndependenceUnit, "cross_attempt_independence_unit_mismatch");
        AddMismatch(mismatches, attemptId, "comparison.axis", expected.ComparisonAxis, actual.ComparisonAxis, "cross_attempt_comparison_axis_mismatch");
        return mismatches;
    }

    private static void AddMismatch(
        List<RetainedGrowthIdentityMismatch> mismatches,
        string attemptId,
        string dimension,
        string expected,
        string actual,
        string reason)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            mismatches.Add(new(attemptId, dimension, expected, actual, reason));
        }
    }

    private static RetainedGrowthCompatibilityIdentity BuildIdentity(
        TypeIdentity targetType,
        HeapSnapshotArtifact snapshot)
        => new(
            snapshot.Runtime.Name,
            snapshot.Runtime.Version,
            snapshot.Runtime.Architecture,
            snapshot.Origin.ToString(),
            targetType,
            QuantityRepresentation,
            CaptureSettings,
            IndependenceUnit,
            ComparisonAxis);

    private static SelectedType SelectType(TypeIdentity targetType, HeapSnapshotArtifact snapshot)
    {
        var selection = HeapSnapshotComparableProjector.FindUniqueBestMatch(
            targetType,
            HeapSnapshotComparableProjector.ProjectTypedByAvailableIdentity(snapshot));
        return selection.Match is { } match
            ? new(true, selection.Ambiguous, match.Metric.TotalBytes, match.Metric.InstanceCount)
            : new(false, selection.Ambiguous, 0, 0);
    }

    private static bool SupportsSelectedTypeAbsence(HeapSnapshotArtifact snapshot)
        => snapshot.Origin == HeapSnapshotOrigin.GcDump
            && GcDumpEvidence.SupportsAbsence(snapshot);

    private static RetainedGrowthRootPathEvidence GetRootPathEvidence(
        TypeIdentity targetType,
        HeapSnapshotArtifact snapshot)
    {
        if (snapshot.Origin == HeapSnapshotOrigin.GcDump)
        {
            return new(
                RetainedGrowthRootPathAvailability.UnavailableByMechanism,
                0,
                1,
                null,
                "gcdump_has_no_root_or_path_graph");
        }
        if (snapshot.RetentionPaths is not { Count: > 0 } paths)
        {
            return new(
                RetainedGrowthRootPathAvailability.NotCollected,
                0,
                1,
                null,
                "retention_paths_not_collected_or_empty");
        }

        var matches = paths.Count(path =>
        {
            var identity = path.TargetIdentity is { } known
                ? known with { TypeFullName = path.TargetTypeFullName }
                : new TypeIdentity(path.TargetTypeFullName);
            return HeapSnapshotComparableProjector.GetMatchScore(targetType, identity) > 0;
        });
        return matches > 0
            ? new(RetainedGrowthRootPathAvailability.Available, 1, 1, matches, null)
            : new(
                RetainedGrowthRootPathAvailability.IncompleteEvidence,
                0,
                1,
                null,
                "retention_paths_present_but_selected_type_not_attributed");
    }

    private static RetainedGrowthObserverEffects PairObserverEffects(
        RetainedGrowthEvidenceAttempt attempt,
        HeapSnapshotArtifact? baseline,
        HeapSnapshotArtifact? current)
    {
        var gcdumpCaptures = (baseline?.Origin == HeapSnapshotOrigin.GcDump ? 1 : 0)
            + (current?.Origin == HeapSnapshotOrigin.GcDump ? 1 : 0);
        return new(
            gcdumpCaptures,
            attempt.ObservedGen2Collections,
            attempt.ObservedGen2Collections.HasValue
                ? "caller-observed-gen2-count"
                : gcdumpCaptures > 0
                    ? "gcdump-capture-provenance"
                    : "no-gcdump-capture",
            gcdumpCaptures > 0
                ? ["Each gcdump capture induces a blocking Gen2 collection and can change the observed heap."]
                : ["The observer effect was not quantified for this non-gcdump pair."]);
    }

    private static string CanonicalInputSha256(
        RetainedGrowthPairEvidence[] pairs)
    {
        var input = new StringBuilder();
        foreach (var pair in pairs.OrderBy(static pair => pair.AttemptId, StringComparer.Ordinal))
        {
            input.Append(pair.AttemptId).Append('\n')
                .Append(pair.RetainedBytesDelta!.Value.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append(pair.RetainedInstancesDelta!.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return HexLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())));
    }

    private static (RetainedGrowthInterval Interval, string SeedSha256) Bootstrap(
        double[] values,
        string inputSha256,
        string quantity)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "|",
            IntervalMethod.Version.ToString(CultureInfo.InvariantCulture),
            inputSha256,
            quantity,
            RetainedGrowthEvidenceResult.SchemaV1)));
        var random = new Xoshiro256StarStar(seed);
        var resamples = new double[RetainedGrowthEvidenceBounds.BootstrapResamples];
        var buffer = new double[values.Length];
        for (var resample = 0; resample < resamples.Length; resample++)
        {
            for (var index = 0; index < buffer.Length; index++)
            {
                buffer[index] = values[random.NextInt(values.Length)];
            }
            resamples[resample] = Median(buffer);
        }

        Array.Sort(resamples);
        var alpha = (1 - RetainedGrowthEvidenceBounds.ConfidenceLevel) / 2;
        var interval = new RetainedGrowthInterval(
            QuantileR7(resamples, alpha),
            QuantileR7(resamples, 1 - alpha),
            RetainedGrowthEvidenceBounds.ConfidenceLevel,
            Median(values));
        return (interval, HexLower(seed));
    }

    private static double MedianAbsoluteDeviation(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        var median = Median(materialized);
        return Median(materialized.Select(value => Math.Abs(value - median)));
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return double.NaN;
        }
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : ordered[middle - 1] / 2d + ordered[middle] / 2d;
    }

    private static double QuantileR7(double[] sortedValues, double probability)
    {
        if (sortedValues.Length == 1)
        {
            return sortedValues[0];
        }
        var position = (sortedValues.Length - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }
        var fraction = position - lower;
        return sortedValues[lower] + fraction * (sortedValues[upper] - sortedValues[lower]);
    }

    private static string IdentityKey(TypeIdentity identity)
        => string.Join(
            "|",
            identity.TypeFullName,
            identity.ModuleVersionId?.ToString("D", CultureInfo.InvariantCulture),
            identity.MetadataToken?.ToString(CultureInfo.InvariantCulture),
            identity.ModuleName,
            identity.ModulePath);

    private static string HexLower(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();

    private readonly record struct SelectedType(
        bool Observed,
        bool Ambiguous,
        long Bytes,
        long Instances);

    private sealed class Xoshiro256StarStar
    {
        private ulong _s0;
        private ulong _s1;
        private ulong _s2;
        private ulong _s3;

        public Xoshiro256StarStar(ReadOnlySpan<byte> seed)
        {
            _s0 = BinaryPrimitives.ReadUInt64LittleEndian(seed);
            _s1 = BinaryPrimitives.ReadUInt64LittleEndian(seed[8..]);
            _s2 = BinaryPrimitives.ReadUInt64LittleEndian(seed[16..]);
            _s3 = BinaryPrimitives.ReadUInt64LittleEndian(seed[24..]);
            if ((_s0 | _s1 | _s2 | _s3) == 0)
            {
                _s0 = 1;
            }
        }

        public int NextInt(int exclusiveMaximum)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
            var bound = (ulong)exclusiveMaximum;
            var threshold = unchecked(0UL - bound) % bound;
            ulong value;
            do
            {
                value = NextUInt64();
            }
            while (value < threshold);
            return (int)(value % bound);
        }

        private ulong NextUInt64()
        {
            var result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
            var temporary = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= temporary;
            _s3 = BitOperations.RotateLeft(_s3, 45);
            return result;
        }
    }
}
