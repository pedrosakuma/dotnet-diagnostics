using System.Globalization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class AllocationHotspotHarness
{
    public const string ProtocolId = "allocation-hotspot-reproducibility";
    public const string ProtocolVersion = "1";
    private const double MinimumMembershipJaccard = 2d / 3d;
    private const double MinimumRankCorrelation = 0.5;

    public static AllocationHotspotStudyReport Evaluate(
        IReadOnlyList<AllocationHotspotCapture> captures,
        string environment)
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        Validate(captures);

        var scenarios = Enum.GetValues<AllocationHotspotScenario>()
            .Select(scenario => EvaluateScenario(
                scenario,
                captures.Where(capture => capture.Scenario == scenario)
                    .OrderBy(capture => capture.CaptureId, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
        var repeatedAa = EvaluateContrast(
            "stable-versus-repeated-aa",
            captures,
            AllocationHotspotScenario.StableHotspot,
            AllocationHotspotScenario.RepeatedAa,
            comparisonAxis: "workload.repetition-set",
            expectChange: false);
        var changedMix = EvaluateContrast(
            "stable-versus-changed-mix",
            captures,
            AllocationHotspotScenario.StableHotspot,
            AllocationHotspotScenario.ChangedMix,
            comparisonAxis: "workload.variant",
            expectChange: true);
        scenarios = scenarios.Select(scenario => scenario.Scenario switch
        {
            AllocationHotspotScenario.RepeatedAa => scenario with
            {
                Assessment = scenario.ExpectedBehaviorObserved
                    && repeatedAa.ExpectedBehaviorObserved
                    ? AllocationHotspotAssessment.RepeatedControlReproducible
                    : AllocationHotspotAssessment.NotReproducible,
                ExpectedBehaviorObserved = scenario.ExpectedBehaviorObserved
                    && repeatedAa.ExpectedBehaviorObserved,
            },
            AllocationHotspotScenario.ChangedMix => scenario with
            {
                Assessment = scenario.ExpectedBehaviorObserved
                    && changedMix.ExpectedBehaviorObserved
                    ? AllocationHotspotAssessment.ChangedMixDetected
                    : AllocationHotspotAssessment.NotReproducible,
                ExpectedBehaviorObserved = scenario.ExpectedBehaviorObserved
                    && changedMix.ExpectedBehaviorObserved,
            },
            _ => scenario,
        }).ToArray();

        var go = scenarios.All(scenario => scenario.ExpectedBehaviorObserved)
            && repeatedAa.ExpectedBehaviorObserved
            && changedMix.ExpectedBehaviorObserved;
        return new(
            AllocationHotspotJson.CurrentSchemaVersion,
            ProtocolId,
            ProtocolVersion,
            environment,
            captures.OrderBy(capture => capture.Scenario)
                .ThenBy(capture => capture.CaptureId, StringComparer.Ordinal)
                .ToArray(),
            scenarios,
            repeatedAa,
            changedMix,
            go ? "GO" : "NO-GO",
            Conclusions:
            [
                "Repeated compatible captures can support a focused allocation investigation when type and stack ranks are reproducible beyond volatile single-capture totals.",
                "Allocated types and allocation stack sites are evaluated as independent ranked inventories; this study does not infer a type-to-stack relationship.",
                "Allocation ticks and estimated bytes remain sampled capture totals. No confidence interval is computed over weighted ticks because the runtime sampler model is not validated for that use.",
                "Raw-empty, no-stack, zero-weight, partial, complete, and unavailable retained-handle states remain distinct and never substitute missing attribution with zero.",
            ],
            Limitations:
            [
                "Raw MAD and top-K similarity are validation-only descriptive gates selected by this scenario; they are not universal allocation thresholds.",
                "One separately started capture against one fresh process run is the independent unit. Allocation events and stack frames within a capture are correlated source evidence.",
                "GCAllocationTick estimated bytes are sampling weights, not exact allocated-byte totals, object counts, retained bytes, or CPU time.",
                "The live workload allowlists its own array types and helper methods independently to avoid ranking unrelated ASP.NET startup traffic.",
            ]);
    }

    public static AllocationAttributionState Classify(AllocationHotspotCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.RawAllocationTicks == 0)
        {
            return AllocationAttributionState.RawEmpty;
        }
        if (capture.HandleState == AllocationHandleState.InvalidOrExpired)
        {
            return AllocationAttributionState.Unavailable;
        }
        if (capture.AttributedAllocationTicks == 0)
        {
            return AllocationAttributionState.NoStack;
        }
        if (capture.AttributedEstimatedBytes == 0)
        {
            return AllocationAttributionState.ZeroWeight;
        }
        return capture.AttributedAllocationTicks == capture.RawAllocationTicks
            && capture.AttributedEstimatedBytes == capture.RawEstimatedBytes
                ? AllocationAttributionState.Complete
                : AllocationAttributionState.Partial;
    }

    private static AllocationScenarioAnalysis EvaluateScenario(
        AllocationHotspotScenario scenario,
        AllocationHotspotCapture[] captures)
    {
        var bytesMedian = EvaluateQuantity(
            scenario,
            captures,
            "allocation.estimated-bytes",
            "bytes",
            capture => capture.RawEstimatedBytes,
            ScenarioStatisticalMethodIds.WindowMedian,
            minimum: 1);
        var bytesMad = EvaluateQuantity(
            scenario,
            captures,
            "allocation.estimated-bytes",
            "bytes",
            capture => capture.RawEstimatedBytes,
            ScenarioStatisticalMethodIds.MedianAbsoluteDeviation,
            minimum: 2);
        var ticksMedian = EvaluateQuantity(
            scenario,
            captures,
            "allocation.ticks",
            "ticks",
            capture => capture.RawAllocationTicks,
            ScenarioStatisticalMethodIds.WindowMedian,
            minimum: 1);
        var ticksMad = EvaluateQuantity(
            scenario,
            captures,
            "allocation.ticks",
            "ticks",
            capture => capture.RawAllocationTicks,
            ScenarioStatisticalMethodIds.MedianAbsoluteDeviation,
            minimum: 2);
        var states = captures.Select(Classify).Distinct().ToArray();
        var state = states.Length == 1
            ? states[0]
            : states.All(item => item is AllocationAttributionState.Partial
                or AllocationAttributionState.Complete)
                ? AllocationAttributionState.Partial
                : AllocationAttributionState.Unavailable;
        var typeStability = PairwiseStability(captures, capture => capture.Types);
        var stackStability = PairwiseStability(captures, capture => capture.Stacks);
        var relativeBytesMad = RelativeMad(bytesMedian, bytesMad);
        var relativeTicksMad = RelativeMad(ticksMedian, ticksMad);
        var limitations = captures.SelectMany(capture => capture.Limitations)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var attributedByteShare = MedianShare(
            captures,
            capture => capture.AttributedEstimatedBytes,
            capture => capture.RawEstimatedBytes);
        var attributedTickShare = MedianShare(
            captures,
            capture => capture.AttributedAllocationTicks,
            capture => capture.RawAllocationTicks);
        var assessment = ExpectedAssessment(
            scenario,
            captures,
            state,
            typeStability,
            stackStability);
        var expected = assessment == ExpectedAssessment(scenario);
        if (states.Length > 1)
        {
            limitations.Add(
                $"The scenario retained multiple attribution states: {string.Join(", ", states.Order().Select(StateName))}.");
        }
        if (relativeBytesMad is > 0.25 || relativeTicksMad is > 0.25)
        {
            limitations.Add(
                $"Capture totals were variable (estimated-byte relative raw MAD={FormatRatio(relativeBytesMad)}; tick relative raw MAD={FormatRatio(relativeTicksMad)}); totals remain descriptive and do not gate hotspot rank reproducibility.");
        }

        return new(
            scenario,
            captures.Length,
            state,
            bytesMedian,
            bytesMad,
            ticksMedian,
            ticksMad,
            relativeBytesMad,
            relativeTicksMad,
            attributedByteShare,
            attributedByteShare is null ? null : 1 - attributedByteShare,
            attributedTickShare,
            typeStability,
            stackStability,
            assessment,
            expected,
            limitations);
    }

    private static AllocationHotspotAssessment ExpectedAssessment(
        AllocationHotspotScenario scenario,
        AllocationHotspotCapture[] captures,
        AllocationAttributionState state,
        AllocationTopKStability typeStability,
        AllocationTopKStability stackStability)
        => scenario switch
        {
            AllocationHotspotScenario.StableHotspot
                when captures.Length >= AllocationHotspotBounds.MinimumStableCaptures
                    && state is AllocationAttributionState.Partial or AllocationAttributionState.Complete
                    && IsStable(typeStability)
                    && IsStable(stackStability)
                => AllocationHotspotAssessment.FocusedInvestigationSupported,
            AllocationHotspotScenario.LowSample
                when captures.Length < AllocationHotspotBounds.MinimumStableCaptures
                    && captures.Any(capture => capture.RawAllocationTicks > 0)
                => AllocationHotspotAssessment.InsufficientCaptureCount,
            AllocationHotspotScenario.RawEmpty
                when state == AllocationAttributionState.RawEmpty
                => AllocationHotspotAssessment.BoundedCaptureObservedNoTicks,
            AllocationHotspotScenario.RawNonzeroNoStack
                when state == AllocationAttributionState.NoStack
                => AllocationHotspotAssessment.StackAttributionUnavailable,
            AllocationHotspotScenario.UsableStackZeroWeight
                when state == AllocationAttributionState.ZeroWeight
                => AllocationHotspotAssessment.ByteWeightedRankingUnavailable,
            AllocationHotspotScenario.InvalidHandle
                when state == AllocationAttributionState.Unavailable
                => AllocationHotspotAssessment.RetainedAttributionUnavailable,
            AllocationHotspotScenario.ChangedMix
                when captures.Length >= AllocationHotspotBounds.MinimumStableCaptures
                    && state is AllocationAttributionState.Partial or AllocationAttributionState.Complete
                    && IsStable(typeStability)
                    && IsStable(stackStability)
                => AllocationHotspotAssessment.ChangedMixDetected,
            AllocationHotspotScenario.RepeatedAa
                when captures.Length >= AllocationHotspotBounds.MinimumStableCaptures
                    && state is AllocationAttributionState.Partial or AllocationAttributionState.Complete
                    && IsStable(typeStability)
                    && IsStable(stackStability)
                => AllocationHotspotAssessment.RepeatedControlReproducible,
            _ => AllocationHotspotAssessment.NotReproducible,
        };

    private static AllocationHotspotAssessment ExpectedAssessment(AllocationHotspotScenario scenario)
        => scenario switch
        {
            AllocationHotspotScenario.StableHotspot
                => AllocationHotspotAssessment.FocusedInvestigationSupported,
            AllocationHotspotScenario.LowSample
                => AllocationHotspotAssessment.InsufficientCaptureCount,
            AllocationHotspotScenario.RawEmpty
                => AllocationHotspotAssessment.BoundedCaptureObservedNoTicks,
            AllocationHotspotScenario.RawNonzeroNoStack
                => AllocationHotspotAssessment.StackAttributionUnavailable,
            AllocationHotspotScenario.UsableStackZeroWeight
                => AllocationHotspotAssessment.ByteWeightedRankingUnavailable,
            AllocationHotspotScenario.ChangedMix
                => AllocationHotspotAssessment.ChangedMixDetected,
            AllocationHotspotScenario.InvalidHandle
                => AllocationHotspotAssessment.RetainedAttributionUnavailable,
            AllocationHotspotScenario.RepeatedAa
                => AllocationHotspotAssessment.RepeatedControlReproducible,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static AllocationHotspotContrast EvaluateContrast(
        string id,
        IReadOnlyList<AllocationHotspotCapture> captures,
        AllocationHotspotScenario baselineScenario,
        AllocationHotspotScenario candidateScenario,
        string comparisonAxis,
        bool expectChange)
    {
        var baseline = captures.Where(capture => capture.Scenario == baselineScenario)
            .OrderBy(capture => capture.CaptureId, StringComparer.Ordinal)
            .ToArray();
        var candidate = captures.Where(capture => capture.Scenario == candidateScenario)
            .OrderBy(capture => capture.CaptureId, StringComparer.Ordinal)
            .ToArray();
        var pairCount = Math.Min(baseline.Length, candidate.Length);
        var pairedBaseline = baseline.Take(pairCount).ToArray();
        var pairedCandidate = candidate.Take(pairCount).ToArray();
        var bytes = EvaluatePairedQuantity(
            id,
            pairedBaseline,
            pairedCandidate,
            "allocation.estimated-bytes",
            "bytes",
            capture => capture.RawEstimatedBytes,
            comparisonAxis);
        var ticks = EvaluatePairedQuantity(
            id,
            pairedBaseline,
            pairedCandidate,
            "allocation.ticks",
            "ticks",
            capture => capture.RawAllocationTicks,
            comparisonAxis);
        var typeTopK = PairedStability(pairedBaseline, pairedCandidate, capture => capture.Types);
        var stackTopK = PairedStability(pairedBaseline, pairedCandidate, capture => capture.Stacks);
        var topTypeChanged = TopKey(pairedBaseline, capture => capture.Types)
            != TopKey(pairedCandidate, capture => capture.Types);
        var topStackChanged = TopKey(pairedBaseline, capture => capture.Stacks)
            != TopKey(pairedCandidate, capture => capture.Stacks);
        var totalsComparable = bytes.Availability == ScenarioStatisticalAvailability.Available
            && ticks.Availability == ScenarioStatisticalAvailability.Available;
        var expected = expectChange
            ? totalsComparable
                && topTypeChanged
                && topStackChanged
                && typeTopK.MedianMembershipJaccard < MinimumMembershipJaccard
                && stackTopK.MedianMembershipJaccard < MinimumMembershipJaccard
            : totalsComparable
                && IsStable(typeTopK)
                && IsStable(stackTopK)
                && !topTypeChanged
                && !topStackChanged;
        var limitations = new List<string>
        {
            "The paired delta compares capture-level sampled totals; it is descriptive and does not create an interval over weighted ticks.",
            "Type and stack top-K contrasts are calculated independently and are not joined into type-to-stack attribution.",
        };
        if (baseline.Length != candidate.Length)
        {
            limitations.Add(
                $"Only {pairCount} complete pair(s) were evaluated from {baseline.Length} baseline and {candidate.Length} candidate capture(s).");
        }

        return new(
            id,
            baselineScenario,
            candidateScenario,
            bytes,
            ticks,
            typeTopK,
            stackTopK,
            topTypeChanged,
            topStackChanged,
            expected,
            limitations);
    }

    private static ScenarioStatisticalEvidenceResult EvaluateQuantity(
        AllocationHotspotScenario scenario,
        AllocationHotspotCapture[] captures,
        string quantityId,
        string unit,
        Func<AllocationHotspotCapture, long> selector,
        string methodId,
        int minimum)
    {
        var attempts = captures.Select(capture => new ScenarioStatisticalAttempt(
            capture.CaptureId,
            "attempt-1",
            ScenarioStatisticalAttemptOutcome.Passed)).ToArray();
        var observations = captures.Select(capture => new ScenarioStatisticalObservation(
            $"{capture.CaptureId}-{quantityId}",
            capture.CaptureId,
            "attempt-1",
            "current-environment",
            capture.CaptureId,
            null,
            capture.CaptureId,
            selector(capture),
            null,
            capture.RawAllocationTicks,
            0,
            ScenarioStatisticalRepresentation.Exact)).ToArray();
        var request = new ScenarioStatisticalEvidenceRequest(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            new ScenarioStatisticalProtocol(
                $"{ProtocolId}-{ScenarioName(scenario)}-{quantityId}-{methodId}",
                ProtocolVersion,
                new ScenarioStatisticalMethod(methodId, 1),
                ScenarioStatisticalIndependenceUnit.Capture,
                minimum,
                AdmitLoss: false,
                AdmitApproximation: false),
            [new ScenarioStatisticalArm(
                "observed",
                ScenarioStatisticalArmRole.Observed,
                Identity(
                    captures[0],
                    quantityId,
                    unit,
                    "workload.variant",
                    captures[0].WorkloadVariant,
                    ScenarioName(scenario)),
                attempts,
                observations)],
            "observed");
        return ScenarioStatisticalEvidenceHarness.Evaluate(request);
    }

    private static ScenarioStatisticalEvidenceResult EvaluatePairedQuantity(
        string contrastId,
        AllocationHotspotCapture[] baseline,
        AllocationHotspotCapture[] candidate,
        string quantityId,
        string unit,
        Func<AllocationHotspotCapture, long> selector,
        string comparisonAxis)
    {
        var pairCount = Math.Min(baseline.Length, candidate.Length);
        ScenarioStatisticalArm Arm(
            string id,
            ScenarioStatisticalArmRole role,
            AllocationHotspotCapture[] source,
            string axisValue)
        {
            var attempts = source.Take(pairCount).Select((capture, index) =>
                new ScenarioStatisticalAttempt(
                    $"pair-{index + 1}",
                    "attempt-1",
                    ScenarioStatisticalAttemptOutcome.Passed,
                    Detail: capture.CaptureId)).ToArray();
            var observations = source.Take(pairCount).Select((capture, index) =>
                new ScenarioStatisticalObservation(
                    $"{id}-{capture.CaptureId}-{quantityId}",
                    $"pair-{index + 1}",
                    "attempt-1",
                    "current-environment",
                    capture.CaptureId,
                    null,
                    capture.CaptureId,
                    selector(capture),
                    null,
                    capture.RawAllocationTicks,
                    0,
                    ScenarioStatisticalRepresentation.Exact,
                    $"pair-{index + 1}",
                    index % 2 == 0
                        ? ScenarioStatisticalPairOrder.BaselineFirst
                        : ScenarioStatisticalPairOrder.CandidateFirst)).ToArray();
            return new(
                id,
                role,
                Identity(
                    source[0],
                    quantityId,
                    unit,
                    comparisonAxis,
                    axisValue,
                    contrastId),
                attempts,
                observations);
        }

        var request = new ScenarioStatisticalEvidenceRequest(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            new ScenarioStatisticalProtocol(
                $"{ProtocolId}-{contrastId}-{quantityId}",
                ProtocolVersion,
                new ScenarioStatisticalMethod(ScenarioStatisticalMethodIds.PairedDelta, 1),
                ScenarioStatisticalIndependenceUnit.Capture,
                AllocationHotspotBounds.MinimumStableCaptures,
                AdmitLoss: false,
                AdmitApproximation: false),
            [
                Arm("baseline", ScenarioStatisticalArmRole.Baseline, baseline, "baseline"),
                Arm("candidate", ScenarioStatisticalArmRole.Candidate, candidate, "candidate"),
            ]);
        return ScenarioStatisticalEvidenceHarness.Evaluate(request);
    }

    private static ScenarioStatisticalCompatibilityIdentity Identity(
        AllocationHotspotCapture capture,
        string quantityId,
        string unit,
        string comparisonAxis,
        string axisValue,
        string scenario)
        => new(
        [
            new("target.runtime-flavor", "CoreCLR"),
            new("target.runtime-version-policy", capture.RuntimeVersion),
            new("target.os", capture.OperatingSystem),
            new("target.architecture", capture.Architecture),
            new("target.process-run-id", capture.CaptureId),
            new("backend.id", "EventPipe-GCAllocationTick"),
            new("backend.version", "1"),
            new("quantity.id", quantityId),
            new("quantity.unit", unit),
            new("quantity.aggregation", "one-capture-total"),
            new("quantity.normalization", "none"),
            new("quantity.filters", "scenario-workload-allowlist-independent-for-types-and-stacks"),
            new("quantity.representation", "exact"),
            new("capture.duration", capture.DurationSeconds.ToString("0.###s", CultureInfo.InvariantCulture)),
            new("capture.windowing", "one-separately-started-capture"),
            new("capture.providers", "Microsoft-Windows-DotNETRuntime:GCAllocationTick"),
            new("capture.sampling-period", "runtime-defined-allocation-tick"),
            new("capture.retention", $"ranked-rows<={AllocationHotspotBounds.MaximumRankedRows};top-k={AllocationHotspotBounds.TopK}"),
            new("capture.enrichment", "rundown-symbolization"),
            new("workload.scenario", scenario),
            new("workload.version", "1"),
            new("workload.parameters", "bounded-allocation-shape-v1"),
            new("workload.concurrency", "1"),
            new("workload.deployment", "fresh-local-process"),
            new("workload.variant", comparisonAxis == "workload.variant" ? axisValue : capture.WorkloadVariant),
            new("workload.repetition-set", comparisonAxis == "workload.repetition-set" ? axisValue : "same"),
            new("independence.unit", "capture"),
            new("independence.nesting", "allocation-ticks->capture->fresh-process-run"),
            new("independence.pairing-key", "capture-index"),
            new("comparison.axis", comparisonAxis),
        ]);

    private static AllocationTopKStability PairwiseStability(
        AllocationHotspotCapture[] captures,
        Func<AllocationHotspotCapture, IReadOnlyList<AllocationRankedEvidence>> selector)
    {
        var comparisons = new List<RankComparison>();
        for (var left = 0; left < captures.Length; left++)
        {
            for (var right = left + 1; right < captures.Length; right++)
            {
                comparisons.Add(CompareRanks(selector(captures[left]), selector(captures[right])));
            }
        }
        return Summarize(comparisons);
    }

    private static AllocationTopKStability PairedStability(
        AllocationHotspotCapture[] baseline,
        AllocationHotspotCapture[] candidate,
        Func<AllocationHotspotCapture, IReadOnlyList<AllocationRankedEvidence>> selector)
    {
        var comparisons = Enumerable.Range(0, Math.Min(baseline.Length, candidate.Length))
            .Select(index => CompareRanks(selector(baseline[index]), selector(candidate[index])))
            .ToArray();
        return Summarize(comparisons);
    }

    private static RankComparison CompareRanks(
        IReadOnlyList<AllocationRankedEvidence> left,
        IReadOnlyList<AllocationRankedEvidence> right)
    {
        var leftTop = left.OrderBy(item => item.Rank).Take(AllocationHotspotBounds.TopK).ToArray();
        var rightTop = right.OrderBy(item => item.Rank).Take(AllocationHotspotBounds.TopK).ToArray();
        var leftKeys = leftTop.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var rightKeys = rightTop.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var union = leftKeys.Union(rightKeys, StringComparer.Ordinal).Count();
        var intersection = leftKeys.Intersect(rightKeys, StringComparer.Ordinal).ToArray();
        var jaccard = union == 0 ? 1 : intersection.Length / (double)union;
        if (intersection.Length < 2)
        {
            return new(jaccard, null);
        }

        var leftRanks = leftTop.Where(item => intersection.Contains(item.Key, StringComparer.Ordinal))
            .OrderBy(item => item.Rank)
            .Select((item, index) => (item.Key, Rank: index + 1))
            .ToDictionary(item => item.Key, item => item.Rank, StringComparer.Ordinal);
        var rightRanks = rightTop.Where(item => intersection.Contains(item.Key, StringComparer.Ordinal))
            .OrderBy(item => item.Rank)
            .Select((item, index) => (item.Key, Rank: index + 1))
            .ToDictionary(item => item.Key, item => item.Rank, StringComparer.Ordinal);
        var squaredDifference = intersection.Sum(key =>
        {
            var difference = leftRanks[key] - rightRanks[key];
            return difference * difference;
        });
        var count = intersection.Length;
        var correlation = 1 - 6d * squaredDifference / (count * (count * count - 1d));
        return new(jaccard, correlation);
    }

    private static AllocationTopKStability Summarize(IReadOnlyList<RankComparison> comparisons)
    {
        if (comparisons.Count == 0)
        {
            return new(0, null, null, null, null, 0);
        }
        var memberships = comparisons.Select(item => item.MembershipJaccard).Order().ToArray();
        var ranks = comparisons.Where(item => item.SharedRankCorrelation is not null)
            .Select(item => item.SharedRankCorrelation!.Value)
            .Order()
            .ToArray();
        return new(
            comparisons.Count,
            Median(memberships),
            memberships[0],
            ranks.Length == 0 ? null : Median(ranks),
            ranks.Length == 0 ? null : ranks[0],
            ranks.Length);
    }

    private static bool IsStable(AllocationTopKStability stability)
        => stability.ComparisonCount > 0
            && stability.MedianMembershipJaccard >= MinimumMembershipJaccard
            && stability.MinimumMembershipJaccard >= 0.5
            && stability.ComparisonsWithRankCorrelation > 0
            && stability.MedianSharedRankCorrelation >= MinimumRankCorrelation;

    private static double? RelativeMad(
        ScenarioStatisticalEvidenceResult median,
        ScenarioStatisticalEvidenceResult mad)
        => median.Estimate is > 0 && mad.Dispersion is double dispersion
            ? dispersion / median.Estimate.Value
            : null;

    private static double? MedianShare(
        AllocationHotspotCapture[] captures,
        Func<AllocationHotspotCapture, long> numerator,
        Func<AllocationHotspotCapture, long> denominator)
    {
        var shares = captures
            .Where(capture => capture.HandleState == AllocationHandleState.Available
                && denominator(capture) > 0)
            .Select(capture => numerator(capture) / (double)denominator(capture))
            .Order()
            .ToArray();
        return shares.Length == 0 ? null : Median(shares);
    }

    private static string? TopKey(
        AllocationHotspotCapture[] captures,
        Func<AllocationHotspotCapture, IReadOnlyList<AllocationRankedEvidence>> selector)
        => captures.Select(capture => selector(capture).SingleOrDefault(item => item.Rank == 1)?.Key)
            .Where(key => key is not null)
            .GroupBy(key => key!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault();

    private static void Validate(IReadOnlyList<AllocationHotspotCapture> captures)
    {
        if (captures.Count == 0
            || captures.Select(capture => capture.CaptureId).Distinct(StringComparer.Ordinal).Count()
                != captures.Count)
        {
            throw new InvalidDataException(
                "Allocation hotspot evidence requires unique captures.");
        }

        foreach (var scenario in Enum.GetValues<AllocationHotspotScenario>())
        {
            var count = captures.Count(capture => capture.Scenario == scenario);
            if (count is < 1 or > AllocationHotspotBounds.MaximumCapturesPerScenario)
            {
                throw new InvalidDataException(
                    $"Scenario '{ScenarioName(scenario)}' capture count {count} is outside [1, {AllocationHotspotBounds.MaximumCapturesPerScenario}].");
            }
        }

        foreach (var capture in captures)
        {
            if (string.IsNullOrWhiteSpace(capture.CaptureId)
                || string.IsNullOrWhiteSpace(capture.WorkloadVariant)
                || string.IsNullOrWhiteSpace(capture.RuntimeVersion)
                || string.IsNullOrWhiteSpace(capture.OperatingSystem)
                || string.IsNullOrWhiteSpace(capture.Architecture)
                || capture.DurationSeconds <= 0
                || capture.RawAllocationTicks < 0
                || capture.RawEstimatedBytes < 0
                || capture.AttributedAllocationTicks < 0
                || capture.AttributedEstimatedBytes < 0
                || capture.AttributedAllocationTicks > capture.RawAllocationTicks
                || capture.AttributedEstimatedBytes > capture.RawEstimatedBytes
                || capture.Types.Count > AllocationHotspotBounds.MaximumRankedRows
                || capture.Stacks.Count > AllocationHotspotBounds.MaximumRankedRows)
            {
                throw new InvalidDataException(
                    $"Allocation hotspot capture '{capture.CaptureId}' contains invalid or out-of-bounds evidence.");
            }
            ValidateRanks(capture, capture.Types, "type");
            ValidateRanks(capture, capture.Stacks, "stack");

            var state = Classify(capture);
            if (state == AllocationAttributionState.RawEmpty
                && (capture.RawEstimatedBytes != 0
                    || capture.AttributedAllocationTicks != 0
                    || capture.AttributedEstimatedBytes != 0
                    || capture.Types.Count != 0
                    || capture.Stacks.Count != 0))
            {
                throw new InvalidDataException(
                    $"Raw-empty capture '{capture.CaptureId}' contains non-empty allocation evidence.");
            }
            if (state == AllocationAttributionState.Unavailable
                && (capture.AttributedAllocationTicks != 0
                    || capture.AttributedEstimatedBytes != 0
                    || capture.Stacks.Count != 0))
            {
                throw new InvalidDataException(
                    $"Unavailable capture '{capture.CaptureId}' must not retain derived stack attribution.");
            }
        }
    }

    private static void ValidateRanks(
        AllocationHotspotCapture capture,
        IReadOnlyList<AllocationRankedEvidence> rows,
        string kind)
    {
        if (rows.Any(row => string.IsNullOrWhiteSpace(row.Key)
                || row.Rank < 1
                || row.EstimatedBytes < 0
                || row.AllocationTicks < 0)
            || rows.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count() != rows.Count
            || rows.Select(row => row.Rank).Distinct().Count() != rows.Count
            || !rows.OrderBy(row => row.Rank).Select(row => row.Rank)
                .SequenceEqual(Enumerable.Range(1, rows.Count)))
        {
            throw new InvalidDataException(
                $"Allocation hotspot capture '{capture.CaptureId}' has invalid {kind} ranks.");
        }
    }

    private static double Median(double[] orderedValues)
    {
        if (orderedValues.Length == 0)
        {
            throw new InvalidOperationException("Median requires at least one value.");
        }
        var middle = orderedValues.Length / 2;
        return orderedValues.Length % 2 == 1
            ? orderedValues[middle]
            : orderedValues[middle - 1] / 2 + orderedValues[middle] / 2;
    }

    private static string ScenarioName(AllocationHotspotScenario scenario)
        => scenario switch
        {
            AllocationHotspotScenario.StableHotspot => "stable-hotspot",
            AllocationHotspotScenario.LowSample => "low-sample",
            AllocationHotspotScenario.RawEmpty => "raw-empty",
            AllocationHotspotScenario.RawNonzeroNoStack => "raw-nonzero-no-stack",
            AllocationHotspotScenario.UsableStackZeroWeight => "usable-stack-zero-weight",
            AllocationHotspotScenario.ChangedMix => "changed-mix",
            AllocationHotspotScenario.InvalidHandle => "invalid-handle",
            AllocationHotspotScenario.RepeatedAa => "repeated-aa",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static string StateName(AllocationAttributionState state)
        => state switch
        {
            AllocationAttributionState.RawEmpty => "raw-empty",
            AllocationAttributionState.NoStack => "no-stack",
            AllocationAttributionState.ZeroWeight => "zero-weight",
            AllocationAttributionState.Partial => "partial",
            AllocationAttributionState.Complete => "complete",
            AllocationAttributionState.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static string FormatRatio(double? value)
        => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "unavailable";

    private sealed record RankComparison(
        double MembershipJaccard,
        double? SharedRankCorrelation);
}
