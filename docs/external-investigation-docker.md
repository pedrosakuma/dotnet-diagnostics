# External-investigation Docker passthrough

_Issue: [#712](https://github.com/pedrosakuma/dotnet-diagnostics/issues/712) — proves the external-investigation flow against a real local Docker target and sidecar._

## What this topology is

The external-investigation workflow runs **two** diagnostics-MCP instances:

| Role | What it does | Who connects to it |
|---|---|---|
| **Sidecar MCP** | Shares the target's PID namespace and `/tmp`; performs the actual EventPipe / ClrMD work. | Central MCP only. |
| **Central (orchestrator) MCP** | Orchestrator-only; no shared PID namespace. Exposes a named external-MCP profile pointing at the sidecar. Clients always connect here. | MCP client (LLM, curl, test). |

The client calls `attach_to_pod(profileName="sidecar")` on the central MCP. After the handle becomes Active, all subsequent diagnostic tool calls that carry the returned `investigationHandleId` are forwarded by the central to the sidecar automatically — the client never needs to know the sidecar URL or bearer token.

`CoreClrSample` below is only this repo's own sample target used to prove the
flow end to end. The topology and every tool call are identical for **any
containerized .NET app** — substitute your own target container and image
wherever `CoreClrSample`/`coreclr-sample:dev` appears. The fastest way to stand
up the sidecar side for an already-running target container of yours is
`dotnet-diagnostics-cli docker-bootstrap` (see
[`cli-reference.md`](./cli-reference.md#docker-bootstrap)); the manual Compose
recipe below remains the reference for understanding every moving part.

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
| `sidecar-dev-delegation-key` | The operator (same static value on both sides) | Central ↔ sidecar only; signs the internal scope-delegation token attached to every proxied tool call |

The central's `Orchestrator:ExternalMcpProfiles:sidecar:BearerToken` and `...:DelegationKey`
are both marked `[JsonIgnore]` so neither is ever serialised into investigation handles, log
messages, or error responses visible to the caller.

Unlike `attach_to_pod` against a Kubernetes pod — where the orchestrator controls the target
and can inject a freshly-generated, per-handle delegation secret into it via exec at attach
time — an external MCP profile points at a standalone server the orchestrator does not
control. That server can only verify a delegation token against whatever static secret its own
`MCP_INTERNAL_SCOPE_DELEGATION_KEY` was started with, so `DelegationKey` must be configured to
the same value on both sides. If a profile has no `DelegationKey` configured, tool calls
proxied through a handle attached to it are refused with a “delegation unavailable” error
rather than forwarded unsigned.

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

## Recommended quick start: `dotnet-diagnostics-cli docker-bootstrap`

When the **target container is already running**, the fastest local bootstrap path is the standalone
CLI command below:

```bash
# Build the sidecar image once (or pass --sidecar-image to use a different tag)
docker build -t dotnet-diagnostics-mcp:dev -f deploy/Dockerfile .

# Start a sidecar for an existing target container and print the matching central config
dotnet-diagnostics-cli docker-bootstrap --target-container <running-target-container>
```

What it does:

- shells out to the local `docker` CLI — **not** from inside the MCP server process;
- inspects the target container's host PID, then bind-mounts `/proc/<pid>/root/tmp` into the sidecar
  so the target diagnostic socket is reachable without pre-authoring a shared Docker volume;
- reads the target's effective UID/GID from `/proc/<pid>/status` and runs the sidecar with that same
  `--user`, plus `--cap-add SYS_PTRACE` by default;
- sets `DOTNET_EnableDiagnostics=0` on the sidecar so only the target's socket is discoverable;
- generates (or accepts) a sidecar bearer token and `MCP_INTERNAL_SCOPE_DELEGATION_KEY`, then prints
  the exact `Orchestrator__ExternalMcpProfiles__<name>__...` env vars and an equivalent
  `appsettings.json` block for the central MCP.

Defaults:

- sidecar image: `dotnet-diagnostics-mcp:dev`
- published sidecar host port: `127.0.0.1:18891 -> 8080`
- emitted central profile URL: `http://127.0.0.1:18891/mcp`
- emitted `AllowedCidrs`: `127.0.0.1/32` (or `127.0.0.1/32` + `::1/128` when `--profile-url` uses `localhost`)

Example with an explicit profile name / port:

```bash
dotnet-diagnostics-cli docker-bootstrap \
  --target-container api \
  --profile-name api-sidecar \
  --host-port 18892
```

If the central MCP reaches the sidecar through a different hostname (for example a Dockerized central
using `host.docker.internal`), override the URL and CIDR explicitly:

```bash
dotnet-diagnostics-cli docker-bootstrap \
  --target-container api \
  --host-port 18892 \
  --profile-url http://host.docker.internal:18892/mcp \
  --allow-cidr 172.17.0.1/32
```

### Host `/proc` accessibility limitation

`docker-bootstrap` assumes the host can directly read the target container's
`/proc/<pid>/status` and `/proc/<pid>/root/tmp` paths after `docker inspect`
reports the container's host PID. That is true on a plain Linux Docker host,
but it can fail on Docker Desktop's VM-backed daemon, rootless Docker,
Docker-in-Docker, or other user-namespace / nested-container setups where the
reported PID is not exposed through the **outer** host `/proc`.

When that happens, the CLI now surfaces a `HostProcNotAccessible` error instead
of incorrectly reporting the target as simply "not running". The target
container may still be healthy in `docker ps`; the specific failure is that the
automatic `/proc/<pid>/root/tmp` bind-mount trick is unavailable on that host.

Fallback: use the manual Compose/shared-volume topology below (the reference
topology for issue #712), or run `docker-bootstrap` from a plain Linux Docker
host that can read `/proc/<pid>/root/...` directly.

### Central-registration limitation

The current public orchestrator surface can **list** external profiles and **attach** to an already
configured one, but it does not expose a runtime "register profile" mutation. So the bootstrap command
does **not** auto-register the profile against a live central instance yet. After running the command:

1. add the printed `Orchestrator__ExternalMcpProfiles__<name>__...` keys (or JSON block) to the central,
2. restart the central MCP,
3. verify with `list_orchestrator(kind="external-profiles")`,
4. then call `attach_to_pod(profileName="<name>")`.

This keeps the bootstrap outside the server and preserves the hard constraint that neither the central
nor the sidecar MCP process ever receives `/var/run/docker.sock`.

## Build and start

For a from-scratch topology where the target, sidecar, PID-namespace anchor, bridge network, and
central are all launched together, keep using the compose recipe below. That manual path remains the
reference for understanding every moving part and for reproducing the exact acceptance topology from
issue #712.

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
