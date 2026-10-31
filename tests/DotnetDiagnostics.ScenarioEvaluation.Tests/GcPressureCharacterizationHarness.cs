using System.Text.Json;
using DotnetDiagnostics.Core.Gc;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class GcPressureCharacterizationHarness
{
    private static readonly JsonSerializerOptions JsonOptions =
        GcPressureCharacterizer.CreateJsonOptions();

    public static GcPressureCharacterizationResult Evaluate(GcPressureCapture capture)
        => GcPressureCharacterizer.Evaluate(capture);

    public static GcPressureReplayFixture ReadFixture(string path)
    {
        using var stream = File.OpenRead(path);
        var fixture = JsonSerializer.Deserialize<GcPressureReplayFixture>(stream, JsonOptions)
            ?? throw new InvalidDataException($"GC pressure fixture '{path}' is empty.");
        if (fixture.SchemaVersion != GcPressureCharacterizer.CurrentCaptureSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported GC pressure fixture schema version {fixture.SchemaVersion}.");
        }

        return fixture;
    }

    public static string Serialize(GcPressureCharacterizationResult result)
        => JsonSerializer.Serialize(result, JsonOptions);

    public static string SerializeCapture(GcPressureCapture capture)
        => JsonSerializer.Serialize(capture, JsonOptions);
}
