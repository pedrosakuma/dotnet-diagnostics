namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalCalibrationBounds
{
    public const decimal MinimumThroughputMultiplier = 1.25m;
    public const decimal PracticalRelativeEffectFloor = 0.10m;
    public const decimal PracticalRelativeEquivalenceFloor = 0.10m;
    public const decimal ModifiedZScoreOutlierThreshold = 3.5m;
    public const decimal ModifiedZScoreScale = 0.6745m;
    public const int MinimumInlierPairs = 2;
}

internal sealed record AllocationDirectionalPairSummary(
    int PairCount,
    decimal Median,
    decimal MedianAbsoluteDeviation,
    IReadOnlyList<int> OutlierIndexes,
    IReadOnlyList<decimal> Inliers)
{
    public bool HasRequiredPairs =>
        PairCount >= AllocationDirectionalBounds.RequiredPairs;

    public bool HasRequiredInliers =>
        Inliers.Count >= AllocationDirectionalCalibrationBounds.MinimumInlierPairs;
}

internal sealed record AllocationDirectionalThroughputGate(
    bool Assessable,
    bool WorkloadManipulationEstablished,
    bool EstimatedBytesPerOperationStable,
    decimal AaEnvelope,
    AllocationDirectionalPairSummary ThroughputMultipliers,
    AllocationDirectionalPairSummary EstimatedBytesPerOperationAbsoluteRelativeDeltas,
    AllocationDirectionalPairSummary EstimatedBytesPerSecondMultipliers,
    string? AbstentionReason)
{
    public bool Passed =>
        Assessable
        && WorkloadManipulationEstablished
        && EstimatedBytesPerOperationStable;
}

internal sealed record AllocationDirectionalWarmupGate(
    bool Assessable,
    bool EarlyMovementEstablished,
    bool Converged,
    decimal AaEnvelope,
    decimal EarlyDecisionFloor,
    AllocationDirectionalPairSummary EarlyRelativeDeltas,
    AllocationDirectionalPairSummary ConvergedAbsoluteRelativeDeltas,
    string? AbstentionReason)
{
    public bool Passed =>
        Assessable
        && EarlyMovementEstablished
        && Converged;
}

internal sealed record AllocationDirectionalMovementGate(
    bool Assessable,
    bool ExpectedMovementEstablished,
    decimal AaEnvelope,
    AllocationDirectionalPairSummary RelativeDeltas,
    string? AbstentionReason);

internal sealed record AllocationDirectionalHealthyAaGate(
    bool Assessable,
    bool WithinPracticalSanity,
    decimal AaEnvelope,
    AllocationDirectionalPairSummary AbsoluteRelativeDeltas,
    string? AbstentionReason);

internal sealed record AllocationDirectionalPracticalStabilityGate(
    bool Assessable,
    bool Equivalent,
    decimal AaEnvelope,
    decimal PracticalFloor,
    decimal EquivalenceMargin,
    AllocationDirectionalPairSummary AaAbsoluteRelativeDeltas,
    AllocationDirectionalPairSummary EffectAbsoluteRelativeDeltas,
    string? AbstentionReason);

internal sealed record AllocationDirectionalPracticalThroughputGate(
    bool Assessable,
    bool WorkloadManipulationEstablished,
    bool EstimatedBytesPerOperationEquivalent,
    decimal EquivalenceMargin,
    AllocationDirectionalPairSummary ThroughputMultipliers,
    AllocationDirectionalPracticalStabilityGate Stability,
    string? AbstentionReason)
{
    public bool Passed =>
        Assessable
        && WorkloadManipulationEstablished
        && EstimatedBytesPerOperationEquivalent;
}

internal sealed record AllocationDirectionalPracticalWarmupGate(
    bool Assessable,
    bool EarlyMovementEstablished,
    bool Converged,
    decimal AaEnvelope,
    decimal EarlyDecisionFloor,
    decimal ConvergenceEquivalenceMargin,
    AllocationDirectionalPairSummary EarlyRelativeDeltas,
    AllocationDirectionalPairSummary ConvergedAbsoluteRelativeDeltas,
    string? AbstentionReason)
{
    public bool Passed => Assessable && EarlyMovementEstablished && Converged;
}

