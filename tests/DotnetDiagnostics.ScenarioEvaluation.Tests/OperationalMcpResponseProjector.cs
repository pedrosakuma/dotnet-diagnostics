using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class OperationalMcpResponseProjector
{
    public static string Apply(
        string raw,
        OperationalMcpArm arm,
        OperationalMcpProjection projection,
        out bool didProject,
        out string? projectedSchema)
    {
        didProject = false;
        projectedSchema = null;
        var root = JsonNode.Parse(raw)?.AsObject()
            ?? throw new JsonException("The MCP response was not a JSON object.");
        if (root["error"] is not null
            || root["data"] is not JsonObject data
            || data["schema"]?.GetValue<string>() is not { } schema
            || !string.Equals(schema, projection.Schema, StringComparison.Ordinal))
        {
            return raw;
        }

        if (!string.Equals(
                data["availability"]?.GetValue<string>(),
                "available",
                StringComparison.Ordinal))
        {
            return raw;
        }

        ApplyReplacements(root, arm, projection.ResponseReplacements);
        didProject = arm == OperationalMcpArm.PriorWording;
        projectedSchema = schema;
        return arm == OperationalMcpArm.HardenedWording
            ? raw
            : root.ToJsonString();
    }

    public static IReadOnlyList<AgentToolDefinition> ApplyToolDefinitions(
        IReadOnlyList<AgentToolDefinition> hardenedDefinitions,
        OperationalMcpArm arm,
        OperationalMcpProjection projection)
    {
        var root = JsonSerializer.SerializeToNode(
            hardenedDefinitions,
            OperationalMcpJson.Options)?.AsArray()
            ?? throw new JsonException("The model-visible tool definitions were invalid.");
        ApplyReplacements(root, arm, projection.ToolDefinitionReplacements);
        return root.Deserialize<IReadOnlyList<AgentToolDefinition>>(OperationalMcpJson.Options)
            ?? throw new JsonException("The projected model-visible tool definitions were empty.");
    }

    public static string WordingFingerprint(
        OperationalMcpProjection projection,
        OperationalMcpArm arm)
        => OperationalMcpProtocolLoader.Fingerprint(new
        {
            projection.Family,
            projection.Schema,
            Version = arm == OperationalMcpArm.PriorWording
                ? projection.PriorVersion
                : projection.HardenedVersion,
            SourceCommit = arm == OperationalMcpArm.PriorWording
                ? projection.PriorSourceCommit
                : projection.HardenedSourceCommit,
            Response = projection.ResponseReplacements.Select(value => new
            {
                value.JsonPointer,
                Wording = arm == OperationalMcpArm.PriorWording
                    ? value.PriorWording
                    : value.HardenedWording,
            }),
            Tools = projection.ToolDefinitionReplacements.Select(value => new
            {
                value.JsonPointer,
                Wording = arm == OperationalMcpArm.PriorWording
                    ? value.PriorWording
                    : value.HardenedWording,
            }),
        });

    private static void ApplyReplacements(
        JsonNode root,
        OperationalMcpArm arm,
        IReadOnlyList<OperationalWordingReplacement> replacements)
    {
        foreach (var replacement in replacements)
        {
            var expected = Expand(root, replacement.HardenedWording);
            var projected = arm == OperationalMcpArm.PriorWording
                ? replacement.PriorWording is null
                    ? null
                    : Expand(root, replacement.PriorWording)
                : expected;
            var node = Resolve(root, replacement.JsonPointer);
            if (node is not JsonValue value
                || !value.TryGetValue<string>(out var actual)
                || !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The hardened wording at '{replacement.JsonPointer}' changed.");
            }

            if (projected is null)
            {
                Remove(root, replacement.JsonPointer);
            }
            else
            {
                Replace(root, replacement.JsonPointer, projected);
            }
        }
    }

    private static string Expand(JsonNode root, string value)
        => value
            .Replace(
                "{availableCaptureCount}",
                Resolve(root, "/data/availableCaptureCount")?.GetValue<int>()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? "{availableCaptureCount}",
                StringComparison.Ordinal)
            .Replace(
                "{topK}",
                Resolve(root, "/data/topK")?.GetValue<int>()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? "{topK}",
                StringComparison.Ordinal);

    private static JsonNode? Resolve(JsonNode root, string pointer)
    {
        JsonNode? current = root;
        foreach (var segment in Segments(pointer))
        {
            current = current switch
            {
                JsonObject value => value[segment],
                JsonArray value when int.TryParse(
                    segment,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var index)
                    && index >= 0
                    && index < value.Count => value[index],
                _ => null,
            };
        }

        return current;
    }

    private static void Replace(JsonNode root, string pointer, string value)
    {
        var segments = Segments(pointer).ToArray();
        if (segments.Length == 0)
        {
            throw new InvalidDataException("A wording replacement cannot target the document root.");
        }

        var parentPointer = "/" + string.Join("/", segments[..^1].Select(Escape));
        var parent = segments.Length == 1 ? root : Resolve(root, parentPointer);
        var last = segments[^1];
        switch (parent)
        {
            case JsonObject jsonObject:
                jsonObject[last] = value;
                break;
            case JsonArray jsonArray when int.TryParse(
                last,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var index)
                && index >= 0
                && index < jsonArray.Count:
                jsonArray[index] = value;
                break;
            default:
                throw new InvalidDataException(
                    $"The wording replacement path '{pointer}' could not be assigned.");
        }
    }

    private static void Remove(JsonNode root, string pointer)
    {
        var segments = Segments(pointer).ToArray();
        if (segments.Length == 0)
        {
            throw new InvalidDataException("A wording replacement cannot remove the document root.");
        }

        var parentPointer = "/" + string.Join("/", segments[..^1].Select(Escape));
        var parent = segments.Length == 1 ? root : Resolve(root, parentPointer);
        var last = segments[^1];
        switch (parent)
        {
            case JsonObject jsonObject when jsonObject.Remove(last):
                break;
            case JsonArray jsonArray when int.TryParse(
                last,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var index)
                && index >= 0
                && index < jsonArray.Count:
                jsonArray.RemoveAt(index);
                break;
            default:
                throw new InvalidDataException(
                    $"The wording replacement path '{pointer}' could not be removed.");
        }
    }

    private static IEnumerable<string> Segments(string pointer)
    {
        if (pointer.Length == 0)
        {
            yield break;
        }

        if (pointer[0] != '/')
        {
            throw new InvalidDataException(
                $"The wording replacement path '{pointer}' is not a JSON Pointer.");
        }

        foreach (var segment in pointer[1..].Split('/'))
        {
            yield return segment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
        }
    }

    private static string Escape(string segment)
        => segment
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
}
