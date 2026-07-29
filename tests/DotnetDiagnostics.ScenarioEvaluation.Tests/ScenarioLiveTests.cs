using System.Runtime.ExceptionServices;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class ScenarioEnvironmentVariables
{
    public const string Repetitions = "DOTNET_DIAGNOSTICS_SCENARIO_REPETITIONS";
    public const string TrialOffset = "DOTNET_DIAGNOSTICS_SCENARIO_TRIAL_OFFSET";
    public const string OutputDirectory = "DOTNET_DIAGNOSTICS_SCENARIO_OUTPUT_DIR";
    public const string ScenarioId = "DOTNET_DIAGNOSTICS_SCENARIO_ID";
    public const string Trial = "DOTNET_DIAGNOSTICS_SCENARIO_TRIAL";
    public const string Attempt = "DOTNET_DIAGNOSTICS_SCENARIO_ATTEMPT";
    public const string TrialArtifactPath = "DOTNET_DIAGNOSTICS_SCENARIO_TRIAL_ARTIFACT_PATH";
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScenarioEvaluationLiveGroup
{
    public const string Name = "ScenarioEvaluationLive";
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class ScenarioLiveTests
{
    [WindowsOnlyFact(
        "Culture-lookup live CPU capture is quarantined on Linux CI because the EventPipe SampleProfiler can crash the test host (#147).",
        Timeout = 600_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public Task LiveCapture_CultureLookup_SatisfiesStructuredEvidenceInvariants()
        => RunLiveCaptureAsync("culture-lookup");

    [Theory(Timeout = 600_000)]
    [MemberData(nameof(NonCpuLiveScenarios))]
    [Trait("Category", "ScenarioEvaluationLive")]
    public Task LiveCapture_WaitScenarios_SatisfyStructuredEvidenceInvariants(string scenarioId)
        => RunLiveCaptureAsync(scenarioId);

    public static TheoryData<string> NonCpuLiveScenarios()
    {
        var data = new TheoryData<string>();
        foreach (var manifest in ScenarioManifestLoader.LoadAll()
                     .Where(item => !string.Equals(item.Id, "culture-lookup", StringComparison.Ordinal))
                     .Where(ScenarioLiveRunner.SupportsCurrentPlatform))
        {
            data.Add(manifest.Id);
        }

        return data;
    }

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
        var raw = Environment.GetEnvironmentVariable(ScenarioEnvironmentVariables.Repetitions);
        return int.TryParse(raw, out var repetitions)
            ? Math.Clamp(repetitions, 1, 20)
            : 1;
    }

    private static int TrialOffset()
    {
        var raw = Environment.GetEnvironmentVariable(ScenarioEnvironmentVariables.TrialOffset);
        return int.TryParse(raw, out var offset)
            ? Math.Max(0, offset)
            : 0;
    }

    private static void PersistWhenRequested(ScenarioEvidence evidence)
    {
        var outputDirectory = Environment.GetEnvironmentVariable(ScenarioEnvironmentVariables.OutputDirectory);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        var fileName = $"{evidence.ScenarioId}.{evidence.Environment.Os}.trial-{evidence.Trial}.evidence.json";
        ScenarioJson.WriteEvidence(Path.Combine(outputDirectory, fileName), evidence);
    }
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class ScenarioIsolatedTrialTests
{
    [EnvironmentRequiredFact(
        ScenarioEnvironmentVariables.ScenarioId,
        "Isolated single-trial capture runs only under scripts/run-scenario-evaluation-isolated.sh.",
        Timeout = 600_000)]
    [Trait("Category", "ScenarioEvaluationIsolated")]
    public async Task IsolatedTrial_ExecutesScenarioFromEnvironment()
    {
        var scenarioId = RequiredTextEnvironmentVariable(ScenarioEnvironmentVariables.ScenarioId);
        var trial = RequiredPositiveIntEnvironmentVariable(ScenarioEnvironmentVariables.Trial);
        var attempt = RequiredPositiveIntEnvironmentVariable(ScenarioEnvironmentVariables.Attempt);
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => item.Id == scenarioId);

        ScenarioTrialArtifact artifact;
        Exception? capturedFailure = null;
        try
        {
            var evidence = await ScenarioLiveRunner.CaptureAsync(
                manifest,
                trial,
                CancellationToken.None);
            var report = ScenarioEvaluator.CreateReport(manifest, evidence);
            var detail = EvidenceFailureDetail(report);
            artifact = string.IsNullOrEmpty(detail)
                ? new ScenarioTrialArtifact(
                    ScenarioJson.CurrentTrialArtifactSchemaVersion,
                    manifest.Id,
                    trial,
                    attempt,
                    ScenarioTrialArtifactOutcome.Passed,
                    ScenarioFailureKind.None,
                    "Activation, collection, and evidence invariants passed.",
                    evidence,
                    report)
                : new ScenarioTrialArtifact(
                    ScenarioJson.CurrentTrialArtifactSchemaVersion,
                    manifest.Id,
                    trial,
                    attempt,
                    ScenarioTrialArtifactOutcome.Failed,
                    ScenarioFailureKind.Evaluation,
                    detail,
                    evidence,
                    report);
        }
        catch (ScenarioRunException exception)
        {
            capturedFailure = exception;
            artifact = new ScenarioTrialArtifact(
                ScenarioJson.CurrentTrialArtifactSchemaVersion,
                manifest.Id,
                trial,
                attempt,
                ScenarioTrialArtifactOutcome.Failed,
                exception.FailureKind,
                exception.Message,
                Evidence: null,
                Report: null);
        }
        catch (Exception exception)
        {
            capturedFailure = exception;
            artifact = new ScenarioTrialArtifact(
                ScenarioJson.CurrentTrialArtifactSchemaVersion,
                manifest.Id,
                trial,
                attempt,
                ScenarioTrialArtifactOutcome.Failed,
                ScenarioFailureClassifier.Classify(exception, ScenarioFailureKind.Evaluation),
                exception.Message,
                Evidence: null,
                Report: null);
        }

        PersistTrialArtifactWhenRequested(artifact);
        if (capturedFailure is not null)
        {
            ExceptionDispatchInfo.Capture(capturedFailure).Throw();
        }

        artifact.Outcome.Should().Be(ScenarioTrialArtifactOutcome.Passed, artifact.Detail);
    }

    private static void PersistTrialArtifactWhenRequested(ScenarioTrialArtifact artifact)
    {
        var explicitPath = Environment.GetEnvironmentVariable(ScenarioEnvironmentVariables.TrialArtifactPath);
        var path = !string.IsNullOrWhiteSpace(explicitPath)
            ? explicitPath
            : TrialArtifactPathFromOutputDirectory(artifact);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ScenarioJson.WriteTrialArtifact(path, artifact);
    }

    private static string? TrialArtifactPathFromOutputDirectory(ScenarioTrialArtifact artifact)
    {
        var outputDirectory = Environment.GetEnvironmentVariable(ScenarioEnvironmentVariables.OutputDirectory);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return null;
        }

        return Path.Combine(
            outputDirectory,
            $"{artifact.ScenarioId}.trial-{artifact.Trial}.attempt-{artifact.Attempt}.trial.json");
    }

    private static string RequiredTextEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required.");
        }

        return value;
    }

    private static int RequiredPositiveIntEnvironmentVariable(string name)
    {
        var raw = RequiredTextEnvironmentVariable(name);
        if (!int.TryParse(raw, out var value) || value < 1)
        {
            throw new InvalidOperationException($"Environment variable '{name}' must be a positive integer.");
        }

        return value;
    }

    private static string EvidenceFailureDetail(ScenarioEvaluationReport report)
    {
        var failures = new List<string>();
        if (report.Activation.Status != ScenarioStageStatus.Passed)
        {
            failures.Add($"activation: {report.Activation.Detail}");
        }

        if (report.Collection.Status != ScenarioStageStatus.Passed)
        {
            failures.Add($"collection: {report.Collection.Detail}");
        }

        failures.AddRange(report.Evidence.Where(item => !item.Passed).Select(item => item.Detail));
        return string.Join(Environment.NewLine, failures);
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

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EnvironmentRequiredFactAttribute : FactAttribute
{
    public EnvironmentRequiredFactAttribute(string variableName, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName)))
        {
            Skip = reason;
        }
    }
}
