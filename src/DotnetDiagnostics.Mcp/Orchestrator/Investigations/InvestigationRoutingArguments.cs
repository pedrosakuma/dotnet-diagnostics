using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

internal static class InvestigationRoutingArguments
{
    public const string InvestigationHandleIdArgument = "investigationHandleId";
    public const string InvestigationHandleIdsArgument = "investigationHandleIds";

    public static string? TryGetExplicitHandleId(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(InvestigationHandleIdArgument, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()
            : null;
    }

    public static IReadOnlyList<string>? TryGetExplicitHandleIds(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        if (arguments.TryGetValue(InvestigationHandleIdsArgument, out var many) &&
            many.ValueKind == JsonValueKind.Array)
        {
            var handles = many.EnumerateArray()
                .Where(static e => e.ValueKind == JsonValueKind.String)
                .Select(static e => e.GetString())
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .Distinct(System.StringComparer.Ordinal)
                .ToArray();
            return handles.Length == 0 ? null : handles;
        }

        var single = TryGetExplicitHandleId(arguments);
        return single is null ? null : new[] { single };
    }

    public static CallToolRequestParams? StripRoutingArguments(CallToolRequestParams? requestParams)
    {
        if (requestParams?.Arguments is null || requestParams.Arguments.Count == 0)
        {
            return requestParams;
        }

        if (!requestParams.Arguments.ContainsKey(InvestigationHandleIdArgument) &&
            !requestParams.Arguments.ContainsKey(InvestigationHandleIdsArgument))
        {
            return requestParams;
        }

        var cleaned = new Dictionary<string, JsonElement>(requestParams.Arguments, System.StringComparer.Ordinal);
        cleaned.Remove(InvestigationHandleIdArgument);
        cleaned.Remove(InvestigationHandleIdsArgument);
        return new CallToolRequestParams
        {
            Name = requestParams.Name,
            Arguments = cleaned,
        };
    }
}
