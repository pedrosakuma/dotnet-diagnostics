namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalCalibrationBounds
{
    public const decimal MinimumThroughputMultiplier = 1.25m;
    public const decimal PracticalRelativeEffectFloor = 0.10m;
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

    private static AllocationDirectionalPair[] ValidPairs(
        IReadOnlyList<AllocationDirectionalPair> pairs,
        string currentPhase)
        => pairs.Where(pair =>
                string.Equals(
                    pair.Current.Phase,
                    currentPhase,
                    StringComparison.Ordinal)
                && IsValidPair(pair))
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

    private static bool HasRequiredPairs(
        IReadOnlyCollection<AllocationDirectionalPair> pairs)
        => pairs.Count >= AllocationDirectionalBounds.RequiredPairs;

    private static decimal RelativeEnvelope(
        IReadOnlyCollection<AllocationDirectionalPair> pairs)
        => HasRequiredPairs(pairs)
            ? pairs.Max(pair => AbsoluteRelativeDelta(
                EstimatedBytesPerOperation(pair.Baseline),
                EstimatedBytesPerOperation(pair.Current)))
            : decimal.MaxValue;

    private static decimal EstimatedBytesPerOperation(
        AllocationDirectionalObservation value)
        => value.SampledEstimatedBytes / (decimal)value.Operations;

    private static decimal OperationsPerSecond(
        AllocationDirectionalObservation value)
        => value.Operations / (decimal)value.CaptureDurationSeconds;

    private static decimal EstimatedBytesPerSecond(
        AllocationDirectionalObservation value)
        => value.SampledEstimatedBytes / (decimal)value.CaptureDurationSeconds;

    private static decimal RelativeDelta(decimal baseline, decimal current)
        => baseline == 0 ? 0 : (current - baseline) / Math.Abs(baseline);

    private static decimal AbsoluteRelativeDelta(decimal baseline, decimal current)
        => Math.Abs(RelativeDelta(baseline, current));
}