internal sealed record AllocationDirectionalPracticalMovementGate(
    bool Assessable,
    bool ExpectedMovementEstablished,
    decimal AaEnvelope,
    decimal DecisionFloor,
    AllocationDirectionalPairSummary RelativeDeltas,
    string? AbstentionReason);

internal sealed record AllocationDirectionalPracticalMixGate(
    bool Assessable,
    bool StableTotalEquivalent,
    bool TypeMovementEstablished,
    bool SiteMovementEstablished,
    decimal TypeAbsoluteDecisionFloor,
    decimal TypeShareDecisionFloor,
    decimal SiteAbsoluteDecisionFloor,
    decimal SiteShareDecisionFloor,
    AllocationDirectionalPracticalStabilityGate StableTotal,
    AllocationDirectionalPairSummary EffectTypeAbsoluteRelativeMovements,
    AllocationDirectionalPairSummary EffectTypeShareMovements,
    AllocationDirectionalPairSummary EffectSiteAbsoluteRelativeMovements,
    AllocationDirectionalPairSummary EffectSiteShareMovements,
    string? AbstentionReason)
{
    public bool Passed =>
        Assessable
        && StableTotalEquivalent
        && TypeMovementEstablished
        && SiteMovementEstablished;
}

internal static class AllocationDirectionalCalibrationSelector
{
    public static AllocationDirectionalThroughputGate EvaluateThroughput(
        IReadOnlyList<AllocationDirectionalPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var aa = ValidPairs(pairs, "aa");
        var effect = ValidPairs(pairs, "effect");
        var aaEnvelope = RelativeEnvelope(aa);
        var throughput = Summarize(effect.Select(pair =>
            OperationsPerSecond(pair.Current)
            / OperationsPerSecond(pair.Baseline)));
        var bytesPerOperation = Summarize(effect.Select(pair =>
            AbsoluteRelativeDelta(
                EstimatedBytesPerOperation(pair.Baseline),
                EstimatedBytesPerOperation(pair.Current))));
        var bytesPerSecond = Summarize(effect.Select(pair =>
            EstimatedBytesPerSecond(pair.Current)
            / EstimatedBytesPerSecond(pair.Baseline)));

        var assessable = HasRequiredPairs(aa)
            && HasRequiredPairs(effect)
            && aaEnvelope <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
            && throughput.HasRequiredInliers
            && bytesPerOperation.HasRequiredInliers;
        if (!assessable)
        {
            return new(
                false,
                false,
                false,
                aaEnvelope,
                throughput,
                bytesPerOperation,
                bytesPerSecond,
                "Throughput calibration requires three valid A/A pairs, three valid effect pairs, bounded A/A variability, and at least two non-outlier effect pairs.");
        }

        var workloadManipulationEstablished =
            throughput.Median
                > AllocationDirectionalCalibrationBounds.MinimumThroughputMultiplier
            && throughput.Inliers.All(value =>
                value > AllocationDirectionalCalibrationBounds.MinimumThroughputMultiplier);
        var estimatedBytesPerOperationStable =
            bytesPerOperation.Median <= aaEnvelope
            && bytesPerOperation.Inliers.All(value => value <= aaEnvelope);
        return new(
            true,
            workloadManipulationEstablished,
            estimatedBytesPerOperationStable,
            aaEnvelope,
            throughput,
            bytesPerOperation,
            bytesPerSecond,
            workloadManipulationEstablished
                ? estimatedBytesPerOperationStable
                    ? null
                    : "The workload manipulation was established, but estimated sampled bytes per operation did not remain inside the workload-specific A/A envelope."
                : "The workload manipulation itself was not established above 1.25x operations per second.");
    }

