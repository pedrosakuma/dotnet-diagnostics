using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Security;

/// <summary>
/// Adds per-tool authorization metadata to <c>tools/list</c> responses so clients can see
/// the static scope requirement and whether the active bearer satisfies it before calling.
/// </summary>
internal static class ToolScopeListToolsFilter
{
    private const string DotnetDiagnosticsMetaKey = "dotnetDiagnostics";
    private const string AuthMetaKey = "auth";

    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> Create(
        ToolScopeRegistry registry,
        Func<IPrincipalAccessor?> principalAccessor)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(principalAccessor);

        return next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);

            var accessor = principalAccessor();
            var principal = accessor?.Current ?? StdioRootPrincipalAccessor.Instance.Current;

            var annotatedTools = new List<Tool>(result.Tools.Count);
            foreach (var tool in result.Tools)
            {
                var requirement = registry.TryGet(tool.Name);
                if (requirement is null)
                {
                    annotatedTools.Add(tool);
                    continue;
                }

                annotatedTools.Add(CloneWithScopeMetadata(tool, requirement.Value, principal));
            }

            result.Tools = annotatedTools;
            return result;
        };
    }

    private static Tool CloneWithScopeMetadata(
        Tool tool,
        ToolScopeRegistry.Requirement requirement,
        BearerPrincipal? principal)
    {
        var meta = tool.Meta?.DeepClone() as JsonObject ?? new JsonObject();
        var dotnetDiagnostics = meta[DotnetDiagnosticsMetaKey] as JsonObject;
        if (dotnetDiagnostics is null)
        {
            dotnetDiagnostics = new JsonObject();
            meta[DotnetDiagnosticsMetaKey] = dotnetDiagnostics;
        }

        var decision = ToolScopeAuthorizationFilter.Authorize(requirement, principal);
        dotnetDiagnostics[AuthMetaKey] = new JsonObject
        {
            ["requiredScopes"] = new JsonArray(requirement.Scopes.Select(s => (JsonNode?)s).ToArray()),
            ["semantics"] = requirement.IsAny ? "any" : "all",
            ["authorized"] = decision.IsAllowed,
        };

        return new Tool
        {
            Name = tool.Name,
            Title = tool.Title,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            OutputSchema = tool.OutputSchema,
            Annotations = tool.Annotations,
            Execution = tool.Execution,
            Icons = tool.Icons,
            Meta = meta,
        };
    }
}
