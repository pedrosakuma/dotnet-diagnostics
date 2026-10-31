using System.Globalization;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class CpuHotspotMovementTests
{
    [Fact]
    public void DeterministicReplay_DistinguishesShiftFromAaWithoutPerformanceVerdict()
    {
        var report = CpuHotspotMovementEvaluator.Evaluate(
            CpuHotspotMovementEvaluator.ReadFixture(CpuHotspotMovementFixtures.FixturePath));

        report.Verdict.Should().Be(
            CpuHotspotMovementVerdict.Go,
            string.Join(
                Environment.NewLine,
                report.Cases.Select(item =>
                    $"{item.Id}: {item.Availability}; {item.UnavailableReason}; {item.NextAction}; "
                    + $"delta={item.PairedShareDelta?.Delta?.Absolute.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}; "
                    + $"baseline={string.Join(",", item.BaselineMedianShare?.UnavailableFields.Select(field => field.Reason) ?? [])}; "
                    + $"paired={string.Join(",", item.PairedShareDelta?.UnavailableFields.Select(field => field.Reason) ?? [])}; "
                    + $"identity={string.Join(",", item.PairedShareDelta?.Compatibility?.Mismatches.Select(mismatch => $"{mismatch.Dimension}:{mismatch.Reason}") ?? [])}")));
        var aa = report.Cases.Single(item => item.Id == "aa-control");
        aa.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        aa.NextAction.Should().Be(CpuHotspotNextAction.NoMovementEstablished);
        aa.PairedShareDelta!.IndependenceUnit
            .Should().Be(ScenarioStatisticalIndependenceUnit.Capture);
        aa.PairedShareDeltas.Should().HaveCount(4)
            .And.OnlyContain(delta => Math.Abs(delta) < 0.03);
        aa.BaselineShareDistribution.InterquartileRange.Should().BeLessThan(0.03);
        aa.RankStability.MedianTopKOverlap.Should().Be(1);
        aa.RankStability.SameSelectedRankFraction.Should().Be(1);

        var shift = report.Cases.Single(item => item.Id == "deterministic-hotspot-shift");
        shift.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        shift.NextAction.Should().Be(CpuHotspotNextAction.MovementObservedCollectDrilldown);
        shift.PairedShareDelta!.Delta!.Absolute.Should().BeLessThan(-0.35);
        shift.RankStability.MedianTopKOverlap.Should().Be(1,
            "top-K membership can stay stable while rank and share move");
        shift.RankStability.SameSelectedRankFraction.Should().Be(0);
        shift.RankStability.MedianAbsoluteSelectedRankDelta.Should().Be(1);

        var json = System.Text.Json.JsonSerializer.Serialize(
            report,
            CpuHotspotMovementJsonContext.Default.CpuHotspotMovementReport)
            .ToLowerInvariant();
        json.Should().Contain("individual samples are correlated observations");
        json.Should().Contain("do not measure elapsed cpu time");
        json.Should().NotContain("\"verdict\":\"regression\"");
        json.Should().NotContain("\"verdict\":\"improvement\"");
        json.Should().NotContain("statistically significant");
    }

    [Fact]
    public void CompatibilityAndQualityControls_AbstainBeforePooling()
    {
        var report = CpuHotspotMovementEvaluator.Evaluate(
            CpuHotspotMovementFixtures.CreateDeterministic());

        var load = report.Cases.Single(item => item.Id == "changed-offered-load");
        load.Availability.Should().Be(ScenarioStatisticalAvailability.Incompatible);
        load.UnavailableReason.Should().Be("capture_compatibility_mismatch");
        load.CompatibilityMismatches.Should().OnlyContain(item =>
            item.Dimension == "workload.offered-load");
        load.PairedShareDelta.Should().BeNull();

        var symbols = report.Cases.Single(item => item.Id == "unresolved-symbol-degradation");
        symbols.Availability.Should().Be(
            ScenarioStatisticalAvailability.UnsupportedApproximation);
        symbols.UnavailableReason.Should().Be("unresolved_symbol_share_exceeds_protocol");
        symbols.CandidateUnresolvedDistribution.Median.Should().Be(0.5);
        symbols.PairedShareDelta.Should().BeNull();

        var backend = report.Cases.Single(item => item.Id == "backend-incompatibility");
        backend.Availability.Should().Be(ScenarioStatisticalAvailability.Incompatible);
        backend.CompatibilityMismatches.Select(item => item.Dimension).Should().Contain(
            "backend.id",
            "backend.evidence-kind");
        backend.PairedShareDelta.Should().BeNull();

        var low = report.Cases.Single(item => item.Id == "low-sample-count");
        low.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        low.UnavailableReason.Should().Be("minimum_capture_sample_count_not_met");
        low.BaselineCaptures.Should().OnlyContain(capture =>
            capture.TotalSamples == 30 && capture.UnresolvedExclusiveSamples == 2);
        report.Cases.Where(item => item.Id is not ("aa-control" or "deterministic-hotspot-shift"))
            .Should().OnlyContain(item =>
                item.NextAction == CpuHotspotNextAction.Inconclusive);
    }

    [Fact]
    public void Go_RequiresTheCompletePredeclaredMatrix()
    {
        var fixture = CpuHotspotMovementFixtures.CreateDeterministic();
        fixture = fixture with
        {
            Cases = fixture.Cases.Where(item => item.Id != "backend-incompatibility").ToArray(),
        };

        CpuHotspotMovementEvaluator.Evaluate(fixture).Verdict
            .Should().Be(CpuHotspotMovementVerdict.NoGo);
    }

    [Fact]
    public void FixedBoundsAndCaptureAccounting_AreEnforced()
    {
        var fixture = CpuHotspotMovementFixtures.CreateDeterministic();
        var evidenceCase = fixture.Cases[0];
        var capture = evidenceCase.Baseline.Captures[0] with
        {
            Methods =
            [
                new("alpha", 900),
                new("beta", 900),
            ],
        };
        fixture = fixture with
        {
            Cases =
            [
                evidenceCase with
                {
                    Baseline = evidenceCase.Baseline with
                    {
                        Captures = evidenceCase.Baseline.Captures
                            .Select((item, index) => index == 0 ? capture : item)
                            .ToArray(),
                    },
                },
            ],
        };

        Action evaluate = () => CpuHotspotMovementEvaluator.Evaluate(fixture);
        evaluate.Should().Throw<InvalidDataException>()
            .WithMessage("*bounded method inventory*");
    }

    [Fact]
    public void RegenerateFixture_WhenExplicitlyRequested()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(
                CpuHotspotMovementFixtures.RegenerateFixtureEnvironmentVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        CpuHotspotMovementEvaluator.WriteFixture(
            CpuHotspotMovementFixtures.FixturePath,
            CpuHotspotMovementFixtures.CreateDeterministic());
    }
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class CpuHotspotMovementLiveTests
{
    [WindowsOrLinuxFact(
        "CPU hotspot movement evidence requires EventPipe and the published BadCodeSample fixture.",
        Timeout = 240_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public async Task LiveCaptures_SeparateDeterministicShiftFromAaVariability()
    {
        var fixture = await CpuHotspotMovementFixtures.CaptureLiveAsync(
            CancellationToken.None);

        var report = CpuHotspotMovementEvaluator.Evaluate(fixture);
        var reportPath = Environment.GetEnvironmentVariable(
            CpuHotspotMovementFixtures.LiveReportPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            CpuHotspotMovementEvaluator.WriteReport(reportPath, report);
        }

        report.Verdict.Should().Be(
            CpuHotspotMovementVerdict.Go,
            string.Join(
                Environment.NewLine,
                report.Cases.Select(item =>
                    $"{item.Id}: {item.Availability}; {item.NextAction}; "
                    + $"delta={(item.PairedShareDelta?.Delta is { } delta ? delta.Absolute.ToString("0.###", CultureInfo.InvariantCulture) : "unavailable")}; "
                    + $"A/A range=[{item.PairedShareDeltas.DefaultIfEmpty().Min():0.###},"
                    + $"{item.PairedShareDeltas.DefaultIfEmpty().Max():0.###}]")));
        var aa = report.Cases.Single(item => item.Id == "aa-control");
        var shift = report.Cases.Single(item => item.Id == "deterministic-hotspot-shift");
        aa.BaselineCaptures.Concat(aa.CandidateCaptures)
            .Concat(shift.BaselineCaptures)
            .Concat(shift.CandidateCaptures)
            .Should().OnlyContain(capture =>
                capture.TotalSamples >= CpuHotspotMovementBounds.MinimumSamplesPerCapture
                && capture.Methods.Count <= CpuHotspotMovementBounds.MaximumMethodsPerCapture);
        Math.Abs(shift.PairedShareDelta!.Delta!.Absolute)
            .Should().BeGreaterThan(
                aa.PairedShareDeltas.Select(Math.Abs).Max()
                + CpuHotspotMovementBounds.MinimumSeparationBeyondAaRange);
        report.Cases.Single(item => item.Id == "changed-offered-load").Availability
            .Should().Be(ScenarioStatisticalAvailability.Incompatible);
        report.Cases.Single(item => item.Id == "unresolved-symbol-degradation").Availability
            .Should().Be(ScenarioStatisticalAvailability.UnsupportedApproximation);
        report.Cases.Single(item => item.Id == "backend-incompatibility").Availability
            .Should().Be(ScenarioStatisticalAvailability.Incompatible);
        report.Cases.Single(item => item.Id == "low-sample-count").Availability
            .Should().Be(ScenarioStatisticalAvailability.InsufficientObservations);

    }
}
