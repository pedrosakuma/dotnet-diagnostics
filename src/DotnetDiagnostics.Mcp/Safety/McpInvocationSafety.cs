using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Safety;

namespace DotnetDiagnostics.Mcp.Safety;

/// <summary>
/// Normalizes MCP JSON arguments into the transport-neutral Core safety request.
/// Prompting and authorization remain separate concerns.
/// </summary>
internal static class McpInvocationSafety
{
    internal sealed record Assessment(
        InvocationSafetyDescriptor Safety,
        IReadOnlyList<InvocationSafetyChildDescriptor> Children);

    internal static InvocationSafetyDescriptor Resolve(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        IDiagnosticHandleStore? handles = null)
        => ResolveAssessment(toolName, arguments, handles).Safety;

    internal static Assessment ResolveAssessment(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        IDiagnosticHandleStore? handles = null)
    {
        var request = CreateRequest(toolName, arguments, handles);
        var children = request.Children
            .Select(static child => new InvocationSafetyChildDescriptor(
                child.Operation,
                child.Arguments,
                InvocationSafetyResolver.Resolve(child)))
            .ToArray();
        return new Assessment(InvocationSafetyResolver.Resolve(request), children);
    }

    internal static InvocationSafetyRequest CreateRequest(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        IDiagnosticHandleStore? handles = null)
    {
        var normalized = NormalizeArguments(arguments).ToList();
        NormalizeQueryHandleKind(toolName, arguments, handles, normalized);
        if (!string.Equals(toolName, DiagnosticOperationCatalog.CollectBatch, StringComparison.Ordinal))
        {
            return new InvocationSafetyRequest(toolName, normalized);
        }

        var children = new List<InvocationSafetyRequest>();
        if (TryGet(arguments, "requests", out var requests) && requests.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in requests.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object)
                {
                    throw new InvocationSafetyResolutionException(
                        toolName,
                        "collect_batch contains a non-object child; defer to tool validation.");
                }

                var operation = GetString(child, "tool");
                var kind = GetString(child, "kind");
                if (operation is null || kind is null)
                {
                    throw new InvocationSafetyResolutionException(
                        toolName,
                        "collect_batch contains a child without tool/kind; defer to tool validation.");
                }

                var valid = operation switch
                {
                    DiagnosticOperationCatalog.CollectSample =>
                        DiagnosticOperationCatalog.CollectSampleKinds.All.Contains(
                            kind,
                            StringComparer.OrdinalIgnoreCase)
                        && !string.Equals(
                            kind,
                            DiagnosticOperationCatalog.CollectSampleKinds.MethodParameters,
                            StringComparison.OrdinalIgnoreCase),
                    DiagnosticOperationCatalog.CollectEvents =>
                        DiagnosticOperationCatalog.CollectEventsKinds.All.Contains(
                            kind,
                            StringComparer.OrdinalIgnoreCase)
                        && !string.Equals(
                            kind,
                            DiagnosticOperationCatalog.CollectEventsKinds.Sweep,
                            StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };
                if (!valid)
                {
                    throw new InvocationSafetyResolutionException(
                        toolName,
                        $"collect_batch child '{operation}/{kind}' is not eligible; defer to tool validation.");
                }

                children.Add(InvocationSafetyRequest.Create(operation, ("kind", kind)));
            }
        }

        return new InvocationSafetyRequest(toolName, normalized, children);
    }

    private static void NormalizeQueryHandleKind(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        IDiagnosticHandleStore? handles,
        List<KeyValuePair<string, string?>> normalized)
    {
        if (!string.Equals(toolName, DiagnosticOperationCatalog.QuerySnapshot, StringComparison.Ordinal))
        {
            return;
        }

        normalized.RemoveAll(static pair =>
            string.Equals(pair.Key, "handleKind", StringComparison.OrdinalIgnoreCase));
        if (handles is null
            || !TryGet(arguments, "handle", out var handleElement)
            || handleElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(handleElement.GetString()))
        {
            return;
        }

        var handle = handleElement.GetString()!.Trim();
        if (handles.LookupWithKind(handle).Lookup is { } lookup)
        {
            normalized.Add(KeyValuePair.Create<string, string?>("handleKind", lookup.Kind));
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> NormalizeArguments(
        IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            yield break;
        }

        foreach (var argument in arguments)
        {
            var value = argument.Value.ValueKind switch
            {
                JsonValueKind.String => argument.Value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => argument.Value.TryGetInt64(out var integer)
                    ? integer.ToString(CultureInfo.InvariantCulture)
                    : argument.Value.GetDouble().ToString(CultureInfo.InvariantCulture),
                JsonValueKind.Object or JsonValueKind.Array => "present",
                _ => null,
            };
            yield return KeyValuePair.Create(argument.Key, value);
        }
    }

    private static string? GetString(JsonElement value, string name)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString()?.Trim();
            }
        }

        return null;
    }

    private static bool TryGet(
        IDictionary<string, JsonElement>? arguments,
        string name,
        out JsonElement value)
    {
        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                if (string.Equals(argument.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = argument.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
