using DotnetDiagnostics.BenchmarkDotNet.Regression;
using FluentAssertions;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public sealed class PerfPairedComparisonAnalyzerTests
{
    [Fact]
    public void ThreeAlternatingCompatiblePairs_StayAdvisoryAndReportCalibration()
    {
        var pairs = Pairs(
            mainCandidateTimes: [120, 121, 119],
            pullRequestCandidateTimes: [144, 145, 143]);
        var diagnostic = Diagnostic(pairs);

        var (manifest, report) = PerfPairedComparisonAnalyzer.Analyze(
            pairs,
            Feasibility(totalMinutes: 8.5),
            diagnostic);

        manifest.Pairs.Select(static pair => pair.Order).Should().Equal(
            PerfPairOrder.MainThenPullRequest,
            PerfPairOrder.PullRequestThenMain,
            PerfPairOrder.MainThenPullRequest);
        manifest.MainBuild.CommitSha.Should().Be("main-sha");
        manifest.PullRequestBuild.CommitSha.Should().Be("pr-sha");
        report.Compatibility.Compatible.Should().BeTrue();
        report.AttributionCompatibility.Compatible.Should().BeTrue();
        report.Verdict.Should().Be(PerfRegressionVerdict.Regression);
        report.EligibleForGate.Should().BeFalse();
        report.Recommendation.Should().Be(PerfGateRecommendation.Advisory);
        report.Decision.Should().Be(PerfExperimentDecision.PartialGo);
        report.Policy.Version.Should().Be(PerfPairedRegressionPolicy.PolicyV1);
        report.Workloads.Single(static workload => workload.WorkloadId == "cpu-lookup")
            .Variants.Single(static variant => variant.Variant == PerfMeasurementRun.CandidateVariant)
            .Timing.Verdict.Should().Be(PerfRegressionVerdict.Regression);
        report.Calibration.Should().OnlyContain(static calibration =>
            calibration.DetectionRatePercent == 100
            && calibration.FalsePositiveRatePercent == 0);
        report.Cadence.Single(static row => row.Cadence == PerfExperimentCadence.EveryPullRequest)
            .Suitability.Should().Be(PerfOperationalSuitability.Conditional);
        report.Notes.Should().Contain(note => note.Contains("within-VM", StringComparison.Ordinal));
        report.Notes.Should().Contain(note => note.Contains("dedicated-runner", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkloadSetChanges_AreClassifiedWithoutRegressionVerdicts()
    {
        var pairs = Pairs(
                mainCandidateTimes: [120, 120, 120],
                pullRequestCandidateTimes: [120, 120, 120])
            .Select(pair => pair with
            {
                Main = pair.Main with
                {
                    Observations =
                    [
                        .. pair.Main.Observations,
                        Observation("removed-workload", "baseline", false, 100),
                        Observation("removed-workload", "candidate", false, 100),
                        Observation("contract-change", "baseline", false, 100),
                        Observation("contract-change", "candidate", false, 100),
                    ],
                },
                PullRequest = pair.PullRequest with
                {
                    Observations =
                    [
                        .. pair.PullRequest.Observations,
                        Observation("new-workload", "baseline", false, 100),
                        Observation("new-workload", "candidate", false, 100),
                        Observation("contract-change", "baseline", false, 100),
                        Observation("contract-change", "candidate-v2", false, 100),
                    ],
                },
            })
            .ToArray();

        var (_, report) = PerfPairedComparisonAnalyzer.Analyze(pairs, Feasibility());

        report.Compatibility.Compatible.Should().BeTrue();
        report.Workloads.Single(static workload => workload.WorkloadId == "new-workload")
            .Status.Should().Be(PerfWorkloadSetStatus.NewUnbaselined);
        report.Workloads.Single(static workload => workload.WorkloadId == "removed-workload")
            .Status.Should().Be(PerfWorkloadSetStatus.Removed);
        report.Workloads.Single(static workload => workload.WorkloadId == "contract-change")
            .Status.Should().Be(PerfWorkloadSetStatus.ContractChanged);
        report.Workloads.Where(static workload =>
                workload.Status != PerfWorkloadSetStatus.Comparable)
            .Should().OnlyContain(static workload =>
                workload.Variants.Count == 0
                && workload.Verdict == PerfRegressionVerdict.Inconclusive);
    }

    [Fact]
    public void NonAlternatingOrder_IsIncompatibleAndMetricsAreNotPooled()
    {
        var pairs = Pairs(
            mainCandidateTimes: [120, 120, 120],
            pullRequestCandidateTimes: [144, 144, 144]);
        pairs[1] = pairs[1] with { Order = PerfPairOrder.MainThenPullRequest };

        var (_, report) = PerfPairedComparisonAnalyzer.Analyze(pairs, Feasibility());

        report.Compatibility.Compatible.Should().BeFalse();
        report.Verdict.Should().Be(PerfRegressionVerdict.EnvironmentChanged);
        report.Workloads.Where(static workload =>
                workload.Status == PerfWorkloadSetStatus.Comparable)
            .Should().OnlyContain(static workload =>
                workload.Variants.Count == 0
                && workload.Verdict == PerfRegressionVerdict.EnvironmentChanged);
        report.Compatibility.Mismatches.Should().Contain(mismatch =>
            mismatch.Contains("alternate", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagnosticElapsedStage_DoesNotChangeMetricVerdict()
    {
        var pairs = Pairs(
            mainCandidateTimes: [120, 120, 120],
            pullRequestCandidateTimes: [144, 144, 144]);
        var shortDiagnostics = Feasibility() with
        {
            Stages =
            [
                new PerfExperimentStageMetric(
                    PerfExperimentStageKind.Diagnostics,
                    "diagnostics",
                    DurationSeconds: 1,
                    ArtifactBytes: 10),
            ],
        };
        var longDiagnostics = shortDiagnostics with
        {
            Stages =
            [
                new PerfExperimentStageMetric(
                    PerfExperimentStageKind.Diagnostics,
                    "diagnostics",
                    DurationSeconds: 10_000,
                    ArtifactBytes: 10),
            ],
        };

        var (_, shortReport) = PerfPairedComparisonAnalyzer.Analyze(pairs, shortDiagnostics, Diagnostic(pairs));
        var (_, longReport) = PerfPairedComparisonAnalyzer.Analyze(pairs, longDiagnostics, Diagnostic(pairs));

        longReport.Verdict.Should().Be(shortReport.Verdict);
        longReport.Workloads.Should().BeEquivalentTo(
            shortReport.Workloads,
            options => options.Excluding(static member => member.Path.EndsWith(".Rationale", StringComparison.Ordinal)));
    }

    [Fact]
    public void DiagnosticProvenanceMismatch_DoesNotVetoCleanPairVerdict()
    {
        var pairs = Pairs(
            mainCandidateTimes: [120, 120, 120],
            pullRequestCandidateTimes: [144, 144, 144]);
        var mismatchedDiagnostic = Diagnostic(pairs) with
        {
            Environment = pairs[0].PullRequest.Environment with { RunnerImage = "different-image" },
        };

        var (_, report) = PerfPairedComparisonAnalyzer.Analyze(
            pairs,
            Feasibility(),
            mismatchedDiagnostic);

        report.Compatibility.Compatible.Should().BeTrue();
        report.AttributionCompatibility.Compatible.Should().BeFalse();
        report.Verdict.Should().Be(PerfRegressionVerdict.Regression);
        report.Workloads.Single(static workload => workload.WorkloadId == "cpu-lookup")
            .Variants.Single(static variant => variant.Variant == PerfMeasurementRun.CandidateVariant)
            .Timing.Verdict.Should().Be(PerfRegressionVerdict.Regression);
        report.Notes.Should().Contain(note =>
            note.Contains("attribution cannot support", StringComparison.Ordinal));
    }

    [Fact]
    public void PairedArtifacts_RoundTripAndMarkdownCarriesEvidenceBoundary()
    {
        var pairs = Pairs(
            mainCandidateTimes: [120, 120, 120],
            pullRequestCandidateTimes: [120, 120, 120]);
        var (manifest, report) = PerfPairedComparisonAnalyzer.Analyze(pairs, Feasibility(), Diagnostic(pairs));

        var manifestJson = PerfRegressionReportSerializer.SerializePairedManifest(manifest);
        var reportJson = PerfRegressionReportSerializer.SerializePairedReport(report);
        var feasibilityJson = PerfRegressionReportSerializer.SerializeFeasibility(report.Feasibility);
        var markdown = PerfRegressionReportSerializer.BuildPairedMarkdown(report);

        PerfRegressionReportSerializer.DeserializePairedManifest(manifestJson)
            .Should().BeEquivalentTo(manifest);
        PerfRegressionReportSerializer.DeserializePairedReport(reportJson)
            .Should().BeEquivalentTo(report);
        PerfRegressionReportSerializer.DeserializeFeasibility(feasibilityJson)
            .Should().BeEquivalentTo(report.Feasibility);
        reportJson.Should().Contain(PerfPairedRegressionReport.SchemaV1);
        reportJson.Should().Contain("\"version\": \"issue-651-advisory-v1\"");
        markdown.Should().Contain("never gate-eligible from one cohort");
        markdown.Should().Contain("Diagnostic elapsed time never enters a regression verdict");
        markdown.Should().Contain("Fixture calibration");
        markdown.Should().Contain("Operational feasibility");
    }

    private static PerfPairedMeasurement[] Pairs(
        IReadOnlyList<double> mainCandidateTimes,
        IReadOnlyList<double> pullRequestCandidateTimes)
    {
        mainCandidateTimes.Count.Should().Be(3);
        pullRequestCandidateTimes.Count.Should().Be(3);
        return Enumerable.Range(0, 3)
            .Select(index => new PerfPairedMeasurement(
                index + 1,
                index % 2 == 0
                    ? PerfPairOrder.MainThenPullRequest
                    : PerfPairOrder.PullRequestThenMain,
                Run(
                    $"main-{index + 1}",
                    new DateTimeOffset(2026, 7, 18, 10, 0, index * 2, TimeSpan.Zero),
                    "main-sha",
                    mainCandidateTimes[index]),
                Run(
                    $"pr-{index + 1}",
                    new DateTimeOffset(2026, 7, 18, 10, 0, index * 2 + 1, TimeSpan.Zero),
                    "pr-sha",
                    pullRequestCandidateTimes[index])))
            .ToArray();
    }

    private static PerfMeasurementRun Run(
        string runId,
        DateTimeOffset capturedAt,
        string sha,
        double candidateTime)
    {
        var build = new PerfBuildIdentity(sha, sha);
        return new PerfMeasurementRun(
            PerfMeasurementRun.SchemaV1,
            runId,
            capturedAt,
            build,
            build,
            Environment(),
            new PerfWorkloadProvenance(
                "issue-647-pilots",
                "v1",
                new Dictionary<string, string> { ["input-size"] = "1000" }),
            [
                Observation("cpu-lookup", "baseline", false, 100),
                Observation("cpu-lookup", "candidate", false, candidateTime),
                Observation("unchanged-control", "baseline", true, 100),
                Observation("unchanged-control", "candidate", true, 100),
            ]);
    }

    private static PerfBenchmarkObservation Observation(
        string scenario,
        string variant,
        bool isControl,
        double time)
        => new(scenario, variant, isControl, time, AllocatedBytesPerOperation: 64);

    private static PerfDiagnosticRun Diagnostic(IReadOnlyList<PerfPairedMeasurement> pairs)
        => new(
            PerfDiagnosticRun.SchemaV1,
            new DateTimeOffset(2026, 7, 18, 10, 1, 0, TimeSpan.Zero),
            pairs[0].PullRequest.CandidateBuild,
            pairs[0].PullRequest.Environment,
            pairs[0].PullRequest.Workload,
            [
                new PerfDiagnosticAttribution(
                    "cpu-lookup",
                    "cpu",
                    "candidate matched",
                    "raw/cpu.json",
                    "candidate",
                    Matched: true,
                    Signals:
                    [
                        new PerfDiagnosticSignal(
                            "cpu.hotspot.exclusivePercent",
                            "Candidate",
                            "Benchmarks!Candidate()",
                            90,
                            "%",
                            PerfSignalDirection.Lower),
                    ]),
            ]);

    private static PerfExperimentFeasibility Feasibility(double totalMinutes = 12)
        => new(
            "single_github_hosted_vm",
            totalMinutes,
            CompactArtifactBytes: 10_000,
            RawArtifactBytes: 1_000_000,
            [
                new PerfExperimentStageMetric(
                    PerfExperimentStageKind.Checkout,
                    "checkout-main",
                    DurationSeconds: 2,
                    ArtifactBytes: 100,
                    Ref: "main"),
                new PerfExperimentStageMetric(
                    PerfExperimentStageKind.CleanPair,
                    "clean-pair-1-main_then_pr",
                    DurationSeconds: 60,
                    ArtifactBytes: 1_000,
                    PairNumber: 1),
            ]);

    private static PerfEnvironmentProvenance Environment()
        => new(
            "10.0.10",
            "Ubuntu 24.04",
            "linux-x64",
            "X64",
            "server=False;concurrent=True",
            "github-hosted-ubuntu-latest",
            "ubuntu24-20260714.240.1");
}