    public static AllocationDirectionalWarmupGate EvaluateWarmup(
        IReadOnlyList<AllocationDirectionalPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var aa = ValidPairs(pairs, "aa");
        var early = ValidPairs(pairs, "early");
        var converged = ValidPairs(pairs, "converged");
        var aaEnvelope = RelativeEnvelope(aa);
        var earlySummary = Summarize(early.Select(pair =>
            RelativeDelta(
                EstimatedBytesPerOperation(pair.Baseline),
                EstimatedBytesPerOperation(pair.Current))));
        var convergedSummary = Summarize(converged.Select(pair =>
            AbsoluteRelativeDelta(
                EstimatedBytesPerOperation(pair.Baseline),
                EstimatedBytesPerOperation(pair.Current))));
        var decisionFloor = Math.Max(
            aaEnvelope,
            AllocationDirectionalCalibrationBounds.PracticalRelativeEffectFloor);

        var assessable = HasRequiredPairs(aa)
            && HasRequiredPairs(early)
            && HasRequiredPairs(converged)
            && aaEnvelope <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
            && earlySummary.HasRequiredInliers
            && convergedSummary.HasRequiredInliers;
        if (!assessable)
        {
            return new(
                false,
                false,
                false,
                aaEnvelope,
                decisionFloor,
                earlySummary,
                convergedSummary,
                "Warmup requires three valid A/A, early, and converged pairs, bounded A/A variability, and at least two non-outlier pairs in each phase.");
        }

        var earlyMovementEstablished =
            earlySummary.Median > decisionFloor
            && earlySummary.Inliers.All(value => value > 0);
        var convergedInsideEnvelope =
            convergedSummary.Median <= aaEnvelope
            && convergedSummary.Inliers.All(value => value <= aaEnvelope);
        return new(
            true,
            earlyMovementEstablished,
            convergedInsideEnvelope,
            aaEnvelope,
            decisionFloor,
            earlySummary,
            convergedSummary,
            earlyMovementEstablished
                ? convergedInsideEnvelope
                    ? null
                    : "The robust converged-pair summary did not return inside the workload-specific A/A envelope."
                : "The robust early-pair summary did not exceed both the A/A envelope and the predeclared 10% practical-effect floor with non-outlier sign agreement.");
    }

    public static AllocationDirectionalMovementGate EvaluateMovement(
        IReadOnlyList<AllocationDirectionalPair> pairs,
        bool expectedIncrease)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var aa = ValidPairs(pairs, "aa");
        var effect = ValidPairs(pairs, "effect");
        var aaEnvelope = RelativeEnvelope(aa);
        var summary = Summarize(effect.Select(pair =>
            RelativeDelta(
                EstimatedBytesPerOperation(pair.Baseline),
                EstimatedBytesPerOperation(pair.Current))));
        var assessable = HasRequiredPairs(aa)
            && HasRequiredPairs(effect)
            && aaEnvelope <= AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta
            && summary.HasRequiredInliers;
        if (!assessable)
        {
            return new(
                false,
                false,
                aaEnvelope,
                summary,
                "Directional movement requires three valid A/A pairs, three valid effect pairs, bounded A/A variability, and at least two non-outlier effect pairs.");
        }

