using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationHotspotTests
{
    private static readonly string FixturePath = ScenarioManifestLoader.ScenarioPath(
        "Fixtures", "AllocationHotspot", "allocation-hotspot.windows.v1.json");

    [Fact]
    public void KnownShapes_ProduceGoWithoutIntervalsOrTypeStackCorrelation()
    {
        var report = AllocationHotspotHarness.Evaluate(SyntheticCaptures(), "synthetic");

        report.Verdict.Should().Be("GO");
        report.Scenarios.Should().OnlyContain(scenario => scenario.ExpectedBehaviorObserved);
        report.RepeatedAaContrast.ExpectedBehaviorObserved.Should().BeTrue();
        report.ChangedMixContrast.ExpectedBehaviorObserved.Should().BeTrue();
        report.ChangedMixContrast.TopTypeChanged.Should().BeTrue();
        report.ChangedMixContrast.TopStackChanged.Should().BeTrue();
        report.Scenarios.Single(scenario =>
                scenario.Scenario == AllocationHotspotScenario.StableHotspot)
            .Assessment.Should().Be(AllocationHotspotAssessment.FocusedInvestigationSupported);

        report.Scenarios.SelectMany(scenario => new[]
            {
                scenario.EstimatedBytesMedian,
                scenario.EstimatedBytesMad,
                scenario.AllocationTicksMedian,
                scenario.AllocationTicksMad,
            })
            .Should().OnlyContain(result => result.Interval == null);
        report.Conclusions.Should().Contain(item =>
            item.Contains("independent ranked inventories", StringComparison.Ordinal));
        var json = AllocationHotspotJson.SerializeReport(report).ToLowerInvariant();
        json.Should().NotContain("type-to-stack correlation");
        json.Should().NotContain("\"confidencelevel\"");
    }

    [Fact]
    public void EvidenceStates_RemainDistinctAndMissingAttributionIsNotZero()
    {
        var report = AllocationHotspotHarness.Evaluate(SyntheticCaptures(), "synthetic");

        report.Scenarios.Single(scenario => scenario.Scenario == AllocationHotspotScenario.RawEmpty)
            .AttributionState.Should().Be(AllocationAttributionState.RawEmpty);
        report.Scenarios.Single(scenario => scenario.Scenario == AllocationHotspotScenario.RawNonzeroNoStack)
            .AttributionState.Should().Be(AllocationAttributionState.NoStack);
        report.Scenarios.Single(scenario => scenario.Scenario == AllocationHotspotScenario.UsableStackZeroWeight)
            .AttributionState.Should().Be(AllocationAttributionState.ZeroWeight);
        report.Scenarios.Single(scenario => scenario.Scenario == AllocationHotspotScenario.InvalidHandle)
            .AttributionState.Should().Be(AllocationAttributionState.Unavailable);
        report.Scenarios.Single(scenario => scenario.Scenario == AllocationHotspotScenario.LowSample)
            .AttributionState.Should().Be(AllocationAttributionState.Partial);
        report.Scenarios.Single(scenario => scenario.Scenario == AllocationHotspotScenario.StableHotspot)
            .AttributionState.Should().Be(AllocationAttributionState.Complete);

        var invalid = report.Scenarios.Single(scenario =>
            scenario.Scenario == AllocationHotspotScenario.InvalidHandle);
        invalid.MedianAttributedByteShare.Should().BeNull();
        invalid.MedianUnattributedByteShare.Should().BeNull();
        invalid.Assessment.Should().Be(
            AllocationHotspotAssessment.RetainedAttributionUnavailable);
    }

    [Fact]
    public void LowSample_DoesNotPromoteOneCaptureOrTwoTicksToStableEvidence()
    {
        var report = AllocationHotspotHarness.Evaluate(SyntheticCaptures(), "synthetic");
        var low = report.Scenarios.Single(scenario =>
            scenario.Scenario == AllocationHotspotScenario.LowSample);

        low.CaptureCount.Should().Be(1);
        low.EstimatedBytesMedian.Availability.Should().Be(
            ScenarioStatisticalAvailability.Available);
        low.EstimatedBytesMad.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        low.TypeTopK.ComparisonCount.Should().Be(0);
        low.StackTopK.ComparisonCount.Should().Be(0);
        low.AttributionState.Should().Be(AllocationAttributionState.Partial);
        low.Assessment.Should().Be(AllocationHotspotAssessment.InsufficientCaptureCount);
    }

    [Fact]
    public void ReorderedCaptures_ProduceEquivalentReport()
    {
        var captures = SyntheticCaptures();

        var first = AllocationHotspotHarness.Evaluate(captures, "synthetic");
        var reordered = AllocationHotspotHarness.Evaluate(
            captures.AsEnumerable().Reverse().ToArray(),
            "synthetic");

        AllocationHotspotJson.SerializeReport(reordered)
            .Should().Be(AllocationHotspotJson.SerializeReport(first));
    }

    [Fact]
    public void CompatiblePartialAndCompleteCaptures_PreserveRankedEvidence()
    {
        var captures = SyntheticCaptures();
        var stableIndices = captures.Select((capture, index) => (capture, index))
            .Where(item => item.capture.Scenario == AllocationHotspotScenario.StableHotspot)
            .Take(2)
            .Select(item => item.index)
            .ToArray();
        foreach (var stableIndex in stableIndices)
        {
            captures[stableIndex] = captures[stableIndex] with
            {
                AttributedAllocationTicks = captures[stableIndex].RawAllocationTicks - 1,
                AttributedEstimatedBytes = captures[stableIndex].RawEstimatedBytes - 100_000,
            };
        }

        var report = AllocationHotspotHarness.Evaluate(captures, "synthetic");
        var stable = report.Scenarios.Single(scenario =>
            scenario.Scenario == AllocationHotspotScenario.StableHotspot);

        report.Verdict.Should().Be("GO");
        stable.AttributionState.Should().Be(AllocationAttributionState.Partial);
        stable.MedianAttributedByteShare.Should().BeLessThan(1);
        stable.Assessment.Should().Be(
            AllocationHotspotAssessment.FocusedInvestigationSupported);
    }

    [Fact]
    public void ContrastAlone_DoesNotOverrideUnavailableByteWeightedRanking()
    {
        var captures = SyntheticCaptures();
        for (var index = 0; index < captures.Count; index++)
        {
            if (captures[index].Scenario is not (
                AllocationHotspotScenario.RepeatedAa
                or AllocationHotspotScenario.ChangedMix))
            {
                continue;
            }

            captures[index] = captures[index] with
            {
                AttributedEstimatedBytes = 0,
                Stacks = captures[index].Stacks
                    .Select(item => item with { EstimatedBytes = 0 })
                    .ToArray(),
            };
        }

        var report = AllocationHotspotHarness.Evaluate(captures, "synthetic");

        report.RepeatedAaContrast.ExpectedBehaviorObserved.Should().BeTrue();
        report.ChangedMixContrast.ExpectedBehaviorObserved.Should().BeTrue();
        report.Scenarios.Where(scenario => scenario.Scenario is
                AllocationHotspotScenario.RepeatedAa
                or AllocationHotspotScenario.ChangedMix)
            .Should().OnlyContain(scenario =>
                !scenario.ExpectedBehaviorObserved
                && scenario.Assessment == AllocationHotspotAssessment.NotReproducible);
        report.Verdict.Should().Be("NO-GO");
    }

    [Fact]
    public void LiveReplayFixture_PreservesGoEvidenceAndBounds()
    {
        var fixture = AllocationHotspotJson.ReadFixture(FixturePath);

        var report = AllocationHotspotHarness.Evaluate(
            fixture.Captures,
            fixture.Environment);

        report.Verdict.Should().Be(fixture.ExpectedVerdict).And.Be("GO");
        report.Captures.Should().OnlyContain(capture =>
            capture.Types.Count <= AllocationHotspotBounds.MaximumRankedRows
            && capture.Stacks.Count <= AllocationHotspotBounds.MaximumRankedRows);
        report.Scenarios.Should().OnlyContain(scenario => scenario.ExpectedBehaviorObserved);
        report.RepeatedAaContrast.TypeTopK.ComparisonCount.Should().Be(
            AllocationHotspotBounds.MinimumStableCaptures);
        report.ChangedMixContrast.TypeTopK.MedianMembershipJaccard
            .Should().BeLessThan(2d / 3d);
        report.ChangedMixContrast.TypeTopK.MinimumSharedRankCorrelation
            .Should().BeInRange(-1d, 1d);
    }

    [Fact]
    public void Bounds_RejectExcessiveRankedInventories()
    {
        var captures = SyntheticCaptures();
        var stableIndex = captures.FindIndex(capture =>
            capture.Scenario == AllocationHotspotScenario.StableHotspot);
        captures[stableIndex] = captures[stableIndex] with
        {
            Types = Enumerable.Range(1, AllocationHotspotBounds.MaximumRankedRows + 1)
                .Select(index => new AllocationRankedEvidence(
                    $"type-{index}",
                    index,
                    100,
                    1))
                .ToArray(),
        };

        var action = () => AllocationHotspotHarness.Evaluate(captures, "synthetic");

        action.Should().Throw<InvalidDataException>().WithMessage("*out-of-bounds*");
    }

    private static List<AllocationHotspotCapture> SyntheticCaptures()
    {
        var captures = new List<AllocationHotspotCapture>();
        long[] ticks = [100, 104, 96, 102];
        long[] bytes = [10_000_000, 10_400_000, 9_600_000, 10_200_000];
        for (var index = 0; index < AllocationHotspotBounds.MinimumStableCaptures; index++)
        {
            captures.Add(Capture(
                AllocationHotspotScenario.StableHotspot,
                $"stable-{index + 1:D2}",
                "primary",
                ticks[index],
                bytes[index],
                PrimaryTypes(),
                PrimaryStacks()));
            captures.Add(Capture(
                AllocationHotspotScenario.RepeatedAa,
                $"repeat-{index + 1:D2}",
                "primary",
                ticks[index] + 1,
                bytes[index] + 100_000,
                PrimaryTypes(),
                PrimaryStacks()));
            captures.Add(Capture(
                AllocationHotspotScenario.ChangedMix,
                $"changed-{index + 1:D2}",
                "changed",
                ticks[index] + 2,
                bytes[index] + 200_000,
                ChangedTypes(),
                ChangedStacks()));
        }

        captures.Add(Capture(
            AllocationHotspotScenario.LowSample,
            "low-01",
            "primary",
            2,
            200_000,
            [new("System.Byte[]", 1, 200_000, 2)],
            [new("BadCodeSample!AllocatePrimaryBytes", 1, 100_000, 1)],
            attributedTicks: 1,
            attributedBytes: 100_000));
        captures.Add(Capture(
            AllocationHotspotScenario.RawEmpty,
            "raw-empty-01",
            "none",
            0,
            0,
            [],
            []));
        captures.Add(Capture(
            AllocationHotspotScenario.RawNonzeroNoStack,
            "no-stack-01",
            "primary",
            10,
            1_000_000,
            PrimaryTypes(),
            [],
            attributedTicks: 0,
            attributedBytes: 0));
        captures.Add(Capture(
            AllocationHotspotScenario.UsableStackZeroWeight,
            "zero-weight-01",
            "primary",
            10,
            1_000_000,
            PrimaryTypes(),
            PrimaryStacks().Select(item => item with { EstimatedBytes = 0 }).ToArray(),
            attributedTicks: 9,
            attributedBytes: 0));
        captures.Add(Capture(
            AllocationHotspotScenario.InvalidHandle,
            "invalid-handle-01",
            "primary",
            10,
            1_000_000,
            PrimaryTypes(),
            [],
            attributedTicks: 0,
            attributedBytes: 0,
            handleState: AllocationHandleState.InvalidOrExpired));
        return captures;
    }

    private static AllocationHotspotCapture Capture(
        AllocationHotspotScenario scenario,
        string id,
        string variant,
        long ticks,
        long bytes,
        IReadOnlyList<AllocationRankedEvidence> types,
        IReadOnlyList<AllocationRankedEvidence> stacks,
        long? attributedTicks = null,
        long? attributedBytes = null,
        AllocationHandleState handleState = AllocationHandleState.Available)
        => new(
            id,
            scenario,
            variant,
            AllocationEvidenceOrigin.DeterministicFixture,
            "10.0",
            "windows",
            "x64",
            3,
            handleState,
            ticks,
            bytes,
            attributedTicks ?? ticks,
            attributedBytes ?? bytes,
            types,
            stacks,
            []);

    private static AllocationRankedEvidence[] PrimaryTypes()
        =>
        [
            new("System.Byte[]", 1, 7_000_000, 70),
            new("System.Char[]", 2, 2_000_000, 20),
            new("System.String", 3, 1_000_000, 10),
        ];

    private static AllocationRankedEvidence[] ChangedTypes()
        =>
        [
            new("System.Int32[]", 1, 7_000_000, 70),
            new("System.Char[]", 2, 2_000_000, 20),
            new("System.Object[]", 3, 1_000_000, 10),
        ];

    private static AllocationRankedEvidence[] PrimaryStacks()
        =>
        [
            new("BadCodeSample!AllocatePrimaryBytes", 1, 7_000_000, 70),
            new("BadCodeSample!AllocateSharedChars", 2, 2_000_000, 20),
            new("BadCodeSample!PrimaryDispatch", 3, 1_000_000, 10),
        ];

    private static AllocationRankedEvidence[] ChangedStacks()
        =>
        [
            new("BadCodeSample!AllocateChangedIntegers", 1, 7_000_000, 70),
            new("BadCodeSample!AllocateSharedChars", 2, 2_000_000, 20),
            new("BadCodeSample!ChangedDispatch", 3, 1_000_000, 10),
        ];
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class AllocationHotspotLiveTests
{
    [WindowsOrLinuxFact(
        "Bounded allocation hotspot characterization requires a live CoreCLR target.",
        Timeout = 600_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public async Task LiveCapture_ProducesBoundedReplayableAllocationEvidence()
    {
        var fixturePath = Environment.GetEnvironmentVariable(
            AllocationHotspotEnvironmentVariables.FixturePath);
        var repetitions = Repetitions(fixturePath);
        var captures = await AllocationHotspotLive.CaptureAllAsync(
            repetitions,
            CancellationToken.None);

        captures.Where(capture => capture.Origin == AllocationEvidenceOrigin.LiveCapture)
            .Should().OnlyContain(capture =>
                capture.RawAllocationTicks > 0
                && capture.RawEstimatedBytes > 0
                && capture.Types.Count >= 2
                && capture.Stacks.Count >= 2);
        captures.Should().OnlyContain(capture =>
            capture.Types.Count <= AllocationHotspotBounds.MaximumRankedRows
            && capture.Stacks.Count <= AllocationHotspotBounds.MaximumRankedRows);
        captures.Select(AllocationHotspotHarness.Classify).Should().Contain(
        [
            AllocationAttributionState.RawEmpty,
            AllocationAttributionState.NoStack,
            AllocationAttributionState.ZeroWeight,
            AllocationAttributionState.Partial,
            AllocationAttributionState.Unavailable,
        ]);

        if (!string.IsNullOrWhiteSpace(fixturePath))
        {
            var report = AllocationHotspotHarness.Evaluate(
                captures,
                $"{Environment.OSVersion}; {Environment.Version}");
            report.Verdict.Should().Be(
                "GO",
                AllocationHotspotJson.SerializeReport(report));
            AllocationHotspotJson.WriteFixture(
                fixturePath,
                new AllocationHotspotStudyFixture(
                    AllocationHotspotJson.CurrentSchemaVersion,
                    "allocation-hotspot-live-windows-v1",
                    report.Environment,
                    captures,
                    report.Verdict));
        }
    }

    private static int Repetitions(string? fixturePath)
    {
        var raw = Environment.GetEnvironmentVariable(
            AllocationHotspotEnvironmentVariables.Repetitions);
        if (int.TryParse(raw, out var repetitions))
        {
            return Math.Clamp(
                repetitions,
                1,
                AllocationHotspotBounds.MaximumCapturesPerScenario);
        }

        return string.IsNullOrWhiteSpace(fixturePath)
            ? 1
            : AllocationHotspotBounds.MinimumStableCaptures;
    }
}
