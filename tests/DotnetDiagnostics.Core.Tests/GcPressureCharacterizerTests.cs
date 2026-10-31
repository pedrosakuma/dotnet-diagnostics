using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class GcPressureCharacterizerTests
{
    [Fact]
    public void AllocationPersistenceTakesPrecedenceOverLateLohGrowth()
    {
        var capture = Capture(
            [2, 32, 36, 72, 25, 25],
            [0, 3, 4, 3.5, 0, 0],
            [0.1, 8, 9, 8.5, 0.5, 0.2],
            [0.2, 20, 24, 22, 1, 0.5]);

        var result = GcPressureCharacterizer.Evaluate(capture);

        result.LohSize.Slope.Slope.Should().BeGreaterThan(1024 * 1024);
        result.Persistence!.LongestAllocationPressureRun.Should().Be(3);
        result.NextAction.Should().Be(GcPressureNextAction.AllocationInvestigation);
    }

    [Fact]
    public void RetainedGrowthSelectsHeapInvestigationWithoutNamingACause()
    {
        var capture = Capture(
            [2, 10, 18, 26, 26, 26],
            [0, 0.2, 0.2, 0.2, 0, 0],
            [0.1, 1, 1.2, 1.4, 0.5, 0.2],
            [0.2, 1, 1.2, 1.4, 1, 0.5],
            GcPressureScenarioKind.RetainedGrowth);

        var result = GcPressureCharacterizer.Evaluate(capture);

        result.NextAction.Should().Be(GcPressureNextAction.RetainedHeapInvestigation);
        result.PostLoadStabilized.Should().BeFalse();
        result.Reason.Contains("leak", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        result.Limitations.Should().OnlyContain(item =>
            !item.Contains("leak", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LossPrecedesInsufficientWindowCount()
    {
        var capture = Capture(
            [2, 32, 36, 4],
            [0, 3, 4, 0],
            [0.1, 8, 9, 0.5],
            [0.2, 20, 24, 1]) with
        {
            Windows =
            [
                .. Capture(
                    [2, 32, 36, 4],
                    [0, 3, 4, 0],
                    [0.1, 8, 9, 0.5],
                    [0.2, 20, 24, 1]).Windows.Select((window, index) =>
                    index == 0
                        ? window
                        : window with
                        {
                            Phase = GcPressureWindowPhase.Active,
                            LostOrDropped = index == 2 ? 7 : 0,
                        }),
            ],
        };

        var result = GcPressureCharacterizer.Evaluate(capture);

        result.NextAction.Should().Be(GcPressureNextAction.JustifiedInconclusion);
        result.LohSize.Median.Availability.Should().Be(GcPressureAvailability.LossDetected);
        result.LohSize.Median.UnavailableReason.Should().Be("source_loss_detected");
    }

    [Fact]
    public void SlopeRequiresFiveValidWindows()
    {
        var capture = Capture(
            [2, 10, 18, 26],
            [0, 0.2, 0.2, 0],
            [0.1, 1, 1.2, 0.2],
            [0.2, 1, 1.2, 0.5]);

        var result = GcPressureCharacterizer.Evaluate(capture);

        result.NextAction.Should().Be(GcPressureNextAction.JustifiedInconclusion);
        result.LohSize.Slope.Availability.Should().Be(GcPressureAvailability.InsufficientObservations);
        result.LohSize.Slope.Slope.Should().BeNull();
    }

    [Fact]
    public void LegacyArtifactsRemainIncompatibleAndAreNotBackfilled()
    {
        var result = GcPressureCharacterizer.IncompatibleLegacy("old-capture", sourceSchemaVersion: null);

        result.Identity.CaptureId.Should().Be("old-capture");
        result.NextAction.Should().Be(GcPressureNextAction.JustifiedInconclusion);
        result.LohSize.Median.Availability.Should().Be(GcPressureAvailability.Incompatible);
        result.LohSize.Median.UnavailableReason.Should().Be("legacy_contract_missing");
        result.Persistence.Should().BeNull();
    }

    [Fact]
    public void MoreThanEightWindowsIsRejected()
    {
        var capture = Capture(
            [2, 2, 2, 2, 2, 2, 2, 2, 2],
            [0, 0, 0, 0, 0, 0, 0, 0, 0],
            [0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1],
            [0.2, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2]);

        var action = () => GcPressureCharacterizer.Evaluate(capture);

        action.Should().Throw<InvalidDataException>().WithMessage("*bounded window protocol*");
    }

    [Fact]
    public void NullWindowIsRejectedAsInvalidData()
    {
        var capture = Capture(
            [2, 2, 2, 2, 2],
            [0, 0, 0, 0, 0],
            [0.1, 0.1, 0.1, 0.1, 0.1],
            [0.2, 0.2, 0.2, 0.2, 0.2]) with
        {
            Windows = [null!],
        };

        var action = () => GcPressureCharacterizer.Evaluate(capture);

        action.Should().Throw<InvalidDataException>().WithMessage("*invalid window*");
    }

    private static GcPressureCapture Capture(
        IReadOnlyList<double> lohMiB,
        IReadOnlyList<double> gen2,
        IReadOnlyList<double> pause,
        IReadOnlyList<double> suspension,
        GcPressureScenarioKind scenario = GcPressureScenarioKind.SustainedAllocationPressure)
    {
        lohMiB.Count.Should().Be(gen2.Count).And.Be(pause.Count).And.Be(suspension.Count);
        var windows = Enumerable.Range(0, lohMiB.Count)
            .Select(index => new GcPressureWindow(
                index,
                index == 0
                    ? GcPressureWindowPhase.Baseline
                    : index >= lohMiB.Count - 2
                        ? GcPressureWindowPhase.Cooldown
                        : GcPressureWindowPhase.Active,
                lohMiB[index] * 1024 * 1024,
                gen2[index],
                pause[index],
                suspension[index],
                SourceEvents: 10,
                LostOrDropped: 0,
                GcPressureRepresentation.Exact))
            .ToArray();
        return new(
            GcPressureCharacterizer.CurrentCaptureSchemaVersion,
            "core-test-capture",
            scenario,
            "10.0",
            "test-os",
            "x64",
            3,
            windows,
            []);
    }
}