        var established = expectedIncrease
            ? summary.Median > aaEnvelope
                && summary.Inliers.All(value => value > 0)
            : summary.Median < -aaEnvelope
                && summary.Inliers.All(value => value < 0);
        return new(
            true,
            established,
            aaEnvelope,
            summary,
            established
                ? null
                : "The pair-first median/MAD summary did not establish the expected signed movement beyond A/A.");
    }

    public static AllocationDirectionalPairSummary Summarize(
        IEnumerable<decimal> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var values = source.ToArray();
        if (values.Length == 0)
        {
            return new(0, 0, 0, [], []);
        }

        var median = Median(values);
        var deviations = values.Select(value => Math.Abs(value - median)).ToArray();
        var mad = Median(deviations);
        var outlierIndexes = values
            .Select((value, index) => new
            {
                Index = index,
                IsOutlier = IsOutlier(value, median, mad),
            })
            .Where(item => item.IsOutlier)
            .Select(item => item.Index)
            .ToArray();
        var outlierSet = outlierIndexes.ToHashSet();
        var inliers = values
            .Where((_, index) => !outlierSet.Contains(index))
            .ToArray();
        return new(values.Length, median, mad, outlierIndexes, inliers);
    }

    private static bool IsOutlier(decimal value, decimal median, decimal mad)
    {
        if (mad == 0)
        {
            return value != median;
        }

        var modifiedZ = AllocationDirectionalCalibrationBounds.ModifiedZScoreScale
            * (value - median)
            / mad;
        return Math.Abs(modifiedZ)
            > AllocationDirectionalCalibrationBounds.ModifiedZScoreOutlierThreshold;
    }

    private static decimal Median(IReadOnlyList<decimal> values)
    {
        var ordered = values.Order().ToArray();
        var midpoint = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[midpoint - 1] + ordered[midpoint]) / 2
            : ordered[midpoint];
    }

    internal static AllocationDirectionalPair[] ValidPairs(
        IReadOnlyList<AllocationDirectionalPair> pairs,
        string currentPhase)
        => pairs.Where(pair =>
                string.Equals(
                    pair.Current.Phase,
                    currentPhase,
                    StringComparison.Ordinal)
                && IsValidPair(pair))
            .ToArray();

    internal static bool IsValidPair(AllocationDirectionalPair pair)
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

    internal static bool HasRequiredPairs(
        IReadOnlyCollection<AllocationDirectionalPair> pairs)
        => pairs.Count >= AllocationDirectionalBounds.RequiredPairs;

    internal static decimal RelativeEnvelope(
        IReadOnlyCollection<AllocationDirectionalPair> pairs)
        => HasRequiredPairs(pairs)
            ? pairs.Max(pair => AbsoluteRelativeDelta(
                EstimatedBytesPerOperation(pair.Baseline),
                EstimatedBytesPerOperation(pair.Current)))
            : decimal.MaxValue;

    internal static decimal EstimatedBytesPerOperation(
        AllocationDirectionalObservation value)
        => value.SampledEstimatedBytes / (decimal)value.Operations;

    internal static decimal OperationsPerSecond(
        AllocationDirectionalObservation value)
        => value.Operations / (decimal)value.CaptureDurationSeconds;

    internal static decimal EstimatedBytesPerSecond(
        AllocationDirectionalObservation value)
        => value.SampledEstimatedBytes / (decimal)value.CaptureDurationSeconds;

    internal static decimal RelativeDelta(decimal baseline, decimal current)
        => baseline == 0 ? 0 : (current - baseline) / Math.Abs(baseline);

    internal static decimal AbsoluteRelativeDelta(decimal baseline, decimal current)
        => Math.Abs(RelativeDelta(baseline, current));
}

