using System.Globalization;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AgentEvidenceResolution(
    string Location,
    bool Exists,
    JsonElement? Value,
    string? Error);

internal static class AgentEvidenceResolver
{
    private const string Prefix = "tool-result://";

    public static AgentEvidenceResolution Resolve(
        string location,
        IReadOnlyList<AgentToolResult> results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var hashIndex = location.IndexOf('#', StringComparison.Ordinal);
        if (!location.StartsWith(Prefix, StringComparison.Ordinal)
            || hashIndex <= Prefix.Length)
        {
            return Missing(location, "The location must use tool-result://<call-id>#<json-pointer>.");
        }

        var callId = location[Prefix.Length..hashIndex];
        var matches = results.Where(result => result.ToolCallId == callId).ToArray();
        if (matches.Length == 0)
        {
            return Missing(location, $"Tool call id '{callId}' was not found.");
        }

        if (matches.Length != 1)
        {
            return Missing(location, $"Tool call id '{callId}' is ambiguous.");
        }

        try
        {
            using var document = JsonDocument.Parse(matches[0].ContentJson);
            if (!TryResolvePointer(document.RootElement, location[(hashIndex + 1)..], out var value, out var error))
            {
                return Missing(location, error!);
            }

            return new AgentEvidenceResolution(location, true, value.Clone(), null);
        }
        catch (JsonException exception)
        {
            return Missing(location, $"The referenced tool result is not valid JSON: {exception.Message}");
        }
    }

    private static bool TryResolvePointer(
        JsonElement root,
        string pointer,
        out JsonElement value,
        out string? error)
    {
        value = root;
        error = null;
        if (pointer.Length == 0)
        {
            return true;
        }

        if (pointer[0] != '/')
        {
            error = "The JSON pointer must be empty or start with '/'.";
            return false;
        }

        foreach (var encodedSegment in pointer[1..].Split('/'))
        {
            if (!TryDecodeSegment(encodedSegment, out var segment))
            {
                error = $"The JSON pointer segment '{encodedSegment}' contains an invalid '~' escape.";
                return false;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                {
                    error = $"Object property '{segment}' does not exist.";
                    return false;
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index < 0
                    || index >= value.GetArrayLength())
                {
                    error = $"Array index '{segment}' is invalid or out of range.";
                    return false;
                }

                value = value[index];
            }
            else
            {
                error = $"The pointer cannot traverse a {value.ValueKind} value.";
                return false;
            }
        }

        return true;
    }

    private static bool TryDecodeSegment(string encoded, out string decoded)
    {
        var builder = new System.Text.StringBuilder(encoded.Length);
        for (var index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] != '~')
            {
                builder.Append(encoded[index]);
                continue;
            }

            if (++index >= encoded.Length || encoded[index] is not ('0' or '1'))
            {
                decoded = string.Empty;
                return false;
            }

            builder.Append(encoded[index] == '0' ? '~' : '/');
        }

        decoded = builder.ToString();
        return true;
    }

    private static AgentEvidenceResolution Missing(string location, string error)
        => new(location, false, null, error);
}
