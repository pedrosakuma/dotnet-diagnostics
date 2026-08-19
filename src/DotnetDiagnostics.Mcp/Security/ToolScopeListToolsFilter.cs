using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Safety;
using DotnetDiagnostics.Mcp.Safety;
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
    private const string SafetyMetaKey = "safety";
    private const string SafetyGuidance =
        " Inspect `_meta.dotnetDiagnostics.safety` before escalating; conditional calls return resolved `safety` and an exact acknowledgement preview when approval is required.";

    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> Create(
        ToolScopeRegistry registry,
        Func<IPrincipalAccessor?> principalAccessor,
        Func<bool>? delegationRequired = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(principalAccessor);
        delegationRequired ??= static () => false;

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

                annotatedTools.Add(CloneWithScopeMetadata(
                    tool,
                    requirement.Value,
                    principal,
                    delegationRequired()));
            }

            result.Tools = annotatedTools;
            return result;
        };
    }

    private static Tool CloneWithScopeMetadata(
        Tool tool,
        ToolScopeRegistry.Requirement requirement,
        BearerPrincipal? principal,
        bool delegationRequired)
    {
        var meta = tool.Meta?.DeepClone() as JsonObject ?? new JsonObject();
        var dotnetDiagnostics = meta[DotnetDiagnosticsMetaKey] as JsonObject;
        if (dotnetDiagnostics is null)
        {
            dotnetDiagnostics = new JsonObject();
            meta[DotnetDiagnosticsMetaKey] = dotnetDiagnostics;
        }

        var decision = ToolScopeAuthorizationFilter.Authorize(requirement, principal);
        var invocation = ToolInvocationScopeResolver.ResolveCatalog(tool.Name);
        var requiredExplicitScopes = invocation.ExplicitAdditionalScopes
            .AddRange(invocation.ExplicitModifierScopes)
            .Distinct()
            .ToArray();
        var explicitScopesAllowed = principal is not null &&
            requiredExplicitScopes.All(principal.HasExplicitScope);
        dotnetDiagnostics[AuthMetaKey] = new JsonObject
        {
            ["requiredScopes"] = new JsonArray(requirement.Scopes.Select(s => (JsonNode?)s).ToArray()),
            ["requiredExplicitScopes"] = new JsonArray(
                requiredExplicitScopes.Select(scope => (JsonNode?)scope).ToArray()),
            ["semantics"] = requirement.IsAny ? "any" : "all",
            ["hasConditionalArgumentScopes"] = invocation.HasConditionalArgumentScopes,
            ["delegationRequired"] = delegationRequired,
            ["authorized"] = !delegationRequired && decision.IsAllowed && explicitScopesAllowed,
        };

        var safety = InvocationSafetyRegistry.Get(tool.Name);
        dotnetDiagnostics[SafetyMetaKey] = System.Text.Json.JsonSerializer.SerializeToNode(
            safety.MaximumSafety);
        dotnetDiagnostics["hasConditionalSafety"] = safety.HasConditionalSafety;

        return new Tool
        {
            Name = tool.Name,
            Title = tool.Title,
            Description = AppendSafetyGuidance(tool.Description),
            InputSchema = AddSafetyAcknowledgementSchema(tool.InputSchema, safety),
            OutputSchema = AddSafetyResultSchema(tool.OutputSchema),
            Annotations = tool.Annotations,
            Icons = tool.Icons,
            Meta = meta,
        };
    }

    private static string AppendSafetyGuidance(string? description)
        => string.IsNullOrWhiteSpace(description)
            ? SafetyGuidance.TrimStart()
            : description.EndsWith(SafetyGuidance, StringComparison.Ordinal)
                ? description
                : description + SafetyGuidance;

    private static System.Text.Json.JsonElement AddSafetyAcknowledgementSchema(
        System.Text.Json.JsonElement inputSchema,
        InvocationSafetyRegistration safety)
    {
        if (safety.MaximumSafety.RiskLevel < InvocationRiskLevel.High
            || string.Equals(
                safety.Operation,
                DiagnosticOperationCatalog.CollectProcessDump,
                StringComparison.Ordinal))
        {
            return inputSchema;
        }

        var root = JsonNode.Parse(inputSchema.GetRawText()) as JsonObject ?? new JsonObject();
        if (root["properties"] is not JsonObject properties)
        {
            properties = new JsonObject();
            root["properties"] = properties;
        }

        properties[McpInvocationSafetyFilter.ReservedArgumentName] = new JsonObject
        {
            ["type"] = "object",
            ["description"] =
                "Reserved safety control. Supply acknowledgement exactly as returned by a safetyApproval preview; the server removes it before tool binding and never trusts it as a descriptor.",
            ["properties"] = new JsonObject
            {
                [McpInvocationSafetyFilter.AcknowledgementPropertyName] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Exact request-bound requiredAcknowledgement object from the immediately reviewed concrete-call preview.",
                },
            },
            ["additionalProperties"] = false,
        };

        return System.Text.Json.JsonSerializer.SerializeToElement(root);
    }

    private static System.Text.Json.JsonElement? AddSafetyResultSchema(
        System.Text.Json.JsonElement? outputSchema)
    {
        if (outputSchema is null)
        {
            return null;
        }

        var root = JsonNode.Parse(outputSchema.Value.GetRawText()) as JsonObject ?? new JsonObject();
        if (root["properties"] is not JsonObject properties)
        {
            properties = new JsonObject();
            root["properties"] = properties;
        }

        properties["safety"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Server-resolved InvocationSafetyDescriptor for this concrete call.",
        };
        properties["childSafety"] = new JsonObject
        {
            ["type"] = "array",
            ["description"] = "Resolved child descriptors for composite calls; the parent safety is their merged maximum.",
            ["items"] = new JsonObject { ["type"] = "object" },
        };
        properties["safetyWarnings"] = new JsonObject
        {
            ["type"] = "array",
            ["description"] = "Warnings for moderate calls; absent for low/high/critical calls.",
            ["items"] = new JsonObject { ["type"] = "string" },
        };
        properties["safetyApproval"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Approval preview/status, including the exact descriptor acknowledgement when fallback is allowed.",
        };

        return System.Text.Json.JsonSerializer.SerializeToElement(root);
    }
}
