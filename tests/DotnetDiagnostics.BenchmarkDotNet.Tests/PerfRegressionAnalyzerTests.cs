using DotnetDiagnostics.BenchmarkDotNet.Regression;
using FluentAssertions;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public sealed class PerfRegressionAnalyzerTests
{
    [Fact]
    public void RepeatedCompatibleTimingRegression_WithAttribution_IsSoftGateCandidate()
    {
        var runs = WithStableControl(Runs(
            baselineTimes: [100, 101, 99],
            candidateTimes: [112, 114, 111],
            baselineAllocations: [64, 64, 64],
            candidateAllocations: [64, 64, 64]));
        var attribution = new[]
        {
            new PerfDiagnosticAttribution(
                "cpu-lookup",
                "cpu",
                "Hottest self-cost: PerfRegressionWorkloads.CultureAwareLookupCandidate",
                "candidate.cpu.json",
                "CultureAwareLookupCandidate",
                Matched: true),
        };

        var report = PerfRegressionAnalyzer.Analyze(runs, Diagnostic(runs, attribution));

        report.Verdict.Should().Be(PerfRegressionVerdict.Regression);
        report.EligibleForGate.Should().BeTrue();
        report.Recommendation.Should().Be(PerfGateRecommendation.SoftGateCandidate);
        report.Scenarios.Single(static scenario => !scenario.IsControl)
            .Timing.RegressionAgreementCount.Should().Be(3);
        report.Scenarios.Single(static scenario => !scenario.IsControl)
            .AttributionConsistent.Should().BeTrue();
    }

    [Fact]
    public void StableAllocationRegression_IsHardGateCandidate()
    {
        var runs = WithStableControl(Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [100, 100, 100],
            baselineAllocations: [100, 100, 100],
            candidateAllocations: [120, 120, 120]));
        var attribution = new[]
        {
            new PerfDiagnosticAttribution(
                "cpu-lookup",
                "allocation",
                "Top site: PerfRegressionWorkloads.AllocationCandidate",
                "candidate.allocation.json",
                "AllocationCandidate",
                Matched: true),
        };

        var report = PerfRegressionAnalyzer.Analyze(runs, Diagnostic(runs, attribution));

        report.Verdict.Should().Be(PerfRegressionVerdict.Regression);
        report.Recommendation.Should().Be(PerfGateRecommendation.HardGateCandidate);
        report.EligibleForGate.Should().BeTrue();
    }

    [Fact]
    public void RegressionWithoutControl_IsNeverGateEligible()
    {
        var runs = Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [120, 120, 120],
            baselineAllocations: [64, 64, 64],
            candidateAllocations: [64, 64, 64]);
        var diagnostic = Diagnostic(
            runs,
            [new PerfDiagnosticAttribution("cpu-lookup", "cpu", "matched", "cpu.json", "matched", true)]);

        var report = PerfRegressionAnalyzer.Analyze(runs, diagnostic);

        report.Verdict.Should().Be(PerfRegressionVerdict.Regression);
        report.EligibleForGate.Should().BeFalse();
        report.Recommendation.Should().Be(PerfGateRecommendation.Advisory);
        report.Notes.Should().Contain(note => note.Contains("No unchanged control", StringComparison.Ordinal));
    }

    [Fact]
    public void RegressionWithoutAttribution_KeepsOverallRecommendationAdvisory()
    {
        var runs = Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [100, 100, 100],
            baselineAllocations: [100, 100, 100],
            candidateAllocations: [120, 120, 120]);

        var report = PerfRegressionAnalyzer.Analyze(runs);

        report.Verdict.Should().Be(PerfRegressionVerdict.Regression);
        report.Recommendation.Should().Be(PerfGateRecommendation.Advisory);
        report.EligibleForGate.Should().BeFalse();
    }

    [Fact]
    public void ThreadPoolAttribution_RequiresThreePositiveIndependentLaunches()
    {
        var runs = WithStableControl(Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [120, 120, 120],
            baselineAllocations: [64, 64, 64],
            candidateAllocations: [64, 64, 64]));
        var candidateAttribution = Enumerable.Range(1, 3)
            .Select(index => new PerfDiagnosticAttribution(
                "cpu-lookup",
                "threadpool",
                $"launch {index}: parsed starvation",
                $"wait-{index}.threadpool.json",
                "parsed starvation",
                Matched: true))
            .ToArray();
        var controlAttribution = Enumerable.Range(1, 3)
            .Select(index => new PerfDiagnosticAttribution(
                "cpu-lookup",
                "threadpool",
                $"control launch {index}: no causal wait",
                $"wait-control-{index}.threadpool.json",
                "no parsed starvation",
                Matched: true,
                IsControl: true))
            .ToArray();

        var report = PerfRegressionAnalyzer.Analyze(
            runs,
            Diagnostic(runs, [.. candidateAttribution, .. controlAttribution]));

        report.Scenarios.Single(static scenario => !scenario.IsControl)
            .AttributionConsistent.Should().BeTrue();
        report.EligibleForGate.Should().BeTrue();
    }

    [Fact]
    public void ThreadPoolAttribution_AbsentOrUnrelatedLaunch_DisablesGateEligibility()
    {
        var runs = WithStableControl(Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [120, 120, 120],
            baselineAllocations: [64, 64, 64],
            candidateAllocations: [64, 64, 64]));
        var attribution = new[]
        {
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "starvation", "wait-1.json", "starvation", true),
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "unrelated climbing", "wait-2.json", "starvation", false),
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "starvation", "wait-3.json", "starvation", true),
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "control clear", "control-1.json", "no starvation", true, IsControl: true),
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "control clear", "control-2.json", "no starvation", true, IsControl: true),
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "control clear", "control-3.json", "no starvation", true, IsControl: true),
        };

        var report = PerfRegressionAnalyzer.Analyze(runs, Diagnostic(runs, attribution));

        report.Scenarios.Single(static scenario => !scenario.IsControl)
            .AttributionConsistent.Should().BeFalse();
        report.EligibleForGate.Should().BeFalse();
        report.Notes.Should().Contain(note =>
            note.Contains("lacks consistent separate diagnostic attribution", StringComparison.Ordinal));
    }

    [Fact]
    public void ThreadPoolAttribution_ControlWithEquivalentEvidence_DisablesGateEligibility()
    {
        var runs = WithStableControl(Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [120, 120, 120],
            baselineAllocations: [64, 64, 64],
            candidateAllocations: [64, 64, 64]));
        var attribution = new[]
        {
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "blocking", "wait-1.json", "blocking", true),
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "blocking", "wait-2.json", "blocking", true),
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "blocking", "wait-3.json", "blocking", true),
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "control clear", "control-1.json", "no blocking", true, IsControl: true),
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "control also blocked", "control-2.json", "no blocking", false, IsControl: true),
            new PerfDiagnosticAttribution("cpu-lookup", "threadpool", "control clear", "control-3.json", "no blocking", true, IsControl: true),
        };

        var report = PerfRegressionAnalyzer.Analyze(runs, Diagnostic(runs, attribution));

        report.Scenarios.Single(static scenario => !scenario.IsControl)
            .AttributionConsistent.Should().BeFalse();
        report.EligibleForGate.Should().BeFalse();
    }

    [Fact]
    public void DuplicateCaptureDocuments_AreRejectedAsIncompatible()
    {
        var run = Runs(
            baselineTimes: [100],
            candidateTimes: [120],
            baselineAllocations: [64],
            candidateAllocations: [64]).Single();

        var report = PerfRegressionAnalyzer.Analyze([run, run, run]);

        report.Verdict.Should().Be(PerfRegressionVerdict.EnvironmentChanged);
        report.Compatibility.Mismatches.Should().Contain(mismatch =>
            mismatch.Contains("duplicate captures", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroAllocationBaseline_RequiresAbsoluteEffectFloor()
    {
        var belowFloor = PerfRegressionAnalyzer.Analyze(Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [100, 100, 100],
            baselineAllocations: [0, 0, 0],
            candidateAllocations: [1, 1, 1]));
        var aboveFloor = PerfRegressionAnalyzer.Analyze(Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [100, 100, 100],
            baselineAllocations: [0, 0, 0],
            candidateAllocations: [72, 72, 72]));

        belowFloor.Scenarios.Single().Allocation.Verdict.Should().Be(PerfRegressionVerdict.Inconclusive);
        aboveFloor.Scenarios.Single().Allocation.Verdict.Should().Be(PerfRegressionVerdict.Regression);
        aboveFloor.Scenarios.Single().Allocation.Rationale.Should().Contain("32 B/op zero-baseline");
    }

    [Fact]
    public void ExcessiveRunLevelVariance_IsInconclusive()
    {
        var runs = Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [80, 112, 150],
            baselineAllocations: [64, 64, 64],
            candidateAllocations: [64, 64, 64]);

        var report = PerfRegressionAnalyzer.Analyze(runs);

        report.Verdict.Should().Be(PerfRegressionVerdict.Inconclusive);
        report.Scenarios.Single().Timing.Rationale.Should().Contain("coefficient of variation");
        report.EligibleForGate.Should().BeFalse();
    }

    [Fact]
    public void FewerThanThreeIndependentRuns_IsInconclusive()
    {
        var report = PerfRegressionAnalyzer.Analyze(Runs(
            baselineTimes: [100],
            candidateTimes: [120],
            baselineAllocations: [64],
            candidateAllocations: [96]));

        report.Compatibility.Compatible.Should().BeTrue();
        report.Verdict.Should().Be(PerfRegressionVerdict.Inconclusive);
        report.Notes.Should().Contain(note => note.Contains("requires 3", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvironmentMismatch_StopsComparison()
    {
        var runs = Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [120, 120, 120],
            baselineAllocations: [64, 64, 64],
            candidateAllocations: [64, 64, 64]).ToArray();
        runs[2] = runs[2] with
        {
            Environment = runs[2].Environment with { RunnerClass = "dedicated-x64" },
        };

        var report = PerfRegressionAnalyzer.Analyze(runs);

        report.Verdict.Should().Be(PerfRegressionVerdict.EnvironmentChanged);
        report.Scenarios.Should().BeEmpty();
        report.Compatibility.Mismatches.Should().ContainSingle()
            .Which.Should().Contain("environment provenance");
    }

    [Fact]
    public void DiagnosticEnvironmentMismatch_StopsComparison()
    {
        var runs = Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [120, 120, 120],
            baselineAllocations: [64, 64, 64],
            candidateAllocations: [64, 64, 64]);
        var diagnostic = Diagnostic(
            runs,
            [new PerfDiagnosticAttribution("cpu-lookup", "cpu", "matched", "cpu.json", "matched", true)])
            with
            {
                Environment = runs[0].Environment with { RuntimeVersion = "10.0.1" },
            };

        var report = PerfRegressionAnalyzer.Analyze(runs, diagnostic);

        report.Verdict.Should().Be(PerfRegressionVerdict.EnvironmentChanged);
        report.Compatibility.Mismatches.Should().ContainSingle()
            .Which.Should().Contain("Diagnostic run");
    }

    [Fact]
    public void RegressingUnchangedControl_DisablesGate()
    {
        var runs = Runs(
                baselineTimes: [100, 100, 100],
                candidateTimes: [120, 120, 120],
                baselineAllocations: [64, 64, 64],
                candidateAllocations: [64, 64, 64])
            .Select((run, index) => run with
            {
                Observations =
                [
                    .. run.Observations,
                    new PerfBenchmarkObservation("unchanged-control", PerfMeasurementRun.BaselineVariant, true, 100, 64),
                    new PerfBenchmarkObservation("unchanged-control", PerfMeasurementRun.CandidateVariant, true, 120 + index, 64),
                ],
            })
            .ToArray();

        var report = PerfRegressionAnalyzer.Analyze(
            runs,
            Diagnostic(runs,
            [
                new PerfDiagnosticAttribution("cpu-lookup", "cpu", "candidate matched", "cpu.json", "candidate", true),
            ]));

        report.FalsePositiveCount.Should().Be(1);
        report.EligibleForGate.Should().BeFalse();
        report.Recommendation.Should().Be(PerfGateRecommendation.Advisory);
    }

    [Fact]
    public void Report_RoundTripsJson_AndLabelsDiagnosticTimingAsExcluded()
    {
        var report = PerfRegressionAnalyzer.Analyze(Runs(
            baselineTimes: [100, 100, 100],
            candidateTimes: [120, 120, 120],
            baselineAllocations: [64, 64, 64],
            candidateAllocations: [64, 64, 64]));

        var json = PerfRegressionReportSerializer.SerializeReport(report);
        var markdown = PerfRegressionReportSerializer.BuildMarkdown(report);

        json.Should().Contain(PerfRegressionReport.SchemaV1);
        json.Should().Contain("\"verdict\": \"regression\"");
        markdown.Should().Contain("Diagnostic-run timing is intentionally excluded");
    }

    [Fact]
    public void MeasurementRun_RoundTripsStableSchema()
    {
        var original = Runs(
            baselineTimes: [100],
            candidateTimes: [120],
            baselineAllocations: [64],
            candidateAllocations: [64]).Single();

        var json = PerfRegressionReportSerializer.SerializeRun(original);
        var restored = PerfRegressionReportSerializer.DeserializeRun(json);

        restored.Should().BeEquivalentTo(original);
        json.Should().Contain(PerfMeasurementRun.SchemaV1);
    }

    [Fact]
    public void DiagnosticRun_RoundTripsCompactSignalsAndRawArtifactMetadata()
    {
        var runs = Runs(
            baselineTimes: [100],
            candidateTimes: [120],
            baselineAllocations: [64],
            candidateAllocations: [64]);
        var original = Diagnostic(
            runs,
            [
                new PerfDiagnosticAttribution(
                    "cpu-lookup",
                    "cpu",
                    "candidate matched",
                    "raw/candidate.cpu.json",
                    "candidate",
                    Matched: true,
                    Signals:
                    [
                        new PerfDiagnosticSignal(
                            "cpu.hotspot.exclusivePercent",
                            "Candidate",
                            "Benchmarks!Candidate()",
                            91.5,
                            "%",
                            PerfSignalDirection.Lower),
                    ],
                    RawArtifact: new PerfRawArtifactReference(
                        "raw/candidate.cpu.json",
                        128_000,
                        "bce8b16592d51d00415c59ca141deea40fb082290d75d4c33bfe255cc96739a4",
                        30),
                    IsControl: true),
            ]);

        var json = PerfRegressionReportSerializer.SerializeDiagnosticRun(original);
        var restored = PerfRegressionReportSerializer.DeserializeDiagnosticRun(json);

        restored.Should().BeEquivalentTo(original);
        restored.Attribution.Single().Signals.Should().ContainSingle()
            .Which.StableId.Should().Be("Benchmarks!Candidate()");
        restored.Attribution.Single().RawArtifact.Should().NotBeNull();
        restored.Attribution.Single().IsControl.Should().BeTrue();
        json.Should().Contain("\"contentSha256\"");
        json.Should().Contain("\"isControl\": true");
        json.Should().Contain("\"retentionDays\": 30");
    }

    private static PerfDiagnosticRun Diagnostic(
        PerfMeasurementRun[] runs,
        IReadOnlyList<PerfDiagnosticAttribution> attribution)
        => new(
            PerfDiagnosticRun.SchemaV1,
            new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero),
            runs[0].CandidateBuild,
            runs[0].Environment,
            runs[0].Workload,
            attribution);

    private static PerfMeasurementRun[] Runs(
        IReadOnlyList<double> baselineTimes,
        IReadOnlyList<double> candidateTimes,
        IReadOnlyList<double> baselineAllocations,
        IReadOnlyList<double> candidateAllocations)
    {
        baselineTimes.Count.Should().Be(candidateTimes.Count);
        baselineTimes.Count.Should().Be(baselineAllocations.Count);
        baselineTimes.Count.Should().Be(candidateAllocations.Count);

        var environment = new PerfEnvironmentProvenance(
            "10.0.0",
            "Ubuntu 24.04",
            "linux-x64",
            "X64",
            "workstation",
            "github-hosted-ubuntu");
        var workload = new PerfWorkloadProvenance(
            "issue-647-pilots",
            "v1",
            new Dictionary<string, string> { ["input-size"] = "1000" });
        var baselineBuild = new PerfBuildIdentity("baseline", "aaaa");
        var candidateBuild = new PerfBuildIdentity("candidate", "bbbb");

        return Enumerable.Range(0, baselineTimes.Count)
            .Select(index => new PerfMeasurementRun(
                PerfMeasurementRun.SchemaV1,
                $"run-{index + 1}",
                new DateTimeOffset(2026, 7, 18, 0, 0, index, TimeSpan.Zero),
                baselineBuild,
                candidateBuild,
                environment,
                workload,
                [
                    new PerfBenchmarkObservation(
                        "cpu-lookup",
                        PerfMeasurementRun.BaselineVariant,
                        false,
                        baselineTimes[index],
                        baselineAllocations[index]),
                    new PerfBenchmarkObservation(
                        "cpu-lookup",
                        PerfMeasurementRun.CandidateVariant,
                        false,
                        candidateTimes[index],
                        candidateAllocations[index]),
                ]))
            .ToArray();
    }

    private static PerfMeasurementRun[] WithStableControl(PerfMeasurementRun[] runs)
        => runs.Select(static run => run with
        {
            Observations =
            [
                .. run.Observations,
                new PerfBenchmarkObservation(
                    "unchanged-control",
                    PerfMeasurementRun.BaselineVariant,
                    true,
                    100,
                    64),
                new PerfBenchmarkObservation(
                    "unchanged-control",
                    PerfMeasurementRun.CandidateVariant,
                    true,
                    100,
                    64),
            ],
        }).ToArray();
}
