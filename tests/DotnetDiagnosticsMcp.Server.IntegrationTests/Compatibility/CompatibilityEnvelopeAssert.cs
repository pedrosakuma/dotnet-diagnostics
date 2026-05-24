using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Execution;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;

/// <summary>
/// RFC 0002 / #204 scaffolding. Shared xUnit helper for sub-issues #205–#212 that ship
/// "legacy tool + successor tool" pairs (e.g. <c>get_module_bytes</c> vs
/// <c>get_bytes(kind=module)</c>): runs both lambdas, serializes each result to JSON, and
/// asserts the two envelopes are structurally equal. Lets a sub-issue add a
/// one-line "the deprecated tool returns the same shape as the successor" assertion
/// without re-implementing JSON comparison in every test class.
/// </summary>
/// <remarks>
/// <para>"Structural equality" means: identical JSON value tree after both envelopes are
/// serialized with <see cref="JsonSerializerOptions.Default"/>. Caller-supplied
/// <see cref="CompatibilityIgnore"/> selectors can mask volatile fields (issued handle
/// IDs, expiration timestamps, etc.) before the diff.</para>
/// <para>The helper is deliberately envelope-agnostic — it works for
/// <c>DiagnosticResult&lt;T&gt;</c>, raw JSON payloads, or any other shape the sub-issue
/// wants to assert on.</para>
/// </remarks>
public static class CompatibilityEnvelopeAssert
{
    /// <summary>JSON path selectors to remove from BOTH envelopes before comparing.
    /// Each entry is a slash-delimited JSON path (e.g. <c>"data/handle"</c> or
    /// <c>"handleExpiresAt"</c>). Use to mask non-deterministic fields like
    /// issued handle IDs or wall-clock timestamps.</summary>
    public sealed record CompatibilityIgnore(IReadOnlyList<string> PathsToRemove)
    {
        public static CompatibilityIgnore None { get; } = new(Array.Empty<string>());
        public static CompatibilityIgnore Paths(params string[] paths) => new(paths);
    }

    /// <summary>Runs <paramref name="legacy"/> and <paramref name="successor"/>, then asserts
    /// their serialized JSON envelopes are structurally equal after applying
    /// <paramref name="ignore"/>. Throws an xUnit assertion failure on mismatch with a
    /// helpful diff in the message.</summary>
    public static async Task AssertEnvelopesEqualAsync<TLegacy, TSuccessor>(
        Func<Task<TLegacy>> legacy,
        Func<Task<TSuccessor>> successor,
        CompatibilityIgnore? ignore = null,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(successor);

        var legacyResult = await legacy().ConfigureAwait(false);
        var successorResult = await successor().ConfigureAwait(false);

        var options = serializerOptions ?? DefaultOptions;
        var legacyJson = Normalize(JsonSerializer.SerializeToNode(legacyResult, options), ignore);
        var successorJson = Normalize(JsonSerializer.SerializeToNode(successorResult, options), ignore);

        var legacyText = legacyJson?.ToJsonString(IndentedOptions) ?? "null";
        var successorText = successorJson?.ToJsonString(IndentedOptions) ?? "null";

        if (!JsonNode.DeepEquals(legacyJson, successorJson))
        {
            throw new AssertionFailedException(
                "Compatibility envelopes diverged.\n" +
                "--- legacy ---\n" + legacyText + "\n" +
                "--- successor ---\n" + successorText);
        }
    }

    private static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions IndentedOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static JsonNode? Normalize(JsonNode? node, CompatibilityIgnore? ignore)
    {
        if (node is null || ignore is null || ignore.PathsToRemove.Count == 0) return node;
        foreach (var path in ignore.PathsToRemove)
        {
            RemovePath(node, path);
        }
        return node;
    }

    private static void RemovePath(JsonNode root, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        RemovePathRecursive(root, parts, index: 0);
    }

    private static void RemovePathRecursive(JsonNode? cursor, string[] parts, int index)
    {
        while (cursor is not null && index < parts.Length - 1)
        {
            switch (cursor)
            {
                case JsonObject obj when obj.TryGetPropertyValue(parts[index], out var next):
                    cursor = next;
                    index++;
                    break;
                case JsonArray arr:
                    foreach (var element in arr)
                    {
                        RemovePathRecursive(element, parts, index);
                    }
                    return;
                default:
                    return;
            }
        }

        switch (cursor)
        {
            case JsonObject leaf:
                leaf.Remove(parts[^1]);
                break;
            case JsonArray arr:
                foreach (var element in arr)
                {
                    if (element is JsonObject elementObj)
                    {
                        elementObj.Remove(parts[^1]);
                    }
                }
                break;
        }
    }
}
