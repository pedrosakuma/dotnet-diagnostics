# Client setup

`dotnet-diagnostics-mcp` supports **two transports**:

- **`--stdio`** (recommended for local dev): the MCP client (Copilot CLI, Claude
  Desktop, Cursor, ...) spawns the server as a child process per session. No daemon,
  no bearer token, no ports — every `dotnet tool update` + client reload picks up
  the fresh binary automatically (see #74 for the motivation).
- **Streamable HTTP** at `POST /mcp` with `Authorization: Bearer <token-or-jwt>`
  (default; intended for Kubernetes sidecar / shared-server scenarios where one
  long-running server is consumed by multiple clients or pods).

This is the MCP server's **start here** doc: pick one transport track first, then follow the matching recipe.

## Start here

| Track | Choose this when | First steps | Continue with |
|---|---|---|---|
| **Option A — `--stdio`** | Local dev; your MCP client can spawn the server on demand | Point the client at `dotnet-diagnostics-mcp --stdio` | [Option A](#option-a--stdio-local-dev) |
| **Option B — Streamable HTTP** | Docker, Kubernetes, Windows service, or any shared long-running deployment | Start the server over HTTP, then configure a bearer token for the client | [Option B](#option-b--streamable-http-daemon-sidecar--shared-deploy), [Linux sidecar checklist](./consumer-install.md#14-linux-sidecar-checklist), [Local Docker sidecar](./local-docker-sidecar.md), [Kubernetes sidecar](../deploy/k8s/README.md#sidecar-topology-refresher) |

For permission-shaped failures on the HTTP track, make `inspect_process(view="preflight")` your first troubleshooting step before retrying a more expensive tool.

## 1. Run the server

### Option A — `--stdio` (local dev)

You don't run the server yourself. Point the MCP client at the binary and it will
spawn / tear down the process per session:

```jsonc
// ~/.copilot/mcp-config.json (Copilot CLI)
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "type": "stdio",
      "command": "dotnet-diagnostics-mcp",
      "args": ["--stdio"],
      "tools": ["*"]
    }
  }
}
```

```jsonc
// Claude Desktop / Cursor (claude_desktop_config.json shape)
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "command": "dotnet-diagnostics-mcp",
      "args": ["--stdio"]
    }
  }
}
```

### Option B — Streamable HTTP daemon (sidecar / shared deploy)

For the short HTTP quickstarts below, either set `MCP_BEARER_TOKEN=<your-token>` before startup or, if you intentionally leave it unset, copy the generated ephemeral token from the startup warning before configuring the client. For non-loopback deployments, prefer `Auth__BearerTokens__*` or OIDC as documented below.

For a **source checkout**, bind to loopback so that the non-loopback cleartext refusal does
not fire (see [Transport security](#transport-security-non-loopback) below for non-loopback options):

```bash
export Auth__BearerTokens__0__Name="local-observer"
export Auth__BearerTokens__0__Token="$(openssl rand -hex 32)"
export Auth__BearerTokens__0__Scopes__0="read-counters"
dotnet run --project src/DotnetDiagnostics.Mcp --urls http://127.0.0.1:8787
```

For an **installed global tool or self-contained binary**, bind to loopback:

```bash
export MCP_BEARER_TOKEN="$(openssl rand -hex 32)"  # or omit and copy the ephemeral token from the startup warning
dotnet-diagnostics-mcp --urls http://127.0.0.1:8787
```

For a **container**, the image sets `ASPNETCORE_URLS=http://0.0.0.0:8080` internally
(non-loopback cleartext). Use the local-dev recipe in
[`consumer-install.md` → § 1b](./consumer-install.md#1b-container)
(`-p 127.0.0.1:8787:8080` + `MCP_ALLOW_INSECURE_HTTP=true`) or configure production
TLS via [§ 1.6](./consumer-install.md#16-transport-security-for-non-loopback-listeners).

Sanity check:

```bash
curl -fsS http://127.0.0.1:8787/health
# {"status":"ok"}

# Auth accepted: GET lacks an MCP session, so expect an MCP 400 rather than 401.
curl -i http://127.0.0.1:8787/mcp \
  -H "Authorization: Bearer $MCP_BEARER_TOKEN"
```

## Transport security (non-loopback)

The server **refuses to start** when bound to a non-loopback address over cleartext HTTP
without a configured trusted TLS terminator or the explicit unsafe override. Three options:

### Direct Kestrel TLS via PEM secrets

Supply the certificate and private key as environment variables (no file on disk, safe for
ECS task definitions, Kubernetes Secrets, and docker-compose secrets):

```bash
# Auth: configure scoped bearer tokens (OIDC is preferred for production — see OIDC quickstart below)
export Auth__BearerTokens__0__Name="agent"
export Auth__BearerTokens__0__Token="$(openssl rand -hex 32)"
export Auth__BearerTokens__0__Scopes__0="read-counters"
export MCP_TLS_CERTIFICATE_PEM="$(cat /path/to/cert.pem)"
export MCP_TLS_PRIVATE_KEY_PEM="$(cat /path/to/key.pem)"
dotnet-diagnostics-mcp --urls https://0.0.0.0:8787
```

Both variables must be set together. Self-signed certificates are accepted for internal
sidecar topologies; use a CA-signed certificate when the MCP client connects from outside
the trust boundary. Add only the scopes required by the intended diagnostics; see
[`authorization.md`](./authorization.md).

> `MCP_BEARER_TOKEN` is accepted for non-loopback but emits a deprecation warning — a future
> release refuses it. Use `Auth:BearerTokens` (as above) or OIDC for non-loopback deployments.

### Behind a trusted TLS-terminating proxy

If nginx, Envoy, or a service mesh terminates TLS upstream, tell the server which proxy
IPs or CIDRs to trust for `X-Forwarded-Proto: https`. The server applies
`ForwardedHeaders` middleware and then enforces HTTPS for non-loopback traffic:

```bash
# Auth: configure Auth__BearerTokens__* or OIDC vars before starting (see Direct Kestrel TLS above)
# Single proxy IP:
export MCP_TRUSTED_PROXY_CIDRS="10.0.0.5"
# Or a CIDR block (comma or semicolon-separated):
export MCP_TRUSTED_PROXY_CIDRS="10.0.0.0/8,172.16.0.0/12"
dotnet-diagnostics-mcp --urls http://0.0.0.0:8787
```

The proxy must forward `X-Forwarded-Proto: https`; requests that resolve to cleartext after
header validation are rejected with `{"error":{"kind":"https_required",...}}` (400).
`/health` is always available for readiness probes regardless of scheme.

### Unsafe development override

For local multi-container topologies where TLS setup is impractical:

```bash
export MCP_ALLOW_INSECURE_HTTP=true   # development only — emits a prominent startup warning
dotnet-diagnostics-mcp --urls http://0.0.0.0:8787
```

Do **not** use `MCP_ALLOW_INSECURE_HTTP=true` in production. Bearer credentials travel in
cleartext and the server warns on every start.

## Safety-aware `tools/call`

After connectivity is established, inspect each tool's
`_meta.dotnetDiagnostics.safety` from `tools/list` and the resolved `safety`
descriptor in `tools/call` results:

- low-risk calls execute directly;
- moderate calls execute directly and return `safetyWarnings`;
- high-risk calls stop before side effects and return
  `safetyApproval.requiredAcknowledgement`;
- critical calls use MCP elicitation when the client advertises it and otherwise
  fail closed with an approval preview.

For a high-risk call, preserve the original arguments, copy the complete
server-returned acknowledgement into the reserved argument, and retry:

```text
preview = tools/call(name, arguments)
ack = preview.structuredContent.safetyApproval.requiredAcknowledgement

retryArguments = copy(arguments)
retryArguments["_dotnetDiagnostics"] = { "acknowledgement": ack }
result = tools/call(name, retryArguments)
```

Do not construct, edit, cache, or reuse the acknowledgement. It is bound to the
operation, concrete arguments, resolved descriptor, and batch children; changing
the request invalidates it. A bearer token with `root` or `*` scope authorizes
the tool but does not approve high/critical impact. See
[`authorization.md`](./authorization.md#per-call-confirmation) for the protocol
contract and [`production-safety.md`](./production-safety.md) for the canonical
matrix and evidence-handling policy.

## OIDC quickstart (HTTP transport)

Set these env vars on the server to enable JWT validation next to the existing opaque bearer tokens:

```bash
export MCP_OIDC_ISSUER="https://issuer.example.com"
export MCP_OIDC_AUDIENCE="dotnet-diagnostics-mcp"
# Optional: require extra issuer-specific claims.
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"azp":"diag-client"}'
```

Then send the access token exactly like the legacy bearer path:

```bash
curl -i http://127.0.0.1:8787/mcp \
  -H "Authorization: Bearer $ACCESS_TOKEN"
```

### Microsoft Entra ID

```bash
export MCP_OIDC_ISSUER="https://login.microsoftonline.com/<tenant-id>/v2.0"
export MCP_OIDC_AUDIENCE="api://dotnet-diagnostics-mcp"
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"azp":"<client-id>"}'
```

Use the app registration / managed identity to mint an access token for `api://dotnet-diagnostics-mcp`, and put your MCP scopes in the token's `scp` claim.

### AWS IAM Identity Center

```bash
export MCP_OIDC_ISSUER="https://oidc.<region>.amazonaws.com/id/<provider-id>"
export MCP_OIDC_AUDIENCE="dotnet-diagnostics-mcp"
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"client_id":"<application-arn-or-client-id>"}'
```

Map your permission set or trusted token issuer so the resulting JWT carries the MCP scopes in `scope` or `scp`.

### Keycloak

```bash
export MCP_OIDC_ISSUER="https://keycloak.example.com/realms/<realm>"
export MCP_OIDC_AUDIENCE="dotnet-diagnostics-mcp"
# Optional when Keycloak stores scopes in a custom claim instead of `scope` / `scp`.
export MCP_OIDC_SCOPE_CLAIM="scope"
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"azp":"dotnet-diagnostics-mcp-client"}'
```

Create a confidential client or service account, add the MCP scopes to its client scope mapping, and pass the resulting access token in the `Authorization` header.

## Managed / workload-identity recipes (HTTP transport)

The same OIDC/JWT path validates tokens minted from a **cloud platform identity** — no
static secret to distribute. The caller mints a short-lived OIDC token from its workload
identity; the sidecar validates it via standard OIDC metadata discovery and maps its
claims onto MCP scopes. Set the issuer/audience the platform stamps, then map the
caller's identity claim with `MCP_OIDC_REQUIRED_CLAIMS_JSON` (see
[`docs/authorization.md`](./authorization.md) for the claim→scope model).

> **Static bearer stays the loopback/local default.** Managed identity is additive and
> opt-in; the opaque bearer path keeps working alongside it (a "break-glass" token).

### Azure — Entra Workload Identity (AKS)

The federated pod identity mints a token for the sidecar's app-registration audience.

```bash
export MCP_OIDC_ISSUER="https://login.microsoftonline.com/<tenant-id>/v2.0"
export MCP_OIDC_AUDIENCE="api://dotnet-diagnostics-mcp"
# Pin the calling workload identity's app/client id.
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"azp":"<workload-identity-client-id>"}'
```

Client side: the AKS workload-identity webhook projects an `AZURE_FEDERATED_TOKEN_FILE`;
use `DefaultAzureCredential`/`WorkloadIdentityCredential` to acquire a token for
`api://dotnet-diagnostics-mcp/.default`, and put MCP scopes in the app role / `scp` claim.

### AWS — IRSA (IAM Roles for Service Accounts)

The EKS OIDC provider issues a projected token; validate it audience-scoped.

```bash
export MCP_OIDC_ISSUER="https://oidc.eks.<region>.amazonaws.com/id/<cluster-id>"
export MCP_OIDC_AUDIENCE="dotnet-diagnostics-mcp"
# Pin the calling ServiceAccount.
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"sub":"system:serviceaccount:<namespace>:<sa-name>"}'
# IRSA tokens carry no MCP scope claim — grant the scopes this identity may use.
export MCP_OIDC_GRANTED_SCOPES="read-counters eventpipe"
```

Client side: project a token with `audience: dotnet-diagnostics-mcp` (a
`serviceAccountToken` projected volume or `aws eks get-token`-style flow) and send it as
the bearer. If your issuing flow can stamp MCP scopes in a `scope`/`scp` claim, use
`MCP_OIDC_SCOPE_CLAIM` instead of (or alongside) `MCP_OIDC_GRANTED_SCOPES`.

### GCP — Workload Identity Federation

```bash
export MCP_OIDC_ISSUER="https://accounts.google.com"
export MCP_OIDC_AUDIENCE="dotnet-diagnostics-mcp"
# Pin the calling service account's unique id (sub) or email.
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"email":"<gsa>@<project>.iam.gserviceaccount.com"}'
```

Client side: mint a Google-signed ID token whose `aud` is `dotnet-diagnostics-mcp` from
the workload's service account and send it as the bearer.

### Kubernetes — projected ServiceAccount token (in-cluster client → sidecar)

For a same-cluster client authenticating to the sidecar with no cloud provider, use a
projected `ServiceAccount` token bound to an explicit audience. The cluster's OIDC issuer
serves the discovery document.

```bash
# Your cluster's issuer — kubectl get --raw /.well-known/openid-configuration | jq -r .issuer
export MCP_OIDC_ISSUER="https://kubernetes.default.svc.cluster.local"
export MCP_OIDC_AUDIENCE="dotnet-diagnostics-mcp"
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"sub":"system:serviceaccount:<namespace>:<sa-name>"}'
# A raw projected SA token carries no scp/scope claim, so grant the MCP scopes this pinned
# identity may use server-side (least-privilege; widen as the flow requires).
export MCP_OIDC_GRANTED_SCOPES="read-counters eventpipe heap-read investigation-export"
```

The client mounts a projected token volume (`audience: dotnet-diagnostics-mcp`,
`expirationSeconds: 3600`) and sends the file contents as the bearer. A ready-to-apply
example is [`deploy/k8s/projected-token-auth.yaml`](../deploy/k8s/projected-token-auth.yaml).

### Trusting more than one issuer at once

A single sidecar can accept tokens from **multiple** issuers — e.g. a cloud
workload-identity tenant *and* an in-cluster projected SA token issuer (handy for
break-glass or mixed clients). The legacy `MCP_OIDC_ISSUER`/`MCP_OIDC_AUDIENCE` define the
first issuer; add more via `MCP_OIDC_PROVIDERS_JSON` (a JSON array; each entry takes
`issuer`, `audience`, optional `scopeClaim`, optional `requiredClaims`):

```bash
export MCP_OIDC_ISSUER="https://login.microsoftonline.com/<tenant-id>/v2.0"
export MCP_OIDC_AUDIENCE="api://dotnet-diagnostics-mcp"
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"azp":"<workload-identity-client-id>"}'

export MCP_OIDC_PROVIDERS_JSON='[
  {
    "issuer": "https://kubernetes.default.svc.cluster.local",
    "audience": "dotnet-diagnostics-mcp",
    "requiredClaims": { "sub": "system:serviceaccount:diag:investigator" }
  }
]'
```

Each presented JWT is validated against every configured issuer in turn; the first issuer
whose signature, audience, and required claims all match wins. Tokens that match no
trusted issuer get the same `401 {"kind":"unauthenticated"}` envelope as a bad opaque
bearer.

## 2. Connect from the C# MCP SDK

The pattern used by our integration tests:

```csharp
using ModelContextProtocol.Client;

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://127.0.0.1:8787/mcp"),
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
    "inspect_process",
    arguments: new Dictionary<string, object?> { ["view"] = "list" });
```

See [`tests/DotnetDiagnostics.Mcp.IntegrationTests/McpToolsTests.cs`](../tests/DotnetDiagnostics.Mcp.IntegrationTests/McpToolsTests.cs)
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
      "url": "http://127.0.0.1:8787/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_TOKEN_HERE"
      }
    }
  }
}
```

For sidecar deployments, replace `127.0.0.1:8787` with the `kubectl port-forward`
target:

```bash
kubectl -n diagnosticsmcp-demo port-forward svc/sample-api-diagnosticsmcp 8787:8787
# then point the client at http://127.0.0.1:8787/mcp
```

## 4. Quick smoke test with `curl`

The MCP protocol is JSON-RPC over HTTP; the cheapest way to confirm the server
is reachable and the token is correct is to send an `initialize` request and
follow up with `tools/list`. This is tedious by hand — prefer one of the SDK
or GUI options above. Use `curl` only to verify network reachability and the
401 vs non-401 authentication boundary:

```bash
# 401 — wrong token, auth working
curl -i http://127.0.0.1:8787/mcp -H "Authorization: Bearer wrong"

# 400 from MCP is expected for this session-less GET — token accepted
curl -i http://127.0.0.1:8787/mcp -H "Authorization: Bearer $MCP_BEARER_TOKEN"
```

## Discoverability: how clients surface the server's guidance (#280)

The server talks to the LLM over four channels with very different reliability. Knowing which
ones a client honors tells you where the "start here" guidance must live.

| Channel | Push / Pull | Notes |
| --- | --- | --- |
| `ServerInfo.Description` | passive | Shown in server lists only. |
| `ServerInstructions` | push (advisory) | Injected into the system prompt **only if the client chooses to**. |
| Tool `[Description]` / `Title` | push (always) | The LLM always sees these in the tool list. The reliable surface. |
| Prompts (`diagnose-*`) | pull | Never auto-invoked — the human picks them from a slash menu. |
| Resources (`diag://guides/*`) | pull | Read only if the model decides to fetch them. |

**Finding — GitHub Copilot CLI.** Copilot CLI does **not** reliably inject MCP
`ServerInstructions` into the model context, and Prompts are pull-only (surfaced as slash
commands, never auto-invoked). The only guidance the model reliably sees is each tool's
`Title` + `[Description]`. Consequently the critical "where do I start for a slow app" path
**must be encoded redundantly in the entry-point tools' own descriptions** — it cannot depend
on `ServerInstructions` reaching the model.

**What we do about it.** `inspect_process` is the evidence-first entry point: its `Title` and
`[Description]` carry the intent-level trigger phrases humans actually use ("app is slow",
"high latency", "high CPU", "memory growing", "where do I start") and point at
`view="triage"` for a one-shot first look. `start_investigation` is the planner path (decision
tree) for non-trivial, multi-step investigations. A regression test
(`EntryPointTools_AdvertiseIntentLevelTriggerPhrases`) keeps those phrases from being edited
out. `ServerInstructions` still describes the same hierarchy for clients that do honor it.

## Operational tips

- **Rotate a scoped bearer** by changing its
  `Auth__BearerTokens__<index>__Token` value (or backing Secret), restarting the
  server, and updating clients. Keep the name and scope array stable unless the
  access change is intentional.
- **Set a fixed token** in production. The auto-generated ephemeral token is
  convenient for local dev but rotates on every restart.
- **TLS** supports direct Kestrel TLS via `MCP_TLS_CERTIFICATE_PEM` + `MCP_TLS_PRIVATE_KEY_PEM`,
  or a reverse proxy (nginx, Envoy, service mesh) configured via `MCP_TRUSTED_PROXY_CIDRS`.
  See [Transport security](#transport-security-non-loopback) for details.
- **Logs** are JSON-friendly via `SimpleConsole`; pipe stdout into your
  collector of choice.

## Long-running collectors: cutover to MCP-native progress and cancellation

Stage A of [issue #211](https://github.com/pedrosakuma/dotnet-diagnostics/issues/211)
adds MCP-native progress and cancellation to `collect_sample`, `collect_events`
and `inspect_heap`. Clients should stop using the legacy polling bridge as soon
as their MCP runtime supports `notifications/progress` + `notifications/cancelled`
on `tools/call`:

- **C# MCP SDK** (≥ `1.3.0`): pass an `IProgress<ProgressNotificationValue>` to
  `client.CallToolAsync(name, args, progress, cancellationToken)`. Cancellation
  flows through the `CancellationToken`.
- **TypeScript MCP SDK** (≥ `1.5.0`): set `_meta.progressToken` on the
  `tools/call` request and listen for `notifications/progress`. To cancel,
  abort the in-flight request (the SDK then sends an MCP
  `notifications/cancelled` whose `requestId` matches the original
  `tools/call` — **cancellation is request-scoped, not progress-token-scoped**).
- **Generic clients**: any MCP-spec-compliant client that handles
  `notifications/progress` works — the server emits progress on a ~1s cadence
  and a terminal `100%` on completion. When the server-side cancel handler
  wins the race, the call returns a structured envelope with `cancelled: true`;
  when the client transport closes first, the SDK typically surfaces the
  cancellation as an exception. Both are spec-conformant — render either as
  a "stopped" state.

Cutover plan:

1. Update your MCP client SDK to a version that emits a progress token on
   long-running `tools/call`, or — for spec-compliant clients — adopts
   MCP Tasks (`io.modelcontextprotocol/tasks` + `tasks/get` + `tasks/update` + `tasks/cancel`).
2. Either path is sufficient: progress + cancel notifications cover the
   in-request lifecycle, while MCP Tasks cover the detached-poll lifecycle.

> **Stage B ([issue #211](https://github.com/pedrosakuma/dotnet-diagnostics/issues/211)).**
> The legacy `collect_sample(kind="cpu")(runAsJob=true)` + `get_collection_status` +
> `cancel_collection` polling bridge has been removed. The tool surface
> dropped by two: clients that still depend on the polling path must adopt
> one of the two paths above before upgrading.

## MCP Tasks for long-running collectors

`collect_sample`, `collect_events` and `inspect_heap` can be promoted to
Tasks when the client opts into `io.modelcontextprotocol/tasks`, and the server advertises
that extension in `capabilities.extensions`. Clients that implement the
full MCP **Tasks** lifecycle can promote any of these calls to a detached task:

- **C# MCP SDK** (≥ `2.0.0`): `client.CallToolAsTaskAsync(...)`, then poll
  `tasks/get`; read the terminal `CallToolResult` from a `completed` response's
  `result` field, answer `input_required` polls with `tasks/update`, and cancel
  via `tasks/cancel`.
- **Generic clients**: opt into
  `params._meta.io.modelcontextprotocol/clientCapabilities.extensions["io.modelcontextprotocol/tasks"]`.

Tasks are **optional** — synchronous `tools/call` (with the in-request
progress/cancel notifications above) keeps working for clients that don't
implement the Tasks lifecycle.

## Human approval for `collect_process_dump` (MCP Elicitation)

`collect_process_dump` is the only destructive tool and requires explicit human
approval ([issue #425](https://github.com/pedrosakuma/dotnet-diagnostics/issues/425)).
How approval is requested depends on the capabilities your client negotiates at
`initialize`:

- **Elicitation-capable clients (preferred).** Advertise the `elicitation`
  capability and register an elicitation handler. The server **always** issues an
  `elicitation/create` request previewing the dump (PID, dump type, output path,
  disk-cost + heap-contents warning) with a single boolean `approve` field; the
  dump is written only when a human approves. A decline writes nothing — and
  `confirm=true` cannot bypass it.
  - **C# MCP SDK**: set `Capabilities.Elicitation = new ElicitationCapability()`
    and `Handlers.ElicitationHandler = (request, ct) => …` on `McpClientOptions`,
    returning an `ElicitResult { Action = "accept", Content = { ["approve"] = true } }`
    (or `Action = "decline"`) after surfacing the request to a human.
- **Clients without elicitation.** Fall back to the two-call `confirm=true`
  contract: the first call returns a `confirmation_required` preview (nothing
  written); after human approval, re-issue with `confirm=true`.

This is capability-gated and degrades gracefully — a client that advertises
neither elicitation nor sends `confirm=true` simply receives the
`confirmation_required` preview and writes nothing.
