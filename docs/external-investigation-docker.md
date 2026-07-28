# External-investigation Docker passthrough

_Issue: [#712](https://github.com/pedrosakuma/dotnet-diagnostics/issues/712) — proves the external-investigation flow against a real local Docker target and sidecar._

## What this topology is

The external-investigation workflow runs **two** diagnostics-MCP instances:

| Role | What it does | Who connects to it |
|---|---|---|
| **Sidecar MCP** | Shares the target's PID namespace and `/tmp`; performs the actual EventPipe / ClrMD work. | Central MCP only. |
| **Central (orchestrator) MCP** | Orchestrator-only; no shared PID namespace. Exposes a named external-MCP profile pointing at the sidecar. Clients always connect here. | MCP client (LLM, curl, test). |

The client calls `attach_to_pod(profileName="sidecar")` on the central MCP. After the handle becomes Active, all subsequent diagnostic tool calls that carry the returned `investigationHandleId` are forwarded by the central to the sidecar automatically — the client never needs to know the sidecar URL or bearer token.

## Architecture diagram

```
╔═══════════════════════════════════════════════════════╗
║  Docker PID namespace (pid-anchor owns PID 1)         ║
║                                                       ║
║   ┌─────────────────────┐   /tmp (shared volume)      ║
║   │  CoreClrSample      │◄─────────────────┐          ║
║   │  (target app)       │   diagnostic     │          ║
║   │  http://…:18080     │   socket         │          ║
║   └─────────────────────┘                  │          ║
║                                            │          ║
║   ┌─────────────────────────────────┐      │          ║
║   │  Sidecar MCP                    │──────┘          ║
║   │  bearer: sidecar-dev-token      │                 ║
║   │  DOTNET_EnableDiagnostics=0     │  (no host port) ║
║   └─────────────────────────────────┘                 ║
╚═══════════════════════════════════════════════════════╝
                            ▲ http://sidecar:8080/mcp
                            │ (Docker bridge network)
╔═══════════════════════════╪═══════════════════════════╗
║  Central (orchestrator)   │                           ║
║  bearer: central-dev-token│                           ║
║  external profile 'sidecar' ──────────────────────────┘
║  http://127.0.0.1:18890   (host port)                 ║
╚═══════════════════════════════════════════════════════╝
                            ▲
                    MCP client (curl / test)
```

## Bearer-token ownership

| Token | Who owns it | Who sees it |
|---|---|---|
| `central-dev-token` | The MCP client (LLM / human / test) | Client headers to central only |
| `sidecar-dev-token` | The operator (configured in the central) | Central ↔ sidecar only; never returned to the client |

The central's `Orchestrator:ExternalMcpProfiles:sidecar:BearerToken` is marked `[JsonIgnore]`
so the sidecar bearer is never serialised into investigation handles, log messages, or error
responses visible to the caller.

## Port assignments

| Service | Host port | Description |
|---|---|---|
| target (CoreClrSample) | `127.0.0.1:18080` | Target app HTTP |
| central MCP | `127.0.0.1:18890` | Central orchestrator MCP — the only external port |
| sidecar MCP | _(none)_ | Internal to the Docker bridge network only |

## UID and capability requirements

- **Sidecar**: runs as root (`user: "0:0"`) with `CAP_SYS_PTRACE` in the compose file.  
  - The target runs as root by default in the `coreclr-sample:dev` image; the sidecar UID must match.  
  - In Kubernetes the recommended approach is to pin both containers to the same non-root UID via `securityContext.runAsUser`.
- **Central**: no UID or capability requirements — it makes only outbound TCP calls to the sidecar.
- **`DOTNET_EnableDiagnostics=0` on the sidecar**: prevents the sidecar from publishing its own
  diagnostic socket in `/tmp`. This ensures exactly one `.NET` process (CoreClrSample) is
  discoverable, so process auto-selection works reliably when no explicit `processId` is passed.

## Build and start

```bash
# Build images (skip with DOCKER_EXT_INV_SKIP_BUILD=1 if already built)
docker compose -f deploy/docker-compose.external-investigation.yml up --build -d --wait
```

`--wait` blocks until the `healthy` health-check fires on both `sidecar` and `central`.

## Smoke-test with curl

### List external profiles

```bash
curl -fsS -X POST http://127.0.0.1:18890/mcp \
  -H 'Authorization: Bearer central-dev-token' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -D central-headers.txt -o central-init.json \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'

SID=$(grep -i '^mcp-session-id:' central-headers.txt | awk '{print $2}' | tr -d '\r')

curl -fsS -X POST http://127.0.0.1:18890/mcp \
  -H 'Authorization: Bearer central-dev-token' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'

curl -fsS -X POST http://127.0.0.1:18890/mcp \
  -H 'Authorization: Bearer central-dev-token' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_orchestrator","arguments":{"kind":"external-profiles"}}}'
```

### Attach and call inspect_process through the central

```bash
ATTACH=$(curl -fsS -X POST http://127.0.0.1:18890/mcp \
  -H 'Authorization: Bearer central-dev-token' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"attach_to_pod","arguments":{"profileName":"sidecar","ttlSeconds":300}}}')

# Extract the handle ID — response is a DiagnosticResult<AttachSession> envelope
HANDLE=$(echo "$ATTACH" | python3 -c "
import sys, json
r = json.load(sys.stdin)
print(r['result']['content'][0]['text'])
" 2>/dev/null | python3 -c "import sys,json; e=json.load(sys.stdin); print(e['data']['handleId'])")

# inspect_process forwarded to the sidecar
curl -fsS -X POST http://127.0.0.1:18890/mcp \
  -H 'Authorization: Bearer central-dev-token' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"inspect_process\",\"arguments\":{\"view\":\"list\",\"investigationHandleId\":\"$HANDLE\"}}}"
```

You should see CoreClrSample in the process list — running in the sidecar's PID namespace,
not in the central's namespace.

### Detach and verify routing fails

```bash
# Detach
curl -fsS -X POST http://127.0.0.1:18890/mcp \
  -H 'Authorization: Bearer central-dev-token' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"detach_from_pod\",\"arguments\":{\"handleId\":\"$HANDLE\"}}}"

# Re-call inspect_process with the stale handle — must return IsError=true
curl -fsS -X POST http://127.0.0.1:18890/mcp \
  -H 'Authorization: Bearer central-dev-token' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"inspect_process\",\"arguments\":{\"view\":\"list\",\"investigationHandleId\":\"$HANDLE\"}}}"
```

The second inspect_process call must return `"isError": true` with a message containing
"unknown or no longer active".

## Acceptance test

```bash
scripts/test-docker-external-investigation.sh
```

The script starts the compose stack, runs
`DockerExternalInvestigationTests.ExternalInvestigation_FullPassthroughWorkflow_AttachInspectCollectDetach`,
and tears everything down on exit. It is gated by the
`DOTNET_DBG_MCP_DOCKER_EXT_INV_TEST=1` environment variable so it is a no-op in
standard `dotnet test` runs.

## Tear-down and cleanup

```bash
# Compose project (removes containers + the diagnostics-tmp volume)
docker compose -f deploy/docker-compose.external-investigation.yml down -v

# Or, if you used the test script (it cleans up on exit automatically):
# Nothing to do — the trap handler calls `down --volumes` on EXIT.
```

## SSRF-safety notes

The central MCP's external-profile configuration enforces:

- **Operator-only URI** — the sidecar URL comes from `Orchestrator:ExternalMcpProfiles:sidecar:Url`,
  never from the caller.
- **CIDR allowlist** — DNS-resolved IP must fall in `AllowedCidrs` (here `192.168.200.0/24`,
  the Docker bridge subnet). IPv4-mapped IPv6 is unwrapped before checking.
- **Port allowlist** — `AllowedPorts` must include the URL's port (`8080`).
- **No proxy, no redirects** — the outbound `HttpClient` uses `UseProxy=false` and
  `AllowAutoRedirect=false`.
- **Response cap** — `MaxResponseBytes` (default 4 MiB) prevents unbounded buffering.

## Kubernetes equivalence

The Docker topology in this doc intentionally mirrors the Kubernetes sidecar topology:

| Docker concept | Kubernetes equivalent |
|---|---|
| `pid: "service:pid-anchor"` | `shareProcessNamespace: true` |
| `volumes: diagnostics-tmp:/tmp` | `emptyDir` on `/tmp` shared between containers |
| `user: "0:0"` | `securityContext.runAsUser` (same UID as target) |
| `CAP_SYS_PTRACE` | `securityContext.capabilities.add: ["SYS_PTRACE"]` |
| Docker bridge CIDR | Pod CIDR in `AllowedCidrs` |

For production Kubernetes deployments see `deploy/k8s/sample-sidecar.yaml` and
`docs/central-orchestrator-design.md`.