internal static class AllocationDirectionalPracticalEquivalenceSelector
{
    public static AllocationDirectionalHealthyAaGate EvaluateHealthyAa(
        IReadOnlyList<AllocationDirectionalPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var aa = AllocationDirectionalCalibrationSelector.ValidPairs(pairs, "aa");
        var summary = AllocationDirectionalCalibrationSelector.Summarize(
            aa.Select(pair =>
                AllocationDirectionalCalibrationSelector.AbsoluteRelativeDelta(
                    AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                        pair.Baseline),
                    AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                        pair.Current))));
        var assessable =
            AllocationDirectionalCalibrationSelector.HasRequiredPairs(aa)
            && summary.HasRequiredInliers;
        var envelope = InlierEnvelope(summary);
        var withinSanity = assessable
            && summary.Median
                <= AllocationDirectionalCalibrationBounds
                    .PracticalRelativeEquivalenceFloor
            && summary.Inliers.All(value =>
                value
                    <= AllocationDirectionalCalibrationBounds
                        .PracticalRelativeEquivalenceFloor);
        return new(
            assessable,
            withinSanity,
            envelope,
            summary,
            assessable
                ? withinSanity
                    ? null
                    : "Healthy A/A variability exceeded the predeclared 10% relative bytes-per-operation sanity bound."
                : "Healthy A/A requires three valid pairs and at least two retained non-outlier pairs.");
    }

    public static AllocationDirectionalPracticalStabilityGate EvaluateStability(
        IReadOnlyList<AllocationDirectionalPair> pairs,
        string effectPhase = "effect")
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectPhase);
        var healthy = EvaluateHealthyAa(pairs);
        var effect = AllocationDirectionalCalibrationSelector.ValidPairs(
            pairs,
            effectPhase);
        var effectSummary = AllocationDirectionalCalibrationSelector.Summarize(
            effect.Select(pair =>
                AllocationDirectionalCalibrationSelector.AbsoluteRelativeDelta(
                    AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                        pair.Baseline),
                    AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                        pair.Current))));
        var practicalFloor =
            AllocationDirectionalCalibrationBounds.PracticalRelativeEquivalenceFloor;
        var margin = Math.Max(healthy.AaEnvelope, practicalFloor);
        var assessable = healthy.Assessable
            && healthy.WithinPracticalSanity
            && AllocationDirectionalCalibrationSelector.HasRequiredPairs(effect)
            && effectSummary.HasRequiredInliers;
        var equivalent = assessable
            && effectSummary.Median <= margin
            && effectSummary.Inliers.All(value => value <= margin);
        return new(
            assessable,
            equivalent,
            healthy.AaEnvelope,
            practicalFloor,
            margin,
            healthy.AbsoluteRelativeDeltas,
            effectSummary,
            assessable
                ? equivalent
                    ? null
                    : "The robust effect summary exceeded the predeclared practical-equivalence margin in estimated sampled bytes per operation."
                : healthy.AbstentionReason
                    ?? "Practical stability requires three valid effect pairs and at least two retained non-outlier pairs.");
    }

    public static AllocationDirectionalPracticalThroughputGate EvaluateThroughput(
        IReadOnlyList<AllocationDirectionalPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var effect = AllocationDirectionalCalibrationSelector.ValidPairs(
            pairs,
            "effect");
        var throughput = AllocationDirectionalCalibrationSelector.Summarize(
            effect.Select(pair =>
                AllocationDirectionalCalibrationSelector.OperationsPerSecond(
                    pair.Current)
                / AllocationDirectionalCalibrationSelector.OperationsPerSecond(
                    pair.Baseline)));
        var stability = EvaluateStability(pairs);
        var assessable = stability.Assessable
            && throughput.HasRequiredInliers;
        var workloadManipulationEstablished = assessable
            && throughput.Median
                > AllocationDirectionalCalibrationBounds.MinimumThroughputMultiplier
            && throughput.Inliers.All(value =>
                value
                    > AllocationDirectionalCalibrationBounds
                        .MinimumThroughputMultiplier);
        return new(
            assessable,
            workloadManipulationEstablished,
            stability.Equivalent,
            stability.EquivalenceMargin,
            throughput,
            stability,
            assessable
                ? workloadManipulationEstablished
                    ? stability.Equivalent
                        ? null
                        : stability.AbstentionReason
                    : "The workload manipulation itself was not established above 1.25x operations per second."
                : stability.AbstentionReason
                    ?? "Throughput requires three valid effect pairs and at least two retained non-outlier multipliers.");
    }

    public static AllocationDirectionalPracticalWarmupGate EvaluateWarmup(
        IReadOnlyList<AllocationDirectionalPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var healthy = EvaluateHealthyAa(pairs);
        var early = AllocationDirectionalCalibrationSelector.ValidPairs(
            pairs,
            "early");
        var converged = AllocationDirectionalCalibrationSelector.ValidPairs(
            pairs,
            "converged");
        var earlySummary = AllocationDirectionalCalibrationSelector.Summarize(
            early.Select(pair =>
                AllocationDirectionalCalibrationSelector.RelativeDelta(
                    AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                        pair.Baseline),
                    AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                        pair.Current))));
        var convergedSummary = AllocationDirectionalCalibrationSelector.Summarize(
            converged.Select(pair =>
                AllocationDirectionalCalibrationSelector.AbsoluteRelativeDelta(
                    AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                        pair.Baseline),
                    AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                        pair.Current))));
        var practicalFloor =
            AllocationDirectionalCalibrationBounds.PracticalRelativeEquivalenceFloor;
        var margin = Math.Max(healthy.AaEnvelope, practicalFloor);
        var assessable = healthy.Assessable
            && healthy.WithinPracticalSanity
            && AllocationDirectionalCalibrationSelector.HasRequiredPairs(early)
            && AllocationDirectionalCalibrationSelector.HasRequiredPairs(converged)
            && earlySummary.HasRequiredInliers
            && convergedSummary.HasRequiredInliers;
        var earlyEstablished = assessable
            && earlySummary.Median > margin
            && earlySummary.Inliers.All(value => value > 0);
        var convergedEquivalent = assessable
            && convergedSummary.Median <= margin
            && convergedSummary.Inliers.All(value => value <= margin);
        return new(
            assessable,
            earlyEstablished,
            convergedEquivalent,
            healthy.AaEnvelope,
            margin,
            margin,
            earlySummary,
            convergedSummary,
            assessable
                ? earlyEstablished
                    ? convergedEquivalent
                        ? null
                        : "The robust converged summary did not return inside the practical-equivalence margin for estimated sampled bytes per operation."
                    : "The robust early summary did not exceed the practical movement floor with retained non-outlier sign agreement."
                : healthy.AbstentionReason
                    ?? "Warmup requires three valid early and converged pairs and at least two retained non-outlier pairs per phase.");
    }

    public static AllocationDirectionalPracticalMovementGate EvaluateMovement(
        IReadOnlyList<AllocationDirectionalPair> pairs,
        bool expectedIncrease)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var healthy = EvaluateHealthyAa(pairs);
        var effect = AllocationDirectionalCalibrationSelector.ValidPairs(
            pairs,
            "effect");
        var summary = AllocationDirectionalCalibrationSelector.Summarize(
            effect.Select(pair =>
                AllocationDirectionalCalibrationSelector.RelativeDelta(
                    AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                        pair.Baseline),
                    AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                        pair.Current))));
        var floor = Math.Max(
            healthy.AaEnvelope,
            AllocationDirectionalCalibrationBounds.PracticalRelativeEffectFloor);
        var assessable = healthy.Assessable
            && healthy.WithinPracticalSanity
            && AllocationDirectionalCalibrationSelector.HasRequiredPairs(effect)
            && summary.HasRequiredInliers;
        var established = assessable && (expectedIncrease
            ? summary.Median > floor
                && summary.Inliers.All(value => value > 0)
            : summary.Median < -floor
                && summary.Inliers.All(value => value < 0));
        return new(
            assessable,
            established,
            healthy.AaEnvelope,
            floor,
            summary,
            assessable
                ? established
                    ? null
                    : "The pair-first movement summary did not exceed the practical relative bytes-per-operation floor with retained non-outlier sign agreement."
                : healthy.AbstentionReason
                    ?? "Directional movement requires three valid effect pairs and at least two retained non-outlier pairs.");
    }

    public static AllocationDirectionalPracticalMixGate EvaluateMix(
        IReadOnlyList<AllocationDirectionalPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var aa = AllocationDirectionalCalibrationSelector.ValidPairs(pairs, "aa");
        var effect = AllocationDirectionalCalibrationSelector.ValidPairs(
            pairs,
            "effect");
        var stability = EvaluateStability(pairs);
        var aaMetrics = aa.Select(MixMetrics).ToArray();
        var effectMetrics = effect.Select(MixMetrics).ToArray();
        var aaTypeAbsolute = AllocationDirectionalCalibrationSelector.Summarize(
            aaMetrics.Where(value => value is not null)
                .Select(value => value!.TypeAbsoluteRelativeMovement));
        var aaTypeShare = AllocationDirectionalCalibrationSelector.Summarize(
            aaMetrics.Where(value => value is not null)
                .Select(value => value!.TypeShareMovement));
        var aaSiteAbsolute = AllocationDirectionalCalibrationSelector.Summarize(
            aaMetrics.Where(value => value is not null)
                .Select(value => value!.SiteAbsoluteRelativeMovement));
        var aaSiteShare = AllocationDirectionalCalibrationSelector.Summarize(
            aaMetrics.Where(value => value is not null)
                .Select(value => value!.SiteShareMovement));
        var effectTypeAbsolute =
            AllocationDirectionalCalibrationSelector.Summarize(
                effectMetrics.Where(value => value is not null)
                    .Select(value => value!.TypeAbsoluteRelativeMovement));
        var effectTypeShare = AllocationDirectionalCalibrationSelector.Summarize(
            effectMetrics.Where(value => value is not null)
                .Select(value => value!.TypeShareMovement));
        var effectSiteAbsolute =
            AllocationDirectionalCalibrationSelector.Summarize(
                effectMetrics.Where(value => value is not null)
                    .Select(value => value!.SiteAbsoluteRelativeMovement));
        var effectSiteShare = AllocationDirectionalCalibrationSelector.Summarize(
            effectMetrics.Where(value => value is not null)
                .Select(value => value!.SiteShareMovement));
        var typeAbsoluteFloor = Math.Max(
            InlierEnvelope(aaTypeAbsolute),
            AllocationDirectionalCalibrationBounds.PracticalRelativeEffectFloor);
        var typeShareFloor = InlierEnvelope(aaTypeShare);
        var siteAbsoluteFloor = Math.Max(
            InlierEnvelope(aaSiteAbsolute),
            AllocationDirectionalCalibrationBounds.PracticalRelativeEffectFloor);
        var siteShareFloor = InlierEnvelope(aaSiteShare);
        var summaries = new[]
        {
            aaTypeAbsolute,
            aaTypeShare,
            aaSiteAbsolute,
            aaSiteShare,
            effectTypeAbsolute,
            effectTypeShare,
            effectSiteAbsolute,
            effectSiteShare,
        };
        var assessable = stability.Assessable
            && AllocationDirectionalCalibrationSelector.HasRequiredPairs(aa)
            && AllocationDirectionalCalibrationSelector.HasRequiredPairs(effect)
            && aaMetrics.All(value => value is not null)
            && effectMetrics.All(value => value is not null)
            && summaries.All(summary => summary.HasRequiredInliers);
        var topKeysChanged = assessable
            && effectMetrics.All(value =>
                value!.TypeTopKeyChanged && value.SiteTopKeyChanged);
        var typeMovement = assessable
            && topKeysChanged
            && effectTypeAbsolute.Median > typeAbsoluteFloor
            && effectTypeAbsolute.Inliers.All(value => value > typeAbsoluteFloor)
            && effectTypeShare.Median > typeShareFloor
            && effectTypeShare.Inliers.All(value => value > typeShareFloor);
        var siteMovement = assessable
            && topKeysChanged
            && effectSiteAbsolute.Median > siteAbsoluteFloor
            && effectSiteAbsolute.Inliers.All(value => value > siteAbsoluteFloor)
            && effectSiteShare.Median > siteShareFloor
            && effectSiteShare.Inliers.All(value => value > siteShareFloor);
        return new(
            assessable,
            stability.Equivalent,
            typeMovement,
            siteMovement,
            typeAbsoluteFloor,
            typeShareFloor,
            siteAbsoluteFloor,
            siteShareFloor,
            stability,
            effectTypeAbsolute,
            effectTypeShare,
            effectSiteAbsolute,
            effectSiteShare,
            assessable
                ? stability.Equivalent
                    ? typeMovement && siteMovement
                        ? null
                        : "A broad total-equivalence margin cannot establish a mix shift without material absolute movement and independent share movement for both type and site."
                    : stability.AbstentionReason
                : "Mix selection requires three valid A/A and effect pairs, complete independent type/site attribution, and at least two retained non-outlier pairs for every quantity.");
    }

    private static decimal InlierEnvelope(
        AllocationDirectionalPairSummary summary)
        => summary.HasRequiredInliers
            ? summary.Inliers.Max()
            : decimal.MaxValue;

    private static AllocationDirectionalMixMetrics? MixMetrics(
        AllocationDirectionalPair pair)
    {
        if (!AllocationDirectionalCalibrationSelector.IsValidPair(pair)
            || pair.Baseline.Types.Count == 0
            || pair.Current.Types.Count == 0
            || pair.Baseline.Sites.Count == 0
            || pair.Current.Sites.Count == 0)
        {
            return null;
        }

        return new(
            AbsoluteMovementRelativeToBaselineTotal(
                pair.Baseline.Types,
                pair.Baseline,
                pair.Current.Types,
                pair.Current),
            MaximumShareMovement(
                pair.Baseline.Types,
                pair.Baseline.SampledEstimatedBytes,
                pair.Current.Types,
                pair.Current.SampledEstimatedBytes),
            AbsoluteMovementRelativeToBaselineTotal(
                pair.Baseline.Sites,
                pair.Baseline,
                pair.Current.Sites,
                pair.Current),
            MaximumShareMovement(
                pair.Baseline.Sites,
                pair.Baseline.SampledEstimatedBytes,
                pair.Current.Sites,
                pair.Current.SampledEstimatedBytes),
            !string.Equals(
                TopKey(pair.Baseline.Types),
                TopKey(pair.Current.Types),
                StringComparison.Ordinal),
            !string.Equals(
                TopKey(pair.Baseline.Sites),
                TopKey(pair.Current.Sites),
                StringComparison.Ordinal));
    }

    private static decimal AbsoluteMovementRelativeToBaselineTotal(
        IReadOnlyList<AllocationDirectionalRankedRow> baselineRows,
        AllocationDirectionalObservation baseline,
        IReadOnlyList<AllocationDirectionalRankedRow> currentRows,
        AllocationDirectionalObservation current)
    {
        var baselineTotalPerOperation =
            AllocationDirectionalCalibrationSelector.EstimatedBytesPerOperation(
                baseline);
        if (baselineTotalPerOperation <= 0)
        {
            return 0;
        }

        var baselineByKey = baselineRows.ToDictionary(
            row => row.Key,
            row => row.EstimatedBytes / (decimal)baseline.Operations,
            StringComparer.Ordinal);
        var currentByKey = currentRows.ToDictionary(
            row => row.Key,
            row => row.EstimatedBytes / (decimal)current.Operations,
            StringComparer.Ordinal);
        return baselineByKey.Keys.Concat(currentByKey.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(key =>
                Math.Abs(
                    currentByKey.GetValueOrDefault(key)
                    - baselineByKey.GetValueOrDefault(key))
                / baselineTotalPerOperation)
            .DefaultIfEmpty()
            .Max();
    }

    private static decimal MaximumShareMovement(
        IReadOnlyList<AllocationDirectionalRankedRow> baselineRows,
        long baselineTotal,
        IReadOnlyList<AllocationDirectionalRankedRow> currentRows,
        long currentTotal)
    {
        if (baselineTotal <= 0 || currentTotal <= 0)
        {
            return 0;
        }

        var baselineByKey = baselineRows.ToDictionary(
            row => row.Key,
            row => row.EstimatedBytes / (decimal)baselineTotal,
            StringComparer.Ordinal);
        var currentByKey = currentRows.ToDictionary(
            row => row.Key,
            row => row.EstimatedBytes / (decimal)currentTotal,
            StringComparer.Ordinal);
        return baselineByKey.Keys.Concat(currentByKey.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(key => Math.Abs(
                currentByKey.GetValueOrDefault(key)
                - baselineByKey.GetValueOrDefault(key)))
            .DefaultIfEmpty()
            .Max();
    }

    private static string? TopKey(
        IReadOnlyList<AllocationDirectionalRankedRow> rows)
        => rows.OrderByDescending(row => row.EstimatedBytes)
            .Select(row => row.Key)
            .FirstOrDefault();

    private sealed record AllocationDirectionalMixMetrics(
        decimal TypeAbsoluteRelativeMovement,
        decimal TypeShareMovement,
        decimal SiteAbsoluteRelativeMovement,
        decimal SiteShareMovement,
        bool TypeTopKeyChanged,
        bool SiteTopKeyChanged);
}
