using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ThreadPoolBacklogEvidenceJson
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFixtureBytes = 256 * 1024;
    public const int MaximumCases = 16;
    public const int MaximumWindowsPerCase = 64;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = ThreadPoolBacklogEvidenceJsonContext.Default,
    };

    public static ThreadPoolBacklogReplayFixture ReadFixture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fileLength = new FileInfo(path).Length;
        if (fileLength is < 1 or > MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"ThreadPool backlog fixture must be between 1 and {MaximumFixtureBytes} bytes.");
        }

        var bytes = File.ReadAllBytes(path);
        try
        {
            var fixture = JsonSerializer.Deserialize<ThreadPoolBacklogReplayFixture>(bytes, Options)
                ?? throw new InvalidDataException($"ThreadPool backlog fixture '{path}' was empty.");
            Validate(fixture);
            return fixture;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"ThreadPool backlog fixture '{path}' is invalid: {exception.Message}",
                exception);
        }
    }

    private static void Validate(ThreadPoolBacklogReplayFixture fixture)
    {
        if (fixture.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported ThreadPool backlog fixture schema version {fixture.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(fixture.Id)
            || fixture.Cases is null
            || fixture.Cases.Count is < 1 or > MaximumCases)
        {
            throw new InvalidDataException("ThreadPool backlog fixture has an invalid identity or case count.");
        }

        if (fixture.Cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count()
            != fixture.Cases.Count)
        {
            throw new InvalidDataException("ThreadPool backlog fixture contains duplicate case identities.");
        }

        foreach (var item in fixture.Cases)
        {
            if (string.IsNullOrWhiteSpace(item.Id)
                || item.Protocol is null
                || item.Reason is null
                || item.Windows is null
                || item.Expected is null
                || item.Windows.Count is < 1 or > MaximumWindowsPerCase)
            {
                throw new InvalidDataException(
                    $"ThreadPool backlog fixture case '{item.Id}' has missing or out-of-bounds data.");
            }
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ThreadPoolBacklogReplayFixture))]
internal sealed partial class ThreadPoolBacklogEvidenceJsonContext : JsonSerializerContext;
