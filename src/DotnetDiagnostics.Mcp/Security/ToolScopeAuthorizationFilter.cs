using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Security;

/// <summary>
/// MCP CallTool filter that enforces the <see cref="RequireScopeAttribute"/> /
/// <see cref="RequireAnyScopeAttribute"/> taxonomy from docs/authorization.md#scopes Runs before the
/// tool body; a scope miss short-circuits with a structured <c>"forbidden"</c> envelope
/// (per MCP spec — return a tool error result, never throw at the SDK).
/// </summary>
/// <remarks>
/// <para>Resolution order:
/// <list type="number">
///   <item><description>Look up the tool's <see cref="ToolScopeRegistry.Requirement"/>
///   in the index built at startup. Unknown tools deny (defense in depth).</description></item>
///   <item><description>Resolve the active principal via <see cref="IPrincipalAccessor"/>.
///   For HTTP, this is the principal stamped by <c>BearerTokenMiddleware</c>; for stdio
///   it is the synthetic root principal (docs/authorization.md#default-policy-by-transport).</description></item>
///   <item><description>Check the principal against the requirement. Wildcard (<c>root</c>
///   / <c>*</c>) scopes satisfy every gate — preserves the legacy
///   <c>MCP_BEARER_TOKEN</c> behavior byte-for-byte.</description></item>
/// </list>
/// </para>
/// <para>Audit logging is per-tool: allow at Information, deny at Warning,
/// neither carries the presented bearer value.</para>
/// </remarks>
internal static class ToolScopeAuthorizationFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(
        ToolScopeRegistry registry,
        Func<IPrincipalAccessor?> principalAccessor,
        Func<IServiceProvider?> servicesAccessor,
        Func<ILogger?> loggerAccessor)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(principalAccessor);
        ArgumentNullException.ThrowIfNull(servicesAccessor);
        ArgumentNullException.ThrowIfNull(loggerAccessor);

        return next => async (request, cancellationToken) =>
        {
            var toolName = request.Params?.Name;
            if (string.IsNullOrEmpty(toolName))
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            var requirement = registry.TryGet(toolName);
            if (requirement is null)
            {
                // Unknown tool — let the SDK produce its own not-found result; nothing to
                // authorize against.
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            if (HasCaseInsensitiveDuplicateKeys(request.Params?.Arguments))
            {
                loggerAccessor()?.LogWarning(
                    "Tool {Tool} denied because its arguments contain case-insensitive duplicate keys.",
                    toolName);
                return BuildInvalidArgumentsResult(
                    toolName,
                    "argument names must be unique ignoring case");
            }

            // Stdio (no IPrincipalAccessor registered) is treated identically to root.
            var accessor = principalAccessor();
            var principal = accessor?.Current ?? StdioRootPrincipalAccessor.Instance.Current;
            var services = servicesAccessor();
            var policies = ToolScopeResolutionPolicies.FromServices(services);
            var delegationFailure = string.Empty;
            BearerPrincipal? delegatedPrincipal = null;
            var delegationKey = (services?.GetService(typeof(ToolScopeDelegationKeyProvider))
                as ToolScopeDelegationKeyProvider)?.Key;
            var hasDelegation = request.Params?.Arguments?.ContainsKey(ToolScopeDelegation.ArgumentName) == true;
            if (!string.IsNullOrWhiteSpace(delegationKey) && !hasDelegation)
            {
                loggerAccessor()?.LogWarning(
                    "Tool {Tool} denied because the pod-local request had no internal scope delegation.",
                    toolName);
                return BuildDelegationForbiddenResult(
                    toolName,
                    "pod-local tool calls require an internal scope delegation");
            }
            if (hasDelegation)
            {
                ToolScopeDelegation.TryConsume(
                    request.Params!,
                    registry,
                    policies,
                    delegationKey,
                    services?.GetService(typeof(TimeProvider)) as TimeProvider,
                    out delegatedPrincipal,
                    out delegationFailure);
                if (delegatedPrincipal is null)
                {
                    loggerAccessor()?.LogWarning(
                        "Tool {Tool} denied because internal scope delegation validation failed: {Reason}.",
                        toolName,
                        delegationFailure);
                    return BuildDelegationForbiddenResult(toolName, delegationFailure);
                }
                principal = delegatedPrincipal;
            }

            var decision = registry.Authorize(
                toolName,
                request.Params?.Arguments,
                principal,
                proxyInvocation: delegatedPrincipal is not null,
                policies: policies);
            var logger = loggerAccessor();
            if (decision.IsAllowed)
            {
                logger?.LogDebug(
                    "Tool {Tool} authorized for principal {TokenName} (scopes {RequiredScopes}).",
                    toolName,
                    principal?.Name ?? "(none)",
                    FormatScopes(decision));
                var taskNeedsPrincipalSnapshot = request.Params?.Task is not null;
                if (delegatedPrincipal is null && !taskNeedsPrincipalSnapshot)
                {
                    return await next(request, cancellationToken).ConfigureAwait(false);
                }

                if (accessor is not HttpContextPrincipalAccessor httpAccessor)
                {
                    return delegatedPrincipal is not null
                        ? BuildDelegationForbiddenResult(
                            toolName,
                            "internal scope delegation requires an HTTP request context")
                        : await next(request, cancellationToken).ConfigureAwait(false);
                }

                using var delegationLease = httpAccessor.PushDelegation(principal!);
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            logger?.LogWarning(
                "Tool {Tool} denied for principal {TokenName} (missing scope {MissingScope}, presented {PrincipalScopes}).",
                toolName,
                principal?.Name ?? "(none)",
                decision.MissingScope,
                FormatPrincipalScopes(principal));

            return BuildForbiddenResult(toolName, decision, principal);
        };
    }

    private static bool HasCaseInsensitiveDuplicateKeys(
        IDictionary<string, System.Text.Json.JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            if (!names.Add(argument.Key) || HasCaseInsensitiveDuplicateKeys(argument.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCaseInsensitiveDuplicateKeys(System.Text.Json.JsonElement value)
    {
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasCaseInsensitiveDuplicateKeys(property.Value))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (HasCaseInsensitiveDuplicateKeys(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static CallToolResult BuildInvalidArgumentsResult(string toolName, string reason)
        => new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = $"invalid arguments: tool '{toolName}' {reason}.",
                },
            ],
        };

    private static CallToolResult BuildDelegationForbiddenResult(string toolName, string reason)
        => new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = $"forbidden: tool '{toolName}' received an invalid internal scope delegation.\n" +
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["error"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["kind"] = "forbidden",
                                ["message"] = reason,
                                ["tool"] = toolName,
                            },
                        }.ToJsonString(),
                },
            ],
        };

    internal readonly record struct AuthorizationDecision(bool IsAllowed, string MissingScope)
    {
        public static AuthorizationDecision Allow() => new(true, string.Empty);
        public static AuthorizationDecision Deny(string missing) => new(false, missing);
    }

    /// <summary>Exposed for unit tests — pure function over (requirement, principal).</summary>
    internal static AuthorizationDecision Authorize(
        ToolScopeRegistry.Requirement requirement,
        BearerPrincipal? principal)
    {
        if (principal is null)
        {
            // No principal => no scopes at all. Report the first required scope as the
            // missing one so the deny envelope is actionable.
            var first = requirement.Scopes.IsDefaultOrEmpty ? "<unknown>" : requirement.Scopes[0];
            return AuthorizationDecision.Deny(first);
        }

        if (requirement.IsAny)
        {
            foreach (var s in requirement.Any)
            {
                if (principal.HasScope(s)) return AuthorizationDecision.Allow();
            }
            // Report the first candidate as the representative miss; the envelope
            // surfaces the full list separately.
            return AuthorizationDecision.Deny(requirement.Any[0]);
        }

        foreach (var s in requirement.All)
        {
            if (!principal.HasScope(s)) return AuthorizationDecision.Deny(s);
        }
        return AuthorizationDecision.Allow();
    }

    internal static CallToolResult BuildForbiddenResult(
        string toolName,
        ToolScopeRegistry.AuthorizationResult authorization,
        BearerPrincipal? principal)
    {
        var presentedList = FormatPrincipalScopes(principal);
        var hasAnyOf = !authorization.AnyOfScopes.IsDefaultOrEmpty;
        var hasAllOf = !authorization.AllOfScopes.IsDefaultOrEmpty;
        var semantics = hasAnyOf && hasAllOf
            ? "any+all"
            : hasAnyOf ? "any" : "all";
        var sb = new StringBuilder();
        sb.Append("forbidden: tool '")
          .Append(toolName)
          .Append("' requires ");
        AppendRequirementSummary(sb, authorization);
        sb.Append("; principal '")
          .Append(principal?.Name ?? "(none)")
          .Append("' presented [")
          .Append(presentedList)
          .Append("].");

        // Structured payload mirrors the BearerTokenMiddleware 401 envelope shape so the
        // client has one error grammar to reason about. The bearer value is NEVER in here.
        var structured = new System.Text.Json.Nodes.JsonObject
        {
            ["error"] = new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "forbidden",
                ["message"] = BuildMissingScopeMessage(authorization),
                ["tool"] = toolName,
                ["required_scopes"] = new System.Text.Json.Nodes.JsonArray(
                    authorization.RequiredScopes.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()),
                ["any_of_scopes"] = new System.Text.Json.Nodes.JsonArray(
                    authorization.AnyOfScopes.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()),
                ["all_of_scopes"] = new System.Text.Json.Nodes.JsonArray(
                    authorization.AllOfScopes.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()),
                ["argument_scopes"] = new System.Text.Json.Nodes.JsonArray(
                    authorization.AdditionalScopes
                        .AddRange(authorization.ExplicitAdditionalScopes)
                        .Select(s => (System.Text.Json.Nodes.JsonNode?)s)
                        .ToArray()),
                ["modifier_scopes"] = new System.Text.Json.Nodes.JsonArray(
                    authorization.ModifierScopes.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()),
                ["principal_scopes"] = new System.Text.Json.Nodes.JsonArray(
                    (principal?.Scopes.OrderBy(s => s, StringComparer.Ordinal)
                                      .Select(s => (System.Text.Json.Nodes.JsonNode?)s)
                                      .ToArray())
                    ?? Array.Empty<System.Text.Json.Nodes.JsonNode?>()),
                ["semantics"] = semantics,
            },
        };

        // The MCP CallToolResult is intentionally text-content-only (same reasoning as
        // ToolErrorSurfaceFilter — strict clients validate structuredContent against the
        // tool's success-path output schema, so we keep the envelope in a text block).
        // The text payload is "<human summary>\n<json envelope>" so both human-readable
        // tooling and machine parsers (tests, the LLM itself) can pull the structured
        // form back out with a simple substring + JSON.Parse.
        sb.Append('\n').Append(structured.ToJsonString());

        return new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = sb.ToString() } },
        };
    }

    private static void AppendRequirementSummary(
        StringBuilder builder,
        ToolScopeRegistry.AuthorizationResult authorization)
    {
        if (!authorization.AnyOfScopes.IsDefaultOrEmpty)
        {
            builder.Append("any of [")
                .Append(string.Join(", ", authorization.AnyOfScopes))
                .Append(']');
        }

        if (!authorization.AnyOfScopes.IsDefaultOrEmpty &&
            !authorization.AllOfScopes.IsDefaultOrEmpty)
        {
            builder.Append(" and ");
        }

        if (!authorization.AllOfScopes.IsDefaultOrEmpty)
        {
            builder.Append("all of [")
                .Append(string.Join(", ", authorization.AllOfScopes))
                .Append(']');
        }
    }

    private static string BuildMissingScopeMessage(
        ToolScopeRegistry.AuthorizationResult authorization)
    {
        if (authorization.Primary.IsAny &&
            authorization.Primary.Any.Contains(authorization.MissingScope, StringComparer.Ordinal))
        {
            return $"tool requires any of scopes [{string.Join(", ", authorization.AnyOfScopes)}]";
        }

        return authorization.MissingExplicitScope
            ? $"tool requires literal modifier scope '{authorization.MissingScope}'"
            : $"tool requires mandatory scope '{authorization.MissingScope}'";
    }

    private static string FormatScopes(ToolScopeRegistry.Requirement requirement)
        => string.Join(", ", requirement.Scopes);

    private static string FormatScopes(ToolScopeRegistry.AuthorizationResult authorization)
        => string.Join(", ", authorization.RequiredScopes);

    private static string FormatPrincipalScopes(BearerPrincipal? principal)
    {
        if (principal is null) return "(none)";
        if (principal.Scopes.Count == 0) return "(empty)";
        return string.Join(", ", principal.Scopes.OrderBy(s => s, StringComparer.Ordinal));
    }
}
