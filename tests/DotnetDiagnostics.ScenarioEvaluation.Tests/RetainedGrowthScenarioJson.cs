using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class RetainedGrowthScenarioJson
{
    public static RetainedGrowthReplayFixture LoadReplayFixture(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(
                stream,
                RetainedGrowthScenarioJsonContext.Default.RetainedGrowthReplayFixture)
            ?? throw new InvalidDataException($"Retained-growth replay fixture '{path}' was empty.");
    }

    public static string SerializeResult(RetainedGrowthScenarioResult result)
        => JsonSerializer.Serialize(
            result,
            RetainedGrowthScenarioJsonContext.Default.RetainedGrowthScenarioResult);
}
