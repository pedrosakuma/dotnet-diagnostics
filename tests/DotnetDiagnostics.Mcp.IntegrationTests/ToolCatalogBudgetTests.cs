using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class ToolCatalogBudgetTests : IClassFixture<ToolCatalogBudgetTests.FullCatalogFactory>
{
    // Baseline measured 2026-07-15: 199,760 bytes. 220,000 leaves 10.1% headroom
    // for deliberate schema evolution without allowing catalog growth to go unnoticed.
    private const int MaximumCatalogBytes = 220_000;

    private readonly FullCatalogFactory _factory;
    private readonly ITestOutputHelper _output;

    public ToolCatalogBudgetTests(FullCatalogFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task FullCatalog_StaysWithinSerializedByteBudget()
    {
        await using var client = await ConnectAsync();
        var result = await client.ListToolsAsync(
            new ListToolsRequestParams(),
            cancellationToken: CancellationToken.None);

        var measurement = ToolCatalogMeasurement.Measure(result);
        _output.WriteLine(measurement.ToReport());

        measurement.Tools.Should().HaveCount(16,
            "the budget must measure the maximal shipping surface, including orchestrator and Azure discovery");
        measurement.SerializedBytes.Should().BeLessThanOrEqualTo(MaximumCatalogBytes,
            "tools/list growth consumes model context on every client catalog refresh; update the measurement report and justify any budget increase");
    }

    private async Task<McpClient> ConnectAsync()
    {
        var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", FullCatalogFactory.Token);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {FullCatalogFactory.Token}",
                },
            },
            httpClient,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(
            transport,
            cancellationToken: CancellationToken.None);
    }

    public sealed class FullCatalogFactory : WebApplicationFactory<DotnetDiagnostics.Mcp.Program>
    {
        public const string Token = "catalog-budget-test-token-do-not-use-in-prod";

        private readonly string? _previousToken;
        private readonly string? _previousOrchestratorEnabled;
        private readonly string? _previousAzureDiscoveryEnabled;

        public FullCatalogFactory()
        {
            _previousToken = Environment.GetEnvironmentVariable("MCP_BEARER_TOKEN");
            _previousOrchestratorEnabled = Environment.GetEnvironmentVariable("Orchestrator__Enabled");
            _previousAzureDiscoveryEnabled = Environment.GetEnvironmentVariable("AzureDiscovery__Enabled");

            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", Token);
            Environment.SetEnvironmentVariable("Orchestrator__Enabled", "true");
            Environment.SetEnvironmentVariable("AzureDiscovery__Enabled", "true");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", _previousToken);
            Environment.SetEnvironmentVariable("Orchestrator__Enabled", _previousOrchestratorEnabled);
            Environment.SetEnvironmentVariable("AzureDiscovery__Enabled", _previousAzureDiscoveryEnabled);
        }
    }
}

