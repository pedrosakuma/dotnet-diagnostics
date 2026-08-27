# Diagnosing Older .NET Target Processes — Feasibility Spike

**Issue**: [#884](https://github.com/pedrosakuma/dotnet-diagnostics/issues/884) · **Date**: 2026-08-27
**Status**: Spike complete; findings acted on. `samples/MultiVersionSample` (multi-targeted
`net8.0;net9.0;net10.0`), the `CrossVersionTargetTests` CI suite, and
[`docs/runtime-version-compat-matrix.md`](../runtime-version-compat-matrix.md) now formalize the
GO verdict below and keep it continuously validated instead of a one-time manual check. The
analysis below is preserved as the original findings record.

## Question

Today the server, CLI, and diagnoser docs position this repo exclusively as ".NET 10 applications"
tooling (see root `README.md`, `AGENTS.md`). Does that reflect a real technical limitation, or just
positioning? Concretely: can this repo diagnose a **target process running an older .NET version**
(6/7/8/9) without code changes, and if not, what would it take?

This is scoped to **the target being diagnosed**, not the SDK the MCP server/CLI itself is built or
run with (that stays pinned via `global.json`, see `AGENTS.md`).

## Executive summary

**Verdict: GO — empirically confirmed against .NET 6, 8, and 9 targets, all with zero code
changes.** There is no technical barrier observed on any tested version. `.NET 7` was not directly
tested but is expected to behave the same by extrapolation (see caveat below). `.NET 6`/`7` are
both EOL (Nov 2024 / May 2024), so the recommendation below to scope *officially supported/tested*
targets to **.NET 8+** is a support/maintenance-investment decision, not a capability gap — 6
already works today and would keep working with no further effort, and 7 is expected to as well.

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
- DATAS (adaptive GC sizing, default-on .NET 9+) has no hard version gate; on runtimes/GC modes
  where it isn't active it returns a controlled `DiagnosticResult.Fail<GcDatasSnapshot>` with a
  `NoDatasEvents` reason — a handled, structured outcome, not a crash or unhandled exception
  (`EventCollectionUseCases.cs`, `GcDatas.cs`).

No other hardcoded runtime-major-version gate exists anywhere in `src/`.

## What was empirically tested

Using runtimes already present in the dev sandbox (`Microsoft.NETCore.App` 8.0.26 and 9.0.14
alongside the pinned 10.x) plus a **standalone .NET 6.0.36 runtime downloaded on demand** (EOL,
no longer bundled anywhere, fetched directly from `dotnetcli.azureedge.net` to close the gap), three
minimal console apps (byte-array allocation loops, one per TFM: `net6.0`, `net8.0`, `net9.0`) were
launched as live targets and driven entirely through the **standalone CLI**
(`dotnet-diagnostics-cli`, built from current `main`, zero code changes):

| Operation | Path | .NET 6.0.36 | .NET 8.0.26 | .NET 9.0.14 |
|---|---|---|---|---|
| `processes` | process discovery | ✅ | ✅ | ✅ |
| `collect --kind counters` | EventPipe | ✅ | ✅ | ✅ |
| `collect --kind gc` | EventPipe | *(not re-run, same path as counters)* | ✅ | ✅ |
| `dump --dump-type WithHeap` | diagnostic IPC | ✅ (102 MB) | ✅ (115 MB) | ✅ (109 MB) |
| `inspect-heap --source dump` | ClrMD / DAC | ✅ correct heap walk | ✅ correct heap walk | ✅ correct heap walk |
| `capabilities` | capability probe | ✅ reports `CoreClr 6.0.36` | ✅ reports `CoreClr 8.0.26` | ✅ reports `CoreClr 9.0.14` |

In every case ClrMD resolved the managed heap correctly against each target's own DAC — top type
and instance counts matched each app's actual allocation pattern (`System.Byte[]` dominant, as
expected). `.NET 7` was not separately downloaded/tested, but given 6, 8, and 9 all passed
end-to-end there is no reason to expect 7 to behave differently — the mechanism (diagnostic IPC +
target-local DAC) is version-agnostic by construction, not something that happens to work on the
versions tried.

`inspect-heap --source live` (direct ClrMD attach) failed with `PermissionDenied` — this is the
sandbox's `kernel.yama.ptrace_scope=1` restriction described in AGENTS.md's
"🪪 CAP_SYS_PTRACE for live memory readers" section, unrelated to target .NET version. The dump-based
path above is the documented fallback and it worked without any elevated privilege.

## What is *not* yet validated

- **CI has zero multi-version coverage.** `.github/workflows/ci.yml` installs a single pinned SDK;
  nothing in the test suite spawns or attaches to an older-runtime target. The empirical check above
  was manual, one collector family at a time, run ad hoc against three runtimes (6.0.36, 8.0.26,
  9.0.14) rather than as a repeatable automated suite.
- **Sample apps only target `net10.0`.** `CoreClrSample`, `BadCodeSample`, `NativeAotSample` have no
  older-TFM sibling or multi-target build, so there's no in-repo fixture for regression testing
  against 8/9.
- **Deeper ClrMD drilldowns are untested** against older runtimes: async state-machine walks,
  closed-generic-instantiation resolution (`CpuSampler_EmitsClosedGenericInstantiations`, itself
  flaky on Linux CI per issue #147), and other features that depend on CLR-internal layout details
  that can shift subtly between major versions. Theoretically low risk (the target's own runtime
  directory ships the matching DAC), but not exercised here.
- **DATAS graceful-degrade specifically** is confirmed by code reading (see `GcDatas.cs`), not
  re-verified live on a workload that would actually trigger DATAS-vs-non-DATAS divergence — the
  test apps here didn't generate enough GC pressure to distinguish the two paths.
- **.NET 7** was not tested (no runtime downloaded); expected to work by extrapolation from 6/8/9,
  not directly observed.

## Recommendation

1. **Scope official support to .NET 8+ (current LTS + STS) as a maintenance decision, not a
   capability one.** 6/7 work today and cost nothing extra technically, but they're EOL — no
   security patches, and the repo already treats .NET 7 as out of scope for method-params for the
   same reason. If a user needs it anyway, nothing in this repo blocks it.
2. **Close the validation gap, not a code gap.** No production code change is indicated by this
   spike. The work is: a second sample app (or multi-target the existing one) pinned to an older TFM,
   a CI job/matrix leg that attaches to it, and a living
   `docs/runtime-version-compat-matrix.md`-style doc (see `docs/resource-boundedness.md` for the
   pattern this repo already uses for capability matrices) so support claims stop being implicit.
3. **Update product positioning** (root `README.md`, `AGENTS.md`) once the matrix doc exists, so
   ".NET 10 applications" doesn't overstate the actual constraint.

See issue [#884](https://github.com/pedrosakuma/dotnet-diagnostics/issues/884) for follow-up scope
and implementation tracking.
