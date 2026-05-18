# Client setup

`dotnet-diagnostics-mcp` speaks MCP over **Streamable HTTP** at `POST /mcp`, with a
required `Authorization: Bearer <token>` header. Any MCP-aware client that
supports HTTP transports can drive it.

This doc covers the three most common ways to connect.

## 1. Run the server

```bash
export MCP_BEARER_TOKEN="$(openssl rand -hex 32)"
dotnet run --project src/DotnetDiagnosticsMcp.Server
# Server listens on http://localhost:5000 (or whatever ASP.NET picks)
```

Sanity check:

```bash
curl -fsS http://localhost:5000/health
# {"status":"ok"}

curl -fsS http://localhost:5000/mcp -H "Authorization: Bearer $MCP_BEARER_TOKEN"
```

## 2. Connect from the C# MCP SDK

The pattern used by our integration tests:

```csharp
using ModelContextProtocol.Client;

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5000/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp,
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {token}",
    },
});

await using var client = await McpClient.CreateAsync(transport);

var tools = await client.ListToolsAsync();
foreach (var tool in tools)
{
    Console.WriteLine($"{tool.Name}: {tool.Description}");
}

var processes = await client.CallToolAsync(
    "list_dotnet_processes",
    arguments: null);
```

See [`tests/DotnetDiagnosticsMcp.Server.IntegrationTests/McpToolsTests.cs`](../tests/DotnetDiagnosticsMcp.Server.IntegrationTests/McpToolsTests.cs)
for a full working example covering every tool.

## 3. Connect from Claude Desktop / a generic MCP client

Claude Desktop and other GUI clients typically read a JSON config block. The
exact field names depend on the host, but the shape for a Streamable HTTP
transport with bearer auth is:

```json
{
  "mcpServers": {
    "dotnet-dbg": {
      "transport": "streamable-http",
      "url": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_TOKEN_HERE"
      }
    }
  }
}
```

For sidecar deployments, replace `localhost:5000` with the `kubectl port-forward`
target:

```bash
kubectl -n diagnosticsmcp-demo port-forward svc/sample-api-diagnosticsmcp 8787:8787
# then point the client at http://localhost:8787/mcp
```

## 4. Quick smoke test with `curl`

The MCP protocol is JSON-RPC over HTTP; the cheapest way to confirm the server
is reachable and the token is correct is to send an `initialize` request and
follow up with `tools/list`. This is tedious by hand — prefer one of the SDK
or GUI options above. Use `curl` only to verify network reachability and the
401 vs 200 boundary:

```bash
# 401 — wrong token, auth working
curl -i http://localhost:5000/mcp -H "Authorization: Bearer wrong"

# 200 (or 4xx from MCP, not from auth) — token accepted
curl -i http://localhost:5000/mcp -H "Authorization: Bearer $MCP_BEARER_TOKEN"
```

## Operational tips

- **Rotate the token** by changing `MCP_BEARER_TOKEN` (or the Kubernetes
  Secret) and restarting the server.
- **Set a fixed token** in production. The auto-generated ephemeral token is
  convenient for local dev but rotates on every restart.
- **TLS termination** is not built in. Run behind a reverse proxy (nginx,
  Envoy) or a service mesh for TLS and additional access controls.
- **Logs** are JSON-friendly via `SimpleConsole`; pipe stdout into your
  collector of choice.
