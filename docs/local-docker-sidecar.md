# Local sidecar validation (Docker)

Reproduces the Kubernetes sidecar topology with plain Docker, so you can run
the whole stack locally before deploying to a cluster.

Two application images, three containers, one shared `/tmp` volume, and one
shared PID namespace reproduce the relevant Kubernetes building blocks. The
third container is an inert PID-namespace anchor. `coreclr-sample`/`BadCodeSample`
below are just this repo's own sample targets — the recipe applies unchanged to
**your own containerized .NET app** in place of them.

> **Don't want the MCP client to see the sidecar's URL/bearer directly?** This
> page has the client connect straight to the sidecar. For the Kubernetes-style
> `attach_to_pod` proxy passthrough instead — a separate central orchestrator MCP
> forwards diagnostic calls to the sidecar via an operator-configured profile, so
> the client only ever talks to the central — see
> [`external-investigation-docker.md`](./external-investigation-docker.md) and the
> `dotnet-diagnostics-cli docker-bootstrap` command in
> [`cli-reference.md`](./cli-reference.md#docker-bootstrap).

> **Crash-guard requires the anchor.** The old two-container recipe made the
> target PID 1 and launched the sidecar with `--pid=container:sample`. Linux
> kills every remaining process in a PID namespace when its PID 1 exits, so a
> target crash also killed the MCP server before it could return the structured
> crash-guard result. Docker cannot make that two-container topology survive.
> The supported recipe below makes a stable anchor PID 1, then joins both the
> target and sidecar to it. This mirrors the relevant Kubernetes pod-sandbox
> ownership without exposing the host PID namespace.

## Build the images

From the repo root:

```bash
docker build -t dotnet-diagnostics-mcp:dev -f deploy/Dockerfile .
docker build -t coreclr-sample:dev   -f samples/CoreClrSample/Dockerfile .
```

> 🔧 **Need a smaller image without `perf`?** Add `--build-arg INSTALL_PERF=false`
> to the sidecar build. The default image ships `perf` so `collect_sample(kind="off_cpu")`
> and the Linux NativeAOT perf-replay thread-snapshot fallback work out of the
> box (perf still needs `CAP_PERFMON` at runtime — add `--cap-add PERFMON` to the
> sidecar `docker run`, or lower `kernel.perf_event_paranoid` on the host).
> Opting out of the install skips ~80 MB of `linux-tools-*` packages; the capability
> detector will then report `canSampleOffCpu: false`. See issue #104.
>
> ```bash
> docker build --build-arg INSTALL_PERF=false \
>   -t dotnet-diagnostics-mcp:dev-lean -f deploy/Dockerfile .
> ```
>

## Run the supported topology

The checked-in Compose file is the canonical and reproducible path:

```bash
docker compose -f deploy/docker-compose.crash-guard.yml up --build -d --wait
```

It publishes the target on `127.0.0.1:18180`, the MCP server on
`127.0.0.1:18887`, uses bearer token `dev-token`, and builds the lean MCP image
without `perf`. Its target is `BadCodeSample`, which provides the deliberate
crash endpoint needed by the acceptance test. Run
`scripts/test-docker-crash-guard.sh` for the full container-backed crash
assertion. If both `:dev` images already exist and the machine is temporarily
offline, set `DOCKER_CRASH_GUARD_SKIP_BUILD=1` to test those local images
without rebuilding.

For the same anchored namespace pattern with `CoreClrSample` instead, use:

```bash
docker network create diagmcp-net 2>/dev/null || true
docker volume  create diagnosticsmcp-tmp >/dev/null

# 1) stable namespace owner — survives target exit
docker run -d --name diag-pid-anchor --network diagmcp-net \
  --entrypoint tail coreclr-sample:dev -f /dev/null

# 2) target app — joins the anchor namespace and is not PID 1
docker run -d --name sample --network diagmcp-net \
  --pid=container:diag-pid-anchor \
  -v diagnosticsmcp-tmp:/tmp \
  -p 18080:8080 \
  coreclr-sample:dev

# 3) MCP sidecar — joins the same stable namespace and /tmp volume
docker run -d --name mcp --network diagmcp-net \
  --pid=container:diag-pid-anchor \
  -v diagnosticsmcp-tmp:/tmp \
  --user 0 \
  --cap-add SYS_PTRACE \
  -e MCP_BEARER_TOKEN=dev-token \
  -p 18887:8080 \
  dotnet-diagnostics-mcp:dev
```

`--user 0` is the easy path for local validation because the sample image runs
as root and creates its `/tmp/dotnet-diagnostic-<pid>` socket as root. In Kubernetes,
the recommended setup is to run **both** containers as the same non-root UID
(the sample manifest pins UID/GID `10001` and sets `fsGroup: 10001`).

The target PID is no longer `1`; discover it with
`inspect_process(view="list", commandLineContains="CoreClrSample")`. Keeping the
shared `/tmp` volume mounted preserves the diagnostic IPC filesystem while the
already-open EventPipe stream drains after target exit.

### Why not `pid: host`?

Host PID mode also survives target exit, but it gives the diagnostics container
visibility into every host process and materially widens the impact of
`CAP_SYS_PTRACE`. It is not the supported container-target recipe. Use it only
for an explicitly trusted developer-machine workflow that must inspect a .NET
process running directly on the host. For production-like parity, use the
anchored Compose topology above or the Kubernetes sidecar manifest.

### Sidecar ops: auto-recycle on image swap

When the sidecar image is rolled forward (`docker pull … && docker run …` with
the same name, or a Kubernetes deployment update of just the sidecar
container), the still-running process keeps serving the **previous** build
until something else recycles it. Set `DOTNET_DIAGNOSTICS_MCP_AUTO_RESTART=true`
on the sidecar container — the built-in `StaleBinaryWatcher` polls the
on-disk MVID once a minute and, on drift, asks the host to stop gracefully so
the supervisor (`--restart=always`, systemd, K8s) brings up the fresh build.
Without the env var the watcher only logs a warning. See issue #75.

### Heads up: live memory readers need `CAP_SYS_PTRACE` on Linux

`collect_thread_snapshot`, `inspect_heap(source="live")`, live `capture_method_bytes`,
`get_bytes(kind="module")`, and the opt-in
`collect_sample(kind="cpu", resolveMethodInstantiations=true)` enrichment attach via ClrMD,
which under the hood issues
`ptrace(PTRACE_ATTACH, …)`. Matching UIDs alone is **not** enough on Linux:
the kernel's [Yama LSM](https://www.kernel.org/doc/Documentation/admin-guide/LSM/Yama.rst)
defaults `kernel.yama.ptrace_scope=1` on Debian/Ubuntu/WSL, which blocks
same-UID peer attach. You will see a structured error like:

```json
{ "error": { "kind": "PermissionDenied",
             "message": "Could not PTRACE_ATTACH to any thread of the process N." } }
```

Mitigations, in order of preference for local Docker:

- Pass `--cap-add SYS_PTRACE` to the **sidecar** container (it is the one that
  performs the ptrace call). The target container does not need it.
- Or relax the host (affects everything on the box):
  `sudo sysctl -w kernel.yama.ptrace_scope=0`.
- Or run the sidecar as root **and** as a parent of the target — covers the
  Yama "parent only" mode (`ptrace_scope=1`).

For Kubernetes, see [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml):
add `capabilities.add: ["SYS_PTRACE"]` to the sidecar container's
`securityContext`, alongside the existing UID alignment.

EventPipe-based tools (`collect_events(kind="counters")`, `collect_sample(kind="cpu")`,
`collect_events(kind="exceptions")`, `collect_events(kind="gc")`, `collect_events(kind="activities")`, `collect_events(kind="event_source")`) do **not**
need `CAP_SYS_PTRACE` — they go through the diagnostic IPC socket only.

## Smoke-test the MCP endpoint

```bash
# Wait for Docker's image healthcheck, which probes /health without requiring auth
until [ "$(docker inspect --format '{{.State.Health.Status}}' mcp)" = healthy ]; do
  sleep 2
done

# Health remains directly available without auth
curl -fsS http://127.0.0.1:18887/health

# Initialize the MCP session and grab the session id from the response header
curl -fsS -X POST http://127.0.0.1:18887/mcp \
  -H 'Authorization: Bearer dev-token' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -D headers.txt -o init.txt \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'

SID=$(grep -i '^mcp-session-id:' headers.txt | awk '{print $2}' | tr -d '\r')

# Finish the handshake
curl -fsS -X POST http://127.0.0.1:18887/mcp \
  -H "Authorization: Bearer dev-token" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'

# Discover .NET processes the sidecar can see
curl -fsS -X POST http://127.0.0.1:18887/mcp \
  -H "Authorization: Bearer dev-token" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"inspect_process","arguments":{"view":"list"}}}'
```

You should see the sample and sidecar .NET processes; PID `1` is the non-.NET
namespace anchor.

Discover the sample PID from the list response, then collect 5 seconds of
`System.Runtime` counters from it:

```bash
curl -fsS -X POST http://127.0.0.1:18887/mcp \
  -H "Authorization: Bearer dev-token" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"collect_events","arguments":{"kind":"counters","processId":<sample-pid>,"durationSeconds":5,"providers":["System.Runtime"]}}}'
```

## Tear down

```bash
docker rm -f mcp sample diag-pid-anchor
docker volume rm diagnosticsmcp-tmp
docker network rm diagmcp-net
```

For the Compose path, use:

```bash
docker compose -f deploy/docker-compose.crash-guard.yml down -v
```
