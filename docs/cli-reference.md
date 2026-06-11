# `dotnet-diagnostics-cli` — standalone CLI reference

`dotnet-diagnostics-cli` is the **human-facing** counterpart to the `dotnet-diagnostics-mcp` server. Both
ship from this repository and run the **same Core diagnostics engine**, but they target different consumers:

| | `dotnet-diagnostics-cli` (this doc) | `dotnet-diagnostics-mcp` (the server) |
|---|---|---|
| Consumer | A human, a shell script, a CI job | An LLM, via an MCP client |
| Surface | Sub-commands you type | MCP tools the model calls |
| Transport | None — in-process, one-shot or REPL | Streamable HTTP (bearer auth) or stdio |
| State | One-shot, or a `session` REPL holding handles | MCP session holding handles |
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

## Global options

These apply to every command:

| Option | Meaning |
|---|---|
| `-p, --pid <int>` | Target OS process id. **Auto-resolved** when exactly one .NET process is visible. |
| `--json` | Emit the raw `DiagnosticResult<T>` envelope as JSON instead of the human table. |
| `--launch -- <app> [args]` | **Dev mode.** Launch `<app>` as a child of the CLI so live attach works under `kernel.yama.ptrace_scope=1` with no privilege — see the [Linux note](#linux-ptrace-note). Supported by `capabilities`, `collect`, `dump`, `inspect-heap` (live), `get-bytes` (module) and `session`. Mutually exclusive with `--pid`; the child is terminated on exit. |
| `-h, --help` | Show the global usage screen, or a focused screen for `<command> --help`. |

Exit codes: `0` success (a `dump` preview is also a success), `1` a structured failure envelope
(e.g. `NotSupported`, `PermissionDenied`), `2` a usage / validation error.

## Commands

### `processes`

List attachable .NET processes (pid, runtime, OS/arch, entrypoint).

```bash
dotnet-diagnostics-cli processes
dotnet-diagnostics-cli processes --json
```

### `capabilities`

Probe a target's diagnostic capability matrix — CoreCLR vs NativeAOT, whether CPU sampling / gcdump /
live attach are available.

```bash
dotnet-diagnostics-cli capabilities --pid 1234
```

### `collect`

Open an EventPipe session and collect a window of events. `--kind` is required.

| Option | Meaning |
|---|---|
| `--kind <kind>` | One of `counters`, `exceptions`, `gc`, `datas`, `catalog`, `event_source`, `activities`, `logs`, `jit`, `threadpool`, `contention`, `db`. |
| `-d, --duration <int>` | Window in seconds (default: `counters` 5, `datas` 15, others 10). |
| `--depth <level>` | Verbosity: `summary`, `detail` (default), `raw`. |
| `--max-events <int>` | Per-kind cap (events / exceptions / activities / catalog occurrence sample). |
| `--interval <int>` | Refresh interval in seconds (`counters`, `db`). Default 1. |
| `--provider <name>` | `counters`: EventCounter provider (repeatable); `catalog`: EventPipe provider (repeatable; replaces broad defaults); `event_source`: required provider name. |
| `--meter <name>` | `counters`: Meter name (repeatable). |
| `--source <name>` | `activities`: ActivitySource filter (repeatable, `*` / `?` globs). |
| `--category <glob>` | `logs`: ILogger category filter (repeatable). |
| `--min-level <level>` | `logs`: minimum level (default `Information`). |
| `--unsafe-provider` | `event_source`: opt in to a non-allowlisted provider. |
| `--save <file>` | Save a comparable snapshot JSON. Supported collect kinds: `counters`, `datas` (`gc-datas`), `gc` (`gc-events`), `contention`, `threadpool`. |

```bash
dotnet-diagnostics-cli collect --kind counters --pid 1234 --duration 5
dotnet-diagnostics-cli collect --kind datas --pid 1234 --duration 30 --save ./before.json
dotnet-diagnostics-cli collect --kind catalog --pid 1234 --json
dotnet-diagnostics-cli collect --kind event_source --provider System.Net.Http --pid 1234
```

> **Timing.** EventPipe sessions take ~500 ms–1 s to start, and `counters` payloads arrive on
> `--interval` boundaries — give `counters` at least ~6 s. For `exceptions` / `gc`, the collection window
> must overlap the load that generates the events.

### `inspect-heap`

Walk the managed heap of a live process or a `.dmp`.

| Option | Meaning |
|---|---|
| `--source <live\|dump>` | Snapshot source. Inferred: `dump` when `--dump-file` is set, else `live`. |
| `--dump-file <path>` | `--source dump`: path to a previously-captured `.dmp`. |
| `--top-types <int>` | Top-N type count (default 20). |
| `--include-retention-paths` | Walk a short GC retention chain for the top types. |
| `--retention-path-limit <int>` | Cap retention-chain depth (default 8). |
| `--include-static-fields` | Rank static reference fields by referenced object size. |
| `--include-delegate-targets` | Group `MulticastDelegate` invocation lists by (target, method). |
| `--include-duplicate-strings` | Rank duplicate strings by aggregate retained bytes. |
| `--symbol-path <path>` | `NT_SYMBOL_PATH`-style search path (remote servers off by default). |

```bash
dotnet-diagnostics-cli inspect-heap --pid 1234 --top-types 30
dotnet-diagnostics-cli inspect-heap --source dump --dump-file ./app.dmp
dotnet-diagnostics-cli inspect-heap --launch -- dotnet App.dll   # ptrace_scope=1, no privilege
```

`--source live` attaches via `ptrace(2)` — see the [Linux note](#linux-ptrace-note), which also
documents the `--launch` zero-privilege dev mode.

### `dump`

Write a process dump to disk. Requires `--confirm`; without it a **preview** is returned (and still
exits 0).

| Option | Meaning |
|---|---|
| `--dump-type <type>` | `Mini` (default), `Triage`, `WithHeap`, `Full`. |
| `--out <dir>` | Directory to write into (default: temp artifact root). |
| `--confirm` | Required to actually write. |

```bash
dotnet-diagnostics-cli dump --pid 1234 --dump-type WithHeap --out ./dumps --confirm
```

> **Scripting.** Parse `--json` to tell a preview apart from a written dump:
> `data.kind == "confirmation_required"` (preview) vs `data.kind == "dump_written"`.

### `get-bytes`

Materialise a module (PE/PDB) or a dump file to disk.

| Option | Meaning |
|---|---|
| `--kind <module\|dump>` | Required. Artifact to materialise. |
| `--out <file>` | Required. Destination file. |
| `--mvid <guid>` | `--kind module`: module version id (GUID) to fetch. |
| `--asset <pe\|pdb>` | `--kind module`: artifact within the module (default `pe`). |
| `--dump-file <path>` | `--kind dump`: path to the source `.dmp` to copy out. |

```bash
dotnet-diagnostics-cli get-bytes --kind module --pid 1234 --mvid <guid> --out ./app.dll
dotnet-diagnostics-cli get-bytes --kind dump --dump-file ./app.dmp --out ./copy.dmp
```

### `compare`

Compare two or more saved comparable snapshots from `collect --save`. Human output keeps the compact verdict, first→last headline, and top metric/key deltas in the terminal; `--json` emits the full `SnapshotJourneyDiff`, and `--save` writes that full matrix to a file. The MCP `compare_to_baseline` / `query_snapshot(view="diff")` path follows the same contract but uses a `journey://diff/{handle}` Resource link instead of a file when the matrix is large.

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

### `query`

Re-render a previously-collected handle under a different view **without re-collecting**.

This is **only meaningful inside a `session`** — drill-down handles live for the lifetime of the host, and
the one-shot CLI builds a fresh host per command and exits. Run one-shot, `query` returns a `NotSupported`
envelope (exit 1); the one-shot path instead emits its full result inline (use `--depth detail` / `--json`).
Inside `session`, `query --handle <id> --view <view>` works against the live handle store (see below).

### `session`

Start the stateful REPL — covered in the next section. Accepts `--launch -- <app> [args]` at startup
to spawn the target as a child and bind it for the whole session (zero-privilege live attach under
`ptrace_scope=1`; see the [Linux note](#linux-ptrace-note)). The child is killed when the session ends.

## The `session` REPL

`session` builds the diagnostic host once and reads commands from stdin until `exit` / `quit` / EOF. Every
handle published by `collect` or `inspect-heap` stays alive (until it expires or the
target exits), so you can drill in repeatedly with `query` and never pay the collection cost twice.

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

Starting with `session --launch -- dotnet App.dll` spawns the target as a child, binds its pid for the
whole session (so live attach works under `ptrace_scope=1` with no privilege), and kills it on exit.
`--launch` is a startup-only flag; inside the REPL the target is already live — use `target <pid>` to
switch.

### Target binding

Bind a target pid once instead of repeating `--pid` on every command:

| Input | Effect |
|---|---|
| `target <pid>` (or `target --pid <pid>`) | Bind a default pid. The prompt becomes `diag(pid <id>)>`. |
| `target` | Show the current binding. |
| `target clear` (or `none` / `off` / `unset`) | Unbind. |

Live-target commands — `capabilities`, `collect`, `dump`, `inspect-heap --source live`,
`get-bytes --kind module` — inherit the bound pid when `--pid` is omitted, and print a
`· using bound target pid N` note. Offline commands (`inspect-heap --source dump`, `get-bytes --kind dump`)
and pid-less commands (`processes`, `query`) never inherit it. **An explicit per-command `--pid` always
overrides the binding.**

### Handles and `query`

A `collect` or `inspect-heap` command prints a handle plus the views you can re-render:

```text
  → handle <id> — query --handle <id> --view <view1|view2|...>
```

`query --handle <id> --view <view>` re-renders that artifact under the chosen view with no new collection.
Handles are evicted when they expire (a TTL) or when the target process exits — a 5 s in-process sweep drops
dead-target handles so you never drill into a stale trace.

For CPU/allocation sample handles (`cpu-sample`, `allocation-sample`, `native-alloc-sample`), the session
exposes drilldown views computed from the merged call tree without re-sampling:

| View | What it shows | Relevant flags |
| --- | --- | --- |
| `call-tree` (default) | the merged inclusive/exclusive call tree | `--max-nodes`, `--min-count`, `--root-method-filter`, `--rank-by` |
| `top-methods` | methods ranked by sample cost | `--top` (default `20`), `--rank-by exclusive\|inclusive` |
| `by-module` | samples grouped by owning module | `--top`, `--rank-by` |
| `by-namespace` | samples grouped by namespace | `--top`, `--rank-by` |
| `hot-path` | the dominant stack from the root down | `--threshold` (percent, default `50`) |
| `caller-callee` | a focus method with its direct callers + callees | `--root-method-filter <substring>` (required), `--top` |

`--rank-by inclusive` ranks/credits by inclusive samples; any other value (including the default) uses
exclusive samples. `caller-callee` requires `--root-method-filter` to resolve exactly one method: zero matches
return a `NotFound` envelope, more than one returns `InvalidArgument` with the candidate list.

GC handles (`collect --kind gc`) expose pause-analysis views over the events already collected:

| View | What it shows | Relevant flags |
| --- | --- | --- |
| `summary` (default) | total/max pause + per-generation counts | — |
| `events` | raw GC events | `--top-types` (cap) |
| `pauseHistogram` | pause-duration buckets | — |
| `timeline` | per-GC rows (index, gen, reason, type, pause, gap-since-previous-start) ordered by start time | `--top-types` (earliest N) |
| `longestPauses` | the N longest pauses, ranked descending | `--top-types` (N) |
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

### Cancellation (Ctrl-C)

- **While a command runs:** the first Ctrl-C cancels only that command (cleaning up any temp `.nettrace` /
  perf files) and keeps the session alive; a second Ctrl-C force-quits the process.
- **At an idle prompt:** Ctrl-C leaves the session (exit code 130). `exit` / `quit` / EOF leave cleanly
  (exit code 0).

## Linux ptrace note

`inspect-heap --source live` and `dump` attach via `ptrace(2)`. On
Debian/Ubuntu/WSL the default `kernel.yama.ptrace_scope=1` blocks same-UID peer attach, surfacing as a
`PermissionDenied` envelope. Fixes:

- **Bare host:** `echo 0 | sudo tee /proc/sys/kernel/yama/ptrace_scope`
- **Container:** add `CAP_SYS_PTRACE` (Docker `--cap-add SYS_PTRACE`; K8s `securityContext.capabilities.add`).
- **Zero privilege (dev):** `--launch -- <app> [args]` makes the CLI the target's parent. Under
  `ptrace_scope=1` a tracer may attach to its own descendants, so live attach works with no sysctl
  change and no capability:

  ```bash
  dotnet-diagnostics-cli inspect-heap --launch -- dotnet App.dll
  dotnet-diagnostics-cli session --launch -- dotnet App.dll   # binds the child for the whole session
  ```

  Launch the app **directly** (`dotnet App.dll` or a published apphost), not via `dotnet run` (which
  spawns a separate runtime child whose PID won't match). The child is killed when the command /
  session exits. This only helps under `ptrace_scope=1`; `scope=2` still needs `CAP_SYS_PTRACE` and
  `scope=3` forbids attach entirely — use the dump-based workflow there. When `capabilities` detects
  this exact environment it advertises the `--launch` tip.

The MCP sidecar must also run as the **same UID** as the target so it can open
`/tmp/dotnet-diagnostic-<pid>`. EventPipe-based commands (`collect`, counters, GC, exceptions) need neither
`CAP_SYS_PTRACE` nor UID matching beyond socket access.

## See also

- [`consumer-install.md`](./consumer-install.md) — install walkthrough (MCP server distributions)
- [`client-setup.md`](./client-setup.md) — connecting an MCP **client** to the server
- [`tool-reference.md`](./tool-reference.md) — the MCP **tool** surface (the server's analogue of these commands)
