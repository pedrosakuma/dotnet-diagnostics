using System.Text.Json.Nodes;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tasks;

internal static class McpTaskRequestMetadata
{
    private const string ClientCapabilitiesMetaKey = "io.modelcontextprotocol/clientCapabilities";
    private const string ExtensionsKey = "extensions";

    public static bool HasTasksExtension(CallToolRequestParams? request)
        => HasTasksExtension(request?.Meta);

    public static bool HasTasksExtension(RequestContext<CallToolRequestParams>? request)
        => HasTasksExtension(request?.Params?.Meta)
           || HasTasksExtension(request?.Server);

    public static bool HasTasksExtension(JsonObject? meta)
    {
        if (meta is null)
        {
            return false;
        }

        if (meta.ContainsKey(TasksProtocol.MetaRelatedTask))
        {
            return true;
        }

        if (meta[ClientCapabilitiesMetaKey] is not JsonObject clientCapabilities ||
            clientCapabilities[ExtensionsKey] is not JsonObject extensions)
        {
            return false;
        }

        return extensions.ContainsKey(TasksProtocol.ExtensionId);
    }

    public static bool HasTasksExtension(McpServer? server)
    {
        var extensions = server?.ClientCapabilities?.Extensions;
        return extensions is not null && extensions.ContainsKey(TasksProtocol.ExtensionId);
    }

    public static JsonObject? RemoveTasksExtension(JsonObject? meta)
    {
        if (!HasTasksExtension(meta))
        {
            return meta;
        }

        var clone = (JsonObject?)meta?.DeepClone();
        if (clone?[ClientCapabilitiesMetaKey] is not JsonObject clientCapabilities ||
            clientCapabilities[ExtensionsKey] is not JsonObject extensions)
        {
            return clone;
        }

        extensions.Remove(TasksProtocol.ExtensionId);
        if (extensions.Count == 0)
        {
            clientCapabilities.Remove(ExtensionsKey);
        }

        if (clientCapabilities.Count == 0)
        {
            clone.Remove(ClientCapabilitiesMetaKey);
        }

        return clone.Count == 0 ? null : clone;
    }
}
