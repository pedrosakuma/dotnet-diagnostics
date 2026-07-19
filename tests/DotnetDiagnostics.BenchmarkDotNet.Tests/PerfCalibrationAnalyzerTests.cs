using DotnetDiagnostics.BenchmarkDotNet.Regression;
using FluentAssertions;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public sealed class PerfCalibrationAnalyzerTests
{
    [Fact]
    public void IndependentHostedAllocations_ReportCrossAllocationButNotCrossDayEvidence()
    {
        var cohorts = Enumerable.Range(1, 3)
            .Select(index => Cohort(
                $"hosted-{index}",
                $"allocation-{index}",
                PerfCalibrationRunnerKind.GitHubHosted,
                "ubuntu-latest",
                new DateTimeOffset(2026, 7, 18, 10, index, 0, TimeSpan.Zero),
                candidateTime: 120 + index))
            .ToArray();

        var (evidence, report) = PerfCalibrationAnalyzer.Analyze(cohorts);

        evidence.Cohorts.Should().HaveCount(3);
        report.Groups.Should().ContainSingle();
        var group = report.Groups.Single();
        group.IndependentAllocationCount.Should().Be(3);
        group.DistinctDayCount.Should().Be(1);
        group.DetectionRates.Should().OnlyContain(static rate => rate.RatePercent == 100);
        group.FalsePositiveRates.Should().OnlyContain(static rate => rate.RatePercent == 0);
        group.DetectionRates.Should().OnlyContain(static rate =>
            rate.Lower95Percent < rate.RatePercent && rate.Upper95Percent == 100);
        group.Variance.Should().Contain(row =>
            row.Metric == PerfMetricKind.Time
            && row.CrossAllocationCoefficientOfVariationPercent > 0
            && row.CrossDayCoefficientOfVariationPercent == null);
        group.MeetsCalibrationTargets.Should().BeFalse();
        group.TargetFailures.Should().Contain(failure =>
            failure.Contains("UTC days", StringComparison.Ordinal));
        report.Decision.Should().Be(PerfExperimentDecision.PartialGo);
        report.EvidenceSupportsTimingGateConsideration.Should().BeFalse();
        report.EligibleForGate.Should().BeFalse();
        report.Recommendation.Should().Be(PerfGateRecommendation.Advisory);
        report.Notes.Should().Contain(note =>
            note.Contains("dedicated/self-hosted", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferentRunnerImages_AreReportedAsSeparateCompatibilityGroups()
    {
        var cohorts = new[]
        {
            Cohort(
                "hosted-1",
                "allocation-1",
                PerfCalibrationRunnerKind.GitHubHosted,
                "ubuntu-latest",
                new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero),
                runnerImage: "ubuntu24-image-a"),
            Cohort(
                "hosted-2",
                "allocation-2",
                PerfCalibrationRunnerKind.GitHubHosted,
                "ubuntu-latest",
                new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero),
                runnerImage: "ubuntu24-image-b"),
        };

        var (_, report) = PerfCalibrationAnalyzer.Analyze(cohorts);

        report.Groups.Should().HaveCount(2);
        report.Groups.Should().OnlyContain(static group =>
            group.CohortIds.Count == 1
            && group.IndependentAllocationCount == 1);
        report.Notes.Should().Contain(note =>
            note.Contains("never pooled", StringComparison.Ordinal));
    }

    [Fact]
    public void HostedAndDedicatedMultiDayTargets_CanSupportConsiderationButNeverEnableGate()
    {
        var cohorts = new List<PerfCalibrationCohortEvidence>();
        for (var day = 0; day < 3; day++)
        {
            cohorts.Add(Cohort(
                $"hosted-{day}",
                $"hosted-allocation-{day}",
                PerfCalibrationRunnerKind.GitHubHosted,
                "ubuntu-latest",
                new DateTimeOffset(2026, 7, 18 + day, 10, 0, 0, TimeSpan.Zero)));
            cohorts.Add(Cohort(
                $"dedicated-{day}",
                $"dedicated-run-{day}",
                PerfCalibrationRunnerKind.Dedicated,
                "self-hosted,linux,x64,dotnet-diagnostics-perf",
                new DateTimeOffset(2026, 7, 18 + day, 12, 0, 0, TimeSpan.Zero),
                runnerClass: "dedicated-linux-x64",
                runnerImage: "dedicated-image-v1"));
        }

        var (_, report) = PerfCalibrationAnalyzer.Analyze(cohorts);

        report.HostedEvidenceMeetsTargets.Should().BeTrue();
        report.DedicatedEvidenceMeetsTargets.Should().BeTrue();
        report.EvidenceSupportsTimingGateConsideration.Should().BeTrue();
        report.EligibleForGate.Should().BeFalse();
        report.Recommendation.Should().Be(PerfGateRecommendation.Advisory);
    }

    [Fact]
    public void DuplicateAllocation_IsRejectedAsNonIndependentEvidence()
    {
        var cohorts = new[]
        {
            Cohort(
                "hosted-1",
                "same-allocation",
                PerfCalibrationRunnerKind.GitHubHosted,
                "ubuntu-latest",
                new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero)),
            Cohort(
                "hosted-2",
                "same-allocation",
                PerfCalibrationRunnerKind.GitHubHosted,
                "ubuntu-latest",
                new DateTimeOffset(2026, 7, 18, 11, 0, 0, TimeSpan.Zero)),
        };

        var act = () => PerfCalibrationAnalyzer.Analyze(cohorts);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*runner allocation ID*not independent evidence*");
    }

    [Fact]
    public void CalibrationArtifacts_RoundTripAndMarkdownStatesEvidenceBoundary()
    {
        var cohort = Cohort(
            "hosted-1",
            "allocation-1",
            PerfCalibrationRunnerKind.GitHubHosted,
            "ubuntu-latest",
            new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero));
        var (evidence, report) = PerfCalibrationAnalyzer.Analyze([cohort]);

        var cohortJson = PerfRegressionReportSerializer.SerializeCalibrationCohort(cohort);
        var evidenceJson = PerfRegressionReportSerializer.SerializeCalibrationEvidence(evidence);
        var reportJson = PerfRegressionReportSerializer.SerializeCalibrationReport(report);
        var markdown = PerfRegressionReportSerializer.BuildCalibrationMarkdown(report);

        PerfRegressionReportSerializer.DeserializeCalibrationCohort(cohortJson)
            .Should().BeEquivalentTo(cohort);
        PerfRegressionReportSerializer.DeserializeCalibrationEvidence(evidenceJson)
            .Should().BeEquivalentTo(evidence);
        PerfRegressionReportSerializer.DeserializeCalibrationReport(reportJson)
            .Should().BeEquivalentTo(report);
        reportJson.Should().Contain(PerfCalibrationPolicy.PolicyV1);
        markdown.Should().Contain("cross-allocation CV");
        markdown.Should().Contain("diagnostic elapsed time is not present");
        markdown.Should().Contain("Eligible for gate: **no**");
    }

    private static PerfCalibrationCohortEvidence Cohort(
        string cohortId,
        string allocationId,
        PerfCalibrationRunnerKind runnerKind,
        string runnerLabel,
        DateTimeOffset capturedAt,
        double candidateTime = 120,
        string runnerClass = "github-hosted-ubuntu-latest",
        string runnerImage = "ubuntu24-image-a")
    {
        var pairs = Enumerable.Range(0, 3)
            .Select(index => new PerfPairedMeasurement(
                index + 1,
                index % 2 == 0
                    ? PerfPairOrder.MainThenPullRequest
                    : PerfPairOrder.PullRequestThenMain,
                Run(
                    $"{cohortId}-main-{index + 1}",
                    capturedAt.AddSeconds(index * 2),
                    "main-sha",
                    120 + index,
                    runnerClass,
                    runnerImage),
                Run(
                    $"{cohortId}-pr-{index + 1}",
                    capturedAt.AddSeconds(index * 2 + 1),
                    "pr-sha",
                    candidateTime + index,
                    runnerClass,
                    runnerImage)))
            .ToArray();
        var feasibility = new PerfExperimentFeasibility(
            "independent_runner_allocation",
            TotalRunnerMinutes: 14,
            CompactArtifactBytes: 40_000,
            RawArtifactBytes: 400_000,
            []);
        var (manifest, _) = PerfPairedComparisonAnalyzer.Analyze(pairs, feasibility);
        return new PerfCalibrationCohortEvidence(
            PerfCalibrationCohortEvidence.SchemaV1,
            cohortId,
            allocationId,
            WorkflowRunId: $"run-{cohortId}",
            WorkflowRunAttempt: 1,
            SelectedSdkVersion: "10.0.302",
            runnerKind,
            runnerLabel,
            manifest,
            pairs);
    }

    private static PerfMeasurementRun Run(
        string runId,
        DateTimeOffset capturedAt,
        string sha,
        double candidateTime,
        string runnerClass,
        string runnerImage)
    {
        var build = new PerfBuildIdentity(sha, sha);
        return new PerfMeasurementRun(
            PerfMeasurementRun.SchemaV1,
            runId,
            capturedAt,
            build,
            build,
            new PerfEnvironmentProvenance(
                "10.0.10",
                "Ubuntu 24.04",
                "linux-x64",
                "X64",
                "server=False;concurrent=True",
                runnerClass,
                runnerImage),
            new PerfWorkloadProvenance(
                "issue-647-pilots",
                "v2",
                new Dictionary<string, string> { ["input-size"] = "1000" }),
            [
                new PerfBenchmarkObservation("cpu-lookup", "baseline", false, 100, 64),
                new PerfBenchmarkObservation("cpu-lookup", "candidate", false, candidateTime, 64),
                new PerfBenchmarkObservation("unchanged-control", "baseline", true, 100, 64),
                new PerfBenchmarkObservation("unchanged-control", "candidate", true, 100, 64),
            ]);
    }
}
