# Diagnosing Older .NET Target Processes — Feasibility Spike

**Issue**: [#884](https://github.com/pedrosakuma/dotnet-diagnostics/issues/884) · **Date**: 2026-08-27
**Status**: Research spike — no production code changed. Output is this findings doc plus a
GO / scope recommendation.

## Question

Today the server, CLI, and diagnoser docs position this repo exclusively as ".NET 10 applications"
tooling (see root `README.md`, `AGENTS.md`). Does that reflect a real technical limitation, or just
positioning? Concretely: can this repo diagnose a **target process running an older .NET version**
(6/7/8/9) without code changes, and if not, what would it take?

This is scoped to **the target being diagnosed**, not the SDK the MCP server/CLI itself is built or
run with (that stays pinned via `global.json`, see `AGENTS.md`).

## Executive summary

**Verdict: GO for .NET 8+ targets, largely for free.** `.NET 6`/`7` are both EOL (Nov 2024 / May 2024)
and are **not recommended as a supported scope** even though nothing observed here technically blocks
them.

The core diagnostic dependencies — `Microsoft.Diagnostics.NETCore.Client`, ClrMD
(`Microsoft.Diagnostics.Runtime`), `Microsoft.Diagnostics.Tracing.TraceEvent` — are the same
cross-CLR-version libraries that back `dotnet-trace`/`dotnet-dump`/`dotnet-counters`/`dotnet-gcdump`
across .NET Core 3.1 through 10. They talk to the target over the diagnostic IPC protocol and resolve
CLR internals via the DAC shipped alongside the target's own runtime — neither mechanism is
inherently tied to the *host* process's .NET version. An empirical smoke test below confirms this in
practice, not just in theory.

The two places version awareness already exists in the codebase are correctly scoped and need no
change:

- `collect_sample(kind="method-params")` hard-gates `.NET 8+` via `TryParseMajor` in
  `MethodParameterCaptureCollector.cs` (profiler-attach + startup-hook injection genuinely requires
  8+; see [`method-parameter-capture.md`](./method-parameter-capture.md)).
- DATAS (adaptive GC sizing, default-on .NET 9+) has no hard gate — it just produces no events on
  older runtimes / Workstation GC, degrading gracefully rather than failing
  (`EventCollectionUseCases.cs`, `GcDatas.cs`).

No other hardcoded runtime-major-version gate exists anywhere in `src/`.

## What was empirically tested

Using runtimes already present in the dev sandbox (`Microsoft.NETCore.App` 8.0.26 alongside the
pinned 10.x), a minimal `net8.0` console app (byte-array allocation loop) was launched as a live
target, and driven entirely through the **standalone CLI** (`dotnet-diagnostics-cli`, built from
current `main`, zero code changes):

| Operation | Path | Result |
|---|---|---|
| `processes` | process discovery | ✅ correctly reports `8.0.26` |
| `collect --kind counters` | EventPipe | ✅ 27 counters captured |
| `collect --kind gc` | EventPipe | ✅ ran cleanly (idle heap, no activity — app-load artifact, not a version issue) |
| `dump --dump-type WithHeap` | diagnostic IPC | ✅ 115 MB dump written |
| `inspect-heap --source dump` | ClrMD / DAC | ✅ correct managed-heap walk — top type and instance counts matched the app's actual allocation pattern (`System.Byte[]`, 90.99%, 11,722 instances) |
| `capabilities` | capability probe | ✅ reports `CoreClr 8.0.26`, `CPU sampling: True`, `gcdump: True` |

`inspect-heap --source live` (direct ClrMD attach) failed with `PermissionDenied` — this is the
sandbox's `kernel.yama.ptrace_scope=1` restriction described in AGENTS.md's
"🪪 CAP_SYS_PTRACE for live memory readers" section, unrelated to target .NET version. The dump-based
path above is the documented fallback and it worked without any elevated privilege.

## What is *not* yet validated

- **CI has zero multi-version coverage.** `.github/workflows/ci.yml` installs a single pinned SDK;
  nothing in the test suite spawns or attaches to an older-runtime target. The empirical check above
  was manual, one collector family at a time, one runtime (8.0.26).
- **Sample apps only target `net10.0`.** `CoreClrSample`, `BadCodeSample`, `NativeAotSample` have no
  older-TFM sibling or multi-target build, so there's no in-repo fixture for regression testing
  against 8/9.
- **Deeper ClrMD drilldowns are untested** against older runtimes: async state-machine walks,
  closed-generic-instantiation resolution (`CpuSampler_EmitsClosedGenericInstantiations`, itself
  flaky on Linux CI per issue #147), and other features that depend on CLR-internal layout details
  that can shift subtly between major versions. Theoretically low risk (the target's own runtime
  directory ships the matching DAC), but not exercised here.
- **.NET 9** was not smoke-tested in this pass despite being available in the sandbox — DATAS
  graceful-degrade behavior specifically is inferred from code reading, not re-verified live.

## Recommendation

1. **Scope to .NET 8+ (current LTS + STS).** Don't chase 6/7 — both EOL, and the repo already treats
   .NET 7 as out of scope for method-params for the same reason.
2. **Close the validation gap, not a code gap.** No production code change is indicated by this
   spike. The work is: a second sample app (or multi-target the existing one) pinned to an older TFM,
   a CI job/matrix leg that attaches to it, and a living
   `docs/runtime-version-compat-matrix.md`-style doc (see `docs/resource-boundedness.md` for the
   pattern this repo already uses for capability matrices) so support claims stop being implicit.
3. **Update product positioning** (root `README.md`, `AGENTS.md`) once the matrix doc exists, so
   ".NET 10 applications" doesn't overstate the actual constraint.

See issue [#884](https://github.com/pedrosakuma/dotnet-diagnostics/issues/884) for follow-up scope
and implementation tracking.
