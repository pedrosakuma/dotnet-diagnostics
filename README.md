# dotnet-diagnostics-mcp

[![CI](https://github.com/pedrosakuma/dotnet-diagnostics/actions/workflows/ci.yml/badge.svg)](https://github.com/pedrosakuma/dotnet-diagnostics/actions/workflows/ci.yml)

An **MCP server** for LLM-driven performance diagnostics on **.NET 10** applications.
Normal EventPipe and ClrMD diagnostics require no target code changes or prior instrumentation.
The explicit exception is `collect_sample(kind="method-params")`, an opt-in, privileged,
security-gated dynamic profiler attach that temporarily instruments an allowlist of methods.

> **Status:** 16 unified tools in the full surface (12 default plus 4 configuration-gated),
> HTTP + stdio transports, IoT-style triage (6+ steps → 2 steps).
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

**Response excerpt (rationales shortened):**
```json
{
  "modelVersion": 2,
  "assessment": "critical",
  "severity": "Critical",
  "observedSignals": [
    {
      "name": "threadpool.queue",
      "level": "critical",
      "summary": "The ThreadPool queue contained 1191 work items.",
      "evidence": [
        {"name": "threadpool-queue-length", "value": 1191, "comparison": ">=", "threshold": 200, "unit": "items", "rationale": "Queue crossed the critical threshold."}
      ]
    }
  ],
  "hypotheses": [
    {
      "name": "threadpool.backlog",
      "confidence": "moderate",
      "summary": "Work was queued faster than the ThreadPool completed it; counters do not prove starvation.",
      "supportingEvidence": [{"name": "threadpool-queue-length", "value": 1191, "comparison": ">=", "threshold": 50, "rationale": "Large queue supports a backlog hypothesis."}],
      "contradictingEvidence": [],
      "nextStep": "Collect ThreadPool events and blocking stacks to distinguish sustained starvation, blocking, and transient demand."
    }
  ],
  "topIndicators": [
    {"name": "threadpool-queue-length", "value": 1191, "score": 100, "level": "critical"}
  ],
  "verdict": "threadpool-starvation",
  "secondaryVerdicts": null
}
```

`observedSignals` report threshold crossings; `hypotheses` explain bounded interpretations and
the evidence needed to confirm them. A low-CPU snapshot with a small queue is `inconclusive`, not
categorically `io-bound`. `verdict` / `secondaryVerdicts` remain for compatibility and are
deprecated for removal in v1.0. **TopIndicators** remain available on every result.

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
| `inspect_process` | Process discovery, capabilities, environment/resources, memory trends, preflight, and evidence-backed triage |
| `collect_events` | EventCounters/Meters and bounded EventPipe event families (GC, exceptions, activities, logs, JIT, networking, and more) |
| `collect_sample` | CPU, off-CPU, managed/native allocation, and explicitly gated method-parameter capture |
| `query_snapshot` | Re-project retained handles into call trees, diffs, histograms, events, roots, and other focused views |
| `inspect_heap` | Live or dump heap walk with retained-type, root, retention-path, and async-state-machine drilldowns |
| `get_bytes` | Materialize authorized module, PDB, dump, or trace bytes from a server-side artifact |
| `discover_azure` | Configuration-gated App Service, Container Apps, and AKS discovery |
| `collect_process_dump` | Write a Mini / Triage / WithHeap / Full dump to disk |
| `collect_thread_snapshot` | Managed thread states, stacks, SyncBlock lock graph, and deadlock evidence |
| `capture_method_bytes` | Read JIT/ReadyToRun native bytes for a managed method |
| `start_investigation` | Build a bounded cold, warm, or hypothesis-driven investigation plan |
| `export_investigation_summary` | Export portable investigation memory as JSON |
| `compare_to_baseline` | Compare a current investigation summary with a saved baseline |
| `list_orchestrator` | Configuration-gated Kubernetes namespace, workload, pod, and investigation inventory |
| `attach_to_pod` | Configuration-gated sidecar/ephemeral-container attach and investigation-handle creation |
| `detach_from_pod` | Close an orchestrated investigation and release its transport resources |

</details>

---

## Documentation

**📖 [`docs/`](./docs) is the documentation hub** — start there. It indexes the tool reference,
CLI reference, investigation playbooks, output examples, authorization/scopes, client setup, and
all deployment guides (Kubernetes, Helm, Azure, AWS, GCP).

---

## Goals

- **No prior instrumentation for standard diagnostics** — EventPipe and ClrMD work through diagnostic IPC without target code changes
- **Explicit sensitive attach boundary** — method-parameter capture is opt-in dynamic profiler instrumentation, not a passive collector
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
| 8 | ✅ | Tool consolidation into unified discriminator tools |
| 9–15 | ✅ | Diagnostic UX, package surfaces, platform parity, and signal grouping (see [`CHANGELOG.md`](./CHANGELOG.md)) |
| **16** | 🚧 | **MCP protocol evolution + external capability gaps** — active roadmap [#551](https://github.com/pedrosakuma/dotnet-diagnostics/issues/551) |

</details>

---

## License

MIT — see [`LICENSE`](./LICENSE).
