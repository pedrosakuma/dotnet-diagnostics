# .NET runtime compatibility matrix (target processes)

> Companion to [`AGENTS.md`](../AGENTS.md) and
> [`research/multi-version-target-support.md`](./research/multi-version-target-support.md) (the
> feasibility spike this matrix formalizes — issue
> [#884](https://github.com/pedrosakuma/dotnet-diagnostics/issues/884)). This page tracks which
> **target-process** .NET runtimes are actually validated, not just theoretically compatible.

This is about the runtime of the **process being diagnosed**, not the SDK this repo itself is
built/run with (that stays pinned via `global.json`; see `AGENTS.md`). Nothing described here
requires modifying the target application. Separately, the referenceable `dotnet-diagnostics-core`
and `dotnet-diagnostics-benchmarkdotnet` libraries multi-target `net8.0;net9.0;net10.0` for
consumer projects; that consumer-TFM runtime compatibility is distinct from the target-process matrix
documented below and is smoke-validated independently by
`tests/DotnetDiagnostics.MultiTargetSmoke{,.Tests}`.

## Official support

| Target runtime | Status | Notes |
|---|---|---|
| **.NET 10** | ✅ Fully supported | Primary development/CI target; all samples and docs assume this by default. |
| **.NET 9** | ✅ Fully supported | `CrossVersionTargetTests` is configured in Linux and Windows Core CI; mechanisms below are narrower than all-reader parity. DATAS has no hard version gate and degrades gracefully off. |
| **.NET 8** | ✅ Fully supported | `CrossVersionTargetTests` is configured in Linux and Windows Core CI. Minimum version for `collect_sample(kind="method-params")`; profiler attach genuinely requires 8+. |
| **.NET 7** | ⚠️ Expected to work, not CI-covered | EOL (May 2024). No known blocker; not validated in this repo's automation because installing an EOL runtime in CI has no ongoing security-support payoff. `method-params` explicitly excludes it (see [`method-parameter-capture.md`](./research/method-parameter-capture.md)). |
| **.NET 6** | ⚠️ Expected to work, not CI-covered | EOL (Nov 2024). Manually verified once during the spike (counters, GC events, dump capture, ClrMD heap walk all succeeded against a live 6.0.36 target) but not part of ongoing CI — same EOL rationale as .NET 7. |
| **.NET Core 3.1 / .NET 5** | ❓ Untested | Older than anything checked here. The underlying libraries (`Microsoft.Diagnostics.NETCore.Client`, ClrMD, `TraceEvent`) are known to support this range for `dotnet-trace`/`dotnet-dump`-style tooling in general, but this repo has never pointed at one. Treat as "probably fine, verify before relying on it."  |

## What's validated per collector family

`CrossVersionTargetTests` (`tests/DotnetDiagnostics.Core.Tests/CrossVersionTargetTests.cs`) runs
against `samples/MultiVersionSample` (a minimal multi-targeted `net8.0;net9.0;net10.0` console app)
in the Linux and Windows Core jobs of `ci.yml` (both install the .NET 8/9 shared
runtimes; docs-only runs skip the test steps), covering:

- **EventCounters** (`collect_events`-style counter snapshot)
- **GC events** (EventPipe GC collector)
- **Dump capture + ClrMD heap inspection** (`dump --dump-type WithHeap` + offline heap walk)

These three families exercise the three fundamentally different attach mechanisms this repo uses
(EventPipe streaming, diagnostic-IPC dump write, ClrMD/DAC offline analysis), so a pass across all
three is reasonably strong evidence the rest of the EventPipe-based collectors (exceptions,
contention, thread-pool, JIT, logs, networking, Kestrel, activities, DATAS, etc.) work the same way
— they all go through the same `DiagnosticsClient` session machinery, just subscribing to different
providers/keywords.

### Not yet validated against older runtimes

- **Live ClrMD attach** (`inspect-heap --source live`, `collect_thread_snapshot`,
  `capture_method_bytes` live path) — the original spike could not attach under its host ptrace
  policy. The new [advisory sidecar lane](./live-clrmd-compatibility.md) prepares live heap/thread
  coverage without changing host policy; execution evidence is still pending. It does **not**
  cover live method bytes or every reader.
- **Deeper ClrMD drilldowns**: async state-machine walks, closed-generic-instantiation resolution
  (`query_snapshot` async/generics views) — depend on CLR-internal layout details that are unlikely
  but not proven to be stable across major versions.
- **`collect_sample(kind="method-params")`** — already hard-gated to .NET 8+; not exercised against
  9/10 targets specifically in `CrossVersionTargetTests` (covered separately by
  `MethodParameterCaptureCollectorTests` against the pinned net10.0 `CoreClrSample`).
- **NativeAOT targets** on older SDKs — out of scope; NativeAOT support is tracked independently in
  [`aot-coverage.md`](./aot-coverage.md) and is inherently tied to the SDK version used to publish
  the AOT binary, not a CoreCLR major version.

### Representative live layout coverage: evidence pending

Issue [#931](https://github.com/pedrosakuma/dotnet-diagnostics/issues/931) adds
`CrossVersionLiveSidecarTests` plus a manual advisory workflow. Each Linux x64
runtime slot requires exactly four passing cases: named retained heap population,
named live thread frame, pending async state machine, and IP-based ClrMD resolution
of `ClosedGenericHold<System.Int32>`. Dump inspection and shared-canon do not satisfy
these assertions. The [runner and evidence contract](./live-clrmd-compatibility.md)
describe the single topology, bounds and retained artifacts.

| Mechanism / topology | .NET 8 | .NET 9 | .NET 10 |
|---|---|---|---|
| Linux x64 matching-UID, shared-PID sidecar; four live/layout cases | Pending execution | Pending execution | Pending execution |
| Windows cross-version live/layout cases | Not claimed | Not claimed | Existing `LiveCoreClrProcessTests` controls only |

No successful sidecar evidence URL exists yet. A build, skipped ordinary test, or
reachable Docker daemon is not compatibility evidence. Update the cells and link
the immutable workflow run/artifact only after all expected cases execute and pass.

## How to extend this matrix

Add a new `[InlineData("netN.0")]` case to the relevant `CrossVersionTargetTests` theory, multi-target
`samples/MultiVersionSample` to include the new TFM, and install its runtime in
`.github/workflows/ci.yml`'s "Install .NET 8 / 9 runtimes for cross-version tests" step. Update the
table above once CI is green.
