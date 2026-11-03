namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalV3Harness
{
    public static AllocationDirectionalStudyReport Evaluate(
        AllocationDirectionalFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        Validate(fixture);

        var healthy = ValidPairs(fixture, AllocationDirectionalScenario.HealthyAa);
        if (!HasRequiredPairs(healthy))
        {
            return Inconclusive(
                "Healthy A/A did not retain the three predeclared valid independent pairs.");
        }

        var aaRelativeEnvelope = RelativeEnvelope(healthy);
        var aaTypeMovements = healthy.Select(pair =>
            MaximumShareMovement(pair.Baseline.Types, pair.Current.Types)).ToArray();
        var aaSiteMovements = healthy.Select(pair =>
            MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites)).ToArray();
        if (aaTypeMovements.Any(value => value is null)
            || aaSiteMovements.Any(value => value is null))
        {
            return Inconclusive(
                "Healthy A/A lacked type or site evidence required by the frozen share envelopes.");
        }

        var aaTypeEnvelope = aaTypeMovements.Max()!.Value;
        var aaSiteEnvelope = aaSiteMovements.Max()!.Value;
        var results = Enum.GetValues<AllocationDirectionalScenario>()
            .Select(scenario => EvaluateScenario(
                fixture,
                scenario,
                aaRelativeEnvelope,
                aaTypeEnvelope,
                aaSiteEnvelope))
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
            AllocationDirectionalV3Protocol.ProtocolId,
            AllocationDirectionalV3Protocol.ProtocolVersion,
            decision,
            aaRelativeEnvelope,
            aaTypeEnvelope,
            aaSiteEnvelope,
            numericGatePassed,
            numericGatePassed,
            results,
            [
                "No measurement-quality field is model-visible. Compatibility, availability, provenance, fixed-stride limitations, and A/A envelopes are evaluator-only gates or explicit abstention reasons.",
                "GCAllocationTick estimated bytes are sampled weights, not exact totals, retained bytes, object counts, severity, health, regression, or causality.",
                "Each live side is a separately started process. Failed and aborted sides remain in pair denominators and are never retried or replaced.",
                "Operations per second proves only the calibrated throughput manipulation; estimated sampled bytes per operation is the normalized allocation quantity.",
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
        decimal aaTypeEnvelope,
        decimal aaSiteEnvelope)
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
            {
                var gate = AllocationDirectionalCalibrationSelector.EvaluateMovement(
                    attempted,
                    expectedIncrease: true);
                assessable = gate.Assessable;
                expected = gate.ExpectedMovementEstablished;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? $"The pair-first median increase ({gate.RelativeDeltas.Median:P4}) exceeded the workload-specific A/A envelope ({gate.AaEnvelope:P4}) with non-outlier sign agreement."
                    : gate.AbstentionReason
                        ?? "The normalized increase was not established.");
                break;
            }

            case AllocationDirectionalScenario.MixShiftStableTotal:
                EvaluateMix(
                    valid,
                    out assessable,
                    out expected,
                    out unsafeEvidence,
                    findings);
                break;

            case AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation:
            {
                var gate = AllocationDirectionalCalibrationSelector.EvaluateThroughput(
                    attempted);
                assessable = gate.Assessable;
                expected = gate.Passed;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? $"The calibrated paced/unpaced manipulation reached a median {gate.ThroughputMultipliers.Median:F5}x operations/second while the median absolute estimated-bytes/operation delta ({gate.EstimatedBytesPerOperationAbsoluteRelativeDeltas.Median:P4}) remained inside A/A."
                    : gate.AbstentionReason
                        ?? "The throughput manipulation or normalized stability gate failed.");
                break;
            }

            case AllocationDirectionalScenario.LowerAllocationStableRetained:
            {
                var gate = AllocationDirectionalCalibrationSelector.EvaluateMovement(
                    attempted,
                    expectedIncrease: false);
                var retainedStable = EffectPairs(valid).All(pair =>
                    pair.Baseline.RetainedBytes == pair.Current.RetainedBytes);
                assessable = gate.Assessable
                    && EffectPairs(valid).Length
                        >= AllocationDirectionalBounds.RequiredPairs;
                expected = assessable
                    && gate.ExpectedMovementEstablished
                    && retainedStable;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? $"The pair-first median decrease ({gate.RelativeDeltas.Median:P4}) exceeded the negative A/A envelope while independently declared retained bytes stayed unchanged."
                    : !retainedStable
                        ? "The lower-allocation scenario did not preserve the independently declared retained quantity."
                        : gate.AbstentionReason
                            ?? "The normalized decrease was not established.");
                break;
            }

            case AllocationDirectionalScenario.WarmupTransientConverges:
            {
                var gate = AllocationDirectionalCalibrationSelector.EvaluateWarmup(
                    attempted);
                assessable = gate.Assessable;
                expected = gate.Passed;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? $"The early median movement ({gate.EarlyRelativeDeltas.Median:P4}) exceeded the strict {gate.EarlyDecisionFloor:P2} practical floor with sign agreement, then the converged median returned inside A/A."
                    : gate.AbstentionReason
                        ?? "The early-then-converged warmup shape was not established.");
                break;
            }

            case AllocationDirectionalScenario.ChangedWorkload:
                assessable = attempted.Length >= AllocationDirectionalBounds.RequiredPairs
                    && attempted.All(Passed);
                expected = assessable
                    && attempted.All(pair => !string.Equals(
                        pair.Baseline.WorkloadIdentity,
                        pair.Current.WorkloadIdentity,
                        StringComparison.Ordinal));
                findings.Add(expected
                    ? "Every changed-workload pair abstained before directional interpretation."
                    : "A changed-workload control was incomplete or compatible evidence was promoted.");
                break;

            case AllocationDirectionalScenario.FixedStrideBias:
                EvaluateFixedStride(
                    valid,
                    out assessable,
                    out expected,
                    out unsafeEvidence,
                    findings);
                break;

            case AllocationDirectionalScenario.PartialAttribution:
                assessable = HasRequiredPairs(valid);
                expected = assessable
                    && valid.All(pair =>
                        pair.Current.Types.Count > 0
                        && pair.Current.Sites.Count == 0
                        && string.Equals(
                            pair.Current.AttributionState,
                            "partial",
                            StringComparison.Ordinal)
                        && string.Equals(
                            pair.Current.TypeAvailability,
                            "available",
                            StringComparison.Ordinal)
                        && string.Equals(
                            pair.Current.SiteAvailability,
                            "unavailable-partial-attribution",
                            StringComparison.Ordinal));
                findings.Add(expected
                    ? "Type-level evidence remained available while site-level evidence independently abstained."
                    : "Partial attribution did not preserve independent per-level availability.");
                break;

            case AllocationDirectionalScenario.MissingOrZeroWeight:
                assessable = attempted.Length >= AllocationDirectionalBounds.RequiredPairs
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
                findings.Add(expected
                    ? "Missing or zero-weight evidence produced explicit abstention and remained in the denominator."
                    : "Missing or zero-weight evidence was promoted or not preserved.");
                break;

            case AllocationDirectionalScenario.HealthyAa:
                assessable = HasRequiredPairs(valid);
                expected = assessable
                    && aaRelativeEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
                    && aaTypeEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaShareMovement
                    && aaSiteEnvelope
                        <= AllocationDirectionalBounds.MaximumAllowedAaShareMovement;
                unsafeEvidence = assessable && !expected;
                findings.Add(expected
                    ? "The three healthy A/A pairs define bounded evaluator-only variability envelopes."
                    : "Healthy A/A variability exceeded the predeclared compatibility bounds.");
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

    private static void EvaluateMix(
        AllocationDirectionalPair[] valid,
        out bool assessable,
        out bool expected,
        out bool unsafeEvidence,
        List<string> findings)
    {
        var aa = CalibrationPairs(valid);
        var effect = EffectPairs(valid);
        var aaTypeShares = aa.Select(pair =>
            MaximumShareMovement(pair.Baseline.Types, pair.Current.Types)).ToArray();
        var aaSiteShares = aa.Select(pair =>
            MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites)).ToArray();
        var aaTypeAbsolute = aa.Select(pair => MaximumAbsoluteMovementPerOperation(
            pair.Baseline.Types,
            pair.Baseline.Operations,
            pair.Current.Types,
            pair.Current.Operations)).ToArray();
        var aaSiteAbsolute = aa.Select(pair => MaximumAbsoluteMovementPerOperation(
            pair.Baseline.Sites,
            pair.Baseline.Operations,
            pair.Current.Sites,
            pair.Current.Operations)).ToArray();
        assessable = HasRequiredPairs(aa)
            && HasRequiredPairs(effect)
            && aaTypeShares.All(value => value is not null)
            && aaSiteShares.All(value => value is not null)
            && aaTypeAbsolute.All(value => value is not null)
            && aaSiteAbsolute.All(value => value is not null)
            && effect.All(HasAttribution);
        if (!assessable)
        {
            expected = false;
            unsafeEvidence = false;
            findings.Add(
                "The stable-total mix shift lacked complete A/A, effect, type, or site evidence.");
            return;
        }

        var totalEnvelope = RelativeEnvelope(aa);
        var typeShareEnvelope = aaTypeShares.Max()!.Value;
        var siteShareEnvelope = aaSiteShares.Max()!.Value;
        var typeAbsoluteEnvelope = aaTypeAbsolute.Max()!.Value;
        var siteAbsoluteEnvelope = aaSiteAbsolute.Max()!.Value;
        expected = totalEnvelope
                <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
            && typeShareEnvelope
                <= AllocationDirectionalBounds.MaximumAllowedAaShareMovement
            && siteShareEnvelope
                <= AllocationDirectionalBounds.MaximumAllowedAaShareMovement
            && effect.All(pair =>
                pair.Baseline.RequestedPayloadBytesPerOperation
                    == pair.Current.RequestedPayloadBytesPerOperation
                && AbsoluteRelativeDelta(
                    EstimatedBytesPerOperation(pair.Baseline),
                    EstimatedBytesPerOperation(pair.Current)) <= totalEnvelope
                && TopKey(pair.Baseline.Types) != TopKey(pair.Current.Types)
                && TopKey(pair.Baseline.Sites) != TopKey(pair.Current.Sites)
                && MaximumShareMovement(
                    pair.Baseline.Types,
                    pair.Current.Types) > typeShareEnvelope
                && MaximumShareMovement(
                    pair.Baseline.Sites,
                    pair.Current.Sites) > siteShareEnvelope
                && MaximumAbsoluteMovementPerOperation(
                    pair.Baseline.Types,
                    pair.Baseline.Operations,
                    pair.Current.Types,
                    pair.Current.Operations) > typeAbsoluteEnvelope
                && MaximumAbsoluteMovementPerOperation(
                    pair.Baseline.Sites,
                    pair.Baseline.Operations,
                    pair.Current.Sites,
                    pair.Current.Operations) > siteAbsoluteEnvelope);
        unsafeEvidence = assessable && !expected;
        findings.Add(expected
            ? "Stable normalized total accompanied independent type and site movement in both absolute estimated bytes per operation and share."
            : "The mix-shift control did not retain stable normalized total plus paired absolute-and-share movement.");
    }

    private static void EvaluateFixedStride(
        AllocationDirectionalPair[] valid,
        out bool assessable,
        out bool expected,
        out bool unsafeEvidence,
        List<string> findings)
    {
        var aaA = valid.Where(pair =>
            pair.Current.Phase == "aa"
            && pair.Baseline.Shape == "periodic-a").ToArray();
        var aaB = valid.Where(pair =>
            pair.Current.Phase == "aa"
            && pair.Baseline.Shape == "periodic-b").ToArray();
        var reordered = EffectPairs(valid);
        var aa = aaA.Concat(aaB).ToArray();
        var typeShares = aa.Select(pair =>
            MaximumShareMovement(pair.Baseline.Types, pair.Current.Types)).ToArray();
        var siteShares = aa.Select(pair =>
            MaximumShareMovement(pair.Baseline.Sites, pair.Current.Sites)).ToArray();
        var typeAbsolute = aa.Select(pair => MaximumAbsoluteMovementPerOperation(
            pair.Baseline.Types,
            pair.Baseline.Operations,
            pair.Current.Types,
            pair.Current.Operations)).ToArray();
        var siteAbsolute = aa.Select(pair => MaximumAbsoluteMovementPerOperation(
            pair.Baseline.Sites,
            pair.Baseline.Operations,
            pair.Current.Sites,
            pair.Current.Operations)).ToArray();
        assessable = HasRequiredPairs(aaA)
            && HasRequiredPairs(aaB)
            && reordered.Length
                >= AllocationDirectionalBounds.RequiredPairs * 2
            && typeShares.All(value => value is not null)
            && siteShares.All(value => value is not null)
            && typeAbsolute.All(value => value is not null)
            && siteAbsolute.All(value => value is not null)
            && reordered.All(HasAttribution);
        if (!assessable)
        {
            expected = false;
            unsafeEvidence = false;
            findings.Add(
                "Fixed-stride safety requires both workload-specific A/A inventories and both reorder directions.");
            return;
        }

        var typeShareEnvelope = typeShares.Max()!.Value;
        var siteShareEnvelope = siteShares.Max()!.Value;
        var typeAbsoluteEnvelope = typeAbsolute.Max()!.Value;
        var siteAbsoluteEnvelope = siteAbsolute.Max()!.Value;
        var biased = reordered.Any(pair =>
            pair.Baseline.RequestedPayloadBytesPerOperation
                != pair.Current.RequestedPayloadBytesPerOperation
            || MaximumShareMovement(
                pair.Baseline.Types,
                pair.Current.Types) > typeShareEnvelope
            || MaximumShareMovement(
                pair.Baseline.Sites,
                pair.Current.Sites) > siteShareEnvelope
            || MaximumAbsoluteMovementPerOperation(
                pair.Baseline.Types,
                pair.Baseline.Operations,
                pair.Current.Types,
                pair.Current.Operations) > typeAbsoluteEnvelope
            || MaximumAbsoluteMovementPerOperation(
                pair.Baseline.Sites,
                pair.Baseline.Operations,
                pair.Current.Sites,
                pair.Current.Operations) > siteAbsoluteEnvelope);
        unsafeEvidence =
            typeShareEnvelope
                > AllocationDirectionalBounds.MaximumAllowedAaShareMovement
            || siteShareEnvelope
                > AllocationDirectionalBounds.MaximumAllowedAaShareMovement
            || biased;
        expected = !unsafeEvidence;
        findings.Add(expected
            ? "Both equal-payload reorder directions stayed inside the combined workload-specific A/A absolute-and-share envelopes."
            : "At least one equal-payload reorder direction exceeded workload-specific A/A; type/site directional attribution is unsafe under fixed-stride sampling.");
    }

    private static AllocationDirectionalStudyReport Inconclusive(string limitation)
        => new(
            AllocationDirectionalJson.CurrentSchemaVersion,
            AllocationDirectionalV3Protocol.ProtocolId,
            AllocationDirectionalV3Protocol.ProtocolVersion,
            AllocationDirectionalDecision.Inconclusive,
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
            || fixture.ProtocolId != AllocationDirectionalV3Protocol.ProtocolId
            || fixture.ProtocolVersion != AllocationDirectionalV3Protocol.ProtocolVersion
            || fixture.SourceBaseCommit != AllocationDirectionalV3Protocol.SourceBaseCommit
            || string.IsNullOrWhiteSpace(fixture.Environment)
            || fixture.Pairs.Count != AllocationDirectionalV3CapturePlan.TotalPairs
            || fixture.Pairs.Select(pair => pair.PairId)
                .Distinct(StringComparer.Ordinal).Count() != fixture.Pairs.Count)
        {
            throw new InvalidDataException(
                "Allocation directional v3 fixture identity or inventory is invalid.");
        }

        var observations = fixture.Pairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();
        if (observations.Any(observation =>
                observation.Types.Count > AllocationDirectionalBounds.MaximumRankedRows
                || observation.Sites.Count > AllocationDirectionalBounds.MaximumRankedRows)
            || observations.Select(observation => observation.AttemptId)
                .Distinct(StringComparer.Ordinal).Count() != observations.Length
            || observations.Select(observation => observation.CaptureId)
                .Any(string.IsNullOrWhiteSpace)
            || observations.Any(observation => observation.QuantityEvidence is null))
        {
            throw new InvalidDataException(
                "Allocation directional v3 observations violate bounds or explicit provenance requirements.");
        }

        var live = observations.Where(observation =>
            observation.Origin == AllocationDirectionalOrigin.LiveMcp).ToArray();
        if (live.Length != AllocationDirectionalV3CapturePlan.LiveMcpSides
            || live.Select(observation => observation.ProcessIdentity)
                .Distinct(StringComparer.Ordinal).Count() != live.Length
            || live.Select(observation => observation.CaptureId)
                .Distinct(StringComparer.Ordinal).Count() != live.Length)
        {
            throw new InvalidDataException(
                "Allocation directional v3 live process or capture identities are incomplete or reused.");
        }
    }

    private static AllocationDirectionalPair[] ValidPairs(
        AllocationDirectionalFixture fixture,
        AllocationDirectionalScenario scenario)
        => fixture.Pairs.Where(pair =>
            pair.Scenario == scenario && IsValidPair(pair)).ToArray();

    private static bool IsValidPair(AllocationDirectionalPair pair)
        => Passed(pair)
            && pair.Baseline.Operations > 0
            && pair.Current.Operations > 0
            && pair.Baseline.SampledEstimatedBytes > 0
            && pair.Current.SampledEstimatedBytes > 0
            && string.Equals(
                pair.Baseline.WorkloadIdentity,
                pair.Current.WorkloadIdentity,
                StringComparison.Ordinal);

    private static bool Passed(AllocationDirectionalPair pair)
        => pair.Baseline.Outcome == AllocationDirectionalAttemptOutcome.Passed
            && pair.Current.Outcome == AllocationDirectionalAttemptOutcome.Passed;

    private static bool HasRequiredPairs(
        IReadOnlyCollection<AllocationDirectionalPair> pairs)
        => pairs.Count >= AllocationDirectionalBounds.RequiredPairs;

    private static AllocationDirectionalPair[] CalibrationPairs(
        AllocationDirectionalPair[] pairs)
        => pairs.Where(pair => pair.Current.Phase == "aa").ToArray();

    private static AllocationDirectionalPair[] EffectPairs(
        AllocationDirectionalPair[] pairs)
        => pairs.Where(pair => pair.Current.Phase == "effect").ToArray();

    private static decimal RelativeEnvelope(
        IReadOnlyCollection<AllocationDirectionalPair> pairs)
        => HasRequiredPairs(pairs)
            ? pairs.Max(pair => AbsoluteRelativeDelta(
                EstimatedBytesPerOperation(pair.Baseline),
                EstimatedBytesPerOperation(pair.Current)))
            : decimal.MaxValue;

    private static decimal EstimatedBytesPerOperation(
        AllocationDirectionalObservation observation)
        => observation.SampledEstimatedBytes / (decimal)observation.Operations;

    private static decimal RelativeDelta(decimal baseline, decimal current)
        => baseline == 0 ? 0 : (current - baseline) / Math.Abs(baseline);

    private static decimal AbsoluteRelativeDelta(decimal baseline, decimal current)
        => Math.Abs(RelativeDelta(baseline, current));

    private static bool HasAttribution(AllocationDirectionalPair pair)
        => pair.Baseline.Types.Count > 0
            && pair.Current.Types.Count > 0
            && pair.Baseline.Sites.Count > 0
            && pair.Current.Sites.Count > 0;

    private static string? TopKey(
        IReadOnlyList<AllocationDirectionalRankedRow> rows)
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
                var left = baseline.Where(row => row.Key == key)
                    .Sum(row => row.EstimatedBytes) / (decimal)baselineTotal;
                var right = current.Where(row => row.Key == key)
                    .Sum(row => row.EstimatedBytes) / (decimal)currentTotal;
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
}
