# dotnet-diagnostics-mcp

[![CI](https://github.com/pedrosakuma/dotnet-diagnostics/actions/workflows/ci.yml/badge.svg)](https://github.com/pedrosakuma/dotnet-diagnostics/actions/workflows/ci.yml)

An **MCP server** for LLM-driven performance diagnostics on **.NET 10** applications — zero instrumentation required.

> **Status:** 15 unified tools, HTTP + stdio transports, IoT-style triage (6+ steps → 2 steps).
> See [`docs/`](./docs) for full reference.

### Two ways to use it

This repo ships **two NuGet tools** built on the same Core diagnostics engine — pick by who is driving:

| Package | Driver | Surface | Docs |
|---|---|---|---|
| **`dotnet-diagnostics-mcp`** | An **LLM**, via an MCP client | MCP tools over HTTP (bearer) or stdio | this README + [`docs/`](./docs) |
| **`dotnet-diagnostics-cli`** | A **human** / script / CI | Sub-commands + a stateful `session` REPL (no HTTP, no bearer, no daemon) | [`docs/cli-reference.md`](./docs/cli-reference.md) |

Most of this README is about the **MCP server**. If you want to run diagnostics yourself, jump to the
[Standalone CLI](#standalone-cli) section or the [CLI reference](./docs/cli-reference.md).

---

## Table of Contents

- [Quick Start](#quick-start)
- [Install](#install)
- [Standalone CLI](#standalone-cli)
- [Tools Overview](#tools-overview)
- [Documentation](#documentation)
- [Goals](#goals)
- [Build & Test](#build--test)

---

## Quick Start

**One call to understand your app's health:**

```bash
# MCP call
inspect_process(view="triage")
```

**Response:**
```json
{
  "verdict": "threadpool-starvation",
  "severity": "Critical",
  "topIndicators": [
    {"name": "threadpool-queue-length", "value": 1191, "score": 100, "level": "critical"},
    {"name": "cpu-usage", "value": 0.13, "score": 0, "level": "normal"},
    {"name": "time-in-gc", "value": 0, "score": 0, "level": "normal"}
  ],
  "hints": [{"nextTool": "collect_events", "suggestedArguments": {"kind": "threadpool"}}]
}
```

**Verdicts:** `cpu-bound`, `gc-pressure`, `memory-pressure`, `threadpool-starvation`, `lock-contention`, `io-bound`, `healthy`

**TopIndicators** are always returned (even when healthy) — enabling **proactive optimization**, not just reactive firefighting. The LLM simply follows the first hint.

---

## Install

Three distributions — pick by environment. Full walkthrough: [`docs/consumer-install.md`](./docs/consumer-install.md)

```bash
# .NET global tool (requires .NET 10 SDK)
dotnet tool install -g dotnet-diagnostics-mcp
dotnet-diagnostics-mcp --urls http://127.0.0.1:8787

# Container (no SDK needed)
docker run -d -p 127.0.0.1:8787:8080 \
  -e MCP_BEARER_TOKEN=$(openssl rand -hex 32) \
  ghcr.io/pedrosakuma/dotnet-diagnostics:latest

# Self-contained binary — see Releases page
```

<details>
<summary><strong>Transport options</strong></summary>

| Transport | Use case | Auth |
|-----------|----------|------|
| **stdio** | Local dev (Copilot CLI, Claude Desktop) | None (OS-level trust) |
| **HTTP** | Sidecar, shared host, multi-client | Bearer token |

</details>

<details>
<summary><strong>Linux ptrace note</strong></summary>

On Debian/Ubuntu/WSL, `kernel.yama.ptrace_scope=1` blocks ClrMD tools. Fix: `echo 0 | sudo tee /proc/sys/kernel/yama/ptrace_scope`. See [`docs/consumer-install.md`](./docs/consumer-install.md#15-linux-enabling-clrmd-backed-tools-ptrace).

</details>

<details>
<summary><strong>Joint with dotnet-assembly-mcp</strong></summary>

For decompilation + call graphs:

```bash
export ASSEMBLIES_DIR=/path/to/binaries
docker compose -f deploy/docker-compose.yml up -d
```

</details>

---

## Standalone CLI

`dotnet-diagnostics-cli` is a separate NuGet tool that runs the **same Core diagnostics engine** as a
command you drive yourself — no HTTP server, bearer token, MCP client, or daemon. Useful for scripts, CI,
and `kubectl exec` into the sidecar (the container image ships it on `PATH`).

```bash
dotnet tool install -g dotnet-diagnostics-cli

# One-shot
dotnet-diagnostics-cli processes
dotnet-diagnostics-cli collect --kind counters --pid 1234 --duration 5
dotnet-diagnostics-cli inspect-heap --pid 1234 --top-types 30

# Inside the sidecar container (image bundles the CLI):
kubectl exec -it <pod> -c diagnostics-mcp -- dotnet-diagnostics-cli inspect-heap --pid 1
```

A stateful `session` REPL keeps collected handles queryable across commands so you can drill in
(`query --handle <id> --view <view>`) without re-collecting, and bind a target pid once with `target <pid>`:

```text
dotnet-diagnostics-cli session
diag> target 1234
diag(pid 1234)> collect --kind gc --duration 10
diag(pid 1234)> query --handle <id> --view pauseHistogram
diag(pid 1234)> exit
```

Self-contained per-OS binaries are attached to each [Release](https://github.com/pedrosakuma/dotnet-diagnostics/releases)
as `dotnet-diagnostics-cli-<version>-<rid>`. **Full reference:** [`docs/cli-reference.md`](./docs/cli-reference.md).

---

## Tools Overview

**16 unified tools.** Full schemas and return shapes: [`docs/tool-reference.md`](./docs/tool-reference.md).

<details>
<summary><strong>The 16 tools at a glance</strong></summary>

| Tool | Purpose |
|---|---|
| `inspect_process(view="triage")` | **IoT-style diagnosis** — 1 call returns verdict + severity + ranked TopIndicators + hints |
| `inspect_process(view="list")` / `inspect_process(view="info")` | Discover .NET processes via diagnostic IPC |
| `inspect_process(view="capabilities")` | Detect CoreCLR vs NativeAOT and what's usable |
| `inspect_process(view="container")` | Linux cgroup v2: CPU throttling, memory pressure, OOM kills, PSI |
| `collect_events(kind="counters")` | EventCounters with **auto-hints** (CPU, GC, starvation, contention, allocation, I/O) |
| `collect_sample(kind="cpu")` / `query_snapshot(view="call-tree")` | Top-N CPU hotspots (inclusive/exclusive) + on-demand caller→callee tree |
| `collect_sample(kind="off_cpu")` / `query_snapshot` | Where threads block (futex / IO / sleep) — Linux `perf` backend |
| `collect_sample(kind="native-alloc")` / `query_snapshot(view="call-tree")` | Native/unmanaged allocation hotspots (off-GC-heap `malloc`) attributed to call sites — Linux `perf` uprobe backend |
| `collect_events(kind="exceptions")` | Managed exceptions thrown in a window, aggregated by type |
| `collect_events(kind="gc")` | GC pauses + per-generation counts |
| `collect_events(kind="activities")` / `query_snapshot` | ActivitySource span capture (trace/span ids, parent linkage, tags, duration) + **GC overlay** correlation |
| `collect_events(kind="event_source")` / `query_snapshot` | Generic EventSource passthrough (HTTP, Kestrel, custom) + re-project artifacts |
| `collect_thread_snapshot` / `query_snapshot` | Managed thread states + SyncBlock lock graph + deadlock / unique-stack drilldown |
| `inspect_heap(source="live")` / `inspect_heap(source="dump")` / `query_snapshot` | Top retained types + retention paths + roots + async state machines, live or from a dump |
| `collect_process_dump` | Write a Mini / Triage / WithHeap / Full dump to disk |
| `start_investigation` | Structured plan (cold / warm / hypothesis) before any collector runs |
| `export_investigation_summary` / `compare_to_baseline` | Portable JSON memory; LLM persists, diffs across deploys |

</details>

---

## Documentation

**📖 [`docs/`](./docs) is the documentation hub** — start there. It indexes the tool reference,
CLI reference, investigation playbooks, output examples, authorization/scopes, client setup, and
all deployment guides (Kubernetes, Helm, Azure, AWS, GCP).

---

## Goals

- **Zero changes to target app** — works via diagnostic IPC
- **Cross-platform** — Linux + Windows, containers first-class
- **Graceful NativeAOT** — unsupported tools return `not_supported`, not crashes
- **LLM-friendly** — summarized JSON, not raw `.nettrace`

---

## Build & Test

```bash
dotnet build
dotnet test
```

Requires .NET 10 SDK (pinned in `global.json`).

<details>
<summary><strong>Contributor setup (shared dev instance)</strong></summary>

```bash
scripts/local-mcp.sh start     # builds + starts in background
scripts/local-mcp.sh status
scripts/local-mcp.sh logs -f
scripts/local-mcp.sh stop
```

Add to `~/.copilot/mcp-config.json`:
```json
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "type": "http",
      "url": "http://127.0.0.1:8787/mcp",
      "headers": { "Authorization": "Bearer demo-local-token-2026" }
    }
  }
}
```

</details>

---

## Roadmap

<details>
<summary><strong>Phase status</strong></summary>

| Phase | Status | Description |
|-------|--------|-------------|
| 1-3 | ✅ | Foundation + Core diagnostics + MCP server |
| 4 | ✅ | GC, exceptions, EventSources, dumps |
| 5 | ✅ | Kubernetes sidecar ([`deploy/k8s/`](./deploy/k8s)) |
| 6 | ✅ | Documentation polish |
| 7 | ✅ | Cloud integrations (Azure, AWS, GCP) |
| 8 | ✅ | Tool consolidation (24 → 15 tools) |
| **12** | ✅ | **Diagnostic Journey UX** — auto-hints + IoT triage |
| **13** | ✅ | **GC overlay** — correlate GC pauses with activity spans |
| Next | ⏳ | Flame graph export, NativeAOT publish |

</details>

---

## License

TBD.
