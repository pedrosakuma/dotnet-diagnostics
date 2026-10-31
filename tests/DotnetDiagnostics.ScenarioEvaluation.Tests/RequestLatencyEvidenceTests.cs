using System.Globalization;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class RequestLatencyEvidenceTests
{
    [Fact]
    public void ControlledReplay_DistinguishesInjectedShiftWithoutPerformanceVerdict()
    {
        var fixture = RequestLatencyEvidenceEvaluator.ReadFixture(
            RequestLatencyEvidenceFixtures.FixturePath);

        var report = RequestLatencyEvidenceEvaluator.Evaluate(fixture);

        report.Verdict.Should().Be(RequestLatencyGoNoGo.Go);
        report.Cases.Single(item => item.Id == "aa-control").NextAction
            .Should().Be(RequestLatencyNextAction.Inconclusive);
        var shift = report.Cases.Single(item => item.Id == "injected-shift");
        shift.NextAction.Should().Be(RequestLatencyNextAction.DeeperInvestigationWarranted);
        shift.Quantiles.Should().OnlyContain(item =>
            item.Availability == ScenarioStatisticalAvailability.Available
            && item.AbsoluteDeltaMilliseconds > 30
            && item.PairedWindowBootstrapInterval!.Lower > 30
            && item.EffectSize.Value == 1);
        var json = System.Text.Json.JsonSerializer.Serialize(
            report,
            RequestLatencyEvidenceJsonContext.Default.RequestLatencyEvidenceReport)
            .ToLowerInvariant();
        json.Should().NotContain("\"verdict\":\"regression\"");
        json.Should().NotContain("\"verdict\":\"improvement\"");
        json.Should().NotContain("statistically significant");
        json.Should().Contain("does not establish equivalent offered load");
    }

    [Fact]
    public void EdgeCases_RemainExplicitAndInconclusive()
    {
        var report = RequestLatencyEvidenceEvaluator.Evaluate(
            RequestLatencyEvidenceEvaluator.ReadFixture(
                RequestLatencyEvidenceFixtures.FixturePath));

        report.Cases.Single(item => item.Id == "changed-request-mix").Availability
            .Should().Be(ScenarioStatisticalAvailability.Incompatible);
        report.Cases.Single(item => item.Id == "low-sample-count").Availability
            .Should().Be(ScenarioStatisticalAvailability.InsufficientObservations);
        report.Cases.Single(item => item.Id == "reservoir-approximation").Availability
            .Should().Be(ScenarioStatisticalAvailability.UnsupportedApproximation);
        var incompatible = report.Cases.Single(item => item.Id == "incompatible-identity");
        incompatible.Availability.Should().Be(ScenarioStatisticalAvailability.Incompatible);
        incompatible.IdentityMismatches.Select(item => item.Dimension).Should().Contain(
            "target.runtime-flavor",
            "capture.windowing",
            "quantity.filters");
        var incomplete = report.Cases.Single(item => item.Id == "incomplete-work");
        incomplete.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        incomplete.CandidateCounts.Errors.Should().BeGreaterThan(0);
        incomplete.CandidateCounts.Censored.Should().BeGreaterThan(0);
        incomplete.CandidateCounts.Missing.Should().BeGreaterThan(0);
        report.Cases.Where(item => item.Id is not ("aa-control" or "injected-shift"))
            .Should().OnlyContain(item =>
                item.NextAction == RequestLatencyNextAction.Inconclusive);
    }

    [Fact]
    public void Reservoir_IsBoundedDeterministicAndOrderInvariant()
    {
        var values = Enumerable.Range(0, 100).Select(index => (double)index).ToArray();
        var first = new DeterministicLatencyReservoir(8, "seed");
        var second = new DeterministicLatencyReservoir(8, "seed");
        foreach (var (value, index) in values.Select((value, index) => (value, index)))
        {
            first.Add($"sample-{index}", value);
        }
        foreach (var (value, index) in values.Select((value, index) => (value, index)).Reverse())
        {
            second.Add($"sample-{index}", value);
        }

        first.Snapshot().Should().Equal(second.Snapshot());
        first.Snapshot().Should().HaveCount(8);
    }

    [Fact]
    public void FixedBounds_RejectOversizedWindows()
    {
        var fixture = RequestLatencyEvidenceFixtures.CreateDeterministic();
        var oversized = fixture.Cases[0].Baseline.Windows[0] with
        {
            SourceCompletedCount =
                RequestLatencyEvidenceBounds.MaximumRetainedSamplesPerWindow + 1,
            RetainedLatenciesMilliseconds = Enumerable.Range(
                    0,
                    RequestLatencyEvidenceBounds.MaximumRetainedSamplesPerWindow + 1)
                .Select(index => (double)index)
                .ToArray(),
        };
        fixture = fixture with
        {
            Cases =
            [
                fixture.Cases[0] with
                {
                    Baseline = fixture.Cases[0].Baseline with
                    {
                        Windows = [oversized],
                    },
                },
            ],
        };

        var action = () => RequestLatencyEvidenceEvaluator.Evaluate(fixture);

        action.Should().Throw<InvalidDataException>().WithMessage("*fixed bound*");
    }

    [Fact]
    public void Go_RequiresEveryEdgeCaseWithItsExpectedAvailability()
    {
        var fixture = RequestLatencyEvidenceFixtures.CreateDeterministic();
        fixture = fixture with
        {
            Cases = fixture.Cases.Where(item => item.Id != "incomplete-work").ToArray(),
        };

        var report = RequestLatencyEvidenceEvaluator.Evaluate(fixture);

        report.Verdict.Should().Be(RequestLatencyGoNoGo.NoGo);
    }

    [Fact]
    public void ExactAndPairIdentityInvariants_FailExplicitly()
    {
        var exactMismatch = RequestLatencyEvidenceFixtures.CreateDeterministic();
        var window = exactMismatch.Cases[0].Baseline.Windows[0] with
        {
            SourceCompletedCount =
                exactMismatch.Cases[0].Baseline.Windows[0].SourceCompletedCount + 1,
        };
        exactMismatch = exactMismatch with
        {
            Cases =
            [
                exactMismatch.Cases[0] with
                {
                    Baseline = exactMismatch.Cases[0].Baseline with
                    {
                        Windows = exactMismatch.Cases[0].Baseline.Windows
                            .Select((item, index) => index == 0 ? window : item)
                            .ToArray(),
                    },
                },
            ],
        };
        Action exactAction = () => RequestLatencyEvidenceEvaluator.Evaluate(exactMismatch);
        exactAction.Should().Throw<InvalidDataException>()
            .WithMessage("*retain every completed request*");

        var duplicate = RequestLatencyEvidenceFixtures.CreateDeterministic();
        var duplicateWindow = duplicate.Cases[0].Baseline.Windows[1] with
        {
            PairKey = duplicate.Cases[0].Baseline.Windows[0].PairKey,
        };
        duplicate = duplicate with
        {
            Cases =
            [
                duplicate.Cases[0] with
                {
                    Baseline = duplicate.Cases[0].Baseline with
                    {
                        Windows = duplicate.Cases[0].Baseline.Windows
                            .Select((item, index) => index == 1 ? duplicateWindow : item)
                            .ToArray(),
                    },
                },
            ],
        };
        Action duplicateAction = () => RequestLatencyEvidenceEvaluator.Evaluate(duplicate);
        duplicateAction.Should().Throw<InvalidDataException>()
            .WithMessage("*pair identities must be unique*");
    }

    [Fact]
    public void UnpairedWindows_AreNotMixedIntoQuantilesOrInference()
    {
        var fixture = RequestLatencyEvidenceFixtures.CreateDeterministic();
        var shift = fixture.Cases.Single(item => item.Id == "injected-shift");
        shift = shift with
        {
            Candidate = shift.Candidate with
            {
                Windows = shift.Candidate.Windows.Skip(1).ToArray(),
            },
        };
        fixture = fixture with { Cases = [shift] };

        var result = RequestLatencyEvidenceEvaluator.Evaluate(fixture).Cases.Single();

        result.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        result.Quantiles.Should().OnlyContain(item =>
            item.Availability == ScenarioStatisticalAvailability.InsufficientObservations
            && item.Baseline.SourceCompletedCount == item.Candidate.SourceCompletedCount);
    }

    [Fact]
    public void RegenerateFixture_WhenExplicitlyRequested()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(
                RequestLatencyEvidenceFixtures.RegenerateFixtureEnvironmentVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        RequestLatencyEvidenceEvaluator.WriteFixture(
            RequestLatencyEvidenceFixtures.FixturePath,
            RequestLatencyEvidenceFixtures.CreateDeterministic());
    }
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class RequestLatencyLiveEvidenceTests
{
    [WindowsOrLinuxFact(
        "Request-latency evidence requires the published BadCodeSample loopback endpoint.",
        Timeout = 180_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public async Task LiveScenarios_ProduceBoundedGoNoGoEvidence()
    {
        var fixture = await RequestLatencyEvidenceFixtures.CaptureLiveAsync(
            CancellationToken.None);

        var report = RequestLatencyEvidenceEvaluator.Evaluate(fixture);

        report.Verdict.Should().Be(
            RequestLatencyGoNoGo.Go,
            string.Join(
                Environment.NewLine,
                report.Cases.Select(item =>
                    $"{item.Id}: {item.Availability}; {item.NextAction}; "
                    + string.Join(", ", item.Quantiles.Select(quantile =>
                        $"p{quantile.Probability * 100:0}="
                        + $"{quantile.PairedWindowMedianDeltaMilliseconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable"}")))));
        fixture.Cases.Should().HaveCount(7);
        report.Cases.Single(item => item.Id == "changed-request-mix").Availability
            .Should().Be(ScenarioStatisticalAvailability.Incompatible);
        report.Cases.Single(item => item.Id == "low-sample-count").Availability
            .Should().Be(ScenarioStatisticalAvailability.InsufficientObservations);
        report.Cases.Single(item => item.Id == "reservoir-approximation").Availability
            .Should().Be(ScenarioStatisticalAvailability.UnsupportedApproximation);
        report.Cases.Single(item => item.Id == "incompatible-identity").Availability
            .Should().Be(ScenarioStatisticalAvailability.Incompatible);
        var incomplete = report.Cases.Single(item => item.Id == "incomplete-work");
        incomplete.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        incomplete.CandidateCounts.Errors.Should().BeGreaterThan(0);
        incomplete.CandidateCounts.Censored.Should().BeGreaterThan(0);
        incomplete.CandidateCounts.Missing.Should().BeGreaterThan(0);
        fixture.Cases.SelectMany(item => item.Baseline.Windows.Concat(item.Candidate.Windows))
            .Should().OnlyContain(window =>
                window.RetainedLatenciesMilliseconds.Count
                    <= RequestLatencyEvidenceBounds.MaximumRetainedSamplesPerWindow);
        var path = Environment.GetEnvironmentVariable(
            RequestLatencyEvidenceFixtures.LiveReportPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            RequestLatencyEvidenceEvaluator.WriteReport(path, report);
        }
    }
}
