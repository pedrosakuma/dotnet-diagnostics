using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ScenarioStatisticalEvidenceJson
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFixtureBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        TypeInfoResolver = ScenarioStatisticalEvidenceJsonContext.Default,
    };

    public static ScenarioStatisticalReplayFixture ReadFixture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 1 or > MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"Statistical replay fixture must be between 1 and {MaximumFixtureBytes} bytes.");
        }

        try
        {
            RejectDuplicateProperties(bytes);
            var fixture = JsonSerializer.Deserialize<ScenarioStatisticalReplayFixture>(bytes, Options)
                ?? throw new InvalidDataException($"Statistical replay fixture '{path}' was empty.");
            ValidateFixture(fixture);
            return fixture;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Statistical replay fixture '{path}' is invalid: {exception.Message}",
                exception);
        }
    }

    public static void WriteFixture(string path, ScenarioStatisticalReplayFixture fixture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fixture);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture, Options);
        if (bytes.Length > MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"Statistical replay fixture exceeds {MaximumFixtureBytes} bytes.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public static string SerializeResult(ScenarioStatisticalEvidenceResult result)
        => JsonSerializer.Serialize(result, Options);

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        Visit(document.RootElement, "$");
    }

    private static void ValidateFixture(ScenarioStatisticalReplayFixture fixture)
    {
        if (fixture.SchemaVersion != CurrentSchemaVersion
            || fixture.Request.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported statistical replay fixture schema version {fixture.SchemaVersion}.");
        }

        if (fixture.Request.Arms is null
            || fixture.Request.Protocol is null
            || fixture.Expected is null
            || fixture.Request.Arms.Any(arm =>
                arm is null
                || arm.Attempts is null
                || arm.Observations is null
                || arm.CompatibilityIdentity is null
                || arm.CompatibilityIdentity.Dimensions is null))
        {
            throw new InvalidDataException(
                "Statistical replay fixture contains a missing or null required record.");
        }
    }

    private static void Visit(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"Statistical replay fixture contains duplicate property '{path}.{property.Name}'.");
                }

                Visit(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                Visit(item, $"{path}[{index++}]");
            }
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ScenarioStatisticalReplayFixture))]
[JsonSerializable(typeof(ScenarioStatisticalEvidenceResult))]
internal sealed partial class ScenarioStatisticalEvidenceJsonContext : JsonSerializerContext;
