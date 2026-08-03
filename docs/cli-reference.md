# `dotnet-diagnostics-cli` — standalone CLI reference

`dotnet-diagnostics-cli` is the **human-facing** counterpart to the `dotnet-diagnostics-mcp` server. Both
ship from this repository and run the **same Core diagnostics engine**, but they target different consumers:

| | `dotnet-diagnostics-cli` (this doc) | `dotnet-diagnostics-mcp` (the server) |
|---|---|---|
| Consumer | A human, a shell script, a CI job | An LLM, via an MCP client |
| Surface | Sub-commands you type | MCP tools the model calls |
| Transport | None — in-process, one-shot or REPL | Streamable HTTP (bearer auth) or stdio |
| State | One-shot inline results, or a `session` REPL holding queryable handles | MCP session holding handles |
| Install | `dotnet tool install -g dotnet-diagnostics-cli` | `dotnet tool install -g dotnet-diagnostics-mcp` |

If you want an LLM to drive diagnostics, use the **server** — see [`client-setup.md`](./client-setup.md) and
[`tool-reference.md`](./tool-reference.md). If you want to run diagnostics yourself, read on.

> The CLI references **Core only** — it never starts an HTTP server, reads a bearer token, or runs a daemon.

## Install

```bash
# .NET global tool (requires the .NET 10 SDK)
dotnet tool install -g dotnet-diagnostics-cli
dotnet-diagnostics-cli --help
```

Other distributions:

