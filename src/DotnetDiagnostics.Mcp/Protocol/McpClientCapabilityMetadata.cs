using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Protocol;

internal static class McpClientCapabilityMetadata
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static bool SupportsElicitation(McpServer? server, CallToolRequestParams? request)
        => server?.ClientCapabilities?.Elicitation is not null
           || TryGetClientCapabilities(request?.Meta)?.Elicitation is not null;

    public static bool SupportsElicitation(RequestContext<CallToolRequestParams>? request)
        => SupportsElicitation(request?.Server, request?.Params);

    public static ClientCapabilities? TryGetClientCapabilities(JsonObject? meta)
    {
        var capabilitiesNode = meta?[MetaKeys.ClientCapabilities];
        return capabilitiesNode?.Deserialize<ClientCapabilities>(SerializerOptions);
    }
}
