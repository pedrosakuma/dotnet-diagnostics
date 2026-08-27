# Research notes — spikes, feasibility studies, and prototypes

These are point-in-time investigations, not living reference docs: each records a question, the
evidence gathered, and a GO/NO-GO or advisory verdict for a specific issue. Read the linked issue
for current status — a spike's verdict can be superseded later (e.g. issue #467's NativeAOT
heap-walk GO was empirically reversed once implementation issue #471 reproduced a crash; the
reversal and its rationale now live directly in [`../aot-coverage.md`](../aot-coverage.md)'s
`[^aot-gcdump]` footnote, and the superseded spike doc itself was removed rather than kept as
dead weight). Where a spike led to shipped work, the current contract lives in
[`tool-reference.md`](../tool-reference.md), [`cli-reference.md`](../cli-reference.md), or the
relevant `design/` doc, not here.

| Doc | Issue | What it investigates |
|---|---|---|
| [`method-parameter-capture.md`](./method-parameter-capture.md) | [#547](https://github.com/pedrosakuma/dotnet-diagnostics/issues/547) | Feasibility of live method-parameter capture via profiler + startup-hook attach; GO verdict feeding [`../design/method-parameter-capture-design.md`](../design/method-parameter-capture-design.md), shipped as `collect_sample(kind="method-params")` |
| [`mcp-2026-draft-migration.md`](./mcp-2026-draft-migration.md) | [#546](https://github.com/pedrosakuma/dotnet-diagnostics/issues/546) | MCP 2026-draft protocol migration readiness; the production migration it planned for **shipped in v0.24.0** (SDK bumped `1.4.0` → `2.2.0`, MCP Tasks moved to the finalized SEP-2663 shape) — kept for the per-SEP technical inventory and the architectural rationale behind investigation handles as the primary orchestrator routing token (see [`../central-orchestrator-design.md`](../central-orchestrator-design.md) §3.8) |
| [`mcp-tool-catalog-budget.md`](./mcp-tool-catalog-budget.md) | [#791](https://github.com/pedrosakuma/dotnet-diagnostics/issues/791) (refreshing #628) | Measured MCP tool-catalog context-window cost for the 13/17-tool surface — the evidence behind the "one tool per concept" budget in [`../../AGENTS.md`](../../AGENTS.md) |
| [`ci-performance-regression-spike.md`](./ci-performance-regression-spike.md) | [#647](https://github.com/pedrosakuma/dotnet-diagnostics/issues/647) | Two-run (clean measurement + separate diagnostic) model for gating perf regressions in CI; piloted under `benchmarks/DiagnosedBenchmarks` and referenced from [`../../src/DotnetDiagnostics.BenchmarkDotNet/README.md`](../../src/DotnetDiagnostics.BenchmarkDotNet/README.md) |
| [`uprobe-uretprobe-native-lock-spike.md`](./uprobe-uretprobe-native-lock-spike.md) | [#852](https://github.com/pedrosakuma/dotnet-diagnostics/issues/852) | Whether paired `uprobe`/`uretprobe` on `pthread_mutex_lock` could promote sampled native-lock *activity* to confirmed *blocking* — **deferred**, several structural gaps remain |

Indexed from [`../README.md`](../README.md).
