using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class RetainedGrowthScenarioTests
{
    public static TheoryData<string> ReplayFixtures { get; } = new()
    {
        "retained-growth.v1.json",
        "allocation-churn.v1.json",
        "stable-heap.v1.json",
        "degraded-incomplete.v1.json",
    };

    [Theory]
    [MemberData(nameof(ReplayFixtures))]
    public void ReplayFixture_EvaluatesObjectiveEvidenceWithoutUsingWorkloadTruth(string fileName)
    {
        var fixture = RetainedGrowthScenarioJson.LoadReplayFixture(FixturePath(fileName));

        var result = RetainedGrowthScenarioEvaluator.Evaluate(fixture.Evidence);

        result.PositiveRetainedGrowthEvidence.Should()
            .Be(fixture.Expected.PositiveRetainedGrowthEvidence);
        result.RetainedBytesDelta.Availability.Should()
            .Be(fixture.Expected.RetainedBytesAvailability);
        result.RetainedInstancesDelta.Availability.Should()
            .Be(fixture.Expected.RetainedInstancesAvailability);
        result.RepeatedRetainedByteChange.Availability.Should()
            .Be(fixture.Expected.RepeatedChangeAvailability);
        result.RepeatedRetainedByteSpread.Availability.Should()
            .Be(fixture.Expected.SpreadAvailability);
        result.RepeatedRetainedByteInterval.Availability.Should()
            .Be(fixture.Expected.IntervalAvailability);
        result.PersistenceRate.Availability.Should()
            .Be(fixture.Expected.PersistenceAvailability);
        result.GrowingTypeConcentration.Availability.Should()
            .Be(fixture.Expected.ConcentrationAvailability);
        result.RootPathEvidence.Availability.Should()
            .Be(fixture.Expected.RootPathAvailability);
        result.RetainedBytesDelta.Delta?.Absolute.Should()
            .Be(fixture.Expected.RetainedBytesAbsoluteDelta);
        result.RetainedInstancesDelta.Delta?.Absolute.Should()
            .Be(fixture.Expected.RetainedInstancesAbsoluteDelta);
        result.RepeatedRetainedByteChange.Estimate.Should()
            .Be(fixture.Expected.RepeatedChangeEstimate);
        result.PersistenceRate.Rate?.Value.Should()
            .Be(fixture.Expected.PersistenceRate);

        var serialized = RetainedGrowthScenarioJson.SerializeResult(result);
        serialized.Should().NotContain("\"verdict\"");
        serialized.Should().NotContain("leak_suspected");
        serialized.Should().NotContain("no_leak");
        serialized.Should().NotContain("\"healthy\"");
        fixture.Expected.NoLeakVerdict.Should().BeTrue();
    }

    [Fact]
    public void RetainedGrowth_UsesRegistryMethodsAtProcessRunIndependence()
    {
        var fixture = RetainedGrowthScenarioJson.LoadReplayFixture(
            FixturePath("retained-growth.v1.json"));

        var result = RetainedGrowthScenarioEvaluator.Evaluate(fixture.Evidence);

        result.RetainedBytesDelta.Method.Id.Should().Be(ScenarioStatisticalMethodIds.PairedDelta);
        result.RetainedInstancesDelta.Method.Id.Should().Be(ScenarioStatisticalMethodIds.PairedDelta);
        result.RepeatedRetainedByteChange.Method.Id.Should().Be(ScenarioStatisticalMethodIds.WindowMedian);
        result.RepeatedRetainedByteSpread.Method.Id.Should()
            .Be(ScenarioStatisticalMethodIds.MedianAbsoluteDeviation);
        result.RepeatedRetainedByteInterval.Method.Id.Should()
            .Be(ScenarioStatisticalMethodIds.PercentileBootstrapInterval);
        result.PersistenceRate.Method.Id.Should().Be(ScenarioStatisticalMethodIds.WilsonScoreRate);
        result.PostGcStabilizationRate.Method.Id.Should()
            .Be(ScenarioStatisticalMethodIds.WilsonScoreRate);
        result.RetainedBytesDelta.IndependenceUnit.Should()
            .Be(ScenarioStatisticalIndependenceUnit.ProcessRun);
        result.RepeatedRetainedByteInterval.CalculationProvenance.ResamplesRequested.Should()
            .Be(RetainedGrowthScenarioBounds.BootstrapResamples);
        result.CaptureInducedGen2Collections.Should().Be(9);
        result.Limitations.Should().Contain(item =>
            item.Contains("does not establish a memory leak", StringComparison.Ordinal));
        result.Limitations.Should().Contain(item =>
            item.Contains("Allocation traffic is reported separately", StringComparison.Ordinal));
        result.Limitations.Should().Contain(item =>
            item.Contains("Gen2 collection(s) were induced", StringComparison.Ordinal));
    }

    [Fact]
    public void ChurnAndRetainedGrowth_HaveComparableTrafficButDifferentRetainedEvidence()
    {
        var retained = RetainedGrowthScenarioEvaluator.Evaluate(
            RetainedGrowthScenarioJson.LoadReplayFixture(
                FixturePath("retained-growth.v1.json")).Evidence);
        var churn = RetainedGrowthScenarioEvaluator.Evaluate(
            RetainedGrowthScenarioJson.LoadReplayFixture(
                FixturePath("allocation-churn.v1.json")).Evidence);

        retained.AllocationTraffic.Estimate.Should().BeApproximately(
            churn.AllocationTraffic.Estimate!.Value,
            churn.AllocationTraffic.Estimate.Value * 0.1);
        retained.PositiveRetainedGrowthEvidence.Should()
            .Be(RetainedGrowthPositiveEvidence.Supported);
        churn.PositiveRetainedGrowthEvidence.Should()
            .Be(RetainedGrowthPositiveEvidence.NotEstablished);
        churn.RetainedBytesDelta.Delta!.Absolute.Should().Be(0);
        churn.PersistenceRate.Rate!.Value.Should().Be(0);
        churn.Limitations.Should().Contain(item =>
            item.Contains("does not prove absence of retention", StringComparison.Ordinal));
    }

    [Fact]
    public void DegradedEvidence_AbstainsEvenWhenTruthSaysRetentionWasIntentional()
    {
        var fixture = RetainedGrowthScenarioJson.LoadReplayFixture(
            FixturePath("degraded-incomplete.v1.json"));
        fixture.Truth.IntentionalRetention.Should().BeTrue();
        fixture.Truth.ExpectedRetainedObjectsAfterWorkload.Should().BeGreaterThan(
            fixture.Truth.SeedRetainedObjects);

        var result = RetainedGrowthScenarioEvaluator.Evaluate(fixture.Evidence);

        result.PositiveRetainedGrowthEvidence.Should()
            .Be(RetainedGrowthPositiveEvidence.Inconclusive);
        result.RootPathEvidence.Availability.Should()
            .Be(RetainedGrowthRootPathAvailability.IncompleteEvidence);
        result.RetainedBytesDelta.Delta.Should().BeNull();
        result.AllocationTraffic.Availability.Should().NotBe(ScenarioStatisticalAvailability.Available);
    }

    private static string FixturePath(string fileName)
        => Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "RetainedGrowth",
            fileName);
}
