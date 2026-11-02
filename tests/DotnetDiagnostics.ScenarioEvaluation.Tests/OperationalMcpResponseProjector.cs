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

        ApplyMutations(root, arm, projection.ResponseMutations);
        didProject = arm != OperationalMcpArm.CompleteCurrent;
        projectedSchema = schema;
        return arm == OperationalMcpArm.CompleteCurrent
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
        ApplyMutations(root, arm, projection.ToolDefinitionMutations);
        return root.Deserialize<IReadOnlyList<AgentToolDefinition>>(OperationalMcpJson.Options)
            ?? throw new JsonException("The projected model-visible tool definitions were empty.");
    }

    public static string ProjectionFingerprint(
        OperationalMcpProjection projection,
        OperationalMcpArm arm)
        => OperationalMcpProtocolLoader.Fingerprint(new
        {
            projection.Family,
            projection.Schema,
            Version = projection.Versions[arm],
            Source = arm == OperationalMcpArm.CompleteCurrent
                ? projection.CurrentSourceCommit
                : projection.Sources[arm],
            Response = projection.ResponseMutations.Select(value => new
            {
                value.JsonPointer,
                value.Operation,
                value.ExpectedKind,
                Value = ProjectedValue(value, arm),
            }),
            Tools = projection.ToolDefinitionMutations.Select(value => new
            {
                value.JsonPointer,
                value.Operation,
                value.ExpectedKind,
                Value = ProjectedValue(value, arm),
            }),
        });

    private static void ApplyMutations(
        JsonNode root,
        OperationalMcpArm arm,
        IReadOnlyList<OperationalProjectionMutation> mutations)
    {
        foreach (var mutation in mutations)
        {
            var actual = Resolve(root, mutation.JsonPointer);
            var expected = mutation.CurrentValue is { } currentValue
                ? Expand(root, currentValue)
                : null;
            if (actual is null
                || (expected is not null && !JsonNode.DeepEquals(actual, expected))
                || !MatchesKind(actual, mutation.ExpectedKind))
            {
                throw new InvalidDataException(
                    $"The current response value at '{mutation.JsonPointer}' changed: "
                    + $"expected={expected?.ToJsonString() ?? mutation.ExpectedKind}, "
                    + $"actual={actual?.ToJsonString() ?? "missing"}.");
            }

            if (!mutation.Arms.Contains(arm))
            {
                continue;
            }

            if (string.Equals(mutation.Operation, "remove", StringComparison.Ordinal))
            {
                Remove(root, mutation.JsonPointer);
            }
            else if (string.Equals(mutation.Operation, "replace", StringComparison.Ordinal)
                && mutation.Values?.TryGetValue(arm, out var projectedValue) == true)
            {
                Replace(root, mutation.JsonPointer, Expand(root, projectedValue));
            }
            else if (string.Equals(mutation.Operation, "select", StringComparison.Ordinal)
                && actual is JsonObject jsonObject
                && mutation.Values?.TryGetValue(arm, out var selectedValue) == true)
            {
                var selected = JsonNode.Parse(selectedValue.GetRawText())?.AsArray()
                    ?? throw new InvalidDataException(
                        $"Projection mutation '{mutation.JsonPointer}' must select an array of properties.");
                var names = selected
                    .Select(value => value?.GetValue<string>()
                        ?? throw new InvalidDataException(
                            $"Projection mutation '{mutation.JsonPointer}' contains an invalid property name."))
                    .ToHashSet(StringComparer.Ordinal);
                Replace(
                    root,
                    mutation.JsonPointer,
                    new JsonObject(jsonObject
                        .Where(property => names.Contains(property.Key))
                        .Select(property => KeyValuePair.Create(
                            property.Key,
                            property.Value?.DeepClone()))));
            }
            else
            {
                throw new InvalidDataException(
                    $"Projection mutation '{mutation.JsonPointer}' has an invalid operation.");
            }
        }
    }

    private static JsonElement? ProjectedValue(
        OperationalProjectionMutation mutation,
        OperationalMcpArm arm)
        => mutation.Arms.Contains(arm)
            ? string.Equals(mutation.Operation, "remove", StringComparison.Ordinal)
                ? null
                : mutation.Values?[arm]
            : mutation.CurrentValue;

    private static bool MatchesKind(JsonNode value, string expectedKind)
        => expectedKind switch
        {
            "string" => value is JsonValue jsonValue
                && jsonValue.TryGetValue<string>(out _),
            "number" => value is JsonValue numericValue
                && (numericValue.TryGetValue<int>(out _)
                    || numericValue.TryGetValue<long>(out _)
                    || numericValue.TryGetValue<double>(out _)),
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            _ => false,
        };

    private static JsonNode? Expand(JsonNode root, JsonElement value)
    {
        var node = JsonNode.Parse(value.GetRawText());
        if (node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text))
        {
            return JsonValue.Create(
                text.Replace(
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
                        StringComparison.Ordinal));
        }

        return node;
    }

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

    private static void Replace(JsonNode root, string pointer, JsonNode? value)
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
                jsonObject[last] = value?.DeepClone();
                break;
            case JsonArray jsonArray when int.TryParse(
                last,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var index)
                && index >= 0
                && index < jsonArray.Count:
                jsonArray[index] = value?.DeepClone();
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
