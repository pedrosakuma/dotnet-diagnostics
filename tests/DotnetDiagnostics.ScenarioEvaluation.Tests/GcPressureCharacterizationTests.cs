using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class GcPressureCharacterizationTests
{
    public static TheoryData<string> ReplayScenarios()
    {
        var fixture = GcPressureCharacterizationHarness.ReadFixture(FixturePath());
        var data = new TheoryData<string>();
        foreach (var scenario in fixture.Scenarios)
        {
            data.Add(scenario.Id);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ReplayScenarios))]
    public void Replay_SelectsExpectedNextActionDeterministically(string scenarioId)
    {
        var scenario = GcPressureCharacterizationHarness.ReadFixture(FixturePath())
            .Scenarios.Single(item => item.Id == scenarioId);

        var first = GcPressureCharacterizationHarness.Evaluate(scenario.Capture);
        var second = GcPressureCharacterizationHarness.Evaluate(scenario.Capture);

        first.NextAction.Should().Be(scenario.ExpectedAction);
        first.PostLoadStabilized.Should().Be(scenario.ExpectedPostLoadStabilized);
        (first.Persistence?.PressureWindows ?? 0)
            .Should().BeGreaterThanOrEqualTo(scenario.MinimumPressureWindows);
        GcPressureCharacterizationHarness.Serialize(second)
            .Should().Be(GcPressureCharacterizationHarness.Serialize(first));
    }

    [Fact]
    public void Replay_DistinguishesTransientSustainedRetainedHealthyAndLoss()
    {
        var results = GcPressureCharacterizationHarness.ReadFixture(FixturePath())
            .Scenarios.ToDictionary(
                scenario => scenario.Capture.Scenario,
                scenario => GcPressureCharacterizationHarness.Evaluate(scenario.Capture));

        results[GcPressureScenarioKind.TransientAllocationBurst].NextAction
            .Should().Be(GcPressureNextAction.ContinuedObservation);
        results[GcPressureScenarioKind.TransientAllocationBurst].PostLoadStabilized
            .Should().BeTrue();
        results[GcPressureScenarioKind.SustainedAllocationPressure].NextAction
            .Should().Be(GcPressureNextAction.AllocationInvestigation);
        results[GcPressureScenarioKind.RetainedGrowth].NextAction
            .Should().Be(GcPressureNextAction.RetainedHeapInvestigation);
        results[GcPressureScenarioKind.RetainedGrowth].LohSize.Slope.Slope
            .Should().BeGreaterThan(4 * 1024 * 1024);
        results[GcPressureScenarioKind.StableHealthy].NextAction
            .Should().Be(GcPressureNextAction.ContinuedObservation);
        results[GcPressureScenarioKind.StableHealthy].Persistence!.PressureWindows.Should().Be(0);
        results[GcPressureScenarioKind.CaptureLoss].NextAction
            .Should().Be(GcPressureNextAction.JustifiedInconclusion);
        results[GcPressureScenarioKind.CaptureLoss].LohSize.Median.Availability
            .Should().Be(GcPressureAvailability.LossDetected);
    }

    [Fact]
    public void Replay_UsesWindowIndependenceAndNeverClaimsCausality()
    {
        var scenario = GcPressureCharacterizationHarness.ReadFixture(FixturePath())
            .Scenarios.Single(item => item.Capture.Scenario == GcPressureScenarioKind.SustainedAllocationPressure);

        var result = GcPressureCharacterizationHarness.Evaluate(scenario.Capture);

        result.LohSize.Median.IndependenceUnit
            .Should().Be("bounded_time_window");
        result.LohSize.Median.Counts.IndependenceUnits.Should().Be(6);
        result.TimingObservations.Should().OnlyContain(observation =>
            observation.Interpretation.Contains("do not establish causality", StringComparison.Ordinal));
        result.Limitations.Should().Contain(limitation =>
            limitation.Contains("does not identify a retaining cause", StringComparison.Ordinal));
    }

    [Fact]
    public void BoundsAndMinimumEvidence_ForceJustifiedInconclusion()
    {
        var scenario = GcPressureCharacterizationHarness.ReadFixture(FixturePath())
            .Scenarios.Single(item => item.Capture.Scenario == GcPressureScenarioKind.SustainedAllocationPressure);
        var insufficient = scenario.Capture with
        {
            Id = "insufficient",
            Windows =
            [
                scenario.Capture.Windows[0],
                scenario.Capture.Windows[1],
                scenario.Capture.Windows[4],
                scenario.Capture.Windows[5],
            ],
        };

        var result = GcPressureCharacterizationHarness.Evaluate(insufficient);

        result.NextAction.Should().Be(GcPressureNextAction.JustifiedInconclusion);
        result.LohSize.Median.Availability
            .Should().Be(GcPressureAvailability.InsufficientObservations);

        var unbounded = scenario.Capture with
        {
            Id = "unbounded",
            Windows = Enumerable.Range(0, GcPressureCharacterizationBounds.MaximumWindows + 1)
                .Select(index => scenario.Capture.Windows[index % scenario.Capture.Windows.Count] with
                {
                    Index = index,
                    Phase = index == 0
                        ? GcPressureWindowPhase.Baseline
                        : GcPressureWindowPhase.Active,
                })
                .ToArray(),
        };

        Action action = () => GcPressureCharacterizationHarness.Evaluate(unbounded);
        action.Should().Throw<InvalidDataException>().WithMessage("*bounded window protocol*");
    }

    [Fact]
    public void MissingCooldownWindows_FailsBeforeAnyRecoveryDecision()
    {
        var scenario = GcPressureCharacterizationHarness.ReadFixture(FixturePath())
            .Scenarios.Single(item => item.Capture.Scenario == GcPressureScenarioKind.TransientAllocationBurst);
        var noCooldown = scenario.Capture with
        {
            Id = "no-cooldown",
            Windows = scenario.Capture.Windows.Select(window => window with
            {
                Phase = window.Index == 0
                    ? GcPressureWindowPhase.Baseline
                    : GcPressureWindowPhase.Active,
            }).ToArray(),
        };

        var result = GcPressureCharacterizationHarness.Evaluate(noCooldown);

        result.NextAction.Should().Be(GcPressureNextAction.JustifiedInconclusion);
        result.LohSize.Median.Availability.Should().Be(GcPressureAvailability.InsufficientObservations);
        result.LohSize.Median.UnavailableReason.Should().Be("minimum_cooldown_windows_not_met");
    }

    [Fact]
    public void SustainedGcPressure_WinsOverAFalseRetainedShapeFromLateLohMaxima()
    {
        var scenario = GcPressureCharacterizationHarness.ReadFixture(FixturePath())
            .Scenarios.Single(item => item.Capture.Scenario == GcPressureScenarioKind.SustainedAllocationPressure);
        double[] lohMiB = [0, 4, 4, 72, 25, 25];
        var lateLohMaximum = scenario.Capture with
        {
            Id = "sustained-with-late-loh-maximum",
            Windows = scenario.Capture.Windows.Select((window, index) => window with
            {
                LohSizeBytes = lohMiB[index] * 1024 * 1024,
            }).ToArray(),
        };

        var result = GcPressureCharacterizationHarness.Evaluate(lateLohMaximum);

        result.LohSize.Slope.Slope.Should().BeGreaterThan(1024 * 1024);
        result.PostLoadStabilized.Should().BeFalse();
        result.NextAction.Should().Be(GcPressureNextAction.AllocationInvestigation);
    }

    private static string FixturePath()
        => ScenarioManifestLoader.ScenarioPath(
            "Fixtures",
            "GcPressureCharacterization",
            "gc-pressure-characterization.v1.json");
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class GcPressureCharacterizationLiveTests
{
    public static TheoryData<GcPressureScenarioKind, GcPressureNextAction> LiveScenarios()
        => new()
        {
            { GcPressureScenarioKind.StableHealthy, GcPressureNextAction.ContinuedObservation },
            { GcPressureScenarioKind.TransientAllocationBurst, GcPressureNextAction.ContinuedObservation },
            { GcPressureScenarioKind.SustainedAllocationPressure, GcPressureNextAction.AllocationInvestigation },
            { GcPressureScenarioKind.RetainedGrowth, GcPressureNextAction.RetainedHeapInvestigation },
            { GcPressureScenarioKind.CaptureLoss, GcPressureNextAction.JustifiedInconclusion },
        };

    [Theory(Timeout = 900_000)]
    [MemberData(nameof(LiveScenarios))]
    [Trait("Category", "ScenarioEvaluationLive")]
    public async Task LiveCapture_ProducesExpectedBoundedDecision(
        GcPressureScenarioKind scenario,
        GcPressureNextAction expectedAction)
    {
        var capture = await GcPressureLiveHarness.CaptureAsync(scenario, CancellationToken.None);

        var result = GcPressureCharacterizationHarness.Evaluate(capture);
        var windows = string.Join(
            "; ",
            capture.Windows.Select(window =>
                FormattableString.Invariant(
                    $"w{window.Index}/{window.Phase}:loh={window.LohSizeBytes:0},gen2={window.Gen2CollectionsPerSecond:0.###},pauseP95={window.PauseP95Milliseconds:0.###},timeGc={window.TimeInGcPercent:0.###},lost={window.LostOrDropped}")));

        result.NextAction.Should().Be(expectedAction, $"{result.Reason} {windows}");
        PersistWhenRequested(capture, result);
        capture.Windows.Should().HaveCount(GcPressureLiveBounds.WindowCount);
        result.LohSize.Median.Counts.Windows.Should()
            .Be(GcPressureLiveBounds.WindowCount);
        if (scenario == GcPressureScenarioKind.CaptureLoss)
        {
            capture.Windows.Should().Contain(window => window.LostOrDropped > 0);
            result.LohSize.Median.Availability
                .Should().Be(GcPressureAvailability.LossDetected);
        }
    }

    private static void PersistWhenRequested(
        GcPressureCapture capture,
        GcPressureCharacterizationResult result)
    {
        var outputDirectory = Environment.GetEnvironmentVariable(
            "DOTNET_DIAGNOSTICS_GC_PRESSURE_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        var prefix = capture.Scenario.ToString().ToLowerInvariant();
        File.WriteAllText(
            Path.Combine(outputDirectory, $"{prefix}.capture.json"),
            GcPressureCharacterizationHarness.SerializeCapture(capture));
        File.WriteAllText(
            Path.Combine(outputDirectory, $"{prefix}.result.json"),
            GcPressureCharacterizationHarness.Serialize(result));
    }
}
