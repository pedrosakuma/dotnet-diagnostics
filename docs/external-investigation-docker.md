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
# An installed release selects its exact matching published GHCR semver tag.
dotnet-diagnostics-cli docker-bootstrap --target-container <running-target-container> \
  --acknowledge-risk high
```

What it does:

- shells out to the local `docker` CLI — **not** from inside the MCP server process;
- starts a short-lived probe from the sidecar image in the Docker daemon's host PID namespace, reads
  the target's effective UID/GID and inner namespace PID from `/proc/<host-pid>/status`, and runs the
  persistent sidecar with that same `--user`, plus `--cap-add SYS_PTRACE` by default;
- points the sidecar's `TMPDIR` at `/proc/<target-namespace-pid>/root/tmp`, making the target diagnostic socket reachable
  through the shared PID namespace without a host `/proc` bind mount or a pre-authored shared volume;
- sets `DOTNET_EnableDiagnostics=0` on the sidecar so only the target's socket is discoverable;
- generates (or accepts) a sidecar bearer token and `MCP_INTERNAL_SCOPE_DELEGATION_KEY`, then prints
  the exact `Orchestrator__ExternalMcpProfiles__<name>__...` env vars and an equivalent
  `appsettings.json` block for the central MCP;
- with `--central-container <name> --apply`, atomically installs that profile in a compatible
  Dockerized central, restarts the existing container, waits for health, and returns only after the
  profile is available to `list_orchestrator(kind="external-profiles")`.

Defaults:

- sidecar image: an installed stable or prerelease CLI selects
  `ghcr.io/pedrosakuma/dotnet-diagnostics:<exact-cli-version>`; a repository/source build selects
  `ghcr.io/pedrosakuma/dotnet-diagnostics:edge`
- published sidecar host port: `127.0.0.1:18891 -> 8080`
- emitted central profile URL: `http://127.0.0.1:18891/mcp`
- emitted `AllowedCidrs`: `127.0.0.1/32` (or `127.0.0.1/32` + `::1/128` when `--profile-url` uses `localhost`)

Example with an explicit profile name / port:

```bash
dotnet-diagnostics-cli docker-bootstrap \
  --target-container api \
  --profile-name api-sidecar \
  --host-port 18892 \
  --acknowledge-risk high
```

For repository development, build the changed MCP image locally and override the published default
explicitly:

```bash
docker build -t dotnet-diagnostics-mcp:dev -f deploy/Dockerfile .
dotnet run --project src/DotnetDiagnostics.Cli -c Release -- \
  docker-bootstrap \
  --target-container api \
  --sidecar-image dotnet-diagnostics-mcp:dev \
  --acknowledge-risk high
```

`--sidecar-image` always wins. Released CLIs never fall back from an unavailable exact version tag
to `:edge`; Docker reports the pull failure and the CLI returns `kind=ExternalDependencyFailed` with
the selected image in the error.

If the central MCP reaches the sidecar through a different hostname (for example a Dockerized central
using `host.docker.internal`), override the URL and CIDR explicitly:

```bash
dotnet-diagnostics-cli docker-bootstrap \
  --target-container api \
  --host-port 18892 \
  --profile-url http://host.docker.internal:18892/mcp \
  --allow-cidr 172.17.0.1/32 \
  --acknowledge-risk high
```

### Docker PID-namespace requirement

The automatic path does not read the client shell's `/proc`, so it works when
the Docker daemon runs behind Docker Desktop and the command is launched from
native Windows PowerShell or a WSL2 shell. The short-lived UID/GID probe uses
`--pid host`; the persistent sidecar joins the target with
`--pid container:<target>`. The probe image must therefore contain `/bin/cat`,
and the daemon must permit both PID namespace modes.

If that probe fails while the target remains running, the CLI returns
`kind=HostProcNotAccessible` with the failed `docker run` command result.
Rootless Docker or hardened daemon policies may still reject the namespace
join. In that case, use the manual Compose/shared-volume topology below.

### Automatic central apply

For the common Dockerized-central topology, use:

```bash
dotnet-diagnostics-cli docker-bootstrap \
  --target-container api \
  --central-container diagnostics-central \
  --apply \
  --acknowledge-risk high
```

The operator CLI verifies that the central image supports bootstrap profile files, writes one
bootstrap-owned file under `/app/.dotnet-diagnostics/bootstrap-profiles/` with mode `0600`, and
restarts the **existing** container. It never recreates the central, so Docker-owned mounts, ports,
labels, health check, restart policy, capabilities, security options, and network aliases are not
reconstructed or lost. Secrets travel on `docker exec -i` stdin, not in its argv. Neither MCP
container receives `/var/run/docker.sock`.