- **Self-contained binary** (no SDK): per-OS archives are attached to every
  [Release](https://github.com/pedrosakuma/dotnet-diagnostics/releases) as
  `dotnet-diagnostics-cli-<version>-<rid>` (`linux-x64`, `linux-arm64`, `osx-arm64`, `win-x64`, `win-arm64`).
- **In the sidecar container**: the diagnostics sidecar image bundles the CLI on `PATH`, so
  `kubectl exec -it <pod> -c diagnostics-mcp -- dotnet-diagnostics-cli processes` works against the
  co-located workload.

## Shell completion

The CLI can emit static completion scripts for bash, zsh and PowerShell. The generated scripts include
sub-commands, top-level flags and enum-valued options such as `collect --kind`, `inspect-heap --source`,
`dump --dump-type` and `get-bytes --kind`.

```bash
# bash (system-wide)
dotnet-diagnostics-cli completion bash | sudo tee /etc/bash_completion.d/dotnet-diagnostics >/dev/null

# bash (current shell only)
source <(dotnet-diagnostics-cli completion bash)
```

```zsh
# zsh: write the generated function into a directory on fpath, then reload completions.
mkdir -p ~/.zsh/completions
dotnet-diagnostics-cli completion zsh > ~/.zsh/completions/_dotnet-diagnostics
print -r 'fpath=(~/.zsh/completions $fpath)' >> ~/.zshrc
autoload -Uz compinit && compinit
```

```powershell
# PowerShell: source once for the current session.
dotnet-diagnostics-cli completion pwsh | Out-String | Invoke-Expression

# To load it every time, add the generated script to your profile.
dotnet-diagnostics-cli completion pwsh > "$HOME/.dotnet-diagnostics-completion.ps1"
Add-Content -Path $PROFILE -Value '. "$HOME/.dotnet-diagnostics-completion.ps1"'
```

If you run the self-contained executable directly, replace `dotnet-diagnostics-cli` above with
`dotnet-diagnostics`. The generated scripts register both command names.

## Global options

These apply to every command:

| Option | Meaning |
|---|---|
| `-p, --pid <pid\|name>` | Target OS process id, or a case-insensitive prefix of the visible .NET process entrypoint/name. Purely numeric values are always treated as literal PIDs. **Auto-resolved** when exactly one .NET process is visible and `--pid` is omitted. |
| `--json` | Emit the raw `DiagnosticResult<T>` envelope as JSON instead of the human table. JSON is never colorized. |
| `--explain-risk` | Print the resolved Core safety descriptor and exit without executing the command. With `--json`, emits a machine-readable safety explanation. |
| `--acknowledge-risk <high\|critical>` | Deliberately authorize a high/critical operation in one-shot or other non-interactive use. The value must exactly match the resolved level. |
| `--launch -- <app> [args]` | **Dev mode.** Launch `<app>` as a child of the CLI so live attach works under `kernel.yama.ptrace_scope=1` with no privilege — see the [Linux note](#linux-ptrace-note). Supported by `capabilities`, `collect`, `dump`, `inspect`, `inspect-heap` (live), `get-bytes` (module) and `session`. Mutually exclusive with `--pid`; the child is terminated on exit. Because process launch/termination is high risk, place the exact resolved `--acknowledge-risk` before the `--` delimiter. |
| `--suspend-startup` | **Cold-start capture (#446).** With `--launch`, launches the target *suspended* on a reverse-connect `DOTNET_DiagnosticPorts=…,suspend` port, arms the EventPipe session **before any managed code runs**, then resumes — capturing non-replayed DependencyInjection call-site activity that an attach after startup misses, plus any loader events emitted by the runtime provider. Applies to `collect --kind startup`. The MCP equivalent is gated `collect_events(kind="startup", launch=...)` over stdio. Default off. |
| `-h, --help` | Show the global usage screen, or a focused screen for `<command> --help`. |

Exit codes: `0` success (a `dump` preview is also a success), `1` a structured failure envelope
(e.g. `NotSupported`, `PermissionDenied`), `2` a usage / validation or safety-acknowledgement error.

## Safety preflight

The CLI and MCP server resolve the same descriptor from the Core safety registry. The CLI applies it
before launch, attach, capture, export, or mutation:

The canonical generated matrix and production operating profiles are in
[`production-safety.md`](./production-safety.md). The `observe`, `investigate`,
and `privileged-response` profiles do not override the shared Core safety
registry; every CLI invocation still resolves its concrete descriptor.

- **low** — executes without extra output;
- **moderate** — prints one concise `SAFETY warning [moderate] ...` line to `stderr`, then executes;
- **high / critical** — prints `reason`, `targetImpact`, `dataExposure`, `sideEffects`, and the artifact
  path when applicable. A real interactive `session` asks the operator to type the exact level. One-shot
  commands and sessions whose stdin is redirected never prompt and require
  `--acknowledge-risk <exact-level>`.

All warnings and acknowledgement failures go to `stderr`; `stdout` remains valid JSON under `--json`.
Prompts contain only the canonical descriptor and destination path, not provider values, symptoms,
credentials, target payloads, or other command data.

Use `--explain-risk` to inspect the resolved descriptor without executing:

```bash
dotnet-diagnostics-cli inspect-heap --source live --pid 1234 --explain-risk
dotnet-diagnostics-cli dump --pid 1234 --out ./dumps --confirm --explain-risk --json
```

Stable CI/non-interactive pattern:

```bash
# Inspect first; "executed" is false.
dotnet-diagnostics-cli collect --kind exceptions --pid 1234 --explain-risk --json

# The acknowledgement is deliberately pinned to the currently resolved level.
dotnet-diagnostics-cli inspect-heap --source live --pid 1234 \
  --acknowledge-risk high --json > heap.json

dotnet-diagnostics-cli dump --pid 1234 --out ./dumps --confirm \
  --acknowledge-risk critical --json > dump.json
```

If a future option changes the resolved level, an old acknowledgement does not silently authorize the
new operation: the CLI exits `2` before execution and tells the caller which level is required.

> **Progress.** Long one-shot collections (`collect`, `inspect-heap`, `dump`) print an elapsed-time
> spinner to stderr while they run, on an interactive terminal only. It is suppressed under `--json`
> and whenever stderr is redirected/piped, so machine-readable output (stdout) and captured logs stay
> clean.
>
> **Color.** Human output uses ANSI color for headlines, section headers, severities and verdicts only
> when stdout is an interactive terminal. Color is disabled automatically for redirected stdout,
> whenever `--json` is used, or when `NO_COLOR` is set to any value.

## Commands

### `docker-bootstrap`

Bootstrap a **local Docker sidecar** for an already-running target container. It can either print the
matching central configuration or apply it to a compatible Dockerized central automatically.

> This command still keeps the CLI on the "outside": it shells out to the local `docker` CLI, and it
> never gives either MCP server process access to `/var/run/docker.sock`.

| Option | Type | Default | Description |
|---|---|---|---|
| `--target-container` | `string` | required | Running Docker container name or id to diagnose. |
| `--central-container` | `string?` | none | Running central MCP container name or id. Enables central-aware private routing and inspection. |
| `--sidecar-name` | `string?` | `<target>-dotnet-diagnostics` | Explicit Docker container name for the sidecar. |
| `--sidecar-image` | `string?` | version-compatible GHCR image | Explicit sidecar image override. Released CLIs select `ghcr.io/pedrosakuma/dotnet-diagnostics:<cli-version>`; repository/source builds select `:edge`. |
| `--profile-name` | `string?` | sanitized target name | Profile key emitted under `Orchestrator:ExternalMcpProfiles`. Restricted to `^[A-Za-z0-9][A-Za-z0-9_-]*$` so the env-var form stays usable. |
| `--profile-url` | `string?` | `http://127.0.0.1:<host-port>/mcp` | Exact URL the **central** will dial for this profile. Must be absolute `http`/`https`, path exactly `/mcp`, no query/fragment/userinfo. |
| `--allow-cidr` | `string[]` | derived from `--profile-url` when it is an IP literal / `localhost` | Repeatable allowlist entries for `AllowedCidrs`. Required when `--profile-url` uses another hostname (for example `host.docker.internal`). |
| `--host-port` | `int?` | host-central: `18891`; central-aware: none | Host port published for the sidecar's internal `8080` listener. In central-aware mode publication is opt-in. |
| `--bearer-token` | `string?` | generated | Operator-supplied sidecar bearer token. When omitted, the CLI generates a random 32-byte hex value and prints it in the output. |
| `--delegation-key` | `string?` | generated | Operator-supplied `MCP_INTERNAL_SCOPE_DELEGATION_KEY`. When omitted, the CLI generates a random 32-byte hex value and prints it in the output. |
| `--wait` | `int?` | `90` | Seconds to wait for the sidecar health check to report `healthy`. |
| `--no-sys-ptrace` | flag | off | Skip `--cap-add SYS_PTRACE`. Leave this off unless you knowingly want EventPipe-only coverage. |
| `--apply` | flag | off | Require `--central-container`, atomically write a mode-`0600` bootstrap-owned profile file through `docker exec` stdin, restart the existing central container, and wait for health. |
| `--replace` | flag | off | With `--apply`, replace a different profile only when the existing file carries bootstrap ownership metadata. |

Implementation details:

- with `--central-container`, inspects both containers and the central's networks using the
  operator-owned Docker CLI. It prefers a user-defined, local bridge network already shared by
  target and central, then falls back deterministically to the lexicographically first eligible
  central network. The generated `docker run` attaches the sidecar only to that selected network;
- emits `http://ddmcp-<sidecar-name-hash>:8080/mcp` using a fixed 30-character network alias
  (avoiding Docker's 63-character DNS-label limit) and derives `AllowedCidrs` as the resolved
  sidecar address with a `/32` (or `/128` for IPv6), not the whole bridge subnet. Docker DNS may
  return another container alias, but the server's DNS-resolution/CIDR gate can connect only to the
  inspected sidecar address, and the validated IP is used directly to prevent rebinding;
- publishes no sidecar host port on the automatic route unless `--host-port` is explicitly supplied.
  An explicit `--profile-url` and `--allow-cidr` win; a non-sidecar URL also requires explicit
  `--host-port` because bootstrap cannot otherwise prove how it reaches the sidecar. Loopback URLs
  are rejected for a non-host-network central because they address the central itself;
- with `--apply`, checks an image compatibility marker before starting the sidecar, writes secrets
  only through redirected stdin (never Docker command arguments), atomically renames the completed
  mode-`0600` file, and uses `docker restart` rather than reconstructing the container. Mounts,
  ports, labels, health checks, restart policy, capabilities, security options, and network aliases
  therefore remain Docker-owned and unchanged. The central loads the file at startup and the CLI
  waits for its existing health check before reporting success;
- rejects stopped/recreated central containers during bootstrap, central `network=host` for the
  automatic route, unsupported/default-only networks, sidecar name collisions, and network-connect
  failures. Multiple candidates are deterministic;
- reports whether it created the network attachment and prints both bootstrap-owned cleanup actions.
  Cleanup disconnects only the selected attachment and removes only the generated sidecar; repeating
  those commands is idempotent even when Docker reports that the attachment/container is already absent;
- embeds the exact release version as the default image tag in official CLI packages and
  self-contained binaries. Stable and prerelease CLIs therefore select the corresponding exact
  semver tag published by the container workflow; they never silently fall forward to `:edge`;
- uses `ghcr.io/pedrosakuma/dotnet-diagnostics:edge` for repository/source builds, whose normal SDK
  version is not an official release identity. This is a deterministic, useful development default;
  pass `--sidecar-image dotnet-diagnostics-mcp:dev` when testing local sidecar changes;
- attaches the sidecar to the target with `docker run --pid container:<target> ...`;
- runs a short-lived probe from the sidecar image with `--pid host`, reads the target's effective
  UID/GID and inner namespace PID from `/proc/<host-pid>/status`, and mirrors the identity onto the
  persistent sidecar's `--user`;
- sets the sidecar's `TMPDIR=/proc/<target-namespace-pid>/root/tmp`, so the target diagnostic socket is visible through the
  shared PID namespace without a host `/proc` bind mount or pre-authored shared volume;
- sets `DOTNET_EnableDiagnostics=0` on the sidecar so the sidecar's own socket is suppressed.

Docker constraint: the daemon must allow both `--pid host` for the constrained
read-only probe and `--pid container:<target>` for the persistent sidecar, and
the sidecar image must contain `/bin/cat`. The
client shell does not need access to Linux `/proc`, so Docker Desktop works
from native Windows PowerShell and WSL2. If the daemon rejects the namespace
probe while the target remains running, the CLI returns
`kind=HostProcNotAccessible`; use the manual shared-volume / compose topology
instead.

Without `--apply`, output remains configuration-only for host-process centrals and operators who manage
configuration themselves. Automatic apply is intentionally limited to a compatible Dockerized central;
the CLI fails with `ApplyUnsupported` rather than guessing how to mutate a host process or an unrelated
image. Identical managed content is a no-op. Different bootstrap-owned content requires `--replace`;
an unowned file is never overwritten. If restart/health verification fails, the prior file is restored
(or the new file removed), restart is retried, and the newly created sidecar is cleaned up.

```bash
# Installed release: pulls the exact matching GHCR semver tag when absent locally.
dotnet-diagnostics-cli docker-bootstrap --target-container api --acknowledge-risk high

# Dockerized central: private container-DNS route, no sidecar host port.
dotnet-diagnostics-cli docker-bootstrap \
  --target-container api \
  --central-container diagnostics-central \
  --apply \
  --acknowledge-risk high

# Repository development with local MCP changes:
docker build -t dotnet-diagnostics-mcp:dev -f deploy/Dockerfile .
dotnet run --project src/DotnetDiagnostics.Cli -c Release -- \
  docker-bootstrap --target-container api --sidecar-image dotnet-diagnostics-mcp:dev \
  --acknowledge-risk high

dotnet-diagnostics-cli docker-bootstrap --target-container api --profile-name api-sidecar \
  --host-port 18892 --acknowledge-risk high

dotnet-diagnostics-cli docker-bootstrap \
  --target-container api \
  --host-port 18892 \
  --profile-url http://host.docker.internal:18892/mcp \
  --allow-cidr 172.17.0.1/32 \
  --acknowledge-risk high
```

### `processes`

List attachable .NET processes (pid, runtime, OS/arch, entrypoint).

| Option | Type | Default | Description |
|---|---|---|---|
| `--command-line-contains` | `string?` | none | Case-insensitive substring filter against each process's command line, to disambiguate among several candidates spawned by a wrapper you don't control (e.g. several `testhost.exe` under `dotnet test`) — issue #665 part B, mirroring the MCP `inspect_process(view="list")` filter. |

```bash
dotnet-diagnostics-cli processes
dotnet-diagnostics-cli processes --json
dotnet-diagnostics-cli processes --command-line-contains testhost
```

### `capabilities`

Probe a target's diagnostic capability matrix — CoreCLR vs NativeAOT, whether CPU sampling / gcdump /
live attach are available.

```bash
dotnet-diagnostics-cli capabilities --pid 1234
```

### `doctor`

Diagnose the **environment** (not the workload) and print the exact fix. Target-optional and
remediation-first: each blocked/degraded check carries a copy-pasteable docker flag / k8s
`securityContext` snippet / `sysctl`. Reuses the cheap host probes (ptrace, perf) plus a
`/proc/*/status` UID read — no EventPipe session, no privilege.

```bash
dotnet-diagnostics-cli doctor                # host-only readiness check
dotnet-diagnostics-cli doctor --pid 1234     # also verify the diagnostic-socket UID vs the target
dotnet-diagnostics-cli doctor --json
```

Checks: `socket-uid` (UID mismatch blocks **all** tools), `clrmd-attach` (ptrace — blocks
thread snapshot / heap / dump), `offcpu-perf` and `native-alloc` (optional samplers). Status
ladder: `Ok` < `Degraded` < `Blocked`.

**Exit code:** `doctor` exits **non-zero (1)** when a hard blocker (`Blocked`) is present and
`0` otherwise, so it can gate a CI job:

```bash
dotnet-diagnostics-cli doctor --pid "$APP_PID" || { echo "environment not ready"; exit 1; }
```

### `inspect`

One-call process inspector exposing three views (`--view` required):

| View | What it does |
|---|---|
| `triage` | Collects counters for `--duration` seconds (default 5), reports threshold-backed observed signals separately from evidence-backed hypotheses, retains host-normalized CPU plus estimated effective-core consumption, and evaluates in-window GC heap / LOH / working-set growth before returning neutral drill-down hints. |
| `runtime-config` | Reads the process's effective runtime configuration: GC mode and heap count, ThreadPool worker/IOCP bounds, tiered-compilation flags, filtered runtime env vars, and AppContext switches. |
| `container` | Reads cgroup/container CPU quota + throttling, memory limits / OOM counters, PSI, pid limits and `oom_score` for the target process. Linux/cgroup-v2-first; returns partial signals plus notes when the host lacks a container envelope or PSI. |

```bash
dotnet-diagnostics-cli inspect --view triage --pid 1234
dotnet-diagnostics-cli inspect --view triage --pid 1234 --duration 10
dotnet-diagnostics-cli inspect --view runtime-config --pid 1234
dotnet-diagnostics-cli inspect --view container --pid 1234
dotnet-diagnostics-cli inspect --view triage --json
```

Human-readable output leads with `Assessment`, then prints `Observed signals`, `Hypotheses`
(confidence, supporting/contradicting evidence, and next step), and ranked indicators. JSON uses
the same `modelVersion=2` contract as MCP. Hypotheses are ordered by confidence and then the
strongest supporting observed-signal level. A low-CPU snapshot with a small transient queue is
`inconclusive`; it is not labeled `io-bound`.

When request latency remains elevated but the ThreadPool queue no longer crosses the backlog
threshold, human output adds a `Signal separation` line: the starvation signal is absent in this
window, while the remaining latency must be checked against workload/SLO expectations. This is a
CLI rendering clarification only; it does not change the Core triage assessment or infer that a
latency value is expected without operator context.

Topology-adjusted CPU uses the target's one-shot `System.Runtime/ProcessorCount` event, never the CLI
process's topology, and emits `cpu.effective-core-consumption` at approximately one busy core even
when host-normalized CPU is small. `cpuTopologyStatus` is `unknown` when the target event is
unavailable. Material first-to-last GC heap, LOH, or working-set growth emits
`memory.intra-window-growth`; the bounded `memory.footprint-growth` hypothesis describes the shape
without claiming a leak or prescribing a solution. Memory-growth indicators apply the same 20% and
1 MB materiality floor as the observed signal.

For compatibility, JSON continues to serialize `verdict`, `secondaryVerdicts`, `severity`,
`evidence`, and `topIndicators`. `verdict` and `secondaryVerdicts` are deprecated for removal in
v1.0; migrate automation to `assessment`, `observedSignals`, and `hypotheses`.

### `collect`

Open an EventPipe session and collect a window of events. `--kind` is required.

| Option | Meaning |
|---|---|
| `--kind <kind>` | One of `counters`, `exceptions`, `crash-guard`, `gc`, `datas`, `catalog`, `event_source`, `activities`, `logs`, `jit`, `threadpool`, `contention`, `db`, `kestrel`, `networking`, `requests`, `startup`, `sweep`, `cpu`, `allocation`, `off_cpu` (alias `off-cpu`), `native-alloc`, `thread-snapshot`. |
| `-d, --duration <int>` | Window in seconds (default: `counters` 5, `datas` 15, `sweep` 6, others 10). |
| `--depth <level>` | Verbosity: `summary`, `detail` (default), `raw`. |
| `--top <n>` | Top-N cap for sampler kinds (`cpu`, `allocation`, `off_cpu`, `native-alloc`) and session query pages/ranked views. |
| `--max-events <int>` | Per-kind cap (events / exceptions / activities / catalog occurrence sample). |
| `--interval <int>` | Refresh interval in seconds (`counters`, `db`, `kestrel`, `networking`). Default 1. |
| `--watch <seconds>` | Re-run the command every N seconds, clear/redraw the human output, and stop cleanly on Ctrl-C. Not compatible with `--json`. With `--capture-when` it is reinterpreted as the metric **sample interval** for the bounded gated watch (no redraw loop). |
| `--capture-when <pred>` | Threshold-gated capture (`--kind counters`). Arm a **bounded** watch and capture when a single metric predicate `<metric><op><value>` trips — e.g. `cpu>85`, `gcHeapMb>=1500`, `rssMb>2000`, `threadCount>400`, `activeTimerCount>1000`. Operators: `>` `>=` `<` `<=`. |
| `--capture <kind>` | What to capture on trip: `dump`, `cpu-sample`, `heap`, `thread-snapshot`. Required with `--capture-when`. |
| `--window <seconds>` | Required with `--capture-when`. Hard upper bound on how long the watch is armed (1–300). |
| `--symbol-path <path>` | `NT_SYMBOL_PATH`-style search path for `cpu`, `off_cpu` and `thread-snapshot` symbol resolution. Remote symbol servers remain allowlist-gated just like `inspect-heap`. |
| `--export-trace` | `cpu`: keep the raw `.nettrace` under the artifact root and surface its relative path (default off — the trace is deleted after parsing). Fetch it later with `get-bytes --kind trace`. |
| `--resolve-source-lines` / `--no-resolve-source-lines` | `cpu`: enable/disable source file:line resolution for the top hotspots. Default **on**. |
| `--resolve-method-instantiations` | `cpu`: opt in to a second ClrMD attach after sampling to recover closed generic method signatures for the hottest managed frames. |
| `--native-aot-map <file>` | `cpu` (and gated `--capture cpu-sample`) against a **NativeAOT** target: resolve method names from a `.map.xml` file (the AOT compiler's symbol map) so hot frames show managed method identities instead of raw addresses. Ignored for CoreCLR targets, which resolve symbols from runtime metadata. |
| `--native-alloc-sample-period <n>` | `native-alloc`: perf sample period (default `1000`) — record one call chain per this many allocator hits. Higher values reduce output volume at the cost of resolution. |
| `--dump-file <path>` | `thread-snapshot`: inspect a previously-captured dump instead of attaching to a live pid. Mutually exclusive with `--pid`. |
| `--max-frames-per-thread <n>` | `thread-snapshot`: cap captured frames per thread (default `64`, hard cap `512`). |
| `--include-runtime-frames` | `thread-snapshot`: include CLR/runtime helper frames. Default off. |
| `--include-native-frames` | `thread-snapshot`: include native frames ClrMD cannot map to managed methods. Default off. |
| `--max-captures <int>` | Stop after N captures (default 1, max 10). |
| `--provider <name>` | `counters`: EventCounter provider (repeatable); `catalog`: EventPipe provider (repeatable; replaces broad defaults); `event_source`: required provider name. |
| `--meter <name>` | `counters`: Meter name (repeatable). |
| `--source <name>` | `activities`: ActivitySource filter (repeatable, `*` / `?` globs). |
| `--category <glob>` | `logs`: ILogger category filter (repeatable). |
| `--min-level <level>` | `logs`: minimum level (default `Information`). |
| `--unsafe-provider` | `event_source`: opt in to a non-allowlisted provider. |
| `--save <file>` | Save a comparable snapshot JSON. Supported collect kinds: `counters`, `datas` (`gc-datas`), `gc` (`gc-events`), `contention`, `threadpool`. |

```bash
dotnet-diagnostics-cli collect --kind counters --pid 1234 --duration 5
dotnet-diagnostics-cli collect --kind counters --pid CoreClrSample --watch 2
dotnet-diagnostics-cli collect --kind counters --pid CoreClrSample --capture-when 'cpu>85' --capture cpu-sample --window 60
dotnet-diagnostics-cli collect --kind counters --pid CoreClrSample --capture-when 'rssMb>2000' --capture dump --window 120 --confirm --acknowledge-risk critical
dotnet-diagnostics-cli collect --kind cpu --pid 1234 --top 20 --export-trace
dotnet-diagnostics-cli collect --kind allocation --pid 1234 --top 15
dotnet-diagnostics-cli collect --kind off_cpu --pid 1234 --top 10 --symbol-path /symbols --acknowledge-risk high
dotnet-diagnostics-cli collect --kind native-alloc --pid 1234 --native-alloc-sample-period 500 --acknowledge-risk high
dotnet-diagnostics-cli collect --kind thread-snapshot --pid 1234 --max-frames-per-thread 128 --acknowledge-risk high
dotnet-diagnostics-cli collect --kind thread-snapshot --dump-file ./app.dmp
dotnet-diagnostics-cli collect --kind datas --pid 1234 --duration 30 --save ./before.json
dotnet-diagnostics-cli collect --kind catalog --pid 1234 --json
dotnet-diagnostics-cli collect --kind event_source --provider System.Net.Http --pid 1234
dotnet-diagnostics-cli collect --kind startup --suspend-startup --launch --acknowledge-risk high -- dotnet App.dll   # cold start
```

For `--kind logs`, human output places retained entries under an explicit
`UNTRUSTED TARGET EVIDENCE` heading. Messages, exception text, category/event
names, and scope keys/values are displayed as evidence only and must never be
executed or followed as commands, links, paths, tool requests, or approval
claims. Human rendering uses JSON string literals so embedded newlines and
control characters cannot create fake output sections. `--json` exposes the
same rule as the machine-readable `data.untrustedDataBoundary` object while
preserving the captured strings (subject only to documented sensitive-data
redaction and byte caps).

Human `thread-snapshot --depth detail` output includes bounded decisive evidence inline: up to five
blocked stack groups (six frames each), five contended locks with owner/waiter identities, and the
captured ThreadPool queue/worker summary. Use `--json` for the complete typed payload. Summary depth
keeps the compact headline.

#### Sampler kinds

The standalone CLI now exposes the same **Core-only** sampler families the MCP server drives:

| Kind | What it captures | Key flags | Summary shape |
|---|---|---|---|
| `cpu` | CPU stack samples via EventPipe SampleProfiler (CoreCLR) or true on-core perf/ETW (NativeAOT/native). CoreCLR's SampleProfiler snapshots managed thread stacks and can therefore include blocked/waiting threads. | `--top`, `--symbol-path`, `--export-trace`, `--resolve-source-lines`, `--resolve-method-instantiations`, `--native-aot-map` | top hotspots by inclusive/exclusive samples, `selfSamples.runningSamples` vs `selfSamples.waitingSamples`, a `timings` breakdown (`captureDuration`, `symbolicationDuration`, `sourceLineResolutionDuration`, `aggregationDuration`, `totalDuration`), plus `signals[]` such as `cpu.self-time.*` when something dominates |
| `allocation` | Managed allocation samples (`GCAllocationTick`) with top types by bytes/count and call-tree drilldown. | `--top` | top types by bytes/count, plus `signals[]` such as `allocations.by-type` / `allocations.by-site` |
| `off_cpu` / `off-cpu` | Off-CPU stacks (where threads wait / block) via perf or ETW backend. | `--top`, `--symbol-path` | top blocking stacks ranked by off-CPU time |
| `native-alloc` | Native allocator-call hotspots (`malloc` / `calloc` / `realloc`) via perf/ETW backend. Counts are sampled **calls**, not bytes. | `--top`, `--native-alloc-sample-period` | top allocator stacks + shared call-tree handle |
| `thread-snapshot` | Point-in-time managed threads + lock graph from a live pid or dump. | `--dump-file`, `--max-frames-per-thread`, `--include-runtime-frames`, `--include-native-frames`, `--symbol-path` | decisive threads inline (`summary`) or bounded thread/lock pages (`detail`/`raw`); continue through the session `query` command |

Examples:

```bash
# CPU hotspots + raw trace for offline PerfView / Speedscope:
dotnet-diagnostics-cli collect --kind cpu --pid 1234 --top 20 --export-trace

# Managed allocation pressure:
dotnet-diagnostics-cli collect --kind allocation --pid 1234 --top 15 --json

# Off-CPU blocking stacks:
dotnet-diagnostics-cli collect --kind off_cpu --pid 1234 --top 10 --acknowledge-risk high

# Native allocation hotspots (calls, not bytes):
dotnet-diagnostics-cli collect --kind native-alloc --pid 1234 --native-alloc-sample-period 500 --acknowledge-risk high

# Live or dump-based thread snapshot:
dotnet-diagnostics-cli collect --kind thread-snapshot --pid 1234 --max-frames-per-thread 128 --acknowledge-risk high
dotnet-diagnostics-cli collect --kind thread-snapshot --dump-file ./app.dmp --include-runtime-frames
```

> **Cold-start capture (`--suspend-startup`).** `collect --kind startup` attaching to an
> already-running pid only sees loader/DI activity emitted *after* attach — the initial cold start
> (especially non-replayed DI call-site construction/resolution activity) is gone. Pair
> `--suspend-startup` with `--launch -- <app>` to launch the target *suspended* on a reverse-connect
> `DOTNET_DiagnosticPorts=…,suspend` port, arm EventPipe before any managed code runs, then resume.
> This mirrors dotnet-monitor reverse-connect. The MCP equivalent is
> `collect_events(kind="startup", launch=...)`, available only over stdio when
> `Diagnostics:AllowProcessLaunch=true`. The suspended child + reverse-connect
> socket are cleaned up on exit and on Ctrl-C.

> **Timing.** EventPipe sessions take ~500 ms–1 s to start, and `counters` payloads arrive on
> `--interval` boundaries — give `counters` at least ~6 s. For `exceptions` / `gc`, the collection window
> must overlap the load that generates the events.

> **Threshold-gated capture (`--capture-when`).** A bounded, one-shot watch (the human/CI equivalent
> of DebugDiag `collect`) — **not** a daemon. It polls one `System.Runtime` EventCounter (`rssMb`=`working-set`,
> `threadCount`=`threadpool-thread-count`) every `--watch` seconds (default 2) for at most `--window`
> seconds and captures `--capture` up to `--max-captures` times the moment the predicate trips, then
> returns. `--capture dump` requires `--confirm` plus critical-risk acknowledgement and writes the dump to disk (the path is in the
> result). For `cpu-sample` / `heap` / `thread-snapshot`, the result records carry headline capture
> stats plus a drilldown handle. That handle is only reachable by a later `query` **within the same
> `session`** (the in-memory handle store is disposed when a one-shot command exits) — run gated
> capture inside the `session` REPL when you need to drill into the captured artifact afterward.

> **Less-obvious kinds.** `crash-guard` arms an unhandled-exception / crash watch and reports the
> managed exception that would fault the process. `requests` enumerates in-flight ASP.NET Core
> requests (no ptrace — reads `Microsoft.AspNetCore.Hosting` diagnostics; query-string values are
> dropped to avoid leaking PII). `startup` captures cold-start activity — pair it with
> `--suspend-startup --launch` (below) to see everything before the first managed instruction.
> `sweep` runs a bounded parallel triage sweep across several collectors at once and returns a single
> consolidated verdict, the fastest "what's wrong right now" one-shot.

> **No arbitrary multi-kind batch (unlike MCP `collect_batch`).** The MCP server has a
> `collect_batch` tool (issue #665 Part C) that fans out a *caller-chosen* set of `collect_sample`/
> `collect_events` kinds concurrently against one process/window, avoiding the process-exit race of
> issuing them as separate sequential tool calls with LLM decision latency in between. The CLI's
> answer to "several signals over one shared window" is the fixed-shape `sweep` kind above (same
> `Task.WhenAll` fan-out, just a pre-selected collector set); its answer to "the target might not
> survive a second round-trip" is `--capture-when`/`--capture`/`--window` (a single threshold-gated
> capture, not N kinds at once). An arbitrary-kind CLI batch was discussed and declined (issue #677):
> the `session` REPL already keeps the target's handle/pid bound across commands typed back-to-back
> (no LLM round-trip latency to race), and a one-shot batch verb would need new human-mode rendering
> for N heterogeneous results with no clear net win over `sweep` + a follow-up `collect --kind`.

### `inspect-heap`

Walk the managed heap of a live process or a `.dmp`.

| Option | Meaning |
|---|---|
| `--source <live\|dump\|gcdump>` | Snapshot source. Inferred: `dump` when `--dump-file` is set, else `live`. `gcdump` triggers an induced GC heap snapshot over EventPipe — no ptrace or dump file, but it induces a GC and exposes heap type metadata, so use the resolved safety preflight/canonical matrix — and returns only per-type byte/instance totals (ClrMD-only views stay empty). |
| `--dump-file <path>` | `--source dump`: path to a previously-captured `.dmp`. |
| `--top-types <int>` | Top-N type count (default 20). |
| `--include-retention-paths` | Walk a short GC retention chain for the top types. |
| `--retention-path-limit <int>` | Cap retention-chain depth (default 8). |
| `--include-static-fields` | Rank static reference fields by referenced object size. |
| `--include-delegate-targets` | Group `MulticastDelegate` invocation lists by (target, method). |
| `--include-duplicate-strings` | Rank duplicate strings by aggregate retained bytes. |
| `--symbol-path <path>` | `NT_SYMBOL_PATH`-style search path (remote servers off by default). |
| `--export-trace` | `--source gcdump`: keep the raw `.nettrace` under the artifact root and print its relative path (default off — the trace is deleted after parsing). Fetch it later with `get-bytes --kind trace`. |

```bash
dotnet-diagnostics-cli inspect-heap --pid 1234 --top-types 30 --acknowledge-risk high
dotnet-diagnostics-cli inspect-heap --source dump --dump-file ./app.dmp
dotnet-diagnostics-cli inspect-heap --source gcdump --pid 1234 --acknowledge-risk high   # induced GC, no ptrace
dotnet-diagnostics-cli inspect-heap --source gcdump --pid 1234 --export-trace --acknowledge-risk high  # keep raw .nettrace
dotnet-diagnostics-cli inspect-heap --launch --acknowledge-risk high -- dotnet App.dll   # ptrace_scope=1, no privilege
```

`--source live` attaches via `ptrace(2)` — see the [Linux note](#linux-ptrace-note), which also
documents the `--launch` zero-privilege dev mode.

### `dump`

Write a process dump to disk. Without `--confirm`, a **preview** is returned (and still exits 0);
because no dump is written, the preview does not require risk acknowledgement. Actual execution
requires both `--confirm` and critical-risk approval.

| Option | Meaning |
|---|---|
| `--dump-type <type>` | `Mini` (default), `Triage`, `WithHeap`, `Full`. |
| `--out <dir>` | Directory to write into (default: temp artifact root). |
| `--confirm` | Required to actually write. In one-shot/non-interactive use, combine it with `--acknowledge-risk critical`. In an interactive `session`, it enables the single safety prompt; there is no second dump-specific prompt. |

```bash
dotnet-diagnostics-cli dump --pid 1234 --dump-type WithHeap --out ./dumps \
  --confirm --acknowledge-risk critical
```

> **Scripting.** Parse `--json` to tell a preview apart from a written dump:
> `data.kind == "confirmation_required"` (preview) vs `data.kind == "dump_written"`.
>
> The preview discloses the resolved artifact directory the dump *would* be written to
> (`would write to : <dir>`) so you can confirm the destination before re-running with `--confirm`
> and `--acknowledge-risk critical`. In an interactive `session`, use `--confirm` and type `critical`
> at the safety prompt.

### `get-bytes`

Materialise a module (PE/PDB), a dump file, or a raw `.nettrace` to disk.

| Option | Meaning |
|---|---|
| `--kind <module\|dump\|trace>` | Required. Artifact to materialise. |
| `--out <file>` | Required. Destination file. |
| `--mvid <guid>` | `--kind module`: module version id (GUID) to fetch. |
| `--asset <pe\|pdb>` | `--kind module`: artifact within the module (default `pe`). |
| `--dump-file <path>` | `--kind dump\|trace`: path to the source `.dmp` / `.nettrace` to copy out. |

```bash
dotnet-diagnostics-cli get-bytes --kind module --pid 1234 --mvid <guid> --out ./app.dll --acknowledge-risk critical
dotnet-diagnostics-cli get-bytes --kind dump --dump-file ./app.dmp --out ./copy.dmp --acknowledge-risk critical
dotnet-diagnostics-cli get-bytes --kind trace --dump-file ./cpu.nettrace --out ./cpu.copy.nettrace --acknowledge-risk critical
```

### `compare`

Compare two or more saved comparable snapshots from `collect --save`. Human output keeps the compact verdict, first→last headline, and top metric/key deltas in the terminal; `--json` emits the full `SnapshotJourneyDiff`, and `--save` writes that full matrix to a file. Local MCP `compare_to_baseline` / `query_snapshot(view="diff")` calls use a `journey://diff/{handle}` Resource link when the matrix is large. Proxied pod calls return full results inline because dynamic pod Resources are not forwarded.

| Option | Meaning |
|---|---|
| `--json` | Emit the full journey diff JSON. |
| `--save <file>` | Write the full journey diff JSON to disk. |
| `--mode trend\|dispersion` | Interpret captures as an ordered trend (default) or unordered replicas for dispersion/outlier detection. |

```bash
dotnet-diagnostics-cli compare ./before.json ./after.json
dotnet-diagnostics-cli compare ./pod-a.json ./pod-b.json ./pod-c.json --mode dispersion
dotnet-diagnostics-cli compare ./before.json ./mid.json ./after.json --save ./matrix.json
```

For how to read the verdict / trend and when to reach for a journey, see
[investigation-playbooks.md §1d](./investigation-playbooks.md#1d-did-my-fix-actually-help--comparative--n-way-trend-journeys).

### `investigate`

Plan a triage investigation. The planner classifies the likely failure mode from what you observed and
returns an ordered, branching plan (next step + rationale + all candidate steps + early-stop conditions)
that you execute yourself with the other CLI commands — the CLI stays **stateless**, it never runs the
plan for you. Requires `--symptom <text>` (or `--hypothesis <text>`) so the plan is anchored to a real
observation; `--max-tool-calls <n>` (default 8) caps the plan length. Steps are rendered in CLI
vocabulary (e.g. `collect`), never MCP tool names.

| Option | Meaning |
|---|---|
| `--symptom <text>` | What you observed (e.g. `"p99 latency spiked"`). Required unless `--hypothesis` is given. |
| `--hypothesis <text>` | A suspected root cause to test (switches the plan into hypothesis mode). |
| `--max-tool-calls <n>` | Upper bound on plan length (default 8). |

```bash
dotnet-diagnostics-cli investigate --pid 1234 --symptom "high CPU after deploy"
dotnet-diagnostics-cli investigate --pid 1234 --hypothesis "lock contention on the cache" --json
```

### `export-summary`

Project a CPU-sample handle into a **portable investigation summary** (JSON): the top hotspots plus
metadata, suitable for attaching to a ticket or feeding another tool. Requires `--handle <id>` from a
`collect --kind cpu` (gated `--capture cpu-sample`) inside a `session`. With no `--out`, the portable
JSON document is written to stdout **verbatim** (identical to what `--out` persists), so it pipes
cleanly; `--out <file>` writes it atomically instead.

| Option | Meaning |
|---|---|
| `--handle <id>` | CPU-sample handle to summarize (required). |
| `--top-hotspots <n>` | Number of hotspots to include (default 10). |
| `--out <file>` | Write the summary to a file (atomic) instead of stdout. |

```bash
# inside a session, after a cpu-sample handle exists:
export-summary --handle h-abc123 --top-hotspots 15
export-summary --handle h-abc123 --out ./cpu-summary.json
```

### Signal-grouping layer

`collect --kind counters`, `exceptions`, `gc`, `sweep`, `cpu`, `allocation`, and `thread-snapshot`
embed a top-level `signals[]` array in
the JSON envelope (`--json` or `--depth detail`/`raw`) whenever something is salient — the same
diagnosis-agnostic "vector" the MCP server documents in
[tool-reference.md](./tool-reference.md#signal-grouping-layer): a stable `signal` id (e.g.
`exceptions.by-type`, `gc.gen2-share`, `counters.trend`, `cpu.self-time.method`,
`allocations.by-type`, `threads.by-wait-target`, `correlation.thread-overlap`), a one-line
`summary`, a `salience` in `[0,1]`, and `buckets[]` referencing a handle. It groups and correlates;
it never names a root cause or a fix. Omitted from the wire when nothing stands out.

```bash
dotnet-diagnostics-cli collect --kind cpu --pid 1234 --json
# ⇒ top-level "signals": [{ "signal": "cpu.self-time.method", "salience": 0.93, ... }] when one
#   method dominates exclusive self-time
```

`off_cpu` and `native-alloc` do **not** currently emit dedicated `signals[]` groupings; their value is
the ranked stack output and the shared `session query` drilldowns.

### `query`

Re-render a previously-collected handle under a different view **without re-collecting**.

This is **only meaningful inside a `session`** — drill-down handles live for the lifetime of the host, and
the one-shot CLI builds a fresh host per command and exits. Run one-shot, `query` returns a `NotSupported`
envelope (exit 1) that redirects you to `dotnet-diagnostics session`, where a `collect` (or `inspect-heap` /
`dump`) issues a handle you can drill into in the same session; for a one-shot answer instead, re-run the
originating command with `--depth detail` / `--json` to get the full result inline.
Inside `session`, `query --handle <id> --view <view>` works against the live handle store (see below).

### `session`

Start the stateful REPL — covered in the next section. Accepts `--launch -- <app> [args]` at startup
to spawn the target as a child and bind it for the whole session (zero-privilege live attach under
`ptrace_scope=1`; see the [Linux note](#linux-ptrace-note)). The child is killed when the session ends.

## The `session` REPL

`session` builds the diagnostic host once and reads commands from stdin until `exit` / `quit` / EOF. Every
handle published by `collect` or `inspect-heap` stays alive (until it expires or the
target exits), so you can drill in repeatedly with `query` and never pay the collection cost twice.

When stdin/stdout are a genuine interactive terminal (not redirected/piped — as in CI or when scripting),
the REPL line editor adds (issue #657):

- **Command history** — Up/Down-arrow (or Ctrl-P/Ctrl-N) recall previous commands from this session. History
  is in-memory only and not persisted to disk (session commands routinely carry pids and file paths).
- **Tab-completion** — Tab (or Ctrl+Space) offers command names, flags, and known enum values (e.g. `--kind`,
  `--source`, `--view`) for the current word, narrowing as you type. A bound `target` pid is offered for
  `--pid`/`-p`.

Redirected/piped input (e.g. `echo 'processes' | dotnet-diagnostics-cli session`, or the test harness)
transparently falls back to plain line reading — no history or completion, same as before.

```text
$ dotnet-diagnostics-cli session
dotnet-diagnostics session — stateful diagnostics REPL. ...
diag> target 1234
Target bound to pid 1234. capabilities/collect/inspect-heap/dump/get-bytes now use it unless you pass --pid.
diag(pid 1234)> collect --kind gc --duration 10
  · using bound target pid 1234
... GC summary ...
  → handle 1TA2BA7KT9PYT60WTWE0 (expires 23:10:18Z) — query --handle 1TA2BA7KT9PYT60WTWE0 --view <pauseHistogram|...>
diag(pid 1234)> query --handle 1TA2BA7KT9PYT60WTWE0 --view pauseHistogram
... re-rendered view, no re-collection ...
diag(pid 1234)> collect --kind datas --duration 15 --save before.json
diag(pid 1234)> collect --kind datas --duration 15 --save after.json
diag(pid 1234)> compare before.json after.json
diag(pid 1234)> exit
```

Starting with `session --launch --acknowledge-risk high -- dotnet App.dll` spawns the target as a child, binds its pid for the
whole session (so live attach works under `ptrace_scope=1` with no privilege), and kills it on exit.
`--launch` is a startup-only flag — it cannot be repeated per-command inside the REPL. The bound pid is
also **fixed for the session's lifetime** (issue #659): since the zero-privilege attach and the child's
lifecycle only hold for the pid the session itself spawned, `target <other-pid>`/`target clear` and any
per-command `--pid <other-pid>` are rejected with an explanatory error; exit the session to investigate
a different process. A no-argument `target` (status query) and a redundant `--pid <the-launched-pid>`
still work.

### Target binding

Bind a target pid once instead of repeating `--pid` on every command. The binding accepts either a
literal pid or a visible .NET process name/prefix using the same matching rules as `--pid <name>`:

| Input | Effect |
|---|---|
| `target <pid>` / `target --pid <pid>` | Bind a default pid. The prompt becomes `diag(pid <id>)>`. |
| `target <name-prefix>` / `target --pid <name-prefix>` | Resolve exactly one visible .NET process by entrypoint/name prefix and bind its pid. Ambiguous matches list pid + name. |
| `target` | Show the current binding. |
| `target clear` (or `none` / `off` / `unset`) | Unbind. |

Live-target commands — `capabilities`, `collect`, `dump`, `inspect-heap --source live`,
`get-bytes --kind module` — inherit the bound pid when `--pid` is omitted, and print a
`· using bound target pid N` note. Offline commands (`inspect-heap --source dump`, `get-bytes --kind dump`)
and pid-less commands (`processes`, `query`) never inherit it. **An explicit per-command `--pid` always
overrides the binding** — except in a `session --launch`-started session, where the pid is locked for
the session's lifetime and any different `--pid`/`target` is rejected (see above). Follow-up hints
omit `--pid N` when that same pid is already supplied by the session binding.

#### Target-exit signal (issue #675)

A background check independently watches the bound target every ~5 s and — separately from any
handle you may have collected — prints a **one-time** notice the first time it detects the bound pid
has exited:

```text
diag(pid 1234)> [session] target pid 1234 has exited; self-contained handles (e.g. process dumps) remain queryable, but new captures against this pid will fail and any live-sourced handles for this pid are dropped by the periodic sweep.
```

A no-argument `target` also actively re-checks liveness and reflects it in the report (`Target bound to
pid 1234 (exited).`), and on a real terminal, tab-completing `collect --kind <TAB>` after the notice
annotates every candidate (`cpu  (target exited)`) since a new capture against a dead pid will fail
immediately — self-contained follow-ups (`query --handle <id> --view <view>`) are unaffected, per #662,
and so is `collect --pid <other-pid> --kind <TAB>` (an explicit `--pid`/`-p` overrides the exited
session binding for that one invocation, so it isn't annotated).
Binding a fresh live pid (`target <pid>`) clears the exited state.

### Handles and `query`

A `collect` or `inspect-heap` command prints a handle plus the views you can re-render:

```text
  → handle <id> — query --handle <id> --view <view1|view2|...>
```

`query --handle <id> --view <view>` re-renders that artifact under the chosen view with no new collection.
Handles are evicted when they expire (a TTL) or when the target process exits — a 5 s in-process sweep drops
dead-target handles so you never drill into a stale trace.

Handles are **process-local**. A handle printed by a one-shot invocation disappears when that CLI
process exits and cannot be queried by a later invocation; one-shot human and JSON output include
this notice. Use `--depth detail` / `--json` for inline evidence, or run the originating command and
`query` inside the same `session` REPL.

The same strictly bounded store as the MCP server is used. Configure its
capacity before starting the CLI with
`Diagnostics__HandleStore__MaxEntries` (default `32`, valid range `1..1024`).
When full, it first removes expired entries, then evicts the handle with the
earliest expiry deadline (oldest registration breaks ties). A bounded
four-per-entry tombstone set lets `query` distinguish `HandleExpired`,
`HandleCapacityEvicted`, and `HandleNotFound`; capacity errors tell you to
re-run the originating command and name the capacity setting. No evicted
artifact is retained for this diagnosis.

For CPU/allocation sample handles (`cpu-sample`, `allocation-sample`, `native-alloc-sample`), the session
exposes drilldown views computed from the merged call tree without re-sampling:

| View | What it shows | Relevant flags |
| --- | --- | --- |
| `call-tree` (default) | the merged inclusive/exclusive call tree; CPU handles can also carry `selfSamples` on the view and per node | `--max-depth` (tree depth, default `8`), `--max-nodes` (default `64`; larger requests are clamped to the `64`-node wire cap), `--min-count`, `--root-method-filter`, `--rank-by` |
| `top-methods` | methods ranked by sample cost; CPU handles include per-method `selfSamples.runningSamples` vs `selfSamples.waitingSamples` | `--top` (default `20`), `--rank-by exclusive\|inclusive` |
| `by-module` | samples grouped by owning module | `--top`, `--rank-by` |
| `by-namespace` | samples grouped by namespace | `--top`, `--rank-by` |
| `hot-path` | the dominant stack from the root down; CPU handles include per-frame `selfSamples` | `--threshold` (percent, default `50`) |
| `caller-callee` | a focus method with its direct callers + callees; CPU handles include the focus method's `selfSamples` | `--root-method-filter <substring>` (required), `--top` |

For session ranked views, `--top` is preferred. The older `--top-types` remains a compatibility
alias; when both are present, `--top` wins.

`--rank-by inclusive` ranks/credits by inclusive samples; any other value (including the default) uses
exclusive samples. `caller-callee` requires `--root-method-filter` to resolve exactly one method: zero matches
return a `NotFound` envelope, more than one returns `InvalidArgument` with the candidate list.

For `collect --kind cpu`, interpret `selfSamples` as a **self/exclusive-time split**:
`runningSamples` are leaf frames that do not match a known wait primitive; `waitingSamples`
are leaf frames such as `Monitor.Wait`, `WaitHandle.Wait*`, `LowLevelLifoSemaphore.*`,
`SemaphoreSlim.Wait*`, `Task.Wait`, or ThreadPool idle waits. On CoreCLR this helps you spot
when EventPipe SampleProfiler is surfacing blocked managed threads rather than genuine on-core
work. For direct blocking analysis, pivot to `collect --kind off_cpu` or `collect --kind thread-snapshot`.

GC handles (`collect --kind gc`) expose pause-analysis views over the events already collected:

| View | What it shows | Relevant flags |
| --- | --- | --- |
| `summary` (default) | total/max pause + per-generation counts | — |
| `events` | raw GC events | `--top` (`--top-types` compatibility alias) |
| `pauseHistogram` | pause-duration buckets | — |
| `timeline` | per-GC rows (index, gen, reason, type, pause, gap-since-previous-start) ordered by start time | `--top` (`--top-types` compatibility alias; earliest N) |
| `longestPauses` | the N longest pauses, ranked descending | `--top` (`--top-types` compatibility alias) |
| `byGeneration` | count + total/mean/max pause per gen0/gen1/gen2/background bucket | — |

`byGeneration` keeps background GCs in their own bucket, so `gen2` counts non-background gen2 collections only.

Catalog handles (`collect --kind catalog`) expose a metadata-only event inventory. The collector captures
provider name, event name, level and timestamps only — no payload field values. By default it enables a
broad curated provider set (`Microsoft-Windows-DotNETRuntime`, `System.Runtime`,
`Microsoft-Diagnostics-DiagnosticSource`, `Microsoft-Extensions-Logging`,
`System.Threading.Tasks.TplEventSource`) at Informational level; pass `--provider` one or more times to
replace that set for custom EventSources, because EventPipe cannot wildcard providers.

| View | What it shows | Relevant flags |
| --- | --- | --- |
| `catalog` (default) | distinct `(provider,eventName,level)` rows ranked by count | `--top`, `--provider-filter`, `--root-method-filter` (event-name substring) |
| `byProvider` | provider rollup with total count + distinct event type count | `--top`, `--provider-filter`, `--root-method-filter` |
| `events` | bounded chronological metadata occurrence sample, never payloads | `--top`, `--provider-filter`, `--root-method-filter` |

Use the targeted `event_source` collector if you need payload values; it carries the allowlist/redaction gates.

DATAS handles (`collect --kind datas`) expose the Server GC's **D**ynamic **A**daptation **T**o
**A**pplication **S**izes tuning loop (default-on in .NET 9+; Workstation GC emits nothing, returning a
graceful `NoDatasEvents` result). The collector decodes the three DATAS `GCDynamicEvent` payloads from
`Microsoft-Windows-DotNETRuntime` (`GCKeyword`, Informational). The default window is 15 s — DATAS
decisions accrue over time, so a sustained window is best.

| View | What it shows | Relevant flags |
| --- | --- | --- |
| `overview` (default) | heap-count range + change count, TCP statistics, mean gen0 budget / SOH stable size | — |
| `tuning` | per-decision heap-count timeline | `--top`, `--changes-only` (only transitions + baseline) |
| `samples` | per-GC measurements behind the decisions | `--top` |
| `gen2` | gen2 "backstop" tuning events | `--top` |

Heap-snapshot handles (`inspect-heap`) expose the projection views rendered from the walked snapshot
(`top-types`, `retention-paths`, `roots-by-kind`, `finalizer-queue`, `fragmentation`, `static-fields`,
`delegate-targets`, `gchandles`, `async`, `timers`, `alc`) plus two address-addressed drilldowns:

| View | What it shows | Relevant flags |
| --- | --- | --- |
| `top-types` (default) | top types by bytes/instances | `--top` (`--top-types` compatibility alias), `--rank-by bytes\|instances` |
| `retention-paths` | short GC retention chains | `--type-filter <substring>`, `--top` (`--top-types` compatibility alias) |
| `gcroot` | shortest GC-root chain for one object (SOS `!gcroot`) | `--address <decimal\|0x-hex>` (**dump-origin handles only**) |
| `object` | one managed object's shape (SOS `!do`) | `--address <decimal\|0x-hex>` (**dump-origin handles only**) |

`gcroot` and `object` re-open the snapshot's origin with ClrMD to answer the address-scoped question.
The Core-only session serves them for **dump-origin** handles (`inspect-heap --source dump`) by re-reading
the recorded `.dmp` — no live attach — so an offline dump can still answer "what roots this object". The
`object` view never prints raw string/field values in-session (the standalone CLI holds no sensitive-value
gate); previews are replaced with `<redacted:metadata-only>`. Live-origin `gcroot`/`object` and the
`objsize` / `duplicate-strings` views stay server-only — use the MCP server's `query_snapshot` tool.

Off-CPU handles (`collect --kind off_cpu` or `off-cpu`) expose the off-CPU drilldowns already
captured in the artifact:

| View | What it shows | Relevant flags |
| --- | --- | --- |
| `topStacks` (default) | blocking stacks ranked by off-CPU time | `--top` (`--top-types` compatibility alias) |
| `byThread` | per-thread off-CPU rollup | `--top` (`--top-types` compatibility alias) |
| `stack` | one specific blocking stack | `--stack-rank <n>` |

Thread-snapshot handles (`collect --kind thread-snapshot`, or a gated `--capture thread-snapshot`
inside a `session`) expose the call-stack / blocking views (`threads-summary`, `stack`,
`lock-graph`, `deadlocks`, `top-blocked`, `unique-stacks`, `async-stalls`, `wait-chains`,
`threadpool`) plus `frame-vars`:

| View | What it shows | Relevant flags |
| --- | --- | --- |
| `threads-summary` | decisive thread summaries, eight per bounded page with up to eight frames each | `--top` (page size, capped at 8), `--cursor <opaque>` from `nextThreadCursor` |
| `top-blocked` (default) | blocked threads and real lock waiters, eight per bounded page with up to eight frames each | `--top` (page size, capped at 8), `--cursor <opaque>` from `nextThreadCursor` |
| `lock-graph` | contended locks, twelve per bounded page; or one lock's waiter IDs | `--top` (page size, capped at 12/8), `--cursor <opaque>` from `nextLockCursor`; add `--address <decimal\|0x-hex>` and use `nextWaiterCursor` to page one lock's waiters |
| `wait-chains` | ranked CoreCLR monitor waiter→owner and async-continuation chains, including ThreadPool-starvation sinks and inferred cycle candidates; links expose `edgeSource` / `confidence` | — |
| `async-stalls` | stalled `async` state machines and their await points | — |
| `unique-stacks` | threads folded into shared stack signatures, ranked by group size | `--frames-to-hash` (top frames in the signature hash, default `20`), `--min-count` (drop groups smaller than N, default `1`) |
| `frame-vars` | one thread's local variables and parameters for a chosen stack frame (re-opens the origin via ClrMD) | `--thread-id <id>` (required) |

`frame-vars` requires `--thread-id` to pick the thread whose frame variables to resolve; the thread must
be present in the captured snapshot.
MCP and CLI session queries share the same versioned opaque cursors. Pass a returned cursor unchanged
to the next `query` command; it is bound to the snapshot handle, view, deterministic final sort key,
and (for waiter pages) lock address. Malformed and cross-handle cursors fail with `InvalidArgument`.
`--offset` remains a compatibility option for positions `0..256`; deeper random offsets are rejected
with guidance to restart at zero and follow cursors, avoiding quadratic rescans.

For every ranked/list query view, `--top N` is the common row/group cap. `--top-types N` remains a
backward-compatible query alias; when both are supplied, `--top` wins. Views whose shape is controlled
by a different bound (`call-tree` uses `--max-nodes`, `stack` selects one rank/thread) are unchanged.

### Cancellation (Ctrl-C)

- **While a command runs:** the first Ctrl-C cancels only that command (cleaning up any temp `.nettrace` /
  perf files) and keeps the session alive; a second Ctrl-C force-quits the process.
- **At an idle prompt:** Ctrl-C leaves the session (exit code 130). `exit` / `quit` / EOF leave cleanly
  (exit code 0).

## Linux ptrace note

`inspect-heap --source live` attaches via `ptrace(2)`. On
Debian/Ubuntu/WSL the default `kernel.yama.ptrace_scope=1` blocks same-UID peer attach, surfacing as a
`PermissionDenied` envelope. Prefer a path that preserves the host policy:

- **Zero privilege (dev):** `--launch -- <app> [args]` makes the CLI the target's parent. Under
  `ptrace_scope=1` a tracer may attach to its own descendants, so live attach works with no sysctl
  change and no capability:

  ```bash
  dotnet-diagnostics-cli inspect-heap --launch --acknowledge-risk high -- dotnet App.dll
  dotnet-diagnostics-cli session --launch --acknowledge-risk high -- dotnet App.dll   # binds the child for the whole session
  ```

  Launch the app **directly** (`dotnet App.dll` or a published apphost), not via `dotnet run` (which
  spawns a separate runtime child whose PID won't match). The child is killed when the command /
  session exits. This only helps under `ptrace_scope=1`; `scope=2` still needs `CAP_SYS_PTRACE` and
  `scope=3` forbids attach entirely — use the dump-based workflow there. When `capabilities` detects
  this exact environment it advertises the `--launch` tip.
- **No live attach:** use `inspect-heap --source dump --dump-file <path>` for offline analysis,
  or use EventPipe-backed `collect` kinds when they answer the diagnostic question. The `dump`
  command writes through diagnostic IPC and does not require Linux `CAP_SYS_PTRACE`.
- **Container or Kubernetes:** if live attach is required, add `CAP_SYS_PTRACE` only to the
  diagnostics sidecar (Docker `--cap-add SYS_PTRACE`; Kubernetes
  `securityContext.capabilities.add`).
- **Bare host, isolated personal-development only:** `echo 0 | sudo tee
  /proc/sys/kernel/yama/ptrace_scope` enables peer attach, but relaxes a **host-wide security
  boundary** for every same-UID process. Never use this on a shared or production host. See the
  canonical [consumer-install safety note](./consumer-install.md#15-linux-enabling-live-memory-readers-kernel-ptrace).

The MCP sidecar must also run as the **same UID** as the target so it can open
`/tmp/dotnet-diagnostic-<pid>`. EventPipe-based commands (`collect`, counters, GC, exceptions)
do not need `CAP_SYS_PTRACE`, but still need access to that diagnostic socket.

## See also

- [`consumer-install.md`](./consumer-install.md) — install walkthrough (MCP server distributions)
- [`client-setup.md`](./client-setup.md) — connecting an MCP **client** to the server
- [`tool-reference.md`](./tool-reference.md) — the MCP **tool** surface (the server's analogue of these commands)