internal sealed record ToolCatalogMeasurement(
    int SerializedBytes,
    int EstimatedTokens,
    int DefaultCatalogBytes,
    int DefaultCatalogEstimatedTokens,
    int CatalogOverheadBytes,
    IReadOnlyList<ToolMeasurement> Tools)
{
    private static readonly HashSet<string> OptionalToolNames = new(StringComparer.Ordinal)
    {
        "attach_to_pod",
        "detach_from_pod",
        "list_orchestrator",
        "discover_azure",
    };

    public static ToolCatalogMeasurement Measure(ListToolsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(
            result,
            McpJsonUtilities.DefaultOptions).Length;
        var tools = result.Tools
            .Select(ToolMeasurement.Measure)
            .OrderByDescending(tool => tool.SerializedBytes)
            .ToArray();
        var catalogOverheadBytes = serializedBytes - tools.Sum(tool => tool.SerializedBytes);
        var defaultCatalogBytes = JsonSerializer.SerializeToUtf8Bytes(
            new ListToolsResult
            {
                Tools = result.Tools
                    .Where(tool => !OptionalToolNames.Contains(tool.Name))
                    .ToArray(),
            },
            McpJsonUtilities.DefaultOptions).Length;

        return new ToolCatalogMeasurement(
            serializedBytes,
            EstimateTokens(serializedBytes),
            defaultCatalogBytes,
            EstimateTokens(defaultCatalogBytes),
            catalogOverheadBytes,
            tools);
    }

    public string ToReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"tools/list result: {SerializedBytes:N0} bytes (~{EstimatedTokens:N0} tokens at 4 UTF-8 bytes/token)");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"default 12-tool subset: {DefaultCatalogBytes:N0} bytes (~{DefaultCatalogEstimatedTokens:N0} tokens)");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"catalog framing: {CatalogOverheadBytes:N0} bytes");
        builder.AppendLine(
            "tool | total bytes | input schema bytes* | output schema bytes* | " +
            "prose bytes | schema-structure bytes | other metadata bytes");
        foreach (var tool in Tools)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"{tool.Name} | {tool.SerializedBytes:N0} | {tool.InputSchemaBytes:N0} | " +
                $"{tool.OutputSchemaBytes:N0} | {tool.ProseBytes:N0} | " +
                $"{tool.SchemaStructureBytes:N0} | {tool.OtherMetadataBytes:N0}");
        }

        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"all tools | {Tools.Sum(tool => tool.SerializedBytes):N0} | " +
            $"{Tools.Sum(tool => tool.InputSchemaBytes):N0} | " +
            $"{Tools.Sum(tool => tool.OutputSchemaBytes):N0} | " +
            $"{Tools.Sum(tool => tool.ProseBytes):N0} | " +
            $"{Tools.Sum(tool => tool.SchemaStructureBytes):N0} | " +
            $"{Tools.Sum(tool => tool.OtherMetadataBytes):N0}");
        builder.AppendLine("* input/output schema byte columns include descriptions and therefore overlap the prose column");
        return builder.ToString();
    }

    private static int EstimateTokens(int bytes) => (bytes + 3) / 4;
}

internal sealed record ToolMeasurement(
    string Name,
    int SerializedBytes,
    int InputSchemaBytes,
    int OutputSchemaBytes,
    int ProseBytes,
    int SchemaStructureBytes,
    int OtherMetadataBytes)
{
    public static ToolMeasurement Measure(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(
            tool,
            McpJsonUtilities.DefaultOptions).Length;
        var full = JsonSerializer.SerializeToNode(
            tool,
            McpJsonUtilities.DefaultOptions)?.AsObject()
            ?? throw new InvalidOperationException($"Could not serialize tool '{tool.Name}'.");

        var nodeBytes = SerializedLength(full);
        if (nodeBytes != serializedBytes)
        {
            throw new InvalidOperationException(
                $"Tool '{tool.Name}' JSON-node serialization changed its byte length " +
                $"({nodeBytes} != {serializedBytes}); the catalog decomposition would no longer be exact.");
        }

        var withoutProse = full.DeepClone().AsObject();
        RemoveProseProperties(withoutProse);

        var withoutProseOrSchemas = withoutProse.DeepClone().AsObject();
        withoutProseOrSchemas.Remove("inputSchema");
        withoutProseOrSchemas.Remove("outputSchema");

        var withoutProseBytes = SerializedLength(withoutProse);
        var otherMetadataBytes = SerializedLength(withoutProseOrSchemas);

        return new ToolMeasurement(
            tool.Name,
            serializedBytes,
            InputSchemaBytes: SerializedLength(full["inputSchema"]!),
            OutputSchemaBytes: SerializedLength(full["outputSchema"]!),
            ProseBytes: serializedBytes - withoutProseBytes,
            SchemaStructureBytes: withoutProseBytes - otherMetadataBytes,
            OtherMetadataBytes: otherMetadataBytes);
    }

    private static int SerializedLength(JsonNode node)
        => Encoding.UTF8.GetByteCount(node.ToJsonString(McpJsonUtilities.DefaultOptions));

    private static void RemoveProseProperties(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("title");
            obj.Remove("description");
            foreach (var property in obj.ToArray())
            {
                if (property.Value is not null)
                {
                    RemoveProseProperties(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    RemoveProseProperties(item);
                }
            }
        }
    }
}
