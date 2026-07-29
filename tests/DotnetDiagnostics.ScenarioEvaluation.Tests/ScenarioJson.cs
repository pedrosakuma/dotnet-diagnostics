using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ScenarioJson
{
    public const int CurrentEvidenceSchemaVersion = 1;
    public const int CurrentReportSchemaVersion = 1;
    public const int CurrentTrialArtifactSchemaVersion = 1;

    public static ScenarioEvidence ReadEvidence(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, ScenarioJsonContext.Default.ScenarioEvidence)
            ?? throw new InvalidDataException($"Scenario evidence '{path}' was empty.");
    }

    public static StructuredInterpretation ReadInterpretation(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, ScenarioJsonContext.Default.StructuredInterpretation)
            ?? throw new InvalidDataException($"Structured interpretation '{path}' was empty.");
    }

    public static AgentResponseMappingRequest ReadAgentResponseRequest(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, ScenarioJsonContext.Default.AgentResponseMappingRequest)
            ?? throw new InvalidDataException($"Agent response request '{path}' was empty.");
    }

    public static string SerializeAgentResponseInterpretation(AgentResponseInterpretation interpretation)
        => JsonSerializer.Serialize(interpretation, ScenarioJsonContext.Default.AgentResponseInterpretation);

    public static void WriteEvidence(string path, ScenarioEvidence evidence)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, evidence, ScenarioJsonContext.Default.ScenarioEvidence);
    }

    public static ScenarioTrialArtifact ReadTrialArtifact(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, ScenarioJsonContext.Default.ScenarioTrialArtifact)
            ?? throw new InvalidDataException($"Scenario trial artifact '{path}' was empty.");
    }

    public static void WriteTrialArtifact(string path, ScenarioTrialArtifact artifact)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, artifact, ScenarioJsonContext.Default.ScenarioTrialArtifact);
    }

    public static string SerializeReport(ScenarioEvaluationReport report)
        => JsonSerializer.Serialize(report, ScenarioJsonContext.Default.ScenarioEvaluationReport);
}
