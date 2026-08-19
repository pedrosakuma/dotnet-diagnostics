using System.Text.Json.Nodes;
using DotnetDiagnostics.Mcp.Tasks;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

internal static class TasksExtensionTestSupport
{
    private const string ClientCapabilitiesMetaKey = "io.modelcontextprotocol/clientCapabilities";
    private const string ExtensionsKey = "extensions";

    public static JsonObject WithTasksExtension(JsonObject? meta = null)
    {
        var clone = (JsonObject?)(meta?.DeepClone()) ?? [];
        var clientCapabilities = clone[ClientCapabilitiesMetaKey] as JsonObject ?? [];
        clone[ClientCapabilitiesMetaKey] = clientCapabilities;
        var extensions = clientCapabilities[ExtensionsKey] as JsonObject ?? [];
        clientCapabilities[ExtensionsKey] = extensions;
        extensions[TasksProtocol.ExtensionId] = new JsonObject();
        return clone;
    }

    public static void EnableTasks(CallToolRequestParams request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Meta = WithTasksExtension(request.Meta);
    }

    public static bool HasTasks(CallToolRequestParams? request)
        => McpTaskRequestMetadata.HasTasksExtension(request);
}
