using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class AllocationHotspotJson
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = AllocationHotspotJsonContext.Default,
        WriteIndented = true,
    };

    public static AllocationHotspotStudyFixture ReadFixture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var length = new FileInfo(path).Length;
        if (length is < 1 or > AllocationHotspotBounds.MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"Allocation hotspot fixture must be between 1 and {AllocationHotspotBounds.MaximumFixtureBytes} bytes.");
        }

        try
        {
            using var stream = File.OpenRead(path);
            var fixture = JsonSerializer.Deserialize<AllocationHotspotStudyFixture>(stream, Options)
                ?? throw new InvalidDataException($"Allocation hotspot fixture '{path}' was empty.");
            ValidateFixture(fixture);
            return fixture;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Allocation hotspot fixture '{path}' is invalid: {exception.Message}",
                exception);
        }
    }

    public static void WriteFixture(string path, AllocationHotspotStudyFixture fixture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fixture);
        ValidateFixture(fixture);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, fixture, Options);
    }

    public static string SerializeReport(AllocationHotspotStudyReport report)
        => JsonSerializer.Serialize(report, Options);

    private static void ValidateFixture(AllocationHotspotStudyFixture fixture)
    {
        if (fixture.SchemaVersion != CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(fixture.Id)
            || string.IsNullOrWhiteSpace(fixture.Environment)
            || fixture.Captures is null
            || fixture.Captures.Count == 0
            || string.IsNullOrWhiteSpace(fixture.ExpectedVerdict))
        {
            throw new InvalidDataException("Allocation hotspot fixture identity is incomplete.");
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AllocationHotspotStudyFixture))]
[JsonSerializable(typeof(AllocationHotspotStudyReport))]
internal sealed partial class AllocationHotspotJsonContext : JsonSerializerContext;
