namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalV4Harness
{
    public static AllocationDirectionalStudyReport Evaluate(
        AllocationDirectionalFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        Validate(fixture);

        var live = fixture.Pairs.SelectMany(pair =>
                new[] { pair.Baseline, pair.Current })
            .Where(observation =>
                observation.Origin == AllocationDirectionalOrigin.LiveMcp)
            .ToArray();
        var overOperatingLimit = live.Where(observation =>
                observation.Outcome == AllocationDirectionalAttemptOutcome.Passed
                && observation.SampledAllocationTicks
                    > AllocationDirectionalV4CapturePlan
                        .AllocationEventOperatingLimit)
            .ToArray();
        if (overOperatingLimit.Length > 0)
        {
            return Terminal(
                AllocationDirectionalDecision.NoGo,
                "A complete successful side exceeded the predeclared 75,000-event operating limit.");
        }

        var healthyPairs = ScenarioPairs(
            fixture,
            AllocationDirectionalScenario.HealthyAa);
        var healthy =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateHealthyAa(
                healthyPairs);
        if (!healthy.Assessable)
        {
            return Terminal(
                AllocationDirectionalDecision.Inconclusive,
                healthy.AbstentionReason
                    ?? "Healthy A/A did not retain the required pair-first inventory.");
        }

        var healthyValid = healthyPairs.Where(
            AllocationDirectionalCalibrationSelector.IsValidPair).ToArray();
        var typeEnvelope = RobustInlierEnvelope(healthyValid.Select(pair =>
            MaximumShareMovement(pair.Baseline.Types, pair.Current.Types)));
        var siteEnvelope = RobustInlierEnvelope(healthyValid.Select(pair =>
            MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites)));
        if (typeEnvelope is null || siteEnvelope is null)
        {
            return Terminal(
                AllocationDirectionalDecision.Inconclusive,
                "Healthy A/A lacked independently available type or site inventories.");
        }

        var results = Enum.GetValues<AllocationDirectionalScenario>()
            .Select(scenario => EvaluateScenario(fixture, scenario))
            .ToArray();
        var numericGatePassed = results.All(result =>
            result.Assessable
            && result.ExpectedBehaviorObserved
            && !result.UnsafeDirectionalEvidenceObserved);
        var decision = results.Any(result => !result.Assessable)
            ? AllocationDirectionalDecision.Inconclusive
            : results.Any(result => result.UnsafeDirectionalEvidenceObserved)
                ? AllocationDirectionalDecision.NoGo
                : AllocationDirectionalDecision.Inconclusive;
        return new(
            AllocationDirectionalJson.CurrentSchemaVersion,
            AllocationDirectionalV4Protocol.ProtocolId,
            AllocationDirectionalV4Protocol.ProtocolVersion,
            decision,
            healthy.AaEnvelope,
            typeEnvelope.Value,
            siteEnvelope.Value,
            numericGatePassed,
            numericGatePassed,
            results,
            [
                "No measurement-quality field is model-visible. Compatibility, availability, boundedness, provenance, fixed-stride limitations, and A/A envelopes are evaluator-only gates or explicit abstention reasons.",
                "GCAllocationTick estimated bytes are sampled weights, not exact totals, retained bytes, object counts, severity, health, regression, or causality.",
                "Every live side used the reviewed one-second capture, 200 ms arm, and 700 ms workload geometry. Calibrated low and periodic shapes pause one millisecond after every eight operations.",
                "Every successful live side remained subject to the predeclared 75,000-event operating limit; Core caps remain unchanged.",
                "Each live side is a separately started process. Failed and aborted sides remain in pair denominators and are never retried or replaced.",
                "Operations per second proves only the calibrated throughput manipulation; estimated sampled bytes per operation is the normalized allocation quantity.",
                "Allocated types and allocation sites are independent inventories; no type-to-site relationship is inferred.",
                numericGatePassed
                    ? "The numeric gate passed, but the final decision remains INCONCLUSIVE until the separately frozen blinded evaluation completes."
                    : "The numeric gate did not authorize blinded evaluation.",
            ]);
    }

    private static AllocationDirectionalScenarioResult EvaluateScenario(
        AllocationDirectionalFixture fixture,
        AllocationDirectionalScenario scenario)
    {
        var attempted = ScenarioPairs(fixture, scenario);
        var findings = new List<string>();
        var assessable = false;
        var expected = false;
        var unsafeEvidence = false;

        switch (scenario)
        {
            case AllocationDirectionalScenario.TrueIncrease:
            {
                var gate =
                    AllocationDirectionalPracticalEquivalenceSelector
                        .EvaluateMovement(attempted, expectedIncrease: true);
                assessable = gate.Assessable;
                expected = gate.ExpectedMovementEstablished;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? $"The pair-first median increase ({gate.RelativeDeltas.Median:P4}) strictly exceeded the practical/A/A floor ({gate.DecisionFloor:P4}) with retained-inlier sign agreement."
                    : gate.AbstentionReason
                        ?? "The normalized increase contract did not pass.");
                break;
            }

            case AllocationDirectionalScenario.MixShiftStableTotal:
            {
                var gate =
                    AllocationDirectionalPracticalEquivalenceSelector
                        .EvaluateMix(attempted);
                assessable = gate.Assessable;
                expected = gate.Passed;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? $"The normalized total remained within the {gate.StableTotal.EquivalenceMargin:P2} practical-equivalence margin while both type and site exceeded independent absolute and share floors."
                    : gate.AbstentionReason
                        ?? "The mix contract did not pass.");
                break;
            }

            case AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation:
            {
                var gate =
                    AllocationDirectionalPracticalEquivalenceSelector
                        .EvaluateThroughput(attempted);
                assessable = gate.Assessable;
                expected = gate.Passed;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? $"The median throughput multiplier ({gate.ThroughputMultipliers.Median:F4}x) strictly exceeded 1.25x while normalized estimated sampled bytes per operation remained within the {gate.EquivalenceMargin:P2} equivalence margin."
                    : gate.AbstentionReason
                        ?? "The throughput manipulation/equivalence contract did not pass.");
                break;
            }

            case AllocationDirectionalScenario.LowerAllocationStableRetained:
            {
                var gate =
                    AllocationDirectionalPracticalEquivalenceSelector
                        .EvaluateMovement(attempted, expectedIncrease: false);
                var effect = attempted.Where(pair =>
                    string.Equals(
                        pair.Current.Phase,
                        "effect",
                        StringComparison.Ordinal)
                    && AllocationDirectionalCalibrationSelector.IsValidPair(pair))
                    .ToArray();
                var retainedStable =
                    effect.Length == AllocationDirectionalBounds.RequiredPairs
                    && effect.All(pair =>
                        pair.Baseline.RetainedBytes == pair.Current.RetainedBytes);
                assessable = gate.Assessable
                    && effect.Length == AllocationDirectionalBounds.RequiredPairs;
                expected = assessable
                    && gate.ExpectedMovementEstablished
                    && retainedStable;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? $"The pair-first median decrease ({gate.RelativeDeltas.Median:P4}) strictly exceeded the practical/A/A floor ({gate.DecisionFloor:P4}) while independently declared retained bytes stayed unchanged."
                    : !retainedStable
                        ? "The lower-allocation scenario did not preserve the independently declared retained quantity."
                        : gate.AbstentionReason
                            ?? "The normalized decrease contract did not pass.");
                break;
            }

            case AllocationDirectionalScenario.WarmupTransientConverges:
            {
                var gate =
                    AllocationDirectionalPracticalEquivalenceSelector
                        .EvaluateWarmup(attempted);
                assessable = gate.Assessable;
                expected = gate.Passed;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? $"The early median movement ({gate.EarlyRelativeDeltas.Median:P4}) strictly exceeded the {gate.EarlyDecisionFloor:P2} floor and the converged summary returned inclusively within the {gate.ConvergenceEquivalenceMargin:P2} equivalence margin."
                    : gate.AbstentionReason
                        ?? "The warmup early/converged contract did not pass.");
                break;
            }

            case AllocationDirectionalScenario.ChangedWorkload:
                assessable = attempted.Length
                        == AllocationDirectionalBounds.RequiredPairs
                    && attempted.All(Passed);
                expected = assessable
                    && attempted.All(pair => !string.Equals(
                        pair.Baseline.WorkloadIdentity,
                        pair.Current.WorkloadIdentity,
                        StringComparison.Ordinal));
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? "Every changed-workload pair was rejected by identity compatibility before directional interpretation."
                    : "The changed-workload control was incomplete or compatible evidence was promoted.");
                break;

            case AllocationDirectionalScenario.FixedStrideBias:
            {
                var gate =
                    AllocationDirectionalPracticalEquivalenceSelector
                        .EvaluateStability(attempted);
                var effect = attempted.Where(pair =>
                    string.Equals(
                        pair.Current.Phase,
                        "effect",
                        StringComparison.Ordinal)
                    && AllocationDirectionalCalibrationSelector.IsValidPair(pair))
                    .ToArray();
                var bothDirections = effect.Count(pair =>
                        pair.ComparisonAxis == "allocation-order-a-to-b")
                        == AllocationDirectionalBounds.RequiredPairs
                    && effect.Count(pair =>
                        pair.ComparisonAxis == "allocation-order-b-to-a")
                        == AllocationDirectionalBounds.RequiredPairs;
                var attributionAvailable = effect.All(pair =>
                    pair.Baseline.Types.Count > 0
                    && pair.Current.Types.Count > 0
                    && pair.Baseline.Sites.Count > 0
                    && pair.Current.Sites.Count > 0);
                assessable = gate.Assessable
                    && effect.Length
                        == AllocationDirectionalBounds.RequiredPairs * 2
                    && bothDirections
                    && attributionAvailable;
                expected = assessable
                    && gate.Equivalent
                    && effect.All(pair =>
                        pair.Baseline.RequestedPayloadBytesPerOperation
                            == pair.Current.RequestedPayloadBytesPerOperation);
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? "Both bounded periodic A/A inventories and both equal-payload reorder directions completed with equivalent normalized totals; type/site pressure and regression interpretation remains suppressed under fixed-stride sensitivity."
                    : gate.AbstentionReason
                        ?? "The fixed-stride bounded/reorder control did not retain equivalent totals and independent attribution.");
                break;
            }

            case AllocationDirectionalScenario.PartialAttribution:
                assessable = attempted.Length
                        == AllocationDirectionalBounds.RequiredPairs
                    && attempted.All(
                        AllocationDirectionalCalibrationSelector.IsValidPair);
                expected = assessable
                    && attempted.All(pair =>
                        pair.Current.Types.Count > 0
                        && pair.Current.Sites.Count == 0
                        && pair.Current.AttributionState == "partial"
                        && pair.Current.TypeAvailability == "available"
                        && pair.Current.SiteAvailability
                            == "unavailable-partial-attribution");
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? "Type evidence remained independently available while site evidence explicitly abstained."
                    : "Partial attribution did not preserve independent type/site availability.");
                break;

            case AllocationDirectionalScenario.MissingOrZeroWeight:
                assessable = attempted.Length
                        == AllocationDirectionalBounds.RequiredPairs
                    && attempted.All(Passed);
                expected = assessable
                    && attempted.All(pair =>
                        pair.Baseline.Operations > 0
                        && pair.Current.Operations == 0
                        && pair.Current.QuantityEvidence is
                        {
                            Availability: "unavailable",
                            AbstentionReason: not null,
                        });
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? "Missing or zero-weight evidence produced explicit abstention and remained in the denominator."
                    : "Missing or zero-weight evidence was promoted or not preserved.");
                break;

            case AllocationDirectionalScenario.HealthyAa:
            {
                var gate =
                    AllocationDirectionalPracticalEquivalenceSelector
                        .EvaluateHealthyAa(attempted);
                assessable = gate.Assessable;
                expected = gate.WithinPracticalSanity;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? $"Healthy A/A retained pair-first median/MAD evidence at or below the {AllocationDirectionalCalibrationBounds.PracticalRelativeEquivalenceFloor:P0} sanity bound."
                    : gate.AbstentionReason
                        ?? "Healthy A/A exceeded the practical sanity bound.");
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        return new(
            scenario,
            attempted.Length,
            attempted.Count(
                AllocationDirectionalCalibrationSelector.IsValidPair),
            expected,
            unsafeEvidence,
            findings)
        {
            Assessable = assessable,
        };
    }

    private static AllocationDirectionalStudyReport Terminal(
        AllocationDirectionalDecision decision,
        string limitation)
        => new(
            AllocationDirectionalJson.CurrentSchemaVersion,
            AllocationDirectionalV4Protocol.ProtocolId,
            AllocationDirectionalV4Protocol.ProtocolVersion,
            decision,
            0,
            0,
            0,
            false,
            false,
            [],
            [limitation]);

    private static void Validate(AllocationDirectionalFixture fixture)
    {
        if (fixture.SchemaVersion != AllocationDirectionalJson.CurrentSchemaVersion
            || fixture.ProtocolId != AllocationDirectionalV4Protocol.ProtocolId
            || fixture.ProtocolVersion
                != AllocationDirectionalV4Protocol.ProtocolVersion
            || fixture.SourceBaseCommit
                != AllocationDirectionalV4Protocol.SourceBaseCommit
            || string.IsNullOrWhiteSpace(fixture.ProtocolSha256)
            || fixture.ProtocolSha256 != fixture.CanonicalProtocolSha256
            || string.IsNullOrWhiteSpace(fixture.Environment)
            || fixture.Pairs.Count != AllocationDirectionalV4CapturePlan.TotalPairs
            || fixture.Pairs.Select(pair => pair.PairId)
                .Distinct(StringComparer.Ordinal).Count() != fixture.Pairs.Count)
        {
            throw new InvalidDataException(
                "Allocation directional v4 fixture identity or inventory is invalid.");
        }

        ValidatePlanParity(fixture);
        var observations = fixture.Pairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();
        if (observations.Any(observation =>
                observation.Types.Count
                    > AllocationDirectionalBounds.MaximumRankedRows
                || observation.Sites.Count
                    > AllocationDirectionalBounds.MaximumRankedRows)
            || observations.Select(observation => observation.AttemptId)
                .Distinct(StringComparer.Ordinal).Count() != observations.Length
            || observations.Select(observation => observation.CaptureId)
                .Any(string.IsNullOrWhiteSpace)
            || observations.Any(observation =>
                observation.QuantityEvidence is null))
        {
            throw new InvalidDataException(
                "Allocation directional v4 observations violate bounds or explicit provenance requirements.");
        }

        foreach (var observation in observations.Where(value =>
                     value.Origin == AllocationDirectionalOrigin.LiveMcp))
        {
            var evidence = observation.AttemptEvidence
                ?? throw new InvalidDataException(
                    "Every live allocation directional v4 observation requires bounded attempt evidence.");
            if (observation.Outcome == AllocationDirectionalAttemptOutcome.Passed)
            {
                if (evidence.FailureCategory is not null
                    || evidence.Stage != AllocationDirectionalAttemptStage.Completed
                    || evidence.IsError != false)
                {
                    throw new InvalidDataException(
                        "A passed allocation directional v4 observation has non-truthful success provenance.");
                }
            }
            else if (evidence.FailureCategory is null)
            {
                throw new InvalidDataException(
                    "A failed or aborted allocation directional v4 observation lacks a truthful failure category.");
            }
        }

        var live = observations.Where(observation =>
            observation.Origin == AllocationDirectionalOrigin.LiveMcp).ToArray();
        if (live.Length != AllocationDirectionalV4CapturePlan.LiveMcpSides
            || live.Select(observation => observation.ProcessIdentity)
                .Distinct(StringComparer.Ordinal).Count() != live.Length
            || live.Select(observation => observation.CaptureId)
                .Distinct(StringComparer.Ordinal).Count() != live.Length)
        {
            throw new InvalidDataException(
                "Allocation directional v4 live process or capture identities are incomplete or reused.");
        }
    }

    private static void ValidatePlanParity(AllocationDirectionalFixture fixture)
    {
        var index = 0;
        foreach (var entry in AllocationDirectionalV4CapturePlan.Entries)
        {
            for (var repetition = 1;
                 repetition <= AllocationDirectionalBounds.RequiredPairs;
                 repetition++)
            {
                var pair = fixture.Pairs[index++];
                if (pair.PairId != $"{entry.PairPrefix}-{repetition:D2}"
                    || pair.Scenario != entry.Scenario
                    || pair.ComparisonAxis != entry.ComparisonAxis
                    || !MatchesSide(pair.Baseline, entry.Baseline)
                    || !MatchesSide(pair.Current, entry.Current))
                {
                    throw new InvalidDataException(
                        "Allocation directional v4 fixture ordering or plan parity is invalid.");
                }
            }
        }

        for (var repetition = 1;
             repetition <= AllocationDirectionalBounds.RequiredPairs;
             repetition++)
        {
            var pair = fixture.Pairs[index++];
            if (pair.PairId != $"final-v4-partial-{repetition:D2}"
                || pair.Scenario != AllocationDirectionalScenario.PartialAttribution
                || pair.Baseline.Origin
                    != AllocationDirectionalOrigin.ControlledProjection
                || pair.Current.Origin
                    != AllocationDirectionalOrigin.ControlledProjection
                || pair.Baseline.WorkloadIdentity
                    != "final-partial-attribution-v4"
                || pair.Current.WorkloadIdentity
                    != "final-partial-attribution-v4")
            {
                throw new InvalidDataException(
                    "Allocation directional v4 controlled projection inventory is invalid.");
            }
        }

        if (index != fixture.Pairs.Count)
        {
            throw new InvalidDataException(
                "Allocation directional v4 plan parity left unregistered pairs.");
        }
    }

    private static bool MatchesSide(
        AllocationDirectionalObservation observation,
        AllocationDirectionalPlannedSide planned)
        => observation.WorkloadIdentity == planned.WorkloadIdentity
            && observation.Shape == planned.Shape
            && observation.Phase == planned.Phase
            && observation.Workers == planned.Workers
            && observation.CaptureDurationSeconds
                == AllocationDirectionalV4CapturePlan.CaptureDurationSeconds
            && (observation.Outcome != AllocationDirectionalAttemptOutcome.Passed
                || observation.RequestedPayloadBytesPerOperation
                    == planned.ExpectedPayloadBytesPerOperation
                && observation.RetainedBytes == planned.RetainedBytes);

    private static AllocationDirectionalPair[] ScenarioPairs(
        AllocationDirectionalFixture fixture,
        AllocationDirectionalScenario scenario)
        => fixture.Pairs.Where(pair => pair.Scenario == scenario).ToArray();

    private static bool Passed(AllocationDirectionalPair pair)
        => pair.Baseline.Outcome == AllocationDirectionalAttemptOutcome.Passed
            && pair.Current.Outcome == AllocationDirectionalAttemptOutcome.Passed;

    private static decimal? RobustInlierEnvelope(IEnumerable<decimal?> values)
    {
        var materialized = values.ToArray();
        if (materialized.Any(value => value is null))
        {
            return null;
        }

        var summary = AllocationDirectionalCalibrationSelector.Summarize(
            materialized.Select(value => value!.Value));
        return summary.HasRequiredPairs && summary.HasRequiredInliers
            ? summary.Inliers.Max()
            : null;
    }

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
                var left = baseline.Where(row => row.Key == key)
                    .Sum(row => row.EstimatedBytes) / (decimal)baselineTotal;
                var right = current.Where(row => row.Key == key)
                    .Sum(row => row.EstimatedBytes) / (decimal)currentTotal;
                return Math.Abs(right - left);
            })
            .DefaultIfEmpty()
            .Max();
    }
}
