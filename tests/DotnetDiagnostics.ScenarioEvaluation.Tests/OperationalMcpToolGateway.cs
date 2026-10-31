using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed class OperationalMcpToolGateway : IBlindedDiagnosticToolGateway, IAsyncDisposable
{
    private const int MaximumReturnedRows = 20;
    private readonly McpClient _client;
    private readonly AgentHarnessBudget _budget;
    private readonly OperationalMcpFamily _family;
    private readonly OperationalMcpArm _arm;
    private readonly OperationalMcpProjection _projection;
    private readonly int _maximumMcpPayloadBytes;
    private readonly int _processId;
    private readonly int _requiredCaptures;
    private readonly HashSet<string> _collectedHandles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _queriedHandles = new(StringComparer.Ordinal);
    private readonly List<OperationalMcpExchange> _exchanges = [];
    private int _captureSeconds;
    private int _artifactBytes;

    private OperationalMcpToolGateway(
        McpClient client,
        AgentHarnessBudget budget,
        OperationalMcpFamily family,
        OperationalMcpArm arm,
        OperationalMcpProjection projection,
        int processId,
        int requiredCaptures,
        int maximumMcpPayloadBytes,
        IReadOnlyList<AgentToolDefinition> tools,
        string catalogFingerprint,
        string toolDefinitionFingerprint)
    {
        _client = client;
        _budget = budget;
        _family = family;
        _arm = arm;
        _projection = projection;
        _processId = processId;
        _requiredCaptures = requiredCaptures;
        _maximumMcpPayloadBytes = maximumMcpPayloadBytes;
        Tools = tools;
        CatalogFingerprint = catalogFingerprint;
        ToolDefinitionFingerprint = toolDefinitionFingerprint;
    }

    public IReadOnlyList<AgentToolDefinition> Tools { get; }

    public int RetainedArtifactBytes => _artifactBytes;

    public string CatalogFingerprint { get; }

    public string ToolDefinitionFingerprint { get; }

    public IReadOnlyList<OperationalMcpExchange> Exchanges => _exchanges;

    public bool RequiredEvidenceSequenceCompleted
        => _collectedHandles.Count == _requiredCaptures
            && _queriedHandles.SetEquals(_collectedHandles);

    public static async Task<OperationalMcpToolGateway> StartAsync(
        int processId,
        AgentHarnessBudget budget,
        OperationalMcpFamily family,
        int requiredCaptures,
        OperationalMcpArm arm,
        OperationalMcpProtocol protocol,
        string artifactRoot,
        CancellationToken cancellationToken)
    {
        if (family is not (
            OperationalMcpFamily.AllocationReproducibility
            or OperationalMcpFamily.CpuHotspotCaptureProfiles))
        {
            throw new ArgumentOutOfRangeException(nameof(family));
        }

        var projection = protocol.Projections.Single(value => value.Family == family);
        Directory.CreateDirectory(artifactRoot);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [FindServerDll(), "--stdio"],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["MCP_ARTIFACT_ROOT"] = artifactRoot,
                ["Orchestrator__Enabled"] = "false",
                ["AzureDiscovery__Enabled"] = "false",
            },
        });
        var client = await McpClient.CreateAsync(
            transport,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var listed = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var selected = listed
                .Where(tool => tool.Name is "collect_sample" or "query_snapshot")
                .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .ToArray();
            if (selected.Length != 2)
            {
                throw new InvalidOperationException(
                    "The real MCP catalog did not contain collect_sample and query_snapshot.");
            }

            var catalog = selected.Select(tool => new
            {
                tool.Name,
                tool.Description,
                InputSchema = JsonNode.Parse(tool.JsonSchema.GetRawText()),
            }).ToArray();
            var catalogFingerprint = OperationalMcpProtocolLoader.Fingerprint(catalog);
            var definitions = CreateToolDefinitions(family, requiredCaptures, selected);
            var toolDefinitionFingerprint =
                OperationalMcpProtocolLoader.Fingerprint(definitions);
            var expectedToolDefinitionFingerprint = protocol.Inventory.Single(entry =>
                entry.Family == family).ExpectedToolDefinitionFingerprint;
            if (!string.Equals(
                    catalogFingerprint,
                    protocol.ExpectedCatalogFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The selected real MCP catalog fingerprint changed: expected {protocol.ExpectedCatalogFingerprint}, observed {catalogFingerprint}.");
            }

            if (!string.Equals(
                    toolDefinitionFingerprint,
                    expectedToolDefinitionFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The model-visible tool-definition fingerprint changed for {family}: expected {expectedToolDefinitionFingerprint}, observed {toolDefinitionFingerprint}.");
            }

            return new(
                client,
                budget,
                family,
                arm,
                projection,
                processId,
                requiredCaptures,
                protocol.Budgets.MaximumMcpPayloadBytes,
                definitions,
                catalogFingerprint,
                toolDefinitionFingerprint);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<(AgentToolResult Result, AgentApprovalEvent Approval)> ExecuteAsync(
        AgentToolCall call,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            call,
            OperationalMcpExchangeOrigin.FollowUp,
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<AgentPredeclaredEvidence>> CollectPredeclaredEvidenceAsync(
        OperationalMcpCase testCase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        if (testCase.Family != _family
            || testCase.RequiredCaptures != _requiredCaptures)
        {
            throw new ArgumentException(
                "The predeclared case does not match this operational MCP gateway.",
                nameof(testCase));
        }

        var evidence = new List<AgentPredeclaredEvidence>();
        var handles = new List<string>();
        for (var capture = 0; capture < _requiredCaptures; capture++)
        {
            var call = new AgentToolCall(
                $"predeclared-collect-{capture + 1}",
                "collect_sample",
                JsonSerializer.Serialize(new
                {
                    target = AgentScenarioTarget.AuthorizedTargetId,
                    kind = _family == OperationalMcpFamily.AllocationReproducibility
                        ? "allocation"
                        : "cpu",
                    durationSeconds = testCase.CaptureDurationSeconds,
                    topN = testCase.CaptureTopN,
                }, OperationalMcpJson.Options));
            var (result, approval) = await ExecuteAsync(
                call,
                OperationalMcpExchangeOrigin.Predeclared,
                cancellationToken).ConfigureAwait(false);
            evidence.Add(new(call, result, approval));
            if (!result.Succeeded)
            {
                return evidence;
            }

            using var response = JsonDocument.Parse(result.ContentJson);
            if (!response.RootElement.TryGetProperty("handle", out var handle)
                || handle.GetString() is not { Length: > 0 } handleValue)
            {
                throw new AgentPredeclaredEvidenceException(
                    "A successful predeclared collect_sample response returned no handle.");
            }

            handles.Add(handleValue);
        }

        if (_family == OperationalMcpFamily.AllocationReproducibility)
        {
            var call = new AgentToolCall(
                "predeclared-query-1",
                "query_snapshot",
                JsonSerializer.Serialize(new
                {
                    handle = handles[^1],
                    view = "allocation-reproducibility",
                    comparisonHandles = handles.Take(handles.Count - 1).ToArray(),
                    topN = testCase.QueryTopN,
                }, OperationalMcpJson.Options));
            var (result, approval) = await ExecuteAsync(
                call,
                OperationalMcpExchangeOrigin.Predeclared,
                cancellationToken).ConfigureAwait(false);
            evidence.Add(new(call, result, approval));
        }
        else
        {
            for (var index = 0; index < handles.Count; index++)
            {
                var call = new AgentToolCall(
                    $"predeclared-query-{index + 1}",
                    "query_snapshot",
                    JsonSerializer.Serialize(new
                    {
                        handle = handles[index],
                        view = "hotspot-variability",
                        topN = testCase.QueryTopN,
                    }, OperationalMcpJson.Options));
                var (result, approval) = await ExecuteAsync(
                    call,
                    OperationalMcpExchangeOrigin.Predeclared,
                    cancellationToken).ConfigureAwait(false);
                evidence.Add(new(call, result, approval));
            }
        }

        return evidence;
    }

    private async Task<(AgentToolResult Result, AgentApprovalEvent Approval)> ExecuteAsync(
        AgentToolCall call,
        OperationalMcpExchangeOrigin origin,
        CancellationToken cancellationToken)
    {
        try
        {
            using var parsed = JsonDocument.Parse(call.ArgumentsJson);
            var arguments = call.Name switch
            {
                "collect_sample" => CollectArguments(parsed.RootElement),
                "query_snapshot" => QueryArguments(parsed.RootElement),
                _ => null,
            };
            if (arguments is null)
            {
                return Failure(
                    call,
                    "tool_not_allowed",
                    "The requested tool is outside the frozen operational MCP allowlist.",
                    origin);
            }

            var result = await _client.CallToolAsync(
                call.Name,
                arguments,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var raw = ResponseJson(result);
            var projected = OperationalMcpResponseProjector.Apply(
                raw,
                _arm,
                _projection,
                out var didProject,
                out var projectedSchema);
            var rawBytes = Encoding.UTF8.GetByteCount(raw);
            var projectedBytes = Encoding.UTF8.GetByteCount(projected);
            if (Math.Max(rawBytes, projectedBytes) > _maximumMcpPayloadBytes
                || _artifactBytes + rawBytes + projectedBytes > _budget.MaximumArtifactBytes)
            {
                return Failure(
                    call,
                    "artifact_budget_exceeded",
                    "The exact MCP response plus model-visible projection exceeded the retained-artifact budget.",
                    origin);
            }

            _artifactBytes += rawBytes + projectedBytes;
            var errorCode = ErrorCode(result, raw);
            var succeeded = errorCode is null;
            if (succeeded && call.Name == "collect_sample")
            {
                using var response = JsonDocument.Parse(raw);
                if (response.RootElement.TryGetProperty("handle", out var handle)
                    && handle.GetString() is { Length: > 0 } value)
                {
                    _collectedHandles.Add(value);
                }
            }
            else if (succeeded && call.Name == "query_snapshot")
            {
                if (arguments.TryGetValue("handle", out var queried)
                    && queried is string queriedHandle)
                {
                    _queriedHandles.Add(queriedHandle);
                }

                if (arguments.TryGetValue("comparisonHandles", out var comparisons)
                    && comparisons is IEnumerable<string> comparisonHandles)
                {
                    _queriedHandles.UnionWith(comparisonHandles);
                }
            }
            _exchanges.Add(new OperationalMcpExchange(
                _exchanges.Count + 1,
                call.Name,
                JsonSerializer.Serialize(arguments, OperationalMcpJson.Options),
                raw,
                BlindedDiagnosticToolGateway.Sha256(raw),
                rawBytes,
                projected,
                BlindedDiagnosticToolGateway.Sha256(projected),
                projectedBytes,
                succeeded,
                didProject,
                projectedSchema,
                errorCode)
            {
                Origin = origin,
            });
            return (
                new AgentToolResult(
                    call.Id,
                    call.Name,
                    succeeded,
                    projected,
                    BlindedDiagnosticToolGateway.Sha256(projected),
                    projectedBytes,
                    false,
                    errorCode),
                Approval(
                    call,
                    executed: true,
                    detail: succeeded
                        ? "Executed through the repository's real stdio MCP server and protocol."
                        : "The real MCP server returned a structured failure."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentCaptureBudgetException exception)
        {
            return Failure(call, "capture_budget_exceeded", exception.Message, origin);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return Failure(call, "invalid_arguments", exception.Message, origin);
        }
        catch (AgentTargetBoundaryException exception)
        {
            return Failure(call, "target_not_authorized", exception.Message, origin);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(call, "environment_denied", exception.Message, origin);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or HttpRequestException
            or IOException
            or McpException)
        {
            return Failure(call, "mcp_transport_failed", exception.Message, origin);
        }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private Dictionary<string, object?> CollectArguments(JsonElement root)
    {
        RejectUnknown(root, "target", "kind", "durationSeconds", "topN");
        RequireTarget(root);
        var expectedKind = _family == OperationalMcpFamily.AllocationReproducibility
            ? "allocation"
            : "cpu";
        var kind = RequiredString(root, "kind");
        if (!string.Equals(kind, expectedKind, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"This frozen case permits collect_sample kind='{expectedKind}' only.");
        }

        var duration = RequiredInt(root, "durationSeconds", 1, 6);
        var topN = RequiredInt(root, "topN", 1, MaximumReturnedRows);
        if (_collectedHandles.Count >= _requiredCaptures)
        {
            throw new ArgumentException(
                $"The frozen case permits exactly {_requiredCaptures} fresh captures.");
        }

        ReserveCapture(duration);
        return new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["processId"] = _processId,
            ["durationSeconds"] = duration,
            ["topN"] = topN,
        };
    }

    private Dictionary<string, object?> QueryArguments(JsonElement root)
    {
        RejectUnknown(root, "handle", "view", "comparisonHandles", "topN");
        var handle = RequiredString(root, "handle");
        if (_collectedHandles.Count != _requiredCaptures)
        {
            throw new ArgumentException(
                $"query_snapshot requires all {_requiredCaptures} fresh captures to complete first.");
        }

        if (!_collectedHandles.Contains(handle))
        {
            throw new ArgumentException(
                "query_snapshot handle must come from this arm's real MCP collect_sample response.");
        }
        var expectedView = _family == OperationalMcpFamily.AllocationReproducibility
            ? "allocation-reproducibility"
            : "hotspot-variability";
        var view = RequiredString(root, "view");
        if (!string.Equals(view, expectedView, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"This frozen case permits query_snapshot view='{expectedView}' only.");
        }

        var arguments = new Dictionary<string, object?>
        {
            ["handle"] = handle,
            ["view"] = view,
        };
        if (root.TryGetProperty("topN", out var topNElement))
        {
            arguments["topN"] = RequiredInt(root, "topN", 1, 16);
        }

        if (root.TryGetProperty("comparisonHandles", out var comparisonElement))
        {
            if (_family != OperationalMcpFamily.AllocationReproducibility
                || comparisonElement.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException(
                    "comparisonHandles is supported only for allocation reproducibility.");
            }

            var handles = comparisonElement.EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            if (handles.Length is < 2 or > 15
                || handles.Any(string.IsNullOrWhiteSpace)
                || handles.Contains(handle, StringComparer.Ordinal)
                || handles.Any(value => !_collectedHandles.Contains(value!))
                || handles.Append(handle).Distinct(StringComparer.Ordinal).Count() != _requiredCaptures)
            {
                throw new ArgumentException(
                    $"comparisonHandles plus handle must contain exactly {_requiredCaptures} distinct handles collected in this arm.");
            }

            arguments["comparisonHandles"] = handles!;
        }
        else if (_family == OperationalMcpFamily.AllocationReproducibility)
        {
            throw new ArgumentException(
                "allocation-reproducibility requires comparisonHandles from separately started captures.");
        }

        return arguments;
    }

    private static IReadOnlyList<AgentToolDefinition> CreateToolDefinitions(
        OperationalMcpFamily family,
        int requiredCaptures,
        IReadOnlyList<McpClientTool> tools)
    {
        var collect = tools.Single(tool => tool.Name == "collect_sample");
        var query = tools.Single(tool => tool.Name == "query_snapshot");
        var kind = family == OperationalMcpFamily.AllocationReproducibility
            ? "allocation"
            : "cpu";
        var view = family == OperationalMcpFamily.AllocationReproducibility
            ? "allocation-reproducibility"
            : "hotspot-variability";
        var comparisonProperty = family == OperationalMcpFamily.AllocationReproducibility
            ? """
              ,"comparisonHandles":{"type":"array","minItems":2,"maxItems":15,"items":{"type":"string"}}
              """
            : string.Empty;
        var comparisonRequired = family == OperationalMcpFamily.AllocationReproducibility
            ? ",\"comparisonHandles\""
            : string.Empty;
        var queryGuidance = family == OperationalMcpFamily.AllocationReproducibility
            ? $"compare all {requiredCaptures} fresh handles in one call"
            : $"query every one of the {requiredCaptures} fresh handles before interpretation";
        return
        [
            new(
                collect.Name,
                $"{collect.Description} This frozen case permits kind='{kind}' and requires exactly {requiredCaptures} fresh separately started captures before interpretation.",
                Schema(
                    $$"""
                    {
                      "type":"object",
                      "additionalProperties":false,
                      "required":["target","kind","durationSeconds","topN"],
                      "properties":{
                        "target":{"type":"string","enum":["target-1"]},
                        "kind":{"type":"string","enum":["{{kind}}"]},
                        "durationSeconds":{"type":"integer","minimum":1,"maximum":6},
                        "topN":{"type":"integer","minimum":1,"maximum":20}
                      }
                    }
                    """)),
            new(
                query.Name,
                $"{query.Description} This frozen case permits view='{view}'; {queryGuidance}.",
                Schema(
                    $$"""
                    {
                      "type":"object",
                      "additionalProperties":false,
                      "required":["handle","view"{{comparisonRequired}}],
                      "properties":{
                        "handle":{"type":"string"},
                        "view":{"type":"string","enum":["{{view}}"]},
                        "topN":{"type":"integer","minimum":1,"maximum":16}
                        {{comparisonProperty}}
                      }
                    }
                    """)),
        ];
    }

    private static string FindServerDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new FileNotFoundException(
                "Could not locate the repository root for the real MCP subprocess.");
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var dll = Path.Combine(
            directory.FullName,
            "src",
            "DotnetDiagnostics.Mcp",
            "bin",
            configuration,
            framework,
            "DotnetDiagnostics.Mcp.dll");
        return File.Exists(dll)
            ? dll
            : throw new FileNotFoundException(
                "The real MCP subprocess must be built before the operational evaluation.",
                dll);
    }

    private static string ResponseJson(CallToolResult result)
        => result.StructuredContent?.GetRawText()
            ?? result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
            ?? throw new JsonException("The real MCP response contained no structured or text content.");

    private static string? ErrorCode(CallToolResult result, string response)
    {
        if (result.IsError != true)
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            return error.TryGetProperty("kind", out var kind) ? kind.GetString() : "mcp_error";
        }

        return "mcp_error";
    }

    private void ReserveCapture(int seconds)
    {
        if (_captureSeconds + seconds > _budget.MaximumCaptureSeconds)
        {
            throw new AgentCaptureBudgetException(
                $"The requested capture would exceed {_budget.MaximumCaptureSeconds} aggregate capture seconds.");
        }

        _captureSeconds += seconds;
    }

    private (AgentToolResult Result, AgentApprovalEvent Approval) Failure(
        AgentToolCall call,
        string code,
        string detail,
        OperationalMcpExchangeOrigin origin)
    {
        var content = new JsonObject
        {
            ["status"] = "failed",
            ["errorCode"] = code,
            ["detail"] = detail,
        }.ToJsonString();
        var bytes = Encoding.UTF8.GetByteCount(content);
        var hash = BlindedDiagnosticToolGateway.Sha256(content);
        _artifactBytes += bytes * 2;
        _exchanges.Add(new OperationalMcpExchange(
            _exchanges.Count + 1,
            call.Name,
            call.ArgumentsJson,
            content,
            hash,
            bytes,
            content,
            hash,
            bytes,
            false,
            false,
            null,
            code)
        {
            Origin = origin,
        });
        return (
            new AgentToolResult(
                call.Id,
                call.Name,
                false,
                content,
                hash,
                bytes,
                false,
                code),
            Approval(
                call,
                executed: false,
                detail: detail,
                decision: code is "tool_not_allowed" or "target_not_authorized"
                    ? "denied-unauthorized"
                    : "not-required-read-only"));
    }

    private static AgentApprovalEvent Approval(
        AgentToolCall call,
        bool executed,
        string detail,
        string decision = "not-required-read-only")
        => new(
            call.Id,
            call.Name,
            Requested: false,
            Decision: decision,
            Attempted: true,
            Executed: executed,
            Detail: detail);

    private static JsonObject Schema(string json)
        => JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Operational MCP tool schema was invalid.");

    private static void RequireTarget(JsonElement root)
    {
        if (!string.Equals(
                RequiredString(root, "target"),
                AgentScenarioTarget.AuthorizedTargetId,
                StringComparison.Ordinal))
        {
            throw new AgentTargetBoundaryException(
                "The requested target is outside the evaluator-owned boundary.");
        }
    }

    private static string RequiredString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new ArgumentException($"Argument '{name}' is required.");

    private static int RequiredInt(
        JsonElement root,
        string name,
        int minimum,
        int maximum)
        => root.TryGetProperty(name, out var value)
           && value.TryGetInt32(out var parsed)
           && parsed >= minimum
           && parsed <= maximum
            ? parsed
            : throw new ArgumentException(
                $"Argument '{name}' must be an integer in [{minimum}, {maximum}].");

    private static void RejectUnknown(JsonElement root, params string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be a JSON object.");
        }

        var names = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Contains(property.Name))
            {
                throw new ArgumentException($"Argument '{property.Name}' is not allowed.");
            }
        }
    }

    private sealed class AgentCaptureBudgetException(string message) : Exception(message);

    private sealed class AgentTargetBoundaryException(string message) : Exception(message);
}
