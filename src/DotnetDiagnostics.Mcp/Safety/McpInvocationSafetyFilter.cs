using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Safety;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Protocol;
using DotnetDiagnostics.Mcp.Security;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Safety;

/// <summary>
/// Resolves safety from server-owned arguments, enforces proportional approval before
/// invocation, removes the reserved control argument before SDK binding, and annotates
/// structured results with the resolved descriptor.
/// </summary>
internal static class McpInvocationSafetyFilter
{
    internal const string ReservedArgumentName = "_dotnetDiagnostics";
    internal const string AcknowledgementPropertyName = "acknowledgement";
    private const string MetaKey = "dotnetDiagnostics";
    private const string ApproveField = "approve";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(
        Func<IDiagnosticHandleStore?> handlesAccessor,
        Func<ILogger?> loggerAccessor)
    {
        ArgumentNullException.ThrowIfNull(handlesAccessor);
        ArgumentNullException.ThrowIfNull(loggerAccessor);

        return next => (request, cancellationToken) => InvokeAsync(
            request.Params,
            request.Server,
            handlesAccessor(),
            ct => next(request, ct),
            loggerAccessor(),
            cancellationToken);
    }

    internal static async ValueTask<CallToolResult> InvokeAsync(
        CallToolRequestParams? parameters,
        McpServer? server,
        IDiagnosticHandleStore? handles,
        Func<CancellationToken, ValueTask<CallToolResult>> next,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var toolName = parameters?.Name;
        if (string.IsNullOrWhiteSpace(toolName)
            || !InvocationSafetyRegistry.TryGet(toolName, out _))
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }

        McpInvocationSafety.Assessment assessment;
        try
        {
            assessment = McpInvocationSafety.ResolveAssessment(toolName, parameters?.Arguments, handles);
        }
        catch (InvocationSafetyResolutionException ex)
        {
            logger?.LogDebug(
                ex,
                "Safety resolution deferred to argument validation for tool {Tool}.",
                toolName);
            RemoveReservedArgument(parameters);
            return await next(cancellationToken).ConfigureAwait(false);
        }

        var acknowledgement = ExtractAcknowledgement(parameters);
        var requiredAcknowledgement = BuildRequiredAcknowledgement(
            toolName,
            parameters?.Arguments,
            assessment);
        RemoveReservedArgument(parameters);

        if (IsMissingQueryHandle(toolName, parameters?.Arguments, handles))
        {
            var missingHandleResult = await next(cancellationToken).ConfigureAwait(false);
            return Decorate(missingHandleResult, assessment);
        }

        // collect_process_dump keeps its established PID/path-specific elicitation and
        // confirm=true fallback. This filter only adds the shared descriptor to its result.
        if (!string.Equals(toolName, DiagnosticOperationCatalog.CollectProcessDump, StringComparison.Ordinal))
        {
            var gate = await EnforceAsync(
                toolName,
                assessment,
                acknowledgement,
                requiredAcknowledgement,
                parameters,
                server,
                cancellationToken).ConfigureAwait(false);
            if (gate is not null)
            {
                logger?.LogInformation(
                    "Tool {Tool} stopped by safety gate {Status} at risk {RiskLevel}.",
                    toolName,
                    gate.Value.Status,
                    assessment.Safety.RiskLevel);
                return BuildApprovalResult(assessment, requiredAcknowledgement, gate.Value);
            }
        }

