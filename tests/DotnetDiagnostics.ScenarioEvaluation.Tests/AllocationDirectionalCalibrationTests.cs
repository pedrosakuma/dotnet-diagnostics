using FluentAssertions;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationDirectionalCalibrationTests
{
    private static readonly string CalibrationArtifactDirectory = RepositoryPath(
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional");

    [Fact]
    public void ThroughputGate_RequiresStrictlyMoreThanOnePointTwoFive()
    {
        var pairs = ThroughputPairs(
            aaDeltas: [0.02m, 0.02m, 0.02m],
            multipliers: [1.25m, 1.26m, 1.27m],
            bytesPerOperationDeltas: [0.01m, 0.01m, 0.01m]);

        var result = AllocationDirectionalCalibrationSelector.EvaluateThroughput(
            pairs);

        result.Assessable.Should().BeTrue();
        result.WorkloadManipulationEstablished.Should().BeFalse();
        result.Passed.Should().BeFalse();
        result.AbstentionReason.Should().Contain("not established above 1.25x");
    }

    [Fact]
    public void ThroughputGate_UsesOperationsPerSecondAndStableBytesPerOperation()
    {
        var pairs = ThroughputPairs(
            aaDeltas: [0.02m, 0.02m, 0.02m],
            multipliers: [1.50m, 1.60m, 1.70m],
            bytesPerOperationDeltas: [0.02m, 0.02m, 0.02m]);

        var result = AllocationDirectionalCalibrationSelector.EvaluateThroughput(
            pairs);

        result.Passed.Should().BeTrue();
        result.ThroughputMultipliers.Median.Should().Be(1.60m);
        result.EstimatedBytesPerOperationAbsoluteRelativeDeltas.Median.Should()
            .Be(0.02m);
        result.EstimatedBytesPerSecondMultipliers.Median.Should().Be(1.632m);
    }

    [Fact]
    public void ThroughputGate_BytesPerOperationBoundaryIsInclusive()
    {
        var atBoundary = AllocationDirectionalCalibrationSelector.EvaluateThroughput(
            ThroughputPairs(
                aaDeltas: [0.02m, 0.02m, 0.02m],
                multipliers: [1.50m, 1.60m, 1.70m],
                bytesPerOperationDeltas: [0.02m, 0.02m, 0.02m]));
        var aboveBoundary = AllocationDirectionalCalibrationSelector.EvaluateThroughput(
            ThroughputPairs(
                aaDeltas: [0.02m, 0.02m, 0.02m],
                multipliers: [1.50m, 1.60m, 1.70m],
                bytesPerOperationDeltas: [0.021m, 0.021m, 0.021m]));

        atBoundary.Passed.Should().BeTrue();
        aboveBoundary.WorkloadManipulationEstablished.Should().BeTrue();
        aboveBoundary.EstimatedBytesPerOperationStable.Should().BeFalse();
        aboveBoundary.Passed.Should().BeFalse();
    }

    [Fact]
    public void ThroughputGate_AbstainsWithInsufficientPairs()
    {
        var result = AllocationDirectionalCalibrationSelector.EvaluateThroughput(
            ThroughputPairs(
                aaDeltas: [0.02m, 0.02m],
                multipliers: [1.50m, 1.60m],
                bytesPerOperationDeltas: [0.01m, 0.01m]));

        result.Assessable.Should().BeFalse();
        result.Passed.Should().BeFalse();
        result.AbstentionReason.Should().Contain("three valid A/A pairs");
    }

    [Fact]
    public void WarmupGate_PracticalFloorIsStrictAndSeparateFromAa()
    {
        var atFloor = AllocationDirectionalCalibrationSelector.EvaluateWarmup(
            WarmupPairs(
                aaDeltas: [0.02m, 0.02m, 0.02m],
                earlyDeltas: [0.10m, 0.10m, 0.10m],
                convergedDeltas: [0.02m, 0.02m, 0.02m]));
        var aboveFloor = AllocationDirectionalCalibrationSelector.EvaluateWarmup(
            WarmupPairs(
                aaDeltas: [0.02m, 0.02m, 0.02m],
                earlyDeltas: [0.101m, 0.101m, 0.101m],
                convergedDeltas: [0.02m, 0.02m, 0.02m]));

        atFloor.Assessable.Should().BeTrue();
        atFloor.EarlyDecisionFloor.Should().Be(
            AllocationDirectionalCalibrationBounds.PracticalRelativeEffectFloor);
        atFloor.EarlyMovementEstablished.Should().BeFalse();
        aboveFloor.Passed.Should().BeTrue();
    }

    [Fact]
    public void WarmupGate_AaBoundaryIsInclusiveButExcessiveAaAbstains()
    {
        var atBoundary = AllocationDirectionalCalibrationSelector.EvaluateWarmup(
            WarmupPairs(
                aaDeltas: [0.25m, 0.25m, 0.25m],
                earlyDeltas: [0.30m, 0.31m, 0.32m],
                convergedDeltas: [0.25m, 0.24m, 0.23m]));
        var aboveBoundary = AllocationDirectionalCalibrationSelector.EvaluateWarmup(
            WarmupPairs(
                aaDeltas: [0.251m, 0.251m, 0.251m],
                earlyDeltas: [0.30m, 0.31m, 0.32m],
                convergedDeltas: [0.25m, 0.24m, 0.23m]));

        atBoundary.Passed.Should().BeTrue();
        aboveBoundary.Assessable.Should().BeFalse();
        aboveBoundary.Passed.Should().BeFalse();
    }

    [Fact]
    public void WarmupGate_ToleratesFlaggedOutliers()
    {
        var result = AllocationDirectionalCalibrationSelector.EvaluateWarmup(
            WarmupPairs(
                aaDeltas: [0.02m, 0.02m, 0.02m],
                earlyDeltas: [0.20m, 0.21m, -0.50m],
                convergedDeltas: [0.01m, 0.015m, 0.50m]));

        result.Passed.Should().BeTrue();
        result.EarlyRelativeDeltas.OutlierIndexes.Should().Equal(2);
        result.ConvergedAbsoluteRelativeDeltas.OutlierIndexes.Should().Equal(2);
    }

    [Fact]
    public void WarmupGate_NonOutlierSignDisagreementAbstains()
    {
        var result = AllocationDirectionalCalibrationSelector.EvaluateWarmup(
            WarmupPairs(
                aaDeltas: [0.02m, 0.02m, 0.02m],
                earlyDeltas: [-0.01m, 0.11m, 0.23m],
                convergedDeltas: [0.01m, 0.015m, 0.02m]));

        result.Assessable.Should().BeTrue();
        result.EarlyRelativeDeltas.OutlierIndexes.Should().BeEmpty();
        result.EarlyMovementEstablished.Should().BeFalse();
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void WarmupGate_ConvergedSummaryMustReturnInsideAa()
    {
        var result = AllocationDirectionalCalibrationSelector.EvaluateWarmup(
            WarmupPairs(
                aaDeltas: [0.02m, 0.02m, 0.02m],
                earlyDeltas: [0.20m, 0.21m, 0.22m],
                convergedDeltas: [0.021m, 0.022m, 0.023m]));

        result.EarlyMovementEstablished.Should().BeTrue();
        result.Converged.Should().BeFalse();
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void WarmupGate_AbstainsWithInsufficientPhasePairs()
    {
        var result = AllocationDirectionalCalibrationSelector.EvaluateWarmup(
            WarmupPairs(
                aaDeltas: [0.02m, 0.02m, 0.02m],
                earlyDeltas: [0.20m, 0.21m],
                convergedDeltas: [0.01m, 0.015m, 0.02m]));

        result.Assessable.Should().BeFalse();
        result.Passed.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SignedMovementGate_UsesPairSummaryInsteadOfStrictAll(
        bool expectedIncrease)
    {
        var effect = expectedIncrease
            ? new[] { 0.20m, 0.21m, -0.50m }
            : [-0.20m, -0.21m, 0.50m];
        var result = AllocationDirectionalCalibrationSelector.EvaluateMovement(
            MovementPairs([0.02m, 0.02m, 0.02m], effect),
            expectedIncrease);

        result.Assessable.Should().BeTrue();
        result.ExpectedMovementEstablished.Should().BeTrue();
        result.RelativeDeltas.OutlierIndexes.Should().Equal(2);
    }

    [Fact]
    public void LiveCalibrationArtifact_PassesAndHasStableFingerprint()
    {
        var fixturePath = Path.Combine(
            CalibrationArtifactDirectory,
            "allocation-directional-throughput-calibration-v1.windows-x64.json");
        var reportPath = Path.Combine(
            CalibrationArtifactDirectory,
            "allocation-directional-throughput-calibration-v1.windows-x64.report.json");
        var fixture = AllocationDirectionalCalibrationJson.ReadFixture(fixturePath);
        var gate = AllocationDirectionalCalibrationSelector.EvaluateThroughput(
            fixture.Pairs);

        fixture.CalibrationId.Should().Be(
            AllocationDirectionalCalibrationLive.CalibrationId);
        fixture.CalibrationVersion.Should().Be(
            AllocationDirectionalCalibrationLive.CalibrationVersion);
        fixture.Pairs.Should().HaveCount(
            AllocationDirectionalBounds.RequiredPairs * 2);
        gate.Passed.Should().BeTrue();
        gate.ThroughputMultipliers.Median.Should().BeGreaterThan(
            AllocationDirectionalCalibrationBounds.MinimumThroughputMultiplier);
        gate.EstimatedBytesPerOperationAbsoluteRelativeDeltas.Median.Should()
            .BeLessThanOrEqualTo(gate.AaEnvelope);

        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        report.RootElement.GetProperty("passed").GetBoolean().Should().BeTrue();
        AssertFingerprint(
            fixturePath,
            53_577,
            "948f2100b8551dc921662d08f9c3af709232abc22e7062d23fc12d6724b65e18");
        AssertFingerprint(
            reportPath,
            2_062,
            "9c62676f514bc70f0f0405f0186c39f515f59d336353e80707a1af9826c1b4bc");
    }

    private static List<AllocationDirectionalPair> ThroughputPairs(
        IReadOnlyList<decimal> aaDeltas,
        IReadOnlyList<decimal> multipliers,
        IReadOnlyList<decimal> bytesPerOperationDeltas)
    {
        aaDeltas.Count.Should().Be(multipliers.Count);
        multipliers.Count.Should().Be(bytesPerOperationDeltas.Count);
        var pairs = new List<AllocationDirectionalPair>();
        for (var index = 0; index < aaDeltas.Count; index++)
        {
            pairs.Add(Pair(
                $"throughput-aa-{index}",
                "aa",
                1m,
                aaDeltas[index]));
            pairs.Add(Pair(
                $"throughput-effect-{index}",
                "effect",
                multipliers[index],
                bytesPerOperationDeltas[index]));
        }

        return pairs;
    }

    private static List<AllocationDirectionalPair> WarmupPairs(
        IReadOnlyList<decimal> aaDeltas,
        IReadOnlyList<decimal> earlyDeltas,
        IReadOnlyList<decimal> convergedDeltas)
    {
        var pairs = new List<AllocationDirectionalPair>();
        AddPhasePairs(pairs, "aa", aaDeltas);
        AddPhasePairs(pairs, "early", earlyDeltas);
        AddPhasePairs(pairs, "converged", convergedDeltas);
        return pairs;
    }

    private static List<AllocationDirectionalPair> MovementPairs(
        IReadOnlyList<decimal> aaDeltas,
        IReadOnlyList<decimal> effectDeltas)
    {
        var pairs = new List<AllocationDirectionalPair>();
        AddPhasePairs(pairs, "aa", aaDeltas);
        AddPhasePairs(pairs, "effect", effectDeltas);
        return pairs;
    }

    private static void AddPhasePairs(
        List<AllocationDirectionalPair> pairs,
        string phase,
        IReadOnlyList<decimal> deltas)
    {
        for (var index = 0; index < deltas.Count; index++)
        {
            pairs.Add(Pair(
                $"{phase}-{index}",
                phase,
                1m,
                deltas[index]));
        }
    }

    private static AllocationDirectionalPair Pair(
        string id,
        string currentPhase,
        decimal operationMultiplier,
        decimal bytesPerOperationRelativeDelta)
    {
        const long baselineOperations = 1_000;
        const long baselineBytesPerOperation = 10_000;
        var currentOperations = decimal.ToInt64(
            baselineOperations * operationMultiplier);
        var currentBytesPerOperation = decimal.ToInt64(
            baselineBytesPerOperation
            * (1 + bytesPerOperationRelativeDelta));
        return new(
            id,
            AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation,
            "synthetic-calibration",
            Observation(
                $"{id}-baseline",
                "steady",
                baselineOperations,
                baselineOperations * baselineBytesPerOperation),
            Observation(
                $"{id}-current",
                currentPhase,
                currentOperations,
                currentOperations * currentBytesPerOperation));
    }

    private static AllocationDirectionalObservation Observation(
        string id,
        string phase,
        long operations,
        long sampledEstimatedBytes)
        => new(
            id,
            AllocationDirectionalAttemptOutcome.Passed,
            AllocationDirectionalOrigin.ControlledProjection,
            "calibration-workload",
            "primary",
            phase,
            1,
            4,
            operations,
            288_000,
            0,
            sampledEstimatedBytes,
            Math.Max(1, sampledEstimatedBytes / 100_000),
            "complete",
            [new("System.Byte[]", sampledEstimatedBytes, 1)],
            [new("AllocationShapeWorkload.PrimaryOperation", sampledEstimatedBytes, 1)],
            null,
            []);

    private static string RepositoryPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName
                ?? throw new DirectoryNotFoundException(
                    "Could not locate the repository root."),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void AssertFingerprint(
        string path,
        int expectedBytes,
        string expectedSha256)
    {
        AllocationDirectionalProtocol.CanonicalByteCount(path).Should()
            .Be(expectedBytes);
        AllocationDirectionalProtocol.Sha256(path).Should().Be(expectedSha256);
    }
}
