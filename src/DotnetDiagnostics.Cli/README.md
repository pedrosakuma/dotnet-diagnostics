# dotnet-diagnostics-cli

A **standalone command-line tool** for on-demand performance diagnostics on running **.NET 10**
applications — no target code changes or prior instrumentation, no MCP client, no HTTP server,
no bearer token, no daemon.

It runs the same Core diagnostics engine as the [`dotnet-diagnostics-mcp`](https://www.nuget.org/packages/dotnet-diagnostics-mcp)
MCP server, but as a tool a human (or a script / CI job) drives directly. Attach to a live process,
collect a window of events, walk the heap, or write a dump — then exit. A stateful `session` REPL keeps
collected artifacts queryable across commands so you can drill in without re-collecting.
The CLI does not expose the MCP server's privileged dynamic-profiler method-parameter capture.

> **Two packages, one engine.** Install **this** package (`dotnet-diagnostics-cli`) for interactive /
> scripted human use. Install [`dotnet-diagnostics-mcp`](https://www.nuget.org/packages/dotnet-diagnostics-mcp)
> instead when you want an **MCP server** that exposes diagnostics as tools to an LLM over HTTP or stdio.

## Install

```bash
dotnet tool install -g dotnet-diagnostics-cli   # requires the .NET 10 SDK
```

Self-contained, per-OS binaries (no SDK required) are attached to every
[GitHub Release](https://github.com/pedrosakuma/dotnet-diagnostics/releases) as
`dotnet-diagnostics-cli-<version>-<rid>`. The diagnostics sidecar container image also ships the CLI on
`PATH`, so `kubectl exec … -- dotnet-diagnostics-cli …` works inside the pod.

## One-shot usage

```bash
# Discover attachable .NET processes
dotnet-diagnostics-cli processes

# Probe a target's capability matrix (CoreCLR vs NativeAOT, what's usable)
dotnet-diagnostics-cli capabilities --pid 1234

# Collect a 5s EventCounters window
dotnet-diagnostics-cli collect --kind counters --pid 1234 --duration 5

# Walk the managed heap (top retained types)
dotnet-diagnostics-cli inspect-heap --pid 1234 --top-types 30

# Write a heap dump to disk (preview without --confirm)
dotnet-diagnostics-cli dump --pid 1234 --dump-type WithHeap --out ./dumps --confirm
```

`--pid` is optional — it is auto-resolved when exactly one .NET process is visible. Pass `--json` on any
command to emit the raw `DiagnosticResult` envelope for scripting. Run `dotnet-diagnostics-cli --help` or
`<command> --help` for the full flag reference.

### Commands

| Command | Purpose |
|---|---|
| `processes` | List attachable .NET processes. |
| `capabilities` | Probe a target's diagnostic capability matrix. |
| `collect` | Open an EventPipe session and collect events (`--kind counters\|exceptions\|gc\|event_source\|activities\|logs\|jit\|threadpool\|contention\|db`). |
| `inspect-heap` | Walk the managed heap of a live process or a `.dmp` (`--source live\|dump`). |
| `dump` | Write a Mini / Triage / WithHeap / Full process dump to disk (requires `--confirm`). |
| `get-bytes` | Materialise a module (PE/PDB) or dump file to disk. |
| `query` | Re-render a collected handle under a different view — **only inside `session`** (returns `NotSupported` one-shot). |
| `session` | Start the stateful REPL (below). |

## The `session` REPL

One-shot commands build the diagnostic host, run, and exit — so a drill-down `query` has nothing to query.
`session` keeps the host (and every collected handle) alive across commands:

```text
dotnet-diagnostics-cli session
diag> target 1234                       # bind a target pid once
diag(pid 1234)> collect --kind gc --duration 10
  → handle 1TA2BA7KT9PYT60WTWE0 — query --handle 1TA2BA7KT9PYT60WTWE0 --view <pauseHistogram|...>
diag(pid 1234)> query --handle 1TA2BA7KT9PYT60WTWE0 --view pauseHistogram
diag(pid 1234)> exit
```

- **`target <pid>`** binds a default pid so live-target commands (`capabilities`, `collect`, `dump`,
  `inspect-heap --source live`, `get-bytes --kind module`) no longer need `--pid`. `target` shows the
  current binding; `target clear` unbinds. An explicit `--pid` always overrides the binding.
- **Handles** published by `collect` / `inspect-heap` stay queryable until they expire
  or the target exits, so `query --handle <id> --view <view>` drills in **without re-collecting**.
- **Ctrl-C** cancels the running command and keeps the session alive; press it again to force-quit. An idle
  Ctrl-C leaves the session.

## Documentation

- **Full CLI reference:** [`docs/cli-reference.md`](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/cli-reference.md)
- **Project README & MCP server:** [github.com/pedrosakuma/dotnet-diagnostics](https://github.com/pedrosakuma/dotnet-diagnostics)

## Linux note (live heap inspection)

`inspect-heap --source live` attaches via `ptrace(2)`. On
Debian/Ubuntu/WSL the default `kernel.yama.ptrace_scope=1` blocks same-UID peer attach — run
`echo 0 | sudo tee /proc/sys/kernel/yama/ptrace_scope` (or grant `CAP_SYS_PTRACE` in a container).
The `dump` command writes through diagnostic IPC and does not need that kernel capability.
EventPipe-based tools (`collect`, counters, GC, exceptions) are also unaffected.

## License

MIT © pedrosakuma