Applying identical content is a no-op. Different bootstrap-owned content requires `--replace`; an
unowned file is never overwritten. A failed restart restores the previous file (or removes the new
one), retries the restart, and removes the newly started sidecar. Human and JSON output include the
exact cleanup action, which deletes only that profile file, restarts the central, disconnects the
bootstrap-owned network attachment, and removes that sidecar.

Host-process centrals remain configuration-only: omit `--apply`, use the emitted env/JSON, and restart
them through their normal supervisor.

### Safety behavior in this workflow

The concrete steps intentionally resolve to different safety levels:

| Step | Resolved behavior |
|---|---|
| CLI `docker-bootstrap --apply` | High: starts a privileged sidecar, writes a central profile, and restarts the existing central container. Non-interactive use requires `--acknowledge-risk high`. |
| MCP `attach_to_pod(profileName="...")` | Moderate: opens an operator-configured outbound MCP session and returns `safetyWarnings`; it does not mutate a Kubernetes Pod. |
| MCP `collect_batch` with counters + GC events | Moderate: EventPipe overhead and target-derived metadata are reported in `safetyWarnings`; payloads can still contain confidential names or identifiers. |
| MCP `detach_from_pod` | Moderate: closes the routed investigation session and returns a warning rather than requiring acknowledgement. |

Do not generalize the external-profile result to Kubernetes. A Kubernetes
`attach_to_pod(namespace=..., podName=...)` can inject or mutate deployment
state, resolves high, and requires the exact request-bound
`safetyApproval.requiredAcknowledgement` challenge/retry described in
[`client-setup.md`](./client-setup.md#safety-aware-toolscall-clients).

#### Design choice and rejected alternatives

- **Selected: operator-owned file plus existing-container restart.** This preserves server startup
  validation and provenance while avoiding unsafe `docker inspect` reconstruction. Restart behavior
  and rollback are explicit.
- **Rejected: recreate the central from partial inspection.** Docker inspect does not provide a safe,
  lossless round-trip for every mount, port, label, health check, restart policy, security option,
  capability, and network alias.
- **Rejected: Docker socket in either MCP.** It expands compromise impact from diagnostics access to
  host-level container control.
- **Rejected: mutable MCP registration tool or management endpoint.** It would add privileged mutable
  server state, authentication/CSRF/SSRF surface, and unclear persistence/rollback. The MCP tool count
  stays unchanged.
- **Rejected: file watching alone.** Existing orchestrator tool registration is decided at startup;
  an explicit restart makes enablement and validation deterministic. A generated command alone was
  also insufficient because it retained the first-run copy/paste break.

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

## Docker-bootstrap acceptance test

```bash
scripts/test-docker-external-investigation.sh
```

The script builds the current checkout, starts a uniquely named CoreClrSample target,
invokes the built CLI `docker-bootstrap --apply` command as the current non-root user against
an already-running central MCP. It then runs
`DockerExternalInvestigationTests.ExternalInvestigation_FullPassthroughWorkflow_AttachInspectCollectDetach`,
which proves profile listing, attach, a routed counters+GC `collect_batch`, detach, and
post-detach routing rejection through the MCP protocol.

The script also asserts that the bootstrap-created sidecar has no host `/proc` bind mount
and that its `TMPDIR` matches `/proc/<target-namespace-pid>/root/tmp`. This specifically
guards the portable daemon-side PID probe fixed by #748/#750. All container/network names
and host ports are unique per run, and the EXIT trap removes only those resources.

Use already-built Release outputs and local images:

```bash
DOCKER_EXT_INV_SKIP_BUILD=1 scripts/test-docker-external-investigation.sh
```

On failure, inspect `TestResults/docker-bootstrap-e2e/` for the bootstrap JSON plus
target, sidecar, central, and Docker-network logs/inspection output. The GitHub workflow
uploads this directory as a failure artifact. The test is gated by the
`DOTNET_DBG_MCP_DOCKER_EXT_INV_TEST=1` environment variable so it is a no-op in
standard `dotnet test` runs.

The acceptance test verifies explicit CLI high acknowledgement, moderate
external-profile attach, moderate routed EventPipe collection, detach, and
stale-handle rejection. It never uses a global acknowledgement bypass.

Treat `TestResults/docker-bootstrap-e2e/` as incident evidence: it can contain
target logs, inspection metadata, tokens generated for the isolated test, and
target-derived diagnostic output. Restrict access and delete it after the
failure is resolved; see
[`production-safety.md`](./production-safety.md#retention-access-and-disposal).

## Tear-down and cleanup

```bash
# Compose project (removes containers + the diagnostics-tmp volume)
docker compose -f deploy/docker-compose.external-investigation.yml down -v

# Or, if you used the test script:
# Nothing to do — its EXIT trap removes its uniquely named containers and network.
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
