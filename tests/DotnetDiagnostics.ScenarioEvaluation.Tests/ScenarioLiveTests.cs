using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScenarioEvaluationLiveGroup
{
    public const string Name = "ScenarioEvaluationLive";
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class ScenarioLiveTests
{
    private const string RepetitionsEnvironmentVariable = "DOTNET_DIAGNOSTICS_SCENARIO_REPETITIONS";
    private const string TrialOffsetEnvironmentVariable = "DOTNET_DIAGNOSTICS_SCENARIO_TRIAL_OFFSET";
    private const string OutputDirectoryEnvironmentVariable = "DOTNET_DIAGNOSTICS_SCENARIO_OUTPUT_DIR";

    [WindowsOnlyFact(
        "Culture-lookup live CPU capture is quarantined on Linux CI because the EventPipe SampleProfiler can crash the test host (#147).",
        Timeout = 600_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public Task LiveCapture_CultureLookup_SatisfiesStructuredEvidenceInvariants()
        => RunLiveCaptureAsync("culture-lookup");

    [Theory(Timeout = 600_000)]
    [InlineData("sync-over-async")]
    [InlineData("lock-storm")]
    [Trait("Category", "ScenarioEvaluationLive")]
    public Task LiveCapture_WaitScenarios_SatisfyStructuredEvidenceInvariants(string scenarioId)
        => RunLiveCaptureAsync(scenarioId);

    private static async Task RunLiveCaptureAsync(string scenarioId)
    {
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => item.Id == scenarioId);
        var repetitions = Repetitions();
        var trialOffset = TrialOffset();
        var reports = new List<ScenarioEvaluationReport>(repetitions);
        for (var repetition = 1; repetition <= repetitions; repetition++)
        {
            var trial = trialOffset + repetition;
            var evidence = await ScenarioLiveRunner.CaptureAsync(
                manifest,
                trial,
                CancellationToken.None);
            var report = ScenarioEvaluator.CreateReport(manifest, evidence);
            reports.Add(report);
            PersistWhenRequested(evidence);
        }

        reports.Should().OnlyContain(report =>
            report.Activation.Status == ScenarioStageStatus.Passed
            && report.Collection.Status == ScenarioStageStatus.Passed
            && report.Evidence.All(item => item.Passed),
            string.Join(
                Environment.NewLine,
                reports.SelectMany(report => report.Evidence.Where(item => !item.Passed).Select(item => $"{report.ScenarioId} trial {report.Trial}: {item.Detail}"))));
    }

    private static int Repetitions()
    {
        var raw = Environment.GetEnvironmentVariable(RepetitionsEnvironmentVariable);
        return int.TryParse(raw, out var repetitions)
            ? Math.Clamp(repetitions, 1, 20)
            : 1;
    }

    private static int TrialOffset()
    {
        var raw = Environment.GetEnvironmentVariable(TrialOffsetEnvironmentVariable);
        return int.TryParse(raw, out var offset)
            ? Math.Max(0, offset)
            : 0;
    }

    private static void PersistWhenRequested(ScenarioEvidence evidence)
    {
        var outputDirectory = Environment.GetEnvironmentVariable(OutputDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        var fileName = $"{evidence.ScenarioId}.{evidence.Environment.Os}.trial-{evidence.Trial}.evidence.json";
        ScenarioJson.WriteEvidence(Path.Combine(outputDirectory, fileName), evidence);
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
    }
}
