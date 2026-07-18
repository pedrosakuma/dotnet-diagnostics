using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ScenarioJson
{
    public const int CurrentEvidenceSchemaVersion = 1;
    public const int CurrentReportSchemaVersion = 1;

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

    public static void WriteEvidence(string path, ScenarioEvidence evidence)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, evidence, ScenarioJsonContext.Default.ScenarioEvidence);
    }

    public static string SerializeReport(ScenarioEvaluationReport report)
        => JsonSerializer.Serialize(report, ScenarioJsonContext.Default.ScenarioEvaluationReport);
}
