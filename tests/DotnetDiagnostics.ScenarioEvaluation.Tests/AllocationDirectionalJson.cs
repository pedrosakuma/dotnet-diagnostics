using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class AllocationDirectionalJson
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AllocationDirectionalFixture ReadFixture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > AllocationDirectionalBounds.MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"Allocation directional fixture must be between 1 and {AllocationDirectionalBounds.MaximumFixtureBytes} bytes.");
        }

        try
        {
            return JsonSerializer.Deserialize<AllocationDirectionalFixture>(
                    File.ReadAllText(path),
                    Options)
                ?? throw new InvalidDataException("Allocation directional fixture was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Allocation directional fixture '{path}' is invalid: {exception.Message}",
                exception);
        }
    }

    public static void WriteFixture(string path, AllocationDirectionalFixture fixture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fixture);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        JsonSerializer.Serialize(stream, fixture, Options);
    }

    public static string SerializeReport(AllocationDirectionalStudyReport report)
        => JsonSerializer.Serialize(report, Options);
}
