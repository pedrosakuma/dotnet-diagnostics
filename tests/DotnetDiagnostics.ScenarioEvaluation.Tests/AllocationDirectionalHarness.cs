namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class AllocationDirectionalHarness
{
    public const string ProtocolId = "allocation-directional-baseline-current";
    public const string ProtocolVersion = "2.0.0";

    public static AllocationDirectionalStudyReport Evaluate(
        AllocationDirectionalFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        Validate(fixture);

        var aaPairs = ValidPairs(fixture, AllocationDirectionalScenario.HealthyAa);
        if (aaPairs.Length < AllocationDirectionalBounds.RequiredPairs)
        {
            return Inconclusive(
                fixture,
                "Healthy A/A did not retain the three predeclared valid independent pairs.");
        }

        var aaRelativeEnvelope = aaPairs
            .Select(pair => AbsoluteRelativeDelta(
                EstimatedBytesPerOperation(pair.Baseline),
                EstimatedBytesPerOperation(pair.Current)))
            .Max();
        var aaTypeShareMovements = aaPairs.Select(pair =>
            MaximumShareMovement(pair.Baseline.Types, pair.Current.Types)).ToArray();
        var aaSiteShareMovements = aaPairs.Select(pair =>
            MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites)).ToArray();
        if (aaTypeShareMovements.Any(value => value is null)
            || aaSiteShareMovements.Any(value => value is null))
        {
            return Inconclusive(
                fixture,
                "Healthy A/A lacked type or site evidence required by the frozen share envelopes.");
        }

        var aaTypeShareEnvelope = aaTypeShareMovements.Max()!.Value;
        var aaSiteShareEnvelope = aaSiteShareMovements.Max()!.Value;

        var results = Enum.GetValues<AllocationDirectionalScenario>()
            .Select(scenario => EvaluateScenario(
                fixture,
                scenario,
                aaRelativeEnvelope,
                aaTypeShareEnvelope,
                aaSiteShareEnvelope))
            .ToArray();
        var numericGatePassed = results.All(result =>
            result.Assessable
            &&
            result.ExpectedBehaviorObserved
            && !result.UnsafeDirectionalEvidenceObserved);
        var decision = results.Any(result => !result.Assessable)
            ? AllocationDirectionalDecision.Inconclusive
            : results.Any(result => result.UnsafeDirectionalEvidenceObserved)
                ? AllocationDirectionalDecision.NoGo
                : AllocationDirectionalDecision.Inconclusive;
        return new(
            AllocationDirectionalJson.CurrentSchemaVersion,
            ProtocolId,
            ProtocolVersion,
            decision,
            aaRelativeEnvelope,
            aaTypeShareEnvelope,
            aaSiteShareEnvelope,
            numericGatePassed,
            numericGatePassed,
            results,
            [
                "Measurement quality is used only for internal compatibility, availability, and abstention gates; it is not a model-visible field or diagnostic headline.",
                "GCAllocationTick estimated bytes are sampled weights, not exact allocation totals, retained bytes, object counts, severity, health, regression, or causality.",
                "Each live side is a separately started process. Failed and aborted sides remain in pair denominators and are never retried or replaced.",
                "Allocated types and allocation sites are independent inventories; no type-to-site relationship is inferred.",
                numericGatePassed
                    ? "The numeric gate passed, but the final decision remains INCONCLUSIVE until the frozen blinded evaluation completes."
                    : "The numeric gate did not authorize blinded evaluation.",
            ]);
    }

    public static AllocationDirectionalStudyReport CompleteBlindedEvaluation(
        AllocationDirectionalStudyReport report,
        bool improvedSupportOrNextStepUsefulness,
        bool increasedUnsupportedCertainty,
        string result)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        if (!report.NumericGatePassed
            || !report.BlindedEvaluationRequired
            || report.BlindedEvaluationRan)
        {
            throw new InvalidOperationException(
                "Blinded evaluation may run exactly once and only after the numeric gate passes.");
        }

        return report with
        {
            Decision = improvedSupportOrNextStepUsefulness
                && !increasedUnsupportedCertainty
                    ? AllocationDirectionalDecision.Go
                    : AllocationDirectionalDecision.NoGo,
            BlindedEvaluationRan = true,
            BlindedEvaluationResult = result,
        };
    }

    public static void ValidateReport(AllocationDirectionalStudyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if ((report.BlindedEvaluationRan
                || report.BlindedEvaluationResult is not null)
            && !report.NumericGatePassed)
        {
            throw new InvalidDataException(
                "A blinded evaluation result cannot exist before the numeric gate passes.");
        }

        if (report.Decision == AllocationDirectionalDecision.Go
            && (!report.BlindedEvaluationRan
                || string.IsNullOrWhiteSpace(report.BlindedEvaluationResult)))
        {
            throw new InvalidDataException(
                "GO requires a completed blinded evaluation.");
        }
    }

    private static AllocationDirectionalScenarioResult EvaluateScenario(
        AllocationDirectionalFixture fixture,
        AllocationDirectionalScenario scenario,
        decimal aaRelativeEnvelope,
        decimal aaTypeShareEnvelope,
        decimal aaSiteShareEnvelope)
    {
        var attempted = fixture.Pairs.Where(pair => pair.Scenario == scenario).ToArray();
        var valid = attempted.Where(IsValidPair).ToArray();
        var findings = new List<string>();
        var expected = false;
        var unsafeEvidence = false;
        var assessable = false;

        switch (scenario)
        {
            case AllocationDirectionalScenario.TrueIncrease:
                var increaseAa = CalibrationPairs(valid);
                var increaseEffect = EffectPairs(valid);
                var increaseEnvelope = RelativeEnvelope(increaseAa);
                var increaseAssessable = HasRequiredPairs(increaseAa)
                    && HasRequiredPairs(increaseEffect);
                assessable = increaseAssessable;
                expected = increaseAssessable
                    && increaseEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
                    && increaseEffect.All(pair =>
                        RelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                        > increaseEnvelope);
                findings.Add(expected
                    ? "Every effect pair increased estimated sampled bytes per operation beyond its workload-specific A/A envelope."
                    : "The intended equal-work increase was incomplete, overlapped its workload-specific A/A envelope, or had excessive A/A variability.");
                unsafeEvidence = increaseAssessable && !expected;
                break;

            case AllocationDirectionalScenario.MixShiftStableTotal:
                var mixAa = CalibrationPairs(valid);
                var mixEffect = EffectPairs(valid);
                var mixAaTypeShares = mixAa.Select(pair =>
                    MaximumShareMovement(pair.Baseline.Types, pair.Current.Types)).ToArray();
                var mixAaSiteShares = mixAa.Select(pair =>
                    MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites)).ToArray();
                var mixAaTypeAbsolute = mixAa.Select(pair =>
                    MaximumAbsoluteMovementPerOperation(
                        pair.Baseline.Types,
                        pair.Baseline.Operations,
                        pair.Current.Types,
                        pair.Current.Operations)).ToArray();
                var mixAaSiteAbsolute = mixAa.Select(pair =>
                    MaximumAbsoluteMovementPerOperation(
                        pair.Baseline.Sites,
                        pair.Baseline.Operations,
                        pair.Current.Sites,
                        pair.Current.Operations)).ToArray();
                var mixEvidenceAvailable = HasRequiredPairs(mixAa)
                    && HasRequiredPairs(mixEffect)
                    && mixAaTypeShares.All(value => value is not null)
                    && mixAaSiteShares.All(value => value is not null)
                    && mixAaTypeAbsolute.All(value => value is not null)
                    && mixAaSiteAbsolute.All(value => value is not null)
                    && mixEffect.All(pair =>
                        pair.Baseline.Types.Count > 0
                        && pair.Current.Types.Count > 0
                        && pair.Baseline.Sites.Count > 0
                        && pair.Current.Sites.Count > 0);
                assessable = mixEvidenceAvailable;
                var mixTotalEnvelope = RelativeEnvelope(mixAa);
                var mixTypeShareEnvelope = mixEvidenceAvailable
                    ? mixAaTypeShares.Max()!.Value
                    : decimal.MaxValue;
                var mixSiteShareEnvelope = mixEvidenceAvailable
                    ? mixAaSiteShares.Max()!.Value
                    : decimal.MaxValue;
                var mixTypeAbsoluteEnvelope = mixEvidenceAvailable
                    ? mixAaTypeAbsolute.Max()!.Value
                    : decimal.MaxValue;
                var mixSiteAbsoluteEnvelope = mixEvidenceAvailable
                    ? mixAaSiteAbsolute.Max()!.Value
                    : decimal.MaxValue;
                expected = mixEvidenceAvailable
                    && mixTotalEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
                    && mixTypeShareEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaShareMovement
                    && mixSiteShareEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaShareMovement
                    && mixEffect.All(pair =>
                        pair.Baseline.RequestedPayloadBytesPerOperation
                            == pair.Current.RequestedPayloadBytesPerOperation
                        && AbsoluteRelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                            <= mixTotalEnvelope
                        && TopKey(pair.Baseline.Types) != TopKey(pair.Current.Types)
                        && TopKey(pair.Baseline.Sites) != TopKey(pair.Current.Sites)
                        && MaximumShareMovement(
                            pair.Baseline.Types,
                            pair.Current.Types) > mixTypeShareEnvelope
                        && MaximumShareMovement(
                            pair.Baseline.Sites,
                            pair.Current.Sites) > mixSiteShareEnvelope
                        && MaximumAbsoluteMovementPerOperation(
                            pair.Baseline.Types,
                            pair.Baseline.Operations,
                            pair.Current.Types,
                            pair.Current.Operations) > mixTypeAbsoluteEnvelope
                        && MaximumAbsoluteMovementPerOperation(
                            pair.Baseline.Sites,
                            pair.Baseline.Operations,
                            pair.Current.Sites,
                            pair.Current.Operations) > mixSiteAbsoluteEnvelope);
                findings.Add(expected
                    ? "The equal-payload mix shift changed both independent leading inventories in absolute estimated bytes per operation and share while normalized total stayed inside workload-specific A/A."
                    : "The mix-shift control lacked compatible A/A, stable normalized total, or paired absolute-and-share inventory movement.");
                unsafeEvidence = mixEvidenceAvailable && !expected;
                break;

            case AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation:
                var throughputAa = valid.Where(pair => pair.Current.Phase == "aa").ToArray();
                var intensity = EffectPairs(valid);
                var throughputEnvelope = HasRequiredPairs(throughputAa)
                    ? throughputAa.Max(pair => AbsoluteRelativeDelta(
                        EstimatedBytesPerOperation(pair.Baseline),
                        EstimatedBytesPerOperation(pair.Current)))
                    : decimal.MaxValue;
                expected = HasRequiredPairs(throughputAa)
                    && HasRequiredPairs(intensity)
                    && throughputEnvelope <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
                    && intensity.All(pair =>
                        OperationsPerSecond(pair.Current)
                            > OperationsPerSecond(pair.Baseline)
                                * AllocationDirectionalCapturePlan.MinimumThroughputMultiplier
                        && AbsoluteRelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                            <= throughputEnvelope);
                assessable = HasRequiredPairs(throughputAa)
                    && HasRequiredPairs(intensity);
                unsafeEvidence = HasRequiredPairs(throughputAa)
                    && HasRequiredPairs(intensity)
                    && !expected;
                findings.Add(expected
                    ? "Higher throughput retained stable estimated sampled bytes per operation."
                    : "Throughput normalization did not remain inside the A/A envelope.");
                break;

            case AllocationDirectionalScenario.LowerAllocationStableRetained:
                var lowerAa = CalibrationPairs(valid);
                var lowerEffect = EffectPairs(valid);
                var lowerEnvelope = RelativeEnvelope(lowerAa);
                var lowerAssessable = HasRequiredPairs(lowerAa)
                    && HasRequiredPairs(lowerEffect);
                assessable = lowerAssessable;
                expected = lowerAssessable
                    && lowerEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
                    && lowerEffect.All(pair =>
                        pair.Baseline.RetainedBytes == pair.Current.RetainedBytes
                        && RelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                            < -lowerEnvelope);
                findings.Add(expected
                    ? "Allocation activity decreased beyond workload-specific A/A while the independently declared retained quantity stayed unchanged."
                    : "The lower-allocation control was incomplete, did not preserve retained bytes, overlapped A/A, or had excessive A/A variability.");
                unsafeEvidence = lowerAssessable && !expected;
                break;

            case AllocationDirectionalScenario.WarmupTransientConverges:
                var warmupAa = CalibrationPairs(valid);
                var early = valid.Where(pair => pair.Current.Phase == "early").ToArray();
                var converged = valid.Where(pair => pair.Current.Phase == "converged").ToArray();
                var warmupEnvelope = RelativeEnvelope(warmupAa);
                var warmupAssessable = HasRequiredPairs(warmupAa)
                    && HasRequiredPairs(early)
                    && HasRequiredPairs(converged);
                assessable = warmupAssessable;
                expected = warmupAssessable
                    && warmupEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
                    && HasRequiredPairs(converged)
                    && early.All(pair =>
                        RelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                            > warmupEnvelope)
                    && converged.All(pair =>
                        AbsoluteRelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                            <= warmupEnvelope);
                findings.Add(expected
                    ? "The early movement exceeded workload-specific A/A and disappeared in the predeclared converged phase."
                    : "The transient control was incomplete, had excessive A/A variability, or did not show the frozen early-then-converged shape.");
                unsafeEvidence = warmupAssessable && !expected;
                break;

            case AllocationDirectionalScenario.ChangedWorkload:
                assessable = attempted.Length >= AllocationDirectionalBounds.RequiredPairs
                    && attempted.All(pair =>
                        pair.Baseline.Outcome == AllocationDirectionalAttemptOutcome.Passed
                        && pair.Current.Outcome == AllocationDirectionalAttemptOutcome.Passed);
                expected = attempted.Length >= AllocationDirectionalBounds.RequiredPairs
                    && attempted.All(pair =>
                        pair.Baseline.Outcome == AllocationDirectionalAttemptOutcome.Passed
                        && pair.Current.Outcome == AllocationDirectionalAttemptOutcome.Passed
                        && !string.Equals(
                            pair.Baseline.WorkloadIdentity,
                            pair.Current.WorkloadIdentity,
                            StringComparison.Ordinal));
                findings.Add(expected
                    ? "Every changed-workload pair was rejected by identity compatibility before directional interpretation."
                    : "At least one changed-workload pair was not rejected by identity compatibility.");
                break;

            case AllocationDirectionalScenario.FixedStrideBias:
                var strideAa = valid.Where(pair => pair.Current.Phase == "aa").ToArray();
                var reordered = EffectPairs(valid);
                var strideTypeMovements = strideAa.Select(pair =>
                    MaximumShareMovement(pair.Baseline.Types, pair.Current.Types)).ToArray();
                var strideSiteMovements = strideAa.Select(pair =>
                    MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites)).ToArray();
                var strideTypeAbsolute = strideAa.Select(pair =>
                    MaximumAbsoluteMovementPerOperation(
                        pair.Baseline.Types,
                        pair.Baseline.Operations,
                        pair.Current.Types,
                        pair.Current.Operations)).ToArray();
                var strideSiteAbsolute = strideAa.Select(pair =>
                    MaximumAbsoluteMovementPerOperation(
                        pair.Baseline.Sites,
                        pair.Baseline.Operations,
                        pair.Current.Sites,
                        pair.Current.Operations)).ToArray();
                var strideEvidenceAvailable = HasRequiredPairs(strideAa)
                    && HasRequiredPairs(reordered)
                    && strideTypeMovements.All(value => value is not null)
                    && strideSiteMovements.All(value => value is not null)
                    && strideTypeAbsolute.All(value => value is not null)
                    && strideSiteAbsolute.All(value => value is not null)
                    && reordered.All(pair =>
                        MaximumShareMovement(pair.Baseline.Types, pair.Current.Types) is not null
                        && MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites) is not null);
                assessable = strideEvidenceAvailable;
                var strideTypeEnvelope = strideEvidenceAvailable
                    ? strideTypeMovements.Max()!.Value
                    : decimal.MaxValue;
                var strideSiteEnvelope = strideEvidenceAvailable
                    ? strideSiteMovements.Max()!.Value
                    : decimal.MaxValue;
                var strideTypeAbsoluteEnvelope = strideEvidenceAvailable
                    ? strideTypeAbsolute.Max()!.Value
                    : decimal.MaxValue;
                var strideSiteAbsoluteEnvelope = strideEvidenceAvailable
                    ? strideSiteAbsolute.Max()!.Value
                    : decimal.MaxValue;
                var biased = reordered.Where(pair =>
                    pair.Baseline.RequestedPayloadBytesPerOperation
                        == pair.Current.RequestedPayloadBytesPerOperation
                    && (MaximumShareMovement(pair.Baseline.Types, pair.Current.Types)
                            > strideTypeEnvelope
                        || MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites)
                            > strideSiteEnvelope
                        || MaximumAbsoluteMovementPerOperation(
                            pair.Baseline.Types,
                            pair.Baseline.Operations,
                            pair.Current.Types,
                            pair.Current.Operations) > strideTypeAbsoluteEnvelope
                        || MaximumAbsoluteMovementPerOperation(
                            pair.Baseline.Sites,
                            pair.Baseline.Operations,
                            pair.Current.Sites,
                            pair.Current.Operations) > strideSiteAbsoluteEnvelope))
                    .ToArray();
                unsafeEvidence = strideEvidenceAvailable
                    && (strideTypeEnvelope
                            > AllocationDirectionalBounds.MaximumAllowedAaShareMovement
                        || strideSiteEnvelope
                            > AllocationDirectionalBounds.MaximumAllowedAaShareMovement
                        || biased.Length > 0);
                expected = strideEvidenceAvailable && !unsafeEvidence;
                findings.Add(unsafeEvidence
                    ? "Equal-payload reorderings produced sampled type/site absolute or share movement beyond A/A; fixed-stride directional attribution is not safe."
                    : "Equal-payload reorderings stayed within the workload-specific A/A absolute and share envelopes.");
                break;

            case AllocationDirectionalScenario.PartialAttribution:
                assessable = HasRequiredPairs(valid);
                expected = HasRequiredPairs(valid)
                    && valid.All(pair =>
                        pair.Current.Types.Count > 0
                        && pair.Current.Sites.Count == 0
                        && string.Equals(
                            pair.Current.AttributionState,
                            "partial",
                            StringComparison.Ordinal));
                findings.Add(expected
                    ? "Type-level evidence remained available while site-level evidence correctly abstained."
                    : "Partial attribution did not preserve independent per-level availability.");
                break;

            case AllocationDirectionalScenario.MissingOrZeroWeight:
                assessable = attempted.Length >= AllocationDirectionalBounds.RequiredPairs
                    && attempted.All(pair =>
                        pair.Baseline.Outcome == AllocationDirectionalAttemptOutcome.Passed
                        && pair.Current.Outcome == AllocationDirectionalAttemptOutcome.Passed);
                expected = attempted.Length >= AllocationDirectionalBounds.RequiredPairs
                    && attempted.All(pair =>
                        pair.Baseline.Outcome == AllocationDirectionalAttemptOutcome.Passed
                        && pair.Current.Outcome == AllocationDirectionalAttemptOutcome.Passed
                        && pair.Baseline.Workers > 0
                        && pair.Baseline.Operations > 0
                        && pair.Current.Workers == 0
                        && pair.Current.Operations == 0);
                findings.Add(expected
                    ? "Missing or zero-weight evidence produced abstention and remained in the denominator."
                    : "Missing or zero-weight evidence was promoted or not preserved.");
                break;

            case AllocationDirectionalScenario.HealthyAa:
                assessable = HasRequiredPairs(valid);
                expected = HasRequiredPairs(valid)
                    && aaRelativeEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
                    && aaTypeShareEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaShareMovement
                    && aaSiteShareEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaShareMovement;
                unsafeEvidence = HasRequiredPairs(valid) && !expected;
                findings.Add(expected
                    ? "The three valid A/A pairs define provenance-only variability envelopes."
                    : "Healthy A/A lacked the three predeclared valid pairs.");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        return new(
            scenario,
            attempted.Length,
            valid.Length,
            expected,
            unsafeEvidence,
            findings)
        {
            Assessable = assessable,
        };
    }

    private static AllocationDirectionalStudyReport Inconclusive(
        AllocationDirectionalFixture fixture,
        string limitation)
        => new(
            AllocationDirectionalJson.CurrentSchemaVersion,
            ProtocolId,
            ProtocolVersion,
            AllocationDirectionalDecision.Inconclusive,
            0,
            0,
            0,
            false,
            false,
            [],
            [limitation]);

    private static AllocationDirectionalPair[] ValidPairs(
        AllocationDirectionalFixture fixture,
        AllocationDirectionalScenario scenario)
        => fixture.Pairs.Where(pair => pair.Scenario == scenario && IsValidPair(pair))
            .ToArray();

    private static bool IsValidPair(AllocationDirectionalPair pair)
        => pair.Baseline.Outcome == AllocationDirectionalAttemptOutcome.Passed
            && pair.Current.Outcome == AllocationDirectionalAttemptOutcome.Passed
            && pair.Baseline.Operations > 0
            && pair.Current.Operations > 0
            && pair.Baseline.SampledEstimatedBytes > 0
            && pair.Current.SampledEstimatedBytes > 0
            && string.Equals(
                pair.Baseline.WorkloadIdentity,
                pair.Current.WorkloadIdentity,
                StringComparison.Ordinal);

    private static bool HasRequiredPairs(AllocationDirectionalPair[] pairs)
        => pairs.Length >= AllocationDirectionalBounds.RequiredPairs;

    private static AllocationDirectionalPair[] CalibrationPairs(
        AllocationDirectionalPair[] pairs)
        => pairs.Where(pair => pair.Current.Phase == "aa").ToArray();

    private static AllocationDirectionalPair[] EffectPairs(
        AllocationDirectionalPair[] pairs)
        => pairs.Where(pair => pair.Current.Phase != "aa").ToArray();

    private static decimal RelativeEnvelope(AllocationDirectionalPair[] pairs)
        => HasRequiredPairs(pairs)
            ? pairs.Max(pair => AbsoluteRelativeDelta(
                EstimatedBytesPerOperation(pair.Baseline),
                EstimatedBytesPerOperation(pair.Current)))
            : decimal.MaxValue;

    private static decimal EstimatedBytesPerOperation(AllocationDirectionalObservation value)
        => value.SampledEstimatedBytes / (decimal)value.Operations;

    private static decimal OperationsPerSecond(AllocationDirectionalObservation value)
        => value.Operations / (decimal)value.CaptureDurationSeconds;

    private static decimal EstimatedBytesPerSecond(AllocationDirectionalObservation value)
        => value.SampledEstimatedBytes / (decimal)value.CaptureDurationSeconds;

    private static decimal RelativeDelta(decimal baseline, decimal current)
        => baseline == 0 ? 0 : (current - baseline) / Math.Abs(baseline);

    private static decimal AbsoluteRelativeDelta(decimal baseline, decimal current)
        => Math.Abs(RelativeDelta(baseline, current));

    private static string? TopKey(IReadOnlyList<AllocationDirectionalRankedRow> rows)
        => rows.OrderByDescending(row => row.EstimatedBytes)
            .ThenBy(row => row.Key, StringComparer.Ordinal)
            .FirstOrDefault()?.Key;

    private static decimal? MaximumShareMovement(
        IReadOnlyList<AllocationDirectionalRankedRow> baseline,
        IReadOnlyList<AllocationDirectionalRankedRow> current)
    {
        var baselineTotal = baseline.Sum(row => row.EstimatedBytes);
        var currentTotal = current.Sum(row => row.EstimatedBytes);
        if (baselineTotal <= 0 || currentTotal <= 0)
        {
            return null;
        }

        return baseline.Select(row => row.Key)
            .Concat(current.Select(row => row.Key))
            .Distinct(StringComparer.Ordinal)
            .Select(key =>
            {
                var left = baseline.Where(row => row.Key == key).Sum(row => row.EstimatedBytes)
                    / (decimal)baselineTotal;
                var right = current.Where(row => row.Key == key).Sum(row => row.EstimatedBytes)
                    / (decimal)currentTotal;
                return Math.Abs(right - left);
            })
            .DefaultIfEmpty(0)
            .Max();
    }

    private static decimal? MaximumAbsoluteMovementPerOperation(
        IReadOnlyList<AllocationDirectionalRankedRow> baseline,
        long baselineOperations,
        IReadOnlyList<AllocationDirectionalRankedRow> current,
        long currentOperations)
    {
        if (baselineOperations <= 0 || currentOperations <= 0)
        {
            return null;
        }

        return baseline.Select(row => row.Key)
            .Concat(current.Select(row => row.Key))
            .Distinct(StringComparer.Ordinal)
            .Select(key =>
            {
                var left = baseline.Where(row => row.Key == key)
                    .Sum(row => row.EstimatedBytes) / (decimal)baselineOperations;
                var right = current.Where(row => row.Key == key)
                    .Sum(row => row.EstimatedBytes) / (decimal)currentOperations;
                return Math.Abs(right - left);
            })
            .DefaultIfEmpty(0)
            .Max();
    }

    private static void Validate(AllocationDirectionalFixture fixture)
    {
        if (fixture.SchemaVersion != AllocationDirectionalJson.CurrentSchemaVersion
            || !string.Equals(fixture.ProtocolId, ProtocolId, StringComparison.Ordinal)
            || !string.Equals(fixture.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fixture.Environment)
            || fixture.Pairs.Count is < 1 or > AllocationDirectionalBounds.MaximumPairs
            || fixture.Pairs.Select(pair => pair.PairId).Distinct(StringComparer.Ordinal).Count()
                != fixture.Pairs.Count)
        {
            throw new InvalidDataException("Allocation directional fixture identity or bounds are invalid.");
        }

        if (fixture.Pairs.SelectMany(pair => new[] { pair.Baseline, pair.Current })
            .Any(observation =>
                observation.Types.Count > AllocationDirectionalBounds.MaximumRankedRows
                || observation.Sites.Count > AllocationDirectionalBounds.MaximumRankedRows))
        {
            throw new InvalidDataException("Allocation directional ranked evidence is out of bounds.");
        }

        var live = fixture.Pairs.SelectMany(pair => new[] { pair.Baseline, pair.Current })
            .Where(observation =>
                observation.Origin == AllocationDirectionalOrigin.LiveMcp)
            .ToArray();
        if (string.Equals(
                fixture.ProtocolVersion,
                ProtocolVersion,
                StringComparison.Ordinal)
            && (live.Any(observation =>
                    string.IsNullOrWhiteSpace(observation.ProcessIdentity)
                    || string.IsNullOrWhiteSpace(observation.CaptureId)
                    || observation.QuantityEvidence is null)
                || live.Select(observation => observation.ProcessIdentity)
                    .Distinct(StringComparer.Ordinal).Count() != live.Length
                || live.Select(observation => observation.CaptureId)
                    .Distinct(StringComparer.Ordinal).Count() != live.Length))
        {
            throw new InvalidDataException(
                "V2 live observations require unique actual process and capture identities plus explicit quantity evidence.");
        }
    }
}
