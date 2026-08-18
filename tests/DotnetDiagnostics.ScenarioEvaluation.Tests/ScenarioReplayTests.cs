using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class ScenarioReplayTests
{
    public static TheoryData<string> EvidenceFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory
                     .EnumerateFiles(
                         ScenarioManifestLoader.ScenarioPath("Fixtures"),
                         "*.evidence.json",
                         SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EvidenceFixtures))]
    public void Replay_EvaluatesRecordedEvidenceDeterministically(string path)
    {
        var evidence = ScenarioJson.ReadEvidence(path);
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => item.Id == evidence.ScenarioId);

        var first = ScenarioEvaluator.CreateReport(manifest, evidence);
        var second = ScenarioEvaluator.CreateReport(manifest, evidence);

        first.Evidence.Should().OnlyContain(result => result.Passed, FormatFailures(first));
        ScenarioJson.SerializeReport(first).Should().Be(ScenarioJson.SerializeReport(second));
    }

    [Theory]
    [MemberData(nameof(EvidenceFixtures))]
    public void StructuredInterpretation_AcceptsSupportedDiagnosisAndRejectsTemptingWrongOne(string path)
    {
        var evidence = ScenarioJson.ReadEvidence(path);
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => item.Id == evidence.ScenarioId);

        var accepted = new StructuredInterpretation(
            manifest.ExpectedEvidence.Select(item => item.Id).ToArray(),
            manifest.AcceptableHypotheses,
            manifest.AcceptableAttributions,
            manifest.AcceptableNextActions,
            manifest.RequiredCausalityPosture,
            []);
        var temptingWrong = new StructuredInterpretation(
            [],
            manifest.TemptingWrongHypotheses,
            [manifest.AcceptableAttributions[0], "unsupported-attribution"],
            [manifest.AcceptableNextActions[0], "unsupported-next-action"],
            "causal-claim-without-evidence",
            manifest.ForbiddenConclusions);

        var acceptedReport = ScenarioEvaluator.CreateReport(manifest, evidence, accepted);
        var rejectedReport = ScenarioEvaluator.CreateReport(manifest, evidence, temptingWrong);

        acceptedReport.Interpretation.Status.Should().Be(ScenarioStageStatus.Passed);
        acceptedReport.InterpretationScore!.WeightedScore.Should().Be(1);
        rejectedReport.Interpretation.Status.Should().Be(ScenarioStageStatus.Failed);
        rejectedReport.Interpretation.FailureKind.Should().Be(ScenarioFailureKind.Evaluation);
        rejectedReport.InterpretationScore!.WeightedScore.Should().BeLessThan(0.5);
        rejectedReport.InterpretationScore.Dimensions
            .Single(dimension => dimension.Name == "attribution")
            .Score.Should().Be(0);
        rejectedReport.InterpretationScore.Dimensions
            .Single(dimension => dimension.Name == "next-action")
            .Score.Should().Be(0);
        rejectedReport.InterpretationScore.Dimensions
            .Single(dimension => dimension.Name == "unsupported-conclusions")
            .Score.Should().Be(0);
    }

    [Fact]
    public void GcStorm_WithoutWindowChurnEvidence_FailsLohSizeMaxInvariant()
    {
        // #858: the windowed `loh-size-max` invariant must not pass merely because a final
        // `loh-size` value happens to be present/absent — it must actually require churn to have
        // been observed at some point during the window. A trial with zero LOH growth and no
        // gen2 pressure anywhere in the window is not gc-storm and must still fail.
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => item.Id == "gc-storm");
        var evidence = ScenarioJson.ReadEvidence(
            ScenarioManifestLoader.ScenarioPath("Fixtures", "gc-storm.linux.evidence.json"));
        var noChurnEvidence = evidence with
        {
            Metrics =
            [
                new ObservedMetric("alloc-rate", 1024, "B"),
                new ObservedMetric("gc-total-collections", 2, "collections"),
                new ObservedMetric("gen-2-gc-count", 0, null),
                new ObservedMetric("loh-size", 0, "B"),
                new ObservedMetric("loh-size-max", 0, "B"),
                new ObservedMetric("time-in-gc", 1, "%"),
            ],
            Signals = [],
        };

        var report = ScenarioEvaluator.CreateReport(manifest, noChurnEvidence);

        report.Evidence.Should().Contain(result => result.Id == "loh-size-elevated" && !result.Passed);
        report.Evidence.Should().Contain(result => result.Id == "gen2-counter-elevated" && !result.Passed);
        report.Evidence.Should().Contain(result => result.Id == "gen2-share-signal" && !result.Passed);
    }

    [Fact]
    public void GcStorm_WithPeakLohMidWindowButZeroFinalRetained_PassesLohSizeMaxInvariant()
    {
        // The regression this scenario exercises: EventCounters report the *last-observed* tick,
        // so a scenario that repeatedly allocates and collects LOH objects can legitimately show
        // `loh-size == 0` at the final sample even though large-object churn happened throughout
        // the window. `loh-size-max` (tracked across every tick) must still pass in that case.
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => item.Id == "gc-storm");
        var evidence = ScenarioJson.ReadEvidence(
            ScenarioManifestLoader.ScenarioPath("Fixtures", "gc-storm.linux.evidence.json"));
        evidence.Metrics.Should().Contain(metric => metric.Name == "loh-size" && metric.Value == 0);

        var report = ScenarioEvaluator.CreateReport(manifest, evidence);

        report.Evidence.Should().Contain(result => result.Id == "loh-size-elevated" && result.Passed);
    }

    [Fact]
    public void StructuredInterpretation_CannotPassWhenRequiredEvidenceFails()
    {
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => item.Id == "sync-over-async");
        var evidence = ScenarioJson.ReadEvidence(
            ScenarioManifestLoader.ScenarioPath("Fixtures", "sync-over-async.windows.evidence.json"));
        var failedEvidence = evidence with { Metrics = [] };
        var interpretation = new StructuredInterpretation(
            manifest.ExpectedEvidence.Select(item => item.Id).ToArray(),
            manifest.AcceptableHypotheses,
            manifest.AcceptableAttributions,
            manifest.AcceptableNextActions,
            manifest.RequiredCausalityPosture,
            []);

        var report = ScenarioEvaluator.CreateReport(manifest, failedEvidence, interpretation);

        report.Evidence.Should().Contain(result => !result.Passed);
        report.Interpretation.Status.Should().Be(ScenarioStageStatus.Failed);
        report.InterpretationScore!.Dimensions
            .Single(dimension => dimension.Name == "evidence-correctness")
            .Score.Should().Be(0);
    }

    [Fact]
    public void Replay_RejectsUnsupportedEvidenceSchemaVersion()
    {
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => item.Id == "lock-storm");
        var evidence = ScenarioJson.ReadEvidence(
            ScenarioManifestLoader.ScenarioPath("Fixtures", "lock-storm.windows.evidence.json")) with
        {
            SchemaVersion = ScenarioJson.CurrentEvidenceSchemaVersion + 1,
        };

        var action = () => ScenarioEvaluator.CreateReport(manifest, evidence);

        action.Should().Throw<InvalidDataException>().WithMessage("*schema version*");
    }

    private static string FormatFailures(ScenarioEvaluationReport report)
        => string.Join(
            Environment.NewLine,
            report.Evidence.Where(result => !result.Passed).Select(result => result.Detail));
}