        var result = await next(cancellationToken).ConfigureAwait(false);
        return Decorate(result, assessment);
    }

    internal static CallToolResult Decorate(
        CallToolResult result,
        McpInvocationSafety.Assessment assessment)
    {
        if (result.StructuredContent is not { ValueKind: JsonValueKind.Object } structured)
        {
            return result;
        }

        var root = JsonNode.Parse(structured.GetRawText()) as JsonObject;
        if (root is null)
        {
            return result;
        }

        root["safety"] = JsonSerializer.SerializeToNode(assessment.Safety, SerializerOptions);
        if (assessment.Children.Count == 0)
        {
            root.Remove("childSafety");
        }
        else
        {
            root["childSafety"] = JsonSerializer.SerializeToNode(assessment.Children, SerializerOptions);
        }

        if (assessment.Safety.ApprovalPolicy == InvocationApprovalPolicy.Warn)
        {
            root["safetyWarnings"] = JsonSerializer.SerializeToNode(BuildWarnings(assessment.Safety), SerializerOptions);
        }
        else
        {
            root.Remove("safetyWarnings");
        }
        root.Remove("safetyApproval");

        result.StructuredContent = JsonSerializer.SerializeToElement(root, SerializerOptions);
        ReplaceJsonTextContent(result, root);
        return result;
    }

    private static async Task<GateResult?> EnforceAsync(
        string toolName,
        McpInvocationSafety.Assessment assessment,
        JsonNode? acknowledgement,
        InvocationSafetyAcknowledgement requiredAcknowledgement,
        CallToolRequestParams? parameters,
        McpServer? server,
        CancellationToken cancellationToken)
    {
        var safety = assessment.Safety;
        if (safety.ApprovalPolicy is InvocationApprovalPolicy.None or InvocationApprovalPolicy.Warn)
        {
            return null;
        }

        if (safety.ApprovalPolicy == InvocationApprovalPolicy.Acknowledge)
        {
            return Acknowledges(acknowledgement, requiredAcknowledgement)
                ? null
                : new GateResult(
                    InvocationSafetyApprovalStatus.AcknowledgementRequired,
                    $"Tool '{toolName}' requires acknowledgement of the exact resolved safety descriptor. " +
                    $"Retry with {ReservedArgumentName}.{AcknowledgementPropertyName} set to requiredAcknowledgement.",
                    IncludeAcknowledgement: true,
                    IsError: false);
        }

        if (server is not null && McpClientCapabilityMetadata.SupportsElicitation(server, parameters))
        {
            var outcome = await RequestHumanApprovalAsync(
                server,
                toolName,
                assessment,
                cancellationToken).ConfigureAwait(false);
            return outcome switch
            {
                ApprovalOutcome.Approved => null,
                ApprovalOutcome.Declined => new GateResult(
                    InvocationSafetyApprovalStatus.Declined,
                    $"Human approval for critical tool '{toolName}' was declined. No diagnostic side effect occurred.",
                    IncludeAcknowledgement: false,
                    IsError: false),
                _ => new GateResult(
                    InvocationSafetyApprovalStatus.Failed,
                    $"Human approval for critical tool '{toolName}' failed. No diagnostic side effect occurred; retry only after the elicitation channel is healthy.",
                    IncludeAcknowledgement: false,
                    IsError: true),
            };
        }

        return Acknowledges(acknowledgement, requiredAcknowledgement)
            ? null
            : new GateResult(
                InvocationSafetyApprovalStatus.HumanApprovalRequired,
                $"Critical tool '{toolName}' requires native MCP elicitation when available. " +
                $"This client did not advertise elicitation, so retry with {ReservedArgumentName}.{AcknowledgementPropertyName} " +
                "set to the exact requiredAcknowledgement descriptor.",
                IncludeAcknowledgement: true,
                IsError: false);
    }

    private static async Task<ApprovalOutcome> RequestHumanApprovalAsync(
        McpServer server,
        string toolName,
        McpInvocationSafety.Assessment assessment,
        CancellationToken cancellationToken)
    {
        var safety = assessment.Safety;
        var childText = assessment.Children.Count == 0
            ? string.Empty
            : $" Batch children: {string.Join("; ", assessment.Children.Select(static child =>
                $"{child.Operation}({FormatArguments(child.Arguments)}): {child.Safety.RiskLevel}"))}.";
        var request = new ElicitRequestParams
        {
            Message =
                $"Approve critical .NET diagnostics tool '{toolName}'? " +
                $"Target impact: {FormatValues(safety.TargetImpact)}. " +
                $"Data exposure: {FormatValues(safety.DataExposure)}. " +
                $"Side effects: {FormatValues(safety.SideEffects)}. " +
                $"{safety.Reason}{childText} " +
                $"Mitigations: {string.Join(" ", safety.Mitigations)}",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    [ApproveField] = new ElicitRequestParams.BooleanSchema
                    {
                        Title = $"Approve {toolName}",
                        Description = "Set true only after reviewing the resolved impact, exposure, side effects, and batch children.",
                        Default = false,
                    },
                },
                Required = [ApproveField],
            },
        };

        try
        {
            var result = await server.ElicitAsync(request, cancellationToken).ConfigureAwait(false);
            return result.IsAccepted
                && result.Content is not null
                && result.Content.TryGetValue(ApproveField, out var value)
                && value.ValueKind == JsonValueKind.True
                    ? ApprovalOutcome.Approved
                    : ApprovalOutcome.Declined;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ApprovalOutcome.Failed;
        }
    }

    private static bool Acknowledges(
        JsonNode? acknowledgement,
        InvocationSafetyAcknowledgement requiredAcknowledgement)
    {
        if (acknowledgement is null)
        {
            return false;
        }

        var expected = JsonSerializer.SerializeToNode(requiredAcknowledgement, SerializerOptions);
        return JsonNode.DeepEquals(expected, acknowledgement);
    }

    private static InvocationSafetyAcknowledgement BuildRequiredAcknowledgement(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        McpInvocationSafety.Assessment assessment)
    {
        var argumentNode = new JsonObject();
        if (arguments is not null)
        {
            foreach (var argument in arguments
                         .Where(static argument =>
                             !string.Equals(
                                 argument.Key,
                                 ReservedArgumentName,
                                 StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(
                                 argument.Key,
                                 ToolScopeDelegation.ArgumentName,
                                 StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(
                                 argument.Key,
                                 InvestigationRoutingArguments.InvestigationHandleIdArgument,
                                 StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(
                                 argument.Key,
                                 InvestigationRoutingArguments.InvestigationHandleIdsArgument,
                                 StringComparison.OrdinalIgnoreCase))
                         .OrderBy(static argument => argument.Key, StringComparer.Ordinal))
            {
                argumentNode[argument.Key] = JsonNode.Parse(argument.Value.GetRawText());
            }
        }

        return new InvocationSafetyAcknowledgement(
            toolName,
            JsonSerializer.SerializeToElement(argumentNode, SerializerOptions),
            assessment.Safety,
            assessment.Children.ToImmutableArray());
    }

    private static JsonNode? ExtractAcknowledgement(CallToolRequestParams? parameters)
    {
        if (TryGetArgument(parameters?.Arguments, ReservedArgumentName, out var reserved)
            && reserved.ValueKind == JsonValueKind.Object
            && TryGetProperty(reserved, AcknowledgementPropertyName, out var argumentAcknowledgement))
        {
            return JsonNode.Parse(argumentAcknowledgement.GetRawText());
        }

        var metaAcknowledgement = parameters?.Meta?[MetaKey]?[AcknowledgementPropertyName];
        return metaAcknowledgement?.DeepClone();
    }

    private static void RemoveReservedArgument(CallToolRequestParams? parameters)
    {
        if (parameters?.Arguments is null)
        {
            return;
        }

        var key = parameters.Arguments.Keys.FirstOrDefault(
            static name => string.Equals(name, ReservedArgumentName, StringComparison.OrdinalIgnoreCase));
        if (key is not null)
        {
            parameters.Arguments.Remove(key);
        }
    }

    private static CallToolResult BuildApprovalResult(
        McpInvocationSafety.Assessment assessment,
        InvocationSafetyAcknowledgement requiredAcknowledgement,
        GateResult gate)
    {
        var approval = new InvocationSafetyApproval(
            gate.Status,
            gate.Message,
            gate.IncludeAcknowledgement
                ? $"{ReservedArgumentName}.{AcknowledgementPropertyName}"
                : null,
            gate.IncludeAcknowledgement ? requiredAcknowledgement : null);
        var error = gate.IsError
            ? new DiagnosticError("ElicitationFailed", gate.Message)
            : null;
        var envelope = new DiagnosticResult<object>(
            gate.Message,
            Array.Empty<NextActionHint>(),
            error);
        var root = JsonSerializer.SerializeToNode(envelope, SerializerOptions)?.AsObject()
            ?? throw new InvalidOperationException("Failed to serialize safety approval preview.");
        root["safety"] = JsonSerializer.SerializeToNode(assessment.Safety, SerializerOptions);
        if (assessment.Children.Count > 0)
        {
            root["childSafety"] = JsonSerializer.SerializeToNode(assessment.Children, SerializerOptions);
        }
        root["safetyApproval"] = JsonSerializer.SerializeToNode(approval, SerializerOptions);
        return SerializeResult(root, gate.IsError);
    }

    private static CallToolResult SerializeResult(JsonObject root, bool isError)
    {
        var structured = JsonSerializer.SerializeToElement(root, SerializerOptions);
        return new CallToolResult
        {
            IsError = isError,
            StructuredContent = structured,
            Content =
            [
                new TextContentBlock { Text = structured.GetRawText() },
            ],
        };
    }

    private static IReadOnlyList<string> BuildWarnings(InvocationSafetyDescriptor safety)
        => [safety.Reason, .. safety.Mitigations];

    private static void ReplaceJsonTextContent(CallToolResult result, JsonObject root)
    {
        if (result.Content is null)
        {
            return;
        }

        var json = root.ToJsonString(SerializerOptions);
        for (var i = 0; i < result.Content.Count; i++)
        {
            if (result.Content[i] is not TextContentBlock text)
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(text.Text) is JsonObject)
                {
                    result.Content[i] = new TextContentBlock { Text = json };
                    return;
                }
            }
            catch (JsonException)
            {
                return;
            }
        }
    }

    private static bool TryGetArgument(
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

    private static bool IsMissingQueryHandle(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        IDiagnosticHandleStore? handles)
    {
        if (!string.Equals(toolName, DiagnosticOperationCatalog.QuerySnapshot, StringComparison.Ordinal)
            || handles is null
            || !TryGetArgument(arguments, "handle", out var handleElement)
            || handleElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(handleElement.GetString()))
        {
            return false;
        }

        return handles.LookupWithKind(handleElement.GetString()!.Trim()).Lookup is null;
    }

    private static bool TryGetProperty(JsonElement value, string name, out JsonElement propertyValue)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                propertyValue = property.Value;
                return true;
            }
        }

        propertyValue = default;
        return false;
    }

    private static string FormatValues<T>(IEnumerable<T> values)
        => string.Join(", ", values.Select(static value => value?.ToString() ?? string.Empty)) is { Length: > 0 } text
            ? text
            : "none";

    private static string FormatArguments(ImmutableDictionary<string, string> arguments)
        => arguments.Count == 0
            ? "default arguments"
            : string.Join(", ", arguments.Select(static pair => $"{pair.Key}={pair.Value}"));

    private readonly record struct GateResult(
        InvocationSafetyApprovalStatus Status,
        string Message,
        bool IncludeAcknowledgement,
        bool IsError);

    private enum ApprovalOutcome
    {
        Approved,
        Declined,
        Failed,
    }
}
