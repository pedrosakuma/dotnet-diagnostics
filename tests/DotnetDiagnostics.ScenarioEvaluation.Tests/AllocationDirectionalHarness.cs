namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class AllocationDirectionalHarness
{
    public const string ProtocolId = "allocation-directional-baseline-current";
    public const string ProtocolVersion = "1.0.0";

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
            result.ExpectedBehaviorObserved
            && !result.UnsafeDirectionalEvidenceObserved);
        var decision = results.Any(result => result.UnsafeDirectionalEvidenceObserved)
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

        switch (scenario)
        {
            case AllocationDirectionalScenario.TrueIncrease:
                expected = HasRequiredPairs(valid)
                    && valid.All(pair =>
                        RelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                        > aaRelativeEnvelope);
                findings.Add(expected
                    ? "Every valid pair increased estimated sampled bytes per operation beyond the complete A/A envelope."
                    : "The intended equal-work increase did not separate from A/A variability in every pair.");
                unsafeEvidence = HasRequiredPairs(valid) && !expected;
                break;

            case AllocationDirectionalScenario.MixShiftStableTotal:
                var mixEvidenceAvailable = HasRequiredPairs(valid)
                    && valid.All(pair =>
                        pair.Baseline.Types.Count > 0
                        && pair.Current.Types.Count > 0
                        && pair.Baseline.Sites.Count > 0
                        && pair.Current.Sites.Count > 0);
                expected = mixEvidenceAvailable
                    && valid.All(pair =>
                        pair.Baseline.RequestedPayloadBytesPerOperation
                            == pair.Current.RequestedPayloadBytesPerOperation
                        && AbsoluteRelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                            <= aaRelativeEnvelope
                        && TopKey(pair.Baseline.Types) != TopKey(pair.Current.Types)
                        && TopKey(pair.Baseline.Sites) != TopKey(pair.Current.Sites));
                findings.Add(expected
                    ? "The equal-payload mix shift changed both independent leading inventories without a total-pressure movement beyond A/A."
                    : "The mix-shift control did not preserve stable normalized total evidence while changing both leading inventories.");
                unsafeEvidence = mixEvidenceAvailable && !expected;
                break;

            case AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation:
                var throughputAa = valid.Where(pair => pair.Current.Phase == "aa").ToArray();
                var intensity = valid.Where(pair => pair.Current.Phase == "intensity").ToArray();
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
                unsafeEvidence = HasRequiredPairs(throughputAa)
                    && HasRequiredPairs(intensity)
                    && !expected;
                findings.Add(expected
                    ? "Higher throughput retained stable estimated sampled bytes per operation."
                    : "Throughput normalization did not remain inside the A/A envelope.");
                break;

            case AllocationDirectionalScenario.LowerAllocationStableRetained:
                expected = HasRequiredPairs(valid)
                    && valid.All(pair =>
                        pair.Baseline.RetainedBytes == pair.Current.RetainedBytes
                        && RelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                            < -aaRelativeEnvelope);
                findings.Add(expected
                    ? "Allocation activity decreased while the independently declared retained quantity stayed unchanged."
                    : "The lower-allocation control did not preserve its unchanged retained quantity or separate from A/A.");
                unsafeEvidence = HasRequiredPairs(valid) && !expected;
                break;

            case AllocationDirectionalScenario.WarmupTransientConverges:
                var early = valid.Where(pair => pair.Current.Phase == "early").ToArray();
                var converged = valid.Where(pair => pair.Current.Phase == "converged").ToArray();
                expected = HasRequiredPairs(early)
                    && HasRequiredPairs(converged)
                    && early.All(pair =>
                        RelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                            > aaRelativeEnvelope)
                    && converged.All(pair =>
                        AbsoluteRelativeDelta(
                            EstimatedBytesPerOperation(pair.Baseline),
                            EstimatedBytesPerOperation(pair.Current))
                            <= aaRelativeEnvelope);
                findings.Add(expected
                    ? "The early movement disappeared in the predeclared converged phase."
                    : "The transient control did not show the frozen early-then-converged shape.");
                unsafeEvidence = HasRequiredPairs(early)
                    && HasRequiredPairs(converged)
                    && !expected;
                break;

            case AllocationDirectionalScenario.ChangedWorkload:
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
                var reordered = valid.Where(pair => pair.Current.Phase == "reorder").ToArray();
                var strideTypeMovements = strideAa.Select(pair =>
                    MaximumShareMovement(pair.Baseline.Types, pair.Current.Types)).ToArray();
                var strideSiteMovements = strideAa.Select(pair =>
                    MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites)).ToArray();
                var strideEvidenceAvailable = HasRequiredPairs(strideAa)
                    && HasRequiredPairs(reordered)
                    && strideTypeMovements.All(value => value is not null)
                    && strideSiteMovements.All(value => value is not null)
                    && reordered.All(pair =>
                        MaximumShareMovement(pair.Baseline.Types, pair.Current.Types) is not null
                        && MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites) is not null);
                var strideTypeEnvelope = strideEvidenceAvailable
                    ? strideTypeMovements.Max()!.Value
                    : decimal.MaxValue;
                var strideSiteEnvelope = strideEvidenceAvailable
                    ? strideSiteMovements.Max()!.Value
                    : decimal.MaxValue;
                var biased = reordered.Where(pair =>
                    pair.Baseline.RequestedPayloadBytesPerOperation
                        == pair.Current.RequestedPayloadBytesPerOperation
                    && (MaximumShareMovement(pair.Baseline.Types, pair.Current.Types)
                            > strideTypeEnvelope
                        || MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites)
                            > strideSiteEnvelope))
                    .ToArray();
                unsafeEvidence = strideEvidenceAvailable
                    && (strideTypeEnvelope
                            > AllocationDirectionalBounds.MaximumAllowedAaShareMovement
                        || strideSiteEnvelope
                            > AllocationDirectionalBounds.MaximumAllowedAaShareMovement
                        || biased.Length > 0);
                expected = strideEvidenceAvailable && !unsafeEvidence;
                findings.Add(unsafeEvidence
                    ? "Equal-payload reorderings produced sampled type/site share movement beyond A/A; directional attribution is not safe."
                    : "Equal-payload reorderings stayed within the A/A type/site share envelopes.");
                break;

            case AllocationDirectionalScenario.PartialAttribution:
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
            findings);
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
    }
}
