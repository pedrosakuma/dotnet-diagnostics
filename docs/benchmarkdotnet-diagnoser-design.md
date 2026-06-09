# BenchmarkDotNet diagnoser — shipping design

_Status: design doc for [issue #346](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/346)._
_This document is design only. It adds no library, package, or rename — those land in the follow-up PRs below._

This document answers one question:
**Now that a PoC has proven value in combining a BenchmarkDotNet run with our diagnostics engine, how do we ship it well?**

Short answer:
- Ship it as a **new NuGet library** (`dotnet-diagnostics-benchmarkdotnet`) consumed by `PackageReference`, **not** as a copy-paste sample or a shell-out-to-installed-tool hack.
- Back it by a **shared, published engine** — make the engine packable (`dotnet-diagnostics-core`) so the CLI tool, the MCP tool, and the benchmark library all reference one engine.
- First, **drop the `DotnetDiagnosticsMcp` prefix** repo-wide: the engine has no coupling to MCP, so a public `*Mcp*` engine package would be misnamed.

## 1. Context

### 1.1 What the PoC proved

A proof-of-concept (uncommitted, under `benchmarks/DiagnosedBenchmarks/`) wired a custom
BenchmarkDotNet [`IDiagnoser`](https://benchmarkdotnet.org/) that attaches to the per-benchmark
child process BDN spawns and shells out to the locally-built `dotnet-diagnostics` CLI:

```
dotnet dotnet-diagnostics.dll collect --kind <k> --pid <childPid> --duration <d> --json
```

Smoke test end-to-end: a `GcChurn` benchmark ran, BDN's native `MemoryDiagnoser` reported
allocations, and our diagnoser captured a valid GC envelope (`200 collections, GC healthy`) written
as a JSON artifact. A benchmark declares which collector explains it via a `[DiagnosticKind("gc")]`
attribute. **Value is confirmed:** the integration produces a per-journey diagnostic view of the
biggest offenders, so any micro-optimization can be verified with real EventPipe indicators rather
than guesswork.

### 1.2 Why the PoC's shape is wrong for shipping

The PoC depends on the CLI **dll on disk**, located by walking up from `AppContext.BaseDirectory`.
That is fine inside this solution (the CLI is built alongside the sample) but a poor consumer story:

- The consumer must separately **install** a global tool and the diagnoser must **find** it.
- Lib and installed-tool versions can **skew**.
- A `[DiagnosticKind("gc")]` round-trips through a serialized JSON envelope rather than typed objects.

The PoC answered "does this work?" — yes. This document answers "how do we ship it?".

## 2. Consumption models considered

The diagnostics functionality already ships in two shapes: **installed tools** (`dotnet-diagnostics-cli`,
`dotnet-diagnostics-mcp`). A benchmark integration is a third shape — a **referenced library**.

- **Model A — tool-based (PoC).** The library shells out to the installed CLI. Keeps the engine
  private, but two-part install + brittle locator + version skew. Rejected for shipping.
- **Model B — library-based (in-process).** The library references the engine and runs collection
  **in-process** against the child PID. One `PackageReference`, no tool install, version-locked,
  typed snapshots. Requires **publishing the engine as a library**. **Chosen.**

Key fact that makes Model B safe: the diagnoser runs in the BDN **orchestrator** process, not the
measured child process. Pulling the engine's heavy closure (ClrMD, TraceEvent) into the orchestrator
does **not** contaminate the child's measurements. The child only runs the workload.

## 3. Naming and the prerequisite rename

Today everything is prefixed `DotnetDiagnosticsMcp.*`, but the **engine** (`*.Core`) is
transport-agnostic and has no MCP knowledge — its own `<Description>` already says so. A public
engine package named `*Mcp*` would be misleading. So before publishing the engine we reduce "Mcp"
to only the MCP server.

### 3.1 Project / assembly / namespace rename (behavior-preserving)

| Current | New |
| --- | --- |
| `DotnetDiagnosticsMcp.Core` | `DotnetDiagnostics.Core` |
| `DotnetDiagnosticsMcp.Cli` | `DotnetDiagnostics.Cli` |
| `DotnetDiagnosticsMcp.Server` | `DotnetDiagnostics.Mcp` |
| `DotnetDiagnosticsMcp.Core.Tests` | `DotnetDiagnostics.Core.Tests` |
| `DotnetDiagnosticsMcp.Cli.Tests` | `DotnetDiagnostics.Cli.Tests` |
| `DotnetDiagnosticsMcp.Server.IntegrationTests` | `DotnetDiagnostics.Mcp.IntegrationTests` |
| `DotnetDiagnosticsMcp.TestSupport` | `DotnetDiagnostics.TestSupport` |
| `DotnetDiagnosticsMcp.slnx` | `DotnetDiagnostics.slnx` |

Blast radius: ~455 source/config files, ~2040 occurrences, 7 folders. Mechanical, no behavior
change. Substitution order matters — replace the special case **first**:

1. `DotnetDiagnosticsMcp.Server` -> `DotnetDiagnostics.Mcp` (this also fixes
   `...Server.IntegrationTests` -> `...Mcp.IntegrationTests`).
2. Remaining `DotnetDiagnosticsMcp` -> `DotnetDiagnostics`.

Use `git mv` for folders and `.csproj` filenames to preserve history. The CLI keeps its
`AssemblyName` `dotnet-diagnostics`. **Not** touched: kebab PackageIds (`dotnet-diagnostics-*`),
`MCP_*` environment variables, and `MCP_BEARER_TOKEN`.

### 3.2 Public NuGet PackageIds

| Artifact | Assembly / namespace | PackageId |
| --- | --- | --- |
| MCP server tool | `DotnetDiagnostics.Mcp` | `dotnet-diagnostics-mcp` (unchanged) |
| CLI tool | `DotnetDiagnostics.Cli` (asm `dotnet-diagnostics`) | `dotnet-diagnostics-cli` (unchanged) |
| Engine library | `DotnetDiagnostics.Core` | `dotnet-diagnostics-core` (NEW) |
| Benchmark diagnoser | `DotnetDiagnostics.BenchmarkDotNet` | `dotnet-diagnostics-benchmarkdotnet` (NEW) |

`BenchmarkDotNet.*` is the reserved first-party integration prefix (`BenchmarkDotNet.Diagnostics.Windows`,
`.Diagnostics.dotTrace`, ...). The benchmark package therefore uses our `dotnet-diagnostics-*` brand
rather than squatting that prefix. The benchmark library's root namespace must not shadow the
`BenchmarkDotNet` namespace (`DotnetDiagnostics.BenchmarkDotNet` is fine; a bare `BenchmarkDotNet.X`
is not).

## 4. Target architecture

```
DotnetDiagnostics.Core                 (engine, now packable -> dotnet-diagnostics-core)
   ^                ^                 ^
   |                |                 |
DotnetDiagnostics.Cli   DotnetDiagnostics.Mcp   DotnetDiagnostics.BenchmarkDotNet
 (tool)                  (tool)                  (library -> dotnet-diagnostics-benchmarkdotnet)
                                                    ^
                                                    | PackageReference
                                          consumer's benchmark project
```

The benchmark library provides:

- `[DiagnosticKind]` — per-benchmark attribute mapping a method to one or more `collect` kinds.
- `IDiagnoser` — at `BeforeActualRun` it starts in-process collection against `parameters.Process`;
  at `AfterActualRun` it finalizes. EventPipe collectors must **not** overlap on one PID (no
  start-session timeout in the runtime), so multiple kinds run **sequentially**.
- A BDN `Exporter` — aggregates the captured indicators into a per-run **offenders report**
  (markdown), the actual product value.

The PoC's `DotnetDiagnosticsDiagnoser` already encodes the lifecycle, sequential-collection,
bounded-wait + teardown, and unique-artifact-key handling; the ship reuses that logic with in-process
calls to `EventCollectionUseCases` instead of a CLI subprocess.

## 5. Execution — small PRs, in order

1. **Rename** (§3.1) — repo-wide, behavior-preserving, full build + test green. No new packages.
2. **Engine packable** — `DotnetDiagnostics.Core` `IsPackable=true` + add to `release.yml`; document
   the supported public surface (`EventCollectionUseCases` + result models).
3. **Benchmark diagnoser library** — new `DotnetDiagnostics.BenchmarkDotNet` with the in-process
   diagnoser + `[DiagnosticKind]` + report exporter; packed in release.
4. **Port the sample** — `benchmarks/DiagnosedBenchmarks` references the library; drop the shell-out
   and the CLI locator.

## 6. Open sub-decisions

- **Core surface**: fully packable as-is vs. carve a slim `Engine`/facade to limit the semver
  commitment. Resolve in PR 2.
- **`collect` kind vocabulary** is hand-duplicated across the MCP `collect_events` discriminator,
  `CliCommands.CollectKinds`, the `CliCollectValidationTests` literal list, and now the benchmark
  `[DiagnosticKind]` strings (the last is unguarded — a typo only fails at runtime). A single source
  of truth would close this drift; it may warrant its own issue.

## 7. Out of scope

- CPU / allocation **sampling** in the benchmark path. The engine's `collect` kinds are EventPipe-based
  (gc, contention, counters, threadpool, exceptions, ...); sampling is MCP-server-only today.
- Replacing BenchmarkDotNet's native diagnosers (`MemoryDiagnoser`, `ThreadingDiagnoser`,
  `EventPipeProfiler`). This integration **complements** them: it diagnoses *why* a workload behaves
  as it does; it is not publication-grade measurement (the diagnostic job is `RunStrategy.Monitoring`).
