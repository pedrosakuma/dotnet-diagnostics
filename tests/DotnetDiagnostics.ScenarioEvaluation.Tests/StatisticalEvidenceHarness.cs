using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ScenarioStatisticalEvidenceHarness
{
    private const int CurrentResultSchemaVersion = 1;
    private const string BootstrapSeedDerivation =
        "sha256(method-version|compatibility-identity|ordered-input|scenario-protocol)-v1";
    private const string BootstrapPrng = "xoshiro256-star-star-v1";
    private const string BootstrapQuantileRule = "linear-r7-v1";
    internal static IReadOnlyList<string> RequiredIdentityDimensions { get; } =
    [
        "target.runtime-flavor",
        "target.runtime-version-policy",
        "target.os",
        "target.architecture",
        "target.process-run-id",
        "backend.id",
        "backend.version",
        "quantity.id",
        "quantity.unit",
        "quantity.aggregation",
        "quantity.normalization",
        "quantity.filters",
        "quantity.representation",
        "capture.duration",
        "capture.windowing",
        "capture.providers",
        "capture.sampling-period",
        "capture.retention",
        "capture.enrichment",
        "workload.scenario",
        "workload.version",
        "workload.parameters",
        "workload.concurrency",
        "workload.deployment",
        "independence.unit",
        "independence.nesting",
        "independence.pairing-key",
        "comparison.axis",
    ];

    private static readonly Dictionary<string, MethodRule> MethodRules =
        new Dictionary<string, MethodRule>(StringComparer.Ordinal)
        {
            [ScenarioStatisticalMethodIds.WindowMedian] = new(
                1, false,
                [
                    ScenarioStatisticalIndependenceUnit.BoundedTimeWindow,
                    ScenarioStatisticalIndependenceUnit.Capture,
                    ScenarioStatisticalIndependenceUnit.ProcessRun,
                    ScenarioStatisticalIndependenceUnit.EnvironmentAllocation,
                ],
                ["estimate"],
                ["estimate"]),
            [ScenarioStatisticalMethodIds.PairedDelta] = new(
                1, true,
                [
                    ScenarioStatisticalIndependenceUnit.Capture,
                    ScenarioStatisticalIndependenceUnit.ProcessRun,
                    ScenarioStatisticalIndependenceUnit.EnvironmentAllocation,
                ],
                ["baseline_estimate", "candidate_estimate", "absolute_delta", "relative_delta"],
                ["delta"]),
            [ScenarioStatisticalMethodIds.MedianAbsoluteDeviation] = new(
                2, false,
                [
                    ScenarioStatisticalIndependenceUnit.BoundedTimeWindow,
                    ScenarioStatisticalIndependenceUnit.Capture,
                    ScenarioStatisticalIndependenceUnit.ProcessRun,
                    ScenarioStatisticalIndependenceUnit.EnvironmentAllocation,
                ],
                ["dispersion"],
                ["dispersion"]),
            [ScenarioStatisticalMethodIds.WilsonScoreRate] = new(
                1, false,
                [
                    ScenarioStatisticalIndependenceUnit.Capture,
                    ScenarioStatisticalIndependenceUnit.ProcessRun,
                    ScenarioStatisticalIndependenceUnit.EnvironmentAllocation,
                ],
                ["rate", "interval"],
                ["rate", "interval"]),
            [ScenarioStatisticalMethodIds.TheilSenSlope] = new(
                2, false,
                [
                    ScenarioStatisticalIndependenceUnit.BoundedTimeWindow,
                    ScenarioStatisticalIndependenceUnit.Capture,
                    ScenarioStatisticalIndependenceUnit.ProcessRun,
                    ScenarioStatisticalIndependenceUnit.EnvironmentAllocation,
                ],
                ["slope"],
                ["slope"]),
            [ScenarioStatisticalMethodIds.PercentileBootstrapInterval] = new(
                2, false,
                [
                    ScenarioStatisticalIndependenceUnit.BoundedTimeWindow,
                    ScenarioStatisticalIndependenceUnit.Capture,
                    ScenarioStatisticalIndependenceUnit.ProcessRun,
                    ScenarioStatisticalIndependenceUnit.EnvironmentAllocation,
                ],
                ["interval"],
                ["interval"]),
        };

    public static IReadOnlyList<ScenarioStatisticalMethodDefinition> RegistryDefinitions { get; } =
        MethodRules
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new ScenarioStatisticalMethodDefinition(
                item.Key,
                1,
                item.Value.MinimumInputCount,
                item.Value.AllowedIndependenceUnits.Order().ToArray(),
                item.Value.RequiresPairing,
                item.Value.Produces,
                EmitsVerdict: false))
            .ToArray();

    public static ScenarioStatisticalEvidenceResult Evaluate(
        ScenarioStatisticalEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBounds(request);
        var protocol = request.Protocol;
        var counts = CountRaw(request.Arms);
        var emptyProvenance = EmptyProvenance();
        var emptyAssumptions = Assumptions(protocol);
        var baseLimitations = BaseLimitations(request.Arms, counts);

        if (!MethodRules.TryGetValue(protocol.Method.Id, out var rule)
            || protocol.Method.Version != 1)
        {
            return Unavailable(
                protocol,
                counts,
                ScenarioStatisticalAvailability.Incompatible,
                ["method"],
                "unsupported_method_or_version",
                null,
                emptyAssumptions,
                Append(baseLimitations, "The requested method identity is not registered in v1."),
                emptyProvenance);
        }

        var operands = SelectOperands(request, rule);
        if (operands.NotApplicableReason is not null)
        {
            return Unavailable(
                protocol,
                counts,
                ScenarioStatisticalAvailability.NotApplicable,
                rule.OutputFields,
                operands.NotApplicableReason,
                null,
                emptyAssumptions,
                baseLimitations,
                emptyProvenance);
        }

        var identityValidation = ValidateIdentities(protocol, operands.Arms);
        if (identityValidation.Reason is not null)
        {
            return Unavailable(
                protocol,
                counts,
                ScenarioStatisticalAvailability.Incompatible,
                rule.OutputFields,
                identityValidation.Reason,
                identityValidation.Comparison,
                emptyAssumptions,
                Append(baseLimitations, identityValidation.Detail!),
                emptyProvenance);
        }

        if (!rule.AllowedIndependenceUnits.Contains(protocol.IndependenceUnit)
            || protocol.IndependenceUnit == ScenarioStatisticalIndependenceUnit.Event)
        {
            return Unavailable(
                protocol,
                counts,
                ScenarioStatisticalAvailability.Incompatible,
                rule.OutputFields,
                "independence_unit_not_allowed",
                identityValidation.Comparison,
                emptyAssumptions,
                Append(baseLimitations, "Event-level independence is rejected without a separate demonstration."),
                emptyProvenance);
        }

        var normalized = Normalize(protocol, operands.Arms);
        counts = counts with
        {
            AcceptedObservations = normalized.Observations.Count,
            ExcludedNonfinite = normalized.ExcludedNonfinite,
            CollapsedCorrelatedDuplicates = normalized.CollapsedDuplicates,
        };
        baseLimitations = BaseLimitations(request.Arms, counts);
        if (normalized.IncompatibleReason is not null)
        {
            return Unavailable(
                protocol,
                counts,
                ScenarioStatisticalAvailability.Incompatible,
                rule.OutputFields,
                normalized.IncompatibleReason,
                identityValidation.Comparison,
                emptyAssumptions,
                Append(baseLimitations, normalized.IncompatibleDetail!),
                emptyProvenance);
        }

        var canonical = Canonicalize(request, operands.Arms, normalized.Observations);
        var provenance = new ScenarioStatisticalCalculationProvenance(
            canonical.InputDigest,
            canonical.IdentityDigest,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            []);

        if (counts.LostOrDropped > 0 && !protocol.AdmitLoss)
        {
            return Unavailable(
                protocol,
                counts,
                ScenarioStatisticalAvailability.LossDetected,
                rule.OutputFields,
                "source_loss_detected",
                identityValidation.Comparison,
                emptyAssumptions,
                baseLimitations,
                provenance);
        }

        if (normalized.Observations.Any(observation =>
                observation.Observation.Representation != ScenarioStatisticalRepresentation.Exact)
            && !protocol.AdmitApproximation)
        {
            return Unavailable(
                protocol,
                counts,
                ScenarioStatisticalAvailability.UnsupportedApproximation,
                rule.OutputFields,
                "approximate_representation_not_admitted",
                identityValidation.Comparison,
                emptyAssumptions,
                baseLimitations,
                provenance);
        }

        return protocol.Method.Id switch
        {
            ScenarioStatisticalMethodIds.WindowMedian => EvaluateMedian(
                protocol, counts, normalized.Observations, identityValidation.Comparison,
                emptyAssumptions, baseLimitations, provenance),
            ScenarioStatisticalMethodIds.PairedDelta => EvaluatePairedDelta(
                protocol, counts, normalized.Observations, identityValidation.Comparison,
                emptyAssumptions, baseLimitations, provenance),
            ScenarioStatisticalMethodIds.MedianAbsoluteDeviation => EvaluateMad(
                protocol, counts, normalized.Observations, identityValidation.Comparison,
                emptyAssumptions, baseLimitations, provenance),
            ScenarioStatisticalMethodIds.WilsonScoreRate => EvaluateWilson(
                protocol, counts, normalized.Observations, identityValidation.Comparison,
                emptyAssumptions, baseLimitations, provenance),
            ScenarioStatisticalMethodIds.TheilSenSlope => EvaluateTheilSen(
                protocol, counts, normalized.Observations, identityValidation.Comparison,
                emptyAssumptions, baseLimitations, provenance),
            ScenarioStatisticalMethodIds.PercentileBootstrapInterval => EvaluateBootstrap(
                protocol, counts, normalized.Observations, identityValidation.Comparison,
                emptyAssumptions, baseLimitations, provenance, canonical),
            _ => throw new UnreachableException(),
        };
    }

    public static ScenarioStatisticalEvidenceRequest ProjectLiveTrialArtifacts(
        ScenarioStatisticalProtocol protocol,
        ScenarioStatisticalCompatibilityIdentity compatibilityIdentity,
        IReadOnlyList<ScenarioTrialArtifact> artifacts,
        string metricName,
        string armId = "observed",
        string? environmentAllocationId = null)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(compatibilityIdentity);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        ArgumentException.ThrowIfNullOrWhiteSpace(armId);
        if (artifacts.Count > ScenarioStatisticalEvidenceBounds.MaximumRepetitions)
        {
            throw new InvalidDataException(
                $"Live trial projection exceeds {ScenarioStatisticalEvidenceBounds.MaximumRepetitions} repetitions.");
        }
        if (protocol.IndependenceUnit == ScenarioStatisticalIndependenceUnit.BoundedTimeWindow)
        {
            throw new InvalidDataException(
                "Trial artifacts contain capture-level metrics, not bounded time-window observations.");
        }
        if (protocol.IndependenceUnit == ScenarioStatisticalIndependenceUnit.EnvironmentAllocation
            && string.IsNullOrWhiteSpace(environmentAllocationId))
        {
            throw new InvalidDataException(
                "Live environment-allocation evidence requires a caller-supplied allocation identity.");
        }

        var attempts = new List<ScenarioStatisticalAttempt>(artifacts.Count);
        var observations = new List<ScenarioStatisticalObservation>(artifacts.Count);
        foreach (var artifact in artifacts.OrderBy(item => item.Trial).ThenBy(item => item.Attempt))
        {
            var repetitionId = artifact.Trial.ToString(CultureInfo.InvariantCulture);
            var attemptId = artifact.Attempt.ToString(CultureInfo.InvariantCulture);
            var metric = artifact.Evidence?.Metrics.SingleOrDefault(item =>
                string.Equals(item.Name, metricName, StringComparison.Ordinal));
            var passed = artifact.Outcome == ScenarioTrialArtifactOutcome.Passed
                && metric is not null;
            attempts.Add(new(
                repetitionId,
                attemptId,
                passed
                    ? ScenarioStatisticalAttemptOutcome.Passed
                    : ScenarioStatisticalAttemptOutcome.Failed,
                passed ? null : artifact.FailureKind.ToString(),
                artifact.Detail));
            if (!passed)
            {
                continue;
            }

            var captureId = $"trial-{artifact.Trial}-capture";
            var allocationId = environmentAllocationId ?? "current-environment";
            var independenceUnitId = protocol.IndependenceUnit switch
            {
                ScenarioStatisticalIndependenceUnit.Capture => captureId,
                ScenarioStatisticalIndependenceUnit.ProcessRun => $"trial-{artifact.Trial}",
                ScenarioStatisticalIndependenceUnit.EnvironmentAllocation => allocationId,
                _ => throw new InvalidDataException(
                    $"Live trial projection does not support {protocol.IndependenceUnit}."),
            };
            observations.Add(new(
                $"trial-{artifact.Trial}-attempt-{artifact.Attempt}-{metricName}",
                repetitionId,
                attemptId,
                allocationId,
                captureId,
                null,
                independenceUnitId,
                metric!.Value,
                null,
                SourceEventCount: 1,
                LostOrDropped: 0,
                ScenarioStatisticalRepresentation.Exact));
        }

        return new(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            protocol,
            [new ScenarioStatisticalArm(
                armId,
                ScenarioStatisticalArmRole.Observed,
                compatibilityIdentity,
                attempts,
                observations)],
            armId);
    }

    private static ScenarioStatisticalEvidenceResult EvaluateMedian(
        ScenarioStatisticalProtocol protocol,
        ScenarioStatisticalCounts counts,
        IReadOnlyList<NormalizedObservation> observations,
        ScenarioStatisticalCompatibilityComparison? compatibility,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> limitations,
        ScenarioStatisticalCalculationProvenance provenance)
    {
        var values = observations.Select(item => item.Observation.Value).ToArray();
        counts = counts with { IndependenceUnits = values.Length };
        var minimum = Math.Max(1, protocol.MinimumIndependenceUnits);
        if (values.Length < minimum)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["estimate"], "minimum_independence_units_not_met", compatibility,
                assumptions, limitations, provenance);
        }

        var estimate = Median(values);
        if (!double.IsFinite(estimate))
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["estimate"], "nonfinite_calculation_result", compatibility,
                assumptions, Append(limitations, "The finite inputs produced a nonfinite median."),
                provenance);
        }

        return Available(
            protocol, counts, estimate, null, null, null, null, null, [],
            compatibility, assumptions, limitations, provenance);
    }

    private static ScenarioStatisticalEvidenceResult EvaluateMad(
        ScenarioStatisticalProtocol protocol,
        ScenarioStatisticalCounts counts,
        IReadOnlyList<NormalizedObservation> observations,
        ScenarioStatisticalCompatibilityComparison? compatibility,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> limitations,
        ScenarioStatisticalCalculationProvenance provenance)
    {
        var values = observations.Select(item => item.Observation.Value).ToArray();
        counts = counts with { IndependenceUnits = values.Length };
        var minimum = Math.Max(2, protocol.MinimumIndependenceUnits);
        if (values.Length < minimum)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["dispersion"], "minimum_independence_units_not_met", compatibility,
                assumptions, limitations, provenance);
        }

        var median = Median(values);
        var deviations = values.Select(value => Math.Abs(value - median)).ToArray();
        var dispersion = Median(deviations);
        if (!double.IsFinite(dispersion))
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["dispersion"], "nonfinite_calculation_result", compatibility,
                assumptions, Append(limitations, "The finite inputs produced a nonfinite raw median absolute deviation."),
                provenance);
        }

        return Available(
            protocol, counts, null, null, dispersion, null, null, null, [],
            compatibility, assumptions, limitations, provenance);
    }

    private static ScenarioStatisticalEvidenceResult EvaluatePairedDelta(
        ScenarioStatisticalProtocol protocol,
        ScenarioStatisticalCounts counts,
        IReadOnlyList<NormalizedObservation> observations,
        ScenarioStatisticalCompatibilityComparison? compatibility,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> limitations,
        ScenarioStatisticalCalculationProvenance provenance)
    {
        var pairedObservations = observations.Where(item =>
            item.Arm.Role is ScenarioStatisticalArmRole.Baseline
                or ScenarioStatisticalArmRole.Candidate).ToArray();
        if (pairedObservations.Any(item =>
                string.IsNullOrWhiteSpace(item.Observation.PairKey)
                || item.Observation.PairOrder is null))
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.Incompatible,
                ["delta"], "pair_identity_or_order_missing", compatibility,
                assumptions, Append(limitations, "Every paired observation must retain its pair identity and order."),
                provenance);
        }

        var baseline = observations.Where(item => item.Arm.Role == ScenarioStatisticalArmRole.Baseline)
            .ToLookup(item => item.Observation.PairKey ?? string.Empty, StringComparer.Ordinal);
        var candidate = observations.Where(item => item.Arm.Role == ScenarioStatisticalArmRole.Candidate)
            .ToLookup(item => item.Observation.PairKey ?? string.Empty, StringComparer.Ordinal);
        var allKeys = baseline.Select(group => group.Key)
            .Concat(candidate.Select(group => group.Key))
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var complete = new List<(string Key, double Baseline, double Candidate)>();
        var incomplete = 0;
        foreach (var key in allKeys)
        {
            var baselineItems = baseline[key].ToArray();
            var candidateItems = candidate[key].ToArray();
            if (baselineItems.Length != 1 || candidateItems.Length != 1)
            {
                incomplete++;
                continue;
            }

            if (baselineItems[0].Observation.PairOrder is null
                || baselineItems[0].Observation.PairOrder != candidateItems[0].Observation.PairOrder)
            {
                return Unavailable(
                    protocol, counts, ScenarioStatisticalAvailability.Incompatible,
                    ["delta"], "pair_order_mismatch", compatibility, assumptions,
                    Append(limitations, $"Pair '{key}' does not retain one consistent order."),
                    provenance);
            }

            complete.Add((
                key,
                baselineItems[0].Observation.Value,
                candidateItems[0].Observation.Value));
        }

        counts = counts with
        {
            IndependenceUnits = complete.Count,
            AcceptedObservations = complete.Count * 2,
            IncompletePairs = incomplete,
        };
        var minimum = Math.Max(1, protocol.MinimumIndependenceUnits);
        if (complete.Count < minimum)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["delta"], "minimum_complete_pairs_not_met", compatibility, assumptions,
                AppendIf(limitations, incomplete > 0, $"{incomplete} incomplete pair(s) were retained but not pooled."),
                provenance with
                {
                    OrderedPairs = complete.Select(item => new ScenarioStatisticalPairProvenance(
                        item.Key,
                        baseline[item.Key].Single().Observation.PairOrder!.Value)).ToArray(),
                });
        }

        var baselineEstimate = Median(complete.Select(item => item.Baseline).ToArray());
        var candidateEstimate = Median(complete.Select(item => item.Candidate).ToArray());
        var absolute = candidateEstimate - baselineEstimate;
        if (!double.IsFinite(baselineEstimate)
            || !double.IsFinite(candidateEstimate)
            || !double.IsFinite(absolute))
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["delta"], "nonfinite_calculation_result", compatibility, assumptions,
                Append(limitations, "The finite paired inputs overflowed while calculating the delta."),
                provenance with
                {
                    OrderedPairs = complete.Select(item => new ScenarioStatisticalPairProvenance(
                        item.Key,
                        baseline[item.Key].Single().Observation.PairOrder!.Value)).ToArray(),
                });
        }

        double? relative = baselineEstimate == 0
            ? null
            : absolute / Math.Abs(baselineEstimate);
        var unavailable = new List<ScenarioStatisticalUnavailableField>();
        if (relative is null || !double.IsFinite(relative.Value))
        {
            relative = null;
            unavailable.Add(new("delta.relative", baselineEstimate == 0
                ? "baseline_estimate_zero"
                : "nonfinite_calculation_result"));
        }

        return Available(
            protocol, counts, null,
            new ScenarioStatisticalDelta(
                baselineEstimate, candidateEstimate, absolute, relative),
            null, null, null, null, unavailable, compatibility, assumptions,
            AppendIf(limitations, incomplete > 0, $"{incomplete} incomplete pair(s) were retained but not pooled."),
            provenance with
            {
                OrderedPairs = complete.Select(item => new ScenarioStatisticalPairProvenance(
                    item.Key,
                    baseline[item.Key].Single().Observation.PairOrder!.Value)).ToArray(),
            });
    }

    private static ScenarioStatisticalEvidenceResult EvaluateWilson(
        ScenarioStatisticalProtocol protocol,
        ScenarioStatisticalCounts counts,
        IReadOnlyList<NormalizedObservation> observations,
        ScenarioStatisticalCompatibilityComparison? compatibility,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> limitations,
        ScenarioStatisticalCalculationProvenance provenance)
    {
        if (protocol.ConfidenceLevel is not > 0 or >= 1)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.Incompatible,
                ["rate", "interval"], "confidence_level_required", compatibility,
                assumptions, Append(limitations, "Wilson score rate requires an explicit confidence level."),
                provenance);
        }

        if (observations.Any(item => item.Observation.Value is not (0 or 1)))
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.Incompatible,
                ["rate", "interval"], "binary_observation_required", compatibility,
                assumptions, Append(limitations, "Wilson score rate accepts only finite binary observations."),
                provenance);
        }

        var minimum = Math.Max(1, protocol.MinimumIndependenceUnits);
        counts = counts with { IndependenceUnits = observations.Count };
        if (observations.Count < minimum)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["rate", "interval"], "minimum_independence_units_not_met", compatibility,
                assumptions, limitations, provenance);
        }

        var positive = observations.Count(item => item.Observation.Value == 1);
        var denominator = observations.Count;
        var rate = (double)positive / denominator;
        var z = InverseStandardNormal(0.5 + protocol.ConfidenceLevel.Value / 2);
        var zSquared = z * z;
        var center = (rate + zSquared / (2 * denominator)) / (1 + zSquared / denominator);
        var margin = z / (1 + zSquared / denominator)
            * Math.Sqrt(rate * (1 - rate) / denominator
                + zSquared / (4d * denominator * denominator));
        var lower = Math.Max(0, center - margin);
        var upper = Math.Min(1, center + margin);
        if (!AllFinite(rate, lower, upper))
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["rate", "interval"], "nonfinite_calculation_result", compatibility,
                assumptions, Append(limitations, "The Wilson score calculation produced a nonfinite result."),
                provenance);
        }

        return Available(
            protocol, counts, null, null, null, null,
            new ScenarioStatisticalRate(positive, denominator, rate),
            new ScenarioStatisticalInterval(lower, upper, protocol.ConfidenceLevel.Value),
            [], compatibility,
            Append(assumptions, "The independent units are interpretable as binomial trials."),
            limitations, provenance);
    }

    private static ScenarioStatisticalEvidenceResult EvaluateTheilSen(
        ScenarioStatisticalProtocol protocol,
        ScenarioStatisticalCounts counts,
        IReadOnlyList<NormalizedObservation> observations,
        ScenarioStatisticalCompatibilityComparison? compatibility,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> limitations,
        ScenarioStatisticalCalculationProvenance provenance)
    {
        var points = observations
            .Where(item => item.Observation.X is double x && double.IsFinite(x))
            .Select(item => (X: item.Observation.X!.Value, item.Observation.Value))
            .ToArray();
        counts = counts with
        {
            IndependenceUnits = points.Length,
            ExcludedNonfinite = counts.ExcludedNonfinite
                + observations.Count(item => item.Observation.X is not double x || !double.IsFinite(x)),
        };
        var minimum = Math.Max(2, protocol.MinimumIndependenceUnits);
        if (points.Length < minimum || points.Select(point => point.X).Distinct().Count() < 2)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["slope"], "minimum_distinct_x_coordinates_not_met", compatibility,
                assumptions, limitations, provenance);
        }

        var slopes = new List<double>(checked(points.Length * (points.Length - 1) / 2));
        for (var left = 0; left < points.Length; left++)
        {
            for (var right = left + 1; right < points.Length; right++)
            {
                var xDelta = points[right].X - points[left].X;
                if (xDelta == 0)
                {
                    continue;
                }

                var slope = (points[right].Value - points[left].Value) / xDelta;
                if (double.IsFinite(slope))
                {
                    slopes.Add(slope);
                }
            }
        }

        if (slopes.Count == 0)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["slope"], "no_finite_pairwise_slopes", compatibility,
                assumptions, Append(limitations, "All pairwise slopes were nonfinite."),
                provenance);
        }

        return Available(
            protocol, counts, null, null, null, Median(slopes), null, null, [],
            compatibility,
            Append(assumptions, "The x coordinates are monotonic and meaningfully spaced in the declared x-axis unit."),
            limitations, provenance);
    }

    private static ScenarioStatisticalEvidenceResult EvaluateBootstrap(
        ScenarioStatisticalProtocol protocol,
        ScenarioStatisticalCounts counts,
        IReadOnlyList<NormalizedObservation> observations,
        ScenarioStatisticalCompatibilityComparison? compatibility,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> limitations,
        ScenarioStatisticalCalculationProvenance provenance,
        CanonicalEvidence canonical)
    {
        if (protocol.ParentMethodId != ScenarioStatisticalMethodIds.WindowMedian)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.Incompatible,
                ["interval"], "unsupported_parent_estimator", compatibility,
                assumptions, Append(limitations, "This bounded harness registers bootstrap intervals over window-median only."),
                provenance);
        }

        if (protocol.ConfidenceLevel is not > 0 or >= 1
            || protocol.BootstrapResamples is not > 0
            || protocol.BootstrapResamples > ScenarioStatisticalEvidenceBounds.MaximumBootstrapResamples)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.Incompatible,
                ["interval"], "bootstrap_parameters_invalid", compatibility,
                assumptions, Append(limitations, "Bootstrap requires bounded resamples and an explicit confidence level."),
                provenance);
        }

        var values = observations.Select(item => item.Observation.Value).ToArray();
        counts = counts with { IndependenceUnits = values.Length };
        var minimum = Math.Max(2, protocol.MinimumIndependenceUnits);
        if (values.Length < minimum)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["interval"], "minimum_independence_units_not_met", compatibility,
                assumptions, limitations, provenance);
        }

        var seedMaterial = Encoding.UTF8.GetBytes(string.Join(
            "|",
            protocol.Method.Version.ToString(CultureInfo.InvariantCulture),
            canonical.IdentityDigest,
            canonical.InputDigest,
            protocol.Id,
            protocol.Version));
        var seed = SHA256.HashData(seedMaterial);
        var random = new Xoshiro256StarStar(seed);
        var resamples = new double[protocol.BootstrapResamples.Value];
        var buffer = new double[values.Length];
        var failed = 0;
        for (var resample = 0; resample < resamples.Length; resample++)
        {
            for (var index = 0; index < buffer.Length; index++)
            {
                buffer[index] = values[random.NextInt(values.Length)];
            }

            var estimate = Median(buffer);
            if (!double.IsFinite(estimate))
            {
                failed++;
                continue;
            }

            resamples[resample] = estimate;
        }

        provenance = provenance with
        {
            SeedDerivation = BootstrapSeedDerivation,
            SeedSha256 = Convert.ToHexStringLower(seed),
            PrngAlgorithm = BootstrapPrng,
            QuantileRule = BootstrapQuantileRule,
            ResamplesRequested = resamples.Length,
            ResamplesCompleted = resamples.Length - failed,
            FailedResamples = failed,
        };
        if (failed > 0)
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["interval"], "bootstrap_resample_failed", compatibility,
                assumptions, Append(limitations, $"{failed} bootstrap resample(s) produced a nonfinite result."),
                provenance);
        }

        Array.Sort(resamples);
        var alpha = (1 - protocol.ConfidenceLevel.Value) / 2;
        var lower = QuantileR7(resamples, alpha);
        var upper = QuantileR7(resamples, 1 - alpha);
        var parentEstimate = Median(values);
        if (!AllFinite(lower, upper, parentEstimate))
        {
            return Unavailable(
                protocol, counts, ScenarioStatisticalAvailability.InsufficientObservations,
                ["interval"], "nonfinite_calculation_result", compatibility,
                assumptions, Append(limitations, "The bootstrap interval produced a nonfinite result."),
                provenance);
        }

        return Available(
            protocol, counts, null, null, null, null, null,
            new ScenarioStatisticalInterval(
                lower, upper, protocol.ConfidenceLevel.Value, parentEstimate),
            [], compatibility,
            Append(assumptions, "Whole declared independence units are exchangeable for percentile resampling."),
            Append(limitations, "The interval describes resampling variability and not systematic bias."),
            provenance);
    }

    private static OperandSelection SelectOperands(
        ScenarioStatisticalEvidenceRequest request,
        MethodRule rule)
    {
        if (rule.RequiresPairing)
        {
            var baseline = request.Arms.Where(arm => arm.Role == ScenarioStatisticalArmRole.Baseline).ToArray();
            var candidate = request.Arms.Where(arm => arm.Role == ScenarioStatisticalArmRole.Candidate).ToArray();
            return baseline.Length == 1 && candidate.Length == 1
                ? new([baseline[0], candidate[0]], null)
                : new([], baseline.Length == 0
                    ? "baseline_arm_not_present"
                    : candidate.Length == 0
                        ? "candidate_arm_not_present"
                        : "unique_paired_arms_not_present");
        }

        if (request.Arms.Count == 0)
        {
            return new([], "observation_arm_not_present");
        }

        if (request.SelectedArmId is not null)
        {
            var selected = request.Arms.Where(arm =>
                string.Equals(arm.Id, request.SelectedArmId, StringComparison.Ordinal)).ToArray();
            return selected.Length == 1
                ? new(selected, null)
                : new([], "selected_arm_not_present");
        }

        return request.Arms.Count == 1
            ? new([request.Arms[0]], null)
            : new([], "selected_arm_not_present");
    }

    private static IdentityValidation ValidateIdentities(
        ScenarioStatisticalProtocol protocol,
        IReadOnlyList<ScenarioStatisticalArm> arms)
    {
        var comparedArms = arms.Select(arm => new ScenarioStatisticalComparedIdentity(
            arm.Id,
            arm.Role,
            arm.CompatibilityIdentity)).ToArray();
        var comparison = new ScenarioStatisticalCompatibilityComparison(comparedArms, []);
        var mismatches = new List<ScenarioStatisticalIdentityMismatch>();
        var dimensionsByArm = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var arm in arms)
        {
            var dimensions = arm.CompatibilityIdentity.Dimensions;
            if (dimensions.Count > ScenarioStatisticalEvidenceBounds.MaximumIdentityDimensions)
            {
                mismatches.Add(new(
                    arm.Id,
                    "identity.dimension-count",
                    dimensions.Count.ToString(CultureInfo.InvariantCulture),
                    ScenarioStatisticalEvidenceBounds.MaximumIdentityDimensions.ToString(CultureInfo.InvariantCulture),
                    "identity_dimension_limit_exceeded"));
            }

            foreach (var dimension in dimensions.Where(item =>
                         string.IsNullOrWhiteSpace(item.Name)
                         || string.IsNullOrWhiteSpace(item.Value)))
            {
                mismatches.Add(new(
                    arm.Id,
                    string.IsNullOrWhiteSpace(dimension.Name) ? "identity.unknown" : dimension.Name,
                    dimension.Value,
                    null,
                    "identity_dimension_empty"));
            }

            foreach (var duplicate in dimensions
                         .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                         .GroupBy(item => item.Name, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                mismatches.Add(new(
                    arm.Id,
                    duplicate.Key,
                    string.Join("|", duplicate.Select(item => item.Value)),
                    null,
                    "identity_dimension_duplicate"));
            }

            var normalized = dimensions
                .Where(item => !string.IsNullOrWhiteSpace(item.Name)
                    && !string.IsNullOrWhiteSpace(item.Value))
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
            dimensionsByArm[arm.Id] = normalized;
            var names = normalized.Keys.ToHashSet(StringComparer.Ordinal);
            var missing = RequiredIdentityDimensions.Where(name => !names.Contains(name)).ToArray();
            foreach (var dimension in missing)
            {
                mismatches.Add(new(
                    arm.Id,
                    dimension,
                    null,
                    null,
                    "required_dimension_missing"));
            }

            if (normalized.TryGetValue("independence.unit", out var declaredUnit)
                && !string.Equals(
                    declaredUnit,
                    IndependenceUnitName(protocol.IndependenceUnit),
                    StringComparison.Ordinal))
            {
                mismatches.Add(new(
                    arm.Id,
                    "independence.unit",
                    declaredUnit,
                    IndependenceUnitName(protocol.IndependenceUnit),
                    "independence_identity_mismatch"));
            }

            if (normalized.TryGetValue("comparison.axis", out var axis)
                && !names.Contains(axis))
            {
                mismatches.Add(new(
                    arm.Id,
                    axis,
                    null,
                    null,
                    "comparison_axis_dimension_missing"));
            }

            var retainedRepresentations = arm.Observations
                .Select(observation => RepresentationName(observation.Representation))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (normalized.TryGetValue("quantity.representation", out var declaredRepresentation)
                && (retainedRepresentations.Length > 1
                    || retainedRepresentations.Length == 1
                    && !string.Equals(
                    retainedRepresentations[0],
                    declaredRepresentation,
                    StringComparison.Ordinal)))
            {
                mismatches.Add(new(
                    arm.Id,
                    "quantity.representation",
                    declaredRepresentation,
                    string.Join("|", retainedRepresentations),
                    "quantity_representation_mismatch"));
            }
        }

        if (arms.Count == 2
            && arms.Any(arm => arm.Role == ScenarioStatisticalArmRole.Baseline)
            && arms.Any(arm => arm.Role == ScenarioStatisticalArmRole.Candidate))
        {
            var baseline = arms.Single(arm => arm.Role == ScenarioStatisticalArmRole.Baseline);
            var candidate = arms.Single(arm => arm.Role == ScenarioStatisticalArmRole.Candidate);
            var baselineDimensions = dimensionsByArm[baseline.Id];
            var candidateDimensions = dimensionsByArm[candidate.Id];
            baselineDimensions.TryGetValue("comparison.axis", out var baselineAxis);
            candidateDimensions.TryGetValue("comparison.axis", out var candidateAxis);
            if (!string.Equals(baselineAxis, candidateAxis, StringComparison.Ordinal))
            {
                mismatches.Add(new(
                    null,
                    "comparison.axis",
                    baselineAxis,
                    candidateAxis,
                    "comparison_axis_mismatch"));
            }

            var ignored = new HashSet<string>(StringComparer.Ordinal)
            {
                "target.process-run-id",
            };
            if (!string.IsNullOrWhiteSpace(baselineAxis)
                && string.Equals(baselineAxis, candidateAxis, StringComparison.Ordinal))
            {
                ignored.Add(baselineAxis);
            }

            foreach (var name in baselineDimensions.Keys
                         .Concat(candidateDimensions.Keys)
                         .Distinct(StringComparer.Ordinal)
                         .Where(name => !ignored.Contains(name))
                         .Order(StringComparer.Ordinal))
            {
                baselineDimensions.TryGetValue(name, out var baselineValue);
                candidateDimensions.TryGetValue(name, out var candidateValue);
                if (!string.Equals(baselineValue, candidateValue, StringComparison.Ordinal))
                {
                    mismatches.Add(new(
                        null,
                        name,
                        baselineValue,
                        candidateValue,
                        baselineValue is null || candidateValue is null
                            ? "required_dimension_missing"
                            : "dimension_value_mismatch"));
                }
            }
        }

        var retained = comparison with
        {
            Mismatches = mismatches
                .DistinctBy(item => new
                {
                    item.ArmId,
                    item.Dimension,
                    item.BaselineValue,
                    item.CandidateValue,
                    item.Reason,
                })
                .OrderBy(item => item.ArmId, StringComparer.Ordinal)
                .ThenBy(item => item.Dimension, StringComparer.Ordinal)
                .ThenBy(item => item.Reason, StringComparer.Ordinal)
                .ToArray(),
        };
        return retained.Mismatches.Count == 0
            ? new(retained, null, null)
            : new(retained, "compatibility_identity_mismatch",
                $"Evidence was not pooled because {retained.Mismatches.Count} compatibility identity mismatch(es) were retained.");
    }

    private static NormalizationResult Normalize(
        ScenarioStatisticalProtocol protocol,
        IReadOnlyList<ScenarioStatisticalArm> arms)
    {
        var attempts = arms
            .SelectMany(arm => arm.Attempts.Select(attempt => (arm, attempt)))
            .ToDictionary(
                item => $"{item.arm.Id}\n{item.attempt.RepetitionId}\n{item.attempt.AttemptId}",
                item => item.attempt,
                StringComparer.Ordinal);
        var finite = new List<NormalizedObservation>();
        var excludedNonfinite = 0;
        foreach (var arm in arms)
        {
            foreach (var observation in arm.Observations)
            {
                var attemptKey = $"{arm.Id}\n{observation.RepetitionId}\n{observation.AttemptId}";
                if (!attempts.TryGetValue(attemptKey, out var attempt))
                {
                    return new([], excludedNonfinite, 0,
                        "observation_attempt_missing",
                        $"Observation '{observation.Id}' does not reference a retained attempt.");
                }

                if (attempt.Outcome != ScenarioStatisticalAttemptOutcome.Passed)
                {
                    return new([], excludedNonfinite, 0,
                        "observation_from_unsuccessful_attempt",
                        $"Observation '{observation.Id}' belongs to {attempt.Outcome.ToString().ToLowerInvariant()} attempt '{attempt.AttemptId}'.");
                }

                if (!double.IsFinite(observation.Value))
                {
                    excludedNonfinite++;
                    continue;
                }

                finite.Add(new(arm, observation, UnitKey(protocol.IndependenceUnit, observation)));
            }
        }

        var normalized = new List<NormalizedObservation>();
        var collapsed = 0;
        foreach (var group in finite.GroupBy(
                     item => $"{item.Arm.Id}\n{item.UnitKey}",
                     StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(item => item.Observation.Id, StringComparer.Ordinal).ToArray();
            var first = ordered[0];
            if (ordered.Skip(1).Any(item =>
                    BitConverter.DoubleToInt64Bits(item.Observation.Value)
                        != BitConverter.DoubleToInt64Bits(first.Observation.Value)
                    || NullableBits(item.Observation.X) != NullableBits(first.Observation.X)
                    || !string.Equals(item.Observation.PairKey, first.Observation.PairKey, StringComparison.Ordinal)
                    || item.Observation.PairOrder != first.Observation.PairOrder))
            {
                return new([], excludedNonfinite, collapsed,
                    "conflicting_independence_unit_values",
                    $"Derived independence unit '{first.UnitKey}' has conflicting retained values.");
            }

            normalized.Add(first);
            collapsed += ordered.Length - 1;
        }

        return new(
            normalized
                .OrderBy(item => item.Arm.Role)
                .ThenBy(item => item.Arm.Id, StringComparer.Ordinal)
                .ThenBy(item => item.UnitKey, StringComparer.Ordinal)
                .ThenBy(item => item.Observation.Id, StringComparer.Ordinal)
                .ToArray(),
            excludedNonfinite,
            collapsed,
            null,
            null);
    }

    private static ScenarioStatisticalCounts CountRaw(IReadOnlyList<ScenarioStatisticalArm> arms)
    {
        var attempts = arms.SelectMany(arm => arm.Attempts)
            .GroupBy(
                attempt => $"{attempt.RepetitionId}\n{attempt.AttemptId}",
                StringComparer.Ordinal)
            .Select(group =>
            {
                var items = group.ToArray();
                return items.Any(item => item.Outcome == ScenarioStatisticalAttemptOutcome.Aborted)
                    ? items.First(item => item.Outcome == ScenarioStatisticalAttemptOutcome.Aborted)
                    : items.Any(item => item.Outcome == ScenarioStatisticalAttemptOutcome.Failed)
                        ? items.First(item => item.Outcome == ScenarioStatisticalAttemptOutcome.Failed)
                        : items[0];
            })
            .ToArray();
        var observations = arms.SelectMany(arm => arm.Observations.Select(observation => (arm.Id, observation))).ToArray();
        return new ScenarioStatisticalCounts(
            observations.Sum(item => item.observation.SourceEventCount),
            observations.Where(item => item.observation.WindowId is not null)
                .Select(item => $"{item.Id}\n{item.observation.RepetitionId}\n{item.observation.AttemptId}\n{item.observation.CaptureId}\n{item.observation.WindowId}")
                .Distinct(StringComparer.Ordinal).Count(),
            observations.Select(item =>
                    $"{item.Id}\n{item.observation.RepetitionId}\n{item.observation.AttemptId}\n{item.observation.CaptureId}")
                .Distinct(StringComparer.Ordinal).Count(),
            attempts.Select(item => item.RepetitionId)
                .Distinct(StringComparer.Ordinal).Count(),
            attempts.Length,
            attempts.Count(item => item.Outcome == ScenarioStatisticalAttemptOutcome.Passed),
            attempts.Count(item => item.Outcome == ScenarioStatisticalAttemptOutcome.Failed),
            attempts.Count(item => item.Outcome == ScenarioStatisticalAttemptOutcome.Aborted),
            0,
            0,
            0,
            0,
            0,
            observations.Sum(item => item.observation.LostOrDropped));
    }

    private static void ValidateBounds(ScenarioStatisticalEvidenceRequest request)
    {
        if (request.SchemaVersion != ScenarioStatisticalEvidenceJson.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported statistical evidence request schema version {request.SchemaVersion}.");
        }

        if (request.Arms.Count is < 1 or > ScenarioStatisticalEvidenceBounds.MaximumArms)
        {
            throw new InvalidDataException(
                $"Statistical evidence must contain 1-{ScenarioStatisticalEvidenceBounds.MaximumArms} arms.");
        }

        var armIds = request.Arms.Select(arm => arm.Id).ToArray();
        foreach (var armId in armIds)
        {
            Require(armId, "arm.Id");
        }

        if (armIds.Distinct(StringComparer.Ordinal).Count() != armIds.Length)
        {
            throw new InvalidDataException("Statistical evidence contains duplicate arm identities.");
        }

        var attempts = request.Arms
            .SelectMany(arm => arm.Attempts)
            .Select(attempt => $"{attempt.RepetitionId}\n{attempt.AttemptId}")
            .Distinct(StringComparer.Ordinal)
            .Count();
        var observations = request.Arms.Sum(arm => arm.Observations.Count);
        var repetitions = request.Arms
            .SelectMany(arm => arm.Attempts.Select(attempt => $"{arm.Id}\n{attempt.RepetitionId}"))
            .Distinct(StringComparer.Ordinal).Count();
        var captures = request.Arms
            .SelectMany(arm => arm.Observations.Select(observation =>
                $"{arm.Id}\n{observation.RepetitionId}\n{observation.AttemptId}\n{observation.CaptureId}"))
            .Distinct(StringComparer.Ordinal).Count();
        var windows = request.Arms
            .SelectMany(arm => arm.Observations.Where(observation => observation.WindowId is not null)
                .Select(observation =>
                    $"{arm.Id}\n{observation.RepetitionId}\n{observation.AttemptId}\n{observation.CaptureId}\n{observation.WindowId}"))
            .Distinct(StringComparer.Ordinal).Count();
        if (attempts > ScenarioStatisticalEvidenceBounds.MaximumAttempts
            || observations > ScenarioStatisticalEvidenceBounds.MaximumObservations
            || repetitions > ScenarioStatisticalEvidenceBounds.MaximumRepetitions
            || captures > ScenarioStatisticalEvidenceBounds.MaximumCaptures
            || windows > ScenarioStatisticalEvidenceBounds.MaximumWindows)
        {
            throw new InvalidDataException("Statistical evidence exceeds a fixed harness bound.");
        }
        foreach (var arm in request.Arms)
        {
            var attemptKeys = arm.Attempts
                .Select(attempt => $"{attempt.RepetitionId}\n{attempt.AttemptId}")
                .ToArray();
            if (attemptKeys.Distinct(StringComparer.Ordinal).Count() != attemptKeys.Length)
            {
                throw new InvalidDataException($"Arm '{arm.Id}' contains duplicate attempt identities.");
            }

            foreach (var attempt in arm.Attempts)
            {
                Require(attempt.RepetitionId, nameof(attempt.RepetitionId));
                Require(attempt.AttemptId, nameof(attempt.AttemptId));
            }

            foreach (var observation in arm.Observations)
            {
                Require(observation.Id, nameof(observation.Id));
                Require(observation.RepetitionId, nameof(observation.RepetitionId));
                Require(observation.AttemptId, nameof(observation.AttemptId));
                Require(observation.EnvironmentAllocationId, nameof(observation.EnvironmentAllocationId));
                Require(observation.CaptureId, nameof(observation.CaptureId));
                Require(observation.IndependenceUnitId, nameof(observation.IndependenceUnitId));
                if (observation.SourceEventCount < 0 || observation.LostOrDropped < 0)
                {
                    throw new InvalidDataException(
                        $"Observation '{observation.Id}' has a negative acquisition count.");
                }
            }
        }

        if (request.Protocol.MinimumIndependenceUnits < 1)
        {
            throw new InvalidDataException("The scenario minimum independence-unit count must be positive.");
        }
    }

    private static CanonicalEvidence Canonicalize(
        ScenarioStatisticalEvidenceRequest request,
        IReadOnlyList<ScenarioStatisticalArm> arms,
        IReadOnlyList<NormalizedObservation> observations)
    {
        var identity = new StringBuilder();
        foreach (var arm in arms.OrderBy(arm => arm.Role).ThenBy(arm => arm.Id, StringComparer.Ordinal))
        {
            AppendCanonicalRecord(identity, arm.Role.ToString(), arm.Id);
            foreach (var dimension in arm.CompatibilityIdentity.Dimensions
                         .OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                AppendCanonicalRecord(identity, dimension.Name, dimension.Value);
            }
        }

        var input = new StringBuilder();
        AppendCanonicalRecord(
            input,
            request.Protocol.Id,
            request.Protocol.Version,
            request.Protocol.Method.Id,
            request.Protocol.Method.Version.ToString(CultureInfo.InvariantCulture),
            IndependenceUnitName(request.Protocol.IndependenceUnit));
        foreach (var arm in arms.OrderBy(arm => arm.Role).ThenBy(arm => arm.Id, StringComparer.Ordinal))
        {
            foreach (var attempt in arm.Attempts
                         .OrderBy(item => item.RepetitionId, StringComparer.Ordinal)
                         .ThenBy(item => item.AttemptId, StringComparer.Ordinal))
            {
                AppendCanonicalRecord(
                    input,
                    "attempt",
                    arm.Id,
                    attempt.RepetitionId,
                    attempt.AttemptId,
                    attempt.Outcome.ToString(),
                    attempt.FailureKind,
                    attempt.Detail);
            }
        }

        foreach (var item in observations)
        {
            var observation = item.Observation;
            AppendCanonicalRecord(
                input,
                "observation",
                item.Arm.Id,
                observation.RepetitionId,
                observation.AttemptId,
                observation.EnvironmentAllocationId,
                observation.CaptureId,
                observation.WindowId,
                item.UnitKey,
                observation.IndependenceUnitId,
                BitConverter.DoubleToInt64Bits(observation.Value).ToString("X16", CultureInfo.InvariantCulture),
                NullableBits(observation.X)?.ToString("X16", CultureInfo.InvariantCulture),
                observation.SourceEventCount.ToString(CultureInfo.InvariantCulture),
                observation.LostOrDropped.ToString(CultureInfo.InvariantCulture),
                observation.Representation.ToString(),
                observation.PairKey,
                observation.PairOrder?.ToString());
        }

        return new(Sha256(input.ToString()), Sha256(identity.ToString()));
    }

    private static void AppendCanonicalRecord(StringBuilder builder, params string?[] fields)
    {
        builder.Append(fields.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var field in fields)
        {
            if (field is null)
            {
                builder.Append("-1:\n");
                continue;
            }

            builder.Append(field.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(field)
                .Append('\n');
        }
    }

    private static ScenarioStatisticalEvidenceResult Available(
        ScenarioStatisticalProtocol protocol,
        ScenarioStatisticalCounts counts,
        double? estimate,
        ScenarioStatisticalDelta? delta,
        double? dispersion,
        double? slope,
        ScenarioStatisticalRate? rate,
        ScenarioStatisticalInterval? interval,
        IReadOnlyList<ScenarioStatisticalUnavailableField> unavailableFields,
        ScenarioStatisticalCompatibilityComparison? compatibility,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> limitations,
        ScenarioStatisticalCalculationProvenance provenance)
        => new(
            CurrentResultSchemaVersion,
            ScenarioStatisticalAvailability.Available,
            protocol.Method,
            protocol.IndependenceUnit,
            counts,
            estimate,
            delta,
            dispersion,
            slope,
            rate,
            interval,
            unavailableFields,
            compatibility,
            assumptions.Take(ScenarioStatisticalEvidenceBounds.MaximumAssumptions).ToArray(),
            Append(limitations, "The result is descriptive evidence and does not establish causation or a workload decision.")
                .Take(ScenarioStatisticalEvidenceBounds.MaximumLimitations).ToArray(),
            provenance);

    private static ScenarioStatisticalEvidenceResult Unavailable(
        ScenarioStatisticalProtocol protocol,
        ScenarioStatisticalCounts counts,
        ScenarioStatisticalAvailability availability,
        IReadOnlyList<string> fields,
        string reason,
        ScenarioStatisticalCompatibilityComparison? compatibility,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> limitations,
        ScenarioStatisticalCalculationProvenance provenance)
        => new(
            CurrentResultSchemaVersion,
            availability,
            protocol.Method,
            protocol.IndependenceUnit,
            counts,
            null,
            null,
            null,
            null,
            null,
            null,
            fields.Select(field => new ScenarioStatisticalUnavailableField(field, reason)).ToArray(),
            compatibility,
            assumptions.Take(ScenarioStatisticalEvidenceBounds.MaximumAssumptions).ToArray(),
            limitations.Take(ScenarioStatisticalEvidenceBounds.MaximumLimitations).ToArray(),
            provenance);

    private static ScenarioStatisticalCalculationProvenance EmptyProvenance()
        => new(string.Empty, string.Empty, null, null, null, null, null, null, 0, []);

    private static IReadOnlyList<string> Assumptions(ScenarioStatisticalProtocol protocol)
        =>
        [
            $"Declared {IndependenceUnitName(protocol.IndependenceUnit)} units are independent or exchangeable for this method.",
            "Workload dimensions and admitted capture settings are stable across compatible observations.",
            "Retained observations represent the declared quantity and normalization.",
        ];

    private static List<string> BaseLimitations(
        IReadOnlyList<ScenarioStatisticalArm> arms,
        ScenarioStatisticalCounts counts)
    {
        var limitations = new List<string>();
        if (counts.FailedAttempts > 0 || counts.AbortedAttempts > 0)
        {
            limitations.Add(
                $"{counts.FailedAttempts} failed and {counts.AbortedAttempts} aborted attempt(s) remain in the {counts.Attempts}-attempt protocol denominator.");
        }
        if (counts.ExcludedNonfinite > 0)
        {
            limitations.Add($"{counts.ExcludedNonfinite} nonfinite observation(s) were excluded before calculation.");
        }
        if (counts.CollapsedCorrelatedDuplicates > 0)
        {
            limitations.Add(
                $"{counts.CollapsedCorrelatedDuplicates} duplicate record(s) sharing an independence-unit identity were collapsed.");
        }
        if (counts.LostOrDropped > 0)
        {
            limitations.Add($"{counts.LostOrDropped} source item(s) were reported lost or dropped.");
        }

        var representations = arms.SelectMany(arm => arm.Observations)
            .Select(observation => observation.Representation)
            .Where(value => value != ScenarioStatisticalRepresentation.Exact)
            .Distinct()
            .Order()
            .ToArray();
        if (representations.Length > 0)
        {
            limitations.Add(
                $"Retained representation includes: {string.Join(", ", representations.Select(value => value.ToString()))}.");
        }

        return limitations;
    }

    private static string[] Append(IReadOnlyList<string> values, string value)
        => values.Concat([value]).ToArray();

    private static IReadOnlyList<string> AppendIf(
        IReadOnlyList<string> values,
        bool condition,
        string value)
        => condition ? Append(values, value) : values;

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0)
        {
            return double.NaN;
        }

        var middle = values.Length / 2;
        return values.Length % 2 == 1
            ? values[middle]
            : values[middle - 1] / 2d + values[middle] / 2d;
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

    private static double InverseStandardNormal(double probability)
    {
        double[] a =
        [
            -3.969683028665376e+01,
            2.209460984245205e+02,
            -2.759285104469687e+02,
            1.383577518672690e+02,
            -3.066479806614716e+01,
            2.506628277459239e+00,
        ];
        double[] b =
        [
            -5.447609879822406e+01,
            1.615858368580409e+02,
            -1.556989798598866e+02,
            6.680131188771972e+01,
            -1.328068155288572e+01,
        ];
        double[] c =
        [
            -7.784894002430293e-03,
            -3.223964580411365e-01,
            -2.400758277161838e+00,
            -2.549732539343734e+00,
            4.374664141464968e+00,
            2.938163982698783e+00,
        ];
        double[] d =
        [
            7.784695709041462e-03,
            3.224671290700398e-01,
            2.445134137142996e+00,
            3.754408661907416e+00,
        ];
        const double low = 0.02425;
        const double high = 1 - low;
        if (probability < low)
        {
            var q = Math.Sqrt(-2 * Math.Log(probability));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        if (probability <= high)
        {
            var q = probability - 0.5;
            var r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q
                / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }

        var tail = Math.Sqrt(-2 * Math.Log(1 - probability));
        return -(((((c[0] * tail + c[1]) * tail + c[2]) * tail + c[3]) * tail + c[4]) * tail + c[5])
            / ((((d[0] * tail + d[1]) * tail + d[2]) * tail + d[3]) * tail + 1);
    }

    private static bool AllFinite(params double[] values)
        => values.All(double.IsFinite);

    private static long? NullableBits(double? value)
        => value is null ? null : BitConverter.DoubleToInt64Bits(value.Value);

    private static string Sha256(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string IndependenceUnitName(ScenarioStatisticalIndependenceUnit unit)
        => unit switch
        {
            ScenarioStatisticalIndependenceUnit.Event => "event",
            ScenarioStatisticalIndependenceUnit.BoundedTimeWindow => "bounded_time_window",
            ScenarioStatisticalIndependenceUnit.Capture => "capture",
            ScenarioStatisticalIndependenceUnit.ProcessRun => "process_run",
            ScenarioStatisticalIndependenceUnit.EnvironmentAllocation => "environment_allocation",
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };

    private static string RepresentationName(ScenarioStatisticalRepresentation representation)
        => representation switch
        {
            ScenarioStatisticalRepresentation.Exact => "exact",
            ScenarioStatisticalRepresentation.ReservoirSampled => "reservoir_sampled",
            ScenarioStatisticalRepresentation.Projected => "projected",
            ScenarioStatisticalRepresentation.Truncated => "truncated",
            _ => throw new ArgumentOutOfRangeException(nameof(representation)),
        };

    private static string UnitKey(
        ScenarioStatisticalIndependenceUnit unit,
        ScenarioStatisticalObservation observation)
        => unit switch
        {
            ScenarioStatisticalIndependenceUnit.BoundedTimeWindow
                when !string.IsNullOrWhiteSpace(observation.WindowId)
                => $"{observation.RepetitionId}\n{observation.AttemptId}\n{observation.CaptureId}\n{observation.WindowId}",
            ScenarioStatisticalIndependenceUnit.BoundedTimeWindow
                => throw new InvalidDataException(
                    $"Observation '{observation.Id}' is missing its bounded window identity."),
            ScenarioStatisticalIndependenceUnit.Capture
                => $"{observation.RepetitionId}\n{observation.AttemptId}\n{observation.CaptureId}",
            ScenarioStatisticalIndependenceUnit.ProcessRun
                => $"{observation.RepetitionId}\n{observation.AttemptId}",
            ScenarioStatisticalIndependenceUnit.EnvironmentAllocation
                => observation.EnvironmentAllocationId,
            ScenarioStatisticalIndependenceUnit.Event
                => observation.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{name} is required.");
        }
    }

    private sealed record MethodRule(
        int MinimumInputCount,
        bool RequiresPairing,
        IReadOnlySet<ScenarioStatisticalIndependenceUnit> AllowedIndependenceUnits,
        IReadOnlyList<string> Produces,
        IReadOnlyList<string> OutputFields)
    {
        public MethodRule(
            int minimumInputCount,
            bool requiresPairing,
            IEnumerable<ScenarioStatisticalIndependenceUnit> allowedIndependenceUnits,
            IReadOnlyList<string> produces,
            IReadOnlyList<string> outputFields)
            : this(
                minimumInputCount,
                requiresPairing,
                allowedIndependenceUnits.ToHashSet(),
                produces,
                outputFields)
        {
        }
    }

    private sealed record OperandSelection(
        IReadOnlyList<ScenarioStatisticalArm> Arms,
        string? NotApplicableReason);

    private sealed record IdentityValidation(
        ScenarioStatisticalCompatibilityComparison? Comparison,
        string? Reason,
        string? Detail);

    private sealed record NormalizedObservation(
        ScenarioStatisticalArm Arm,
        ScenarioStatisticalObservation Observation,
        string UnitKey);

    private sealed record NormalizationResult(
        IReadOnlyList<NormalizedObservation> Observations,
        int ExcludedNonfinite,
        int CollapsedDuplicates,
        string? IncompatibleReason,
        string? IncompatibleDetail);

    private sealed record CanonicalEvidence(string InputDigest, string IdentityDigest);

    private sealed class Xoshiro256StarStar
    {
        private ulong _s0;
        private ulong _s1;
        private ulong _s2;
        private ulong _s3;

        public Xoshiro256StarStar(ReadOnlySpan<byte> seed)
        {
            if (seed.Length < 32)
            {
                throw new ArgumentException("A 256-bit seed is required.", nameof(seed));
            }

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
