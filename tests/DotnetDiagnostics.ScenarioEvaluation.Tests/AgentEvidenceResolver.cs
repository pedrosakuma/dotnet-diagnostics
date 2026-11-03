using System.Globalization;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AgentEvidenceResolution(
    string Location,
    bool Exists,
    JsonElement? Value,
    string? Error,
    AgentCitationFailureKind? FailureKind,
    string ConsideredNamespace,
    string Syntax);

internal static class AgentEvidenceResolver
{
    private const string Prefix = "tool-result://";
    private const string DirectSyntax = "direct-json-pointer";
    private const string CopilotCliEnvelopeSyntax = "copilot-cli-json-envelope";

    public static AgentEvidenceResolution Resolve(
        string location,
        IReadOnlyList<AgentToolResult> results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var hashIndex = location.IndexOf('#', StringComparison.Ordinal);
        if (!location.StartsWith(Prefix, StringComparison.Ordinal)
            || hashIndex <= Prefix.Length)
        {
            return Missing(
                location,
                AgentCitationFailureKind.InvalidLocation,
                "The location must use tool-result://<call-id>#<json-pointer>.",
                "tool-result://<call-id>#",
                DirectSyntax);
        }

        var callId = location[Prefix.Length..hashIndex];
        var evidenceNamespace = $"{Prefix}{callId}#";
        var matches = results.Where(result => result.ToolCallId == callId).ToArray();
        if (matches.Length == 0)
        {
            return Missing(
                location,
                AgentCitationFailureKind.UnavailableArtifact,
                $"Tool call id '{callId}' was not found.",
                evidenceNamespace,
                DirectSyntax);
        }

        if (matches.Length != 1)
        {
            return Missing(
                location,
                AgentCitationFailureKind.UnavailableArtifact,
                $"Tool call id '{callId}' is ambiguous.",
                evidenceNamespace,
                DirectSyntax);
        }

        try
        {
            using var document = JsonDocument.Parse(matches[0].ContentJson);
            var pointer = location[(hashIndex + 1)..];
            if (pointer == "/")
            {
                pointer = string.Empty;
            }

            if (TryResolvePointer(document.RootElement, pointer, out var value, out var error))
            {
                return Resolved(
                    location,
                    value,
                    evidenceNamespace,
                    DirectSyntax);
            }

            string? envelopeError = null;
            var usesEnvelopeSyntax = TryNormalizeCopilotCliEnvelopePointer(
                pointer,
                out var normalizedPointer);
            if (usesEnvelopeSyntax
                && TryResolvePointer(
                    document.RootElement,
                    normalizedPointer,
                    out value,
                    out envelopeError))
            {
                return Resolved(
                    location,
                    value,
                    evidenceNamespace,
                    CopilotCliEnvelopeSyntax);
            }

            var resolutionError = usesEnvelopeSyntax
                ? envelopeError
                : error;
            return Missing(
                location,
                resolutionError == "The JSON pointer must be empty or start with '/'."
                    || resolutionError?.Contains(
                        "invalid '~' escape",
                        StringComparison.Ordinal) == true
                    ? AgentCitationFailureKind.InvalidLocation
                    : AgentCitationFailureKind.OutsideRetainedEvidence,
                resolutionError ?? "The location did not resolve.",
                evidenceNamespace,
                usesEnvelopeSyntax
                    ? CopilotCliEnvelopeSyntax
                    : DirectSyntax);
        }
        catch (JsonException exception)
        {
            return Missing(
                location,
                AgentCitationFailureKind.InvalidRetainedArtifact,
                $"The referenced tool result is not valid JSON: {exception.Message}",
                evidenceNamespace,
                DirectSyntax);
        }
    }

    private static AgentEvidenceResolution Resolved(
        string location,
        JsonElement value,
        string evidenceNamespace,
        string syntax)
        => new(
            location,
            true,
            value.Clone(),
            null,
            null,
            evidenceNamespace,
            syntax);

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

    private static bool TryNormalizeCopilotCliEnvelopePointer(
        string pointer,
        out string normalized)
    {
        if (pointer is "/json" or "/json/")
        {
            normalized = string.Empty;
            return true;
        }

        if (pointer.StartsWith("/json/", StringComparison.Ordinal))
        {
            normalized = pointer["/json".Length..];
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    private static AgentEvidenceResolution Missing(
        string location,
        AgentCitationFailureKind failureKind,
        string error,
        string consideredNamespace,
        string syntax)
        => new(
            location,
            false,
            null,
            error,
            failureKind,
            consideredNamespace,
            syntax);
}
