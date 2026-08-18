# Linux perf compatibility matrix

Tracks the environments and `perf`/kernel capability combinations the perf-backed
collectors — CPU sampling fallback for NativeAOT (`PerfNativeAotCpuSampler`), off-CPU
(`PerfSchedOffCpuSampler`), native allocation (`PerfNativeAllocSampler`), and native lock
contention (`PerfNativeLockContentionSampler`) — are supported and tested against. See
[issue #851](https://github.com/pedrosakuma/dotnet-diagnostics/issues/851) for the context that
motivated this document: the [#830](https://github.com/pedrosakuma/dotnet-diagnostics/issues/830)
smoke uncovered command-line and environment quirks unit tests alone did not expose.

## Why this exists

All four collectors above shell out to the same external `perf` binary and share a small set of
building blocks (`PerfBinaryResolver`, `PerfNativeAotCpuSampler.FormatPerfFileSize`,
`PerfHostProbe`). Those building blocks are unit-tested (see
`tests/DotnetDiagnostics.Core.Tests/PerfCompatSmokeTests.cs` and the collector-specific test
files), but perf's actual on-disk behavior — which binary a given distro/kernel resolves to,
whether a tracepoint/uprobe is available, whether the calling process has enough privilege —
varies by topology in ways a pure unit test cannot observe. This matrix documents what is
covered where, and what remains a manual/documented-only environment.

## Coverage summary

| Environment | Perf binary resolution | Command-line construction | Live capture (CPU/off-CPU/native-alloc/native-lock) | How it's exercised |
|---|---|---|---|---|
| **Native Linux (ubuntu-latest GitHub-hosted runner)** | Tested live | Tested live + unit | Tested live (opt-in) | `.github/workflows/linux-perf-compat-smoke.yml` (manual `workflow_dispatch` + weekly schedule) |
| **Container (sidecar topology, same-UID + capabilities)** | Documented; not exercised by an automated job in this repo | Unit-tested only | Not automated here — validate manually per [`docs/local-docker-sidecar.md`](./local-docker-sidecar.md) | Manual, following the local Docker sidecar walkthrough |
| **WSL2** | Unit-tested (the wrapper-with-no-binary shape is reproduced in `PerfCompatSmokeTests`) | Unit-tested only | **Documented/manual only** — no stable hosted GitHub Actions runner exists for WSL2 | Manual; see "WSL2" section below |
| **Any host, pure logic** | Unit-tested (`PerfBinaryResolverTests`, `PerfCompatSmokeTests`) | Unit-tested (`PerfCompatSmokeTests`, `OffCpuSamplerTests`) | N/A | `dotnet test tests/DotnetDiagnostics.Core.Tests/ --filter FullyQualifiedName~Perf` |

## Non-privileged unit coverage (runs on every `dotnet test`, no perf/root required)

These tests are pure functions over strings/fakes — they never spawn `perf` and never require
elevated privileges, so they run on every CI leg (Linux and Windows) without any capability gate:

- **Perf binary discovery** (`PerfBinaryResolverTests`, `PerfCompatSmokeTests`): the
  configured-path-first / kernel-matched-candidate-next / newest-other-candidate-last resolution
  order, including the WSL topology where `/usr/bin/perf` is a wrapper that resolves to nothing
  usable and an older `/usr/lib/linux-tools-*/perf` install still works.
- **Portable `--max-size` formatting** (`PerfNativeAotCpuSampler.FormatPerfFileSize`, covered in
  `PerfScriptParserTests` and `PerfCompatSmokeTests`): every collector's byte-count cap must
  round-trip to a human-readable suffix (e.g. `512M`) because some perf builds reject a raw byte
  count for `--max-size`.
- **Full command-line construction** for every perf-backed collector
  (`PerfCompatSmokeTests.CpuSampler_BuildRecordArguments_*`, `..._NativeAlloc_...`,
  `..._NativeLockContention_...`, and `PerfSchedOffCpuCommandBuilderTests` for off-CPU): argument
  order, `--call-graph dwarf`, sample-period/tracepoint wiring, and the portable `--max-size`
  value are asserted against a fixed argument list — a perf-version-specific flag rename or
  reordering regression is caught here before it reaches a real host.
- **Structured failure classification** (`PerfFailureClassifier`, covered in
  `PerfCompatSmokeTests.Classify_*`): a pure classifier over `perf`'s combined stdout/stderr text
  that tells apart `MissingPerf`, `UnusableWrapper`, `MissingTracepoint`, `PermissionDenied`, and
  `UnsupportedCallGraph` — see "Failure mode reference" below.
- **Host capability probing** (`PerfHostProbeTests`): the same-UID `perf_event_paranoid` /
  `CAP_PERFMON` / `CAP_SYS_ADMIN` gate logic used by `inspect_process(view="capabilities")`.

Run just this slice locally:

```bash
dotnet test tests/DotnetDiagnostics.Core.Tests/ -c Release --no-build \
  --filter "FullyQualifiedName~Perf|FullyQualifiedName~NativeAlloc|FullyQualifiedName~NativeLockContention|FullyQualifiedName~OffCpu"
```

## Live smoke matrix (opt-in, not required)

[`linux-perf-compat-smoke.yml`](../.github/workflows/linux-perf-compat-smoke.yml) is a
**manual/opt-in** workflow (`workflow_dispatch` + a weekly `schedule`) that:

1. Installs `linux-tools-generic` + `linux-tools-$(uname -r)` on `ubuntu-latest` and reports
   which `perf` candidate actually resolves (mirroring `PerfBinaryResolver`'s own probe order).
2. Reports the host's `perf_event_paranoid` value and effective capability set.
3. Re-runs the non-privileged unit slice above.
4. Publishes and starts both the `CoreClrSample` webapi and a NativeAOT-published
   `NativeAotSample`, then drives `dotnet-diagnostics-cli collect --kind cpu` against the
   NativeAOT sample (CPU perf sampling only ever routes through `PerfNativeAotCpuSampler` for a
   NativeAOT target — `RoutingCpuSampler` sends CoreCLR targets to the managed EventPipe
   SampleProfiler instead) and `--kind {off_cpu, native-alloc, native-lock-contention}` against
   the CoreCLR sample (those attach at the OS/libc level and apply to any same-UID target),
   capturing stdout/stderr per kind.
5. Uploads every collected JSON/log as a build artifact and writes a capability-gap summary to
   the job summary — a failure here is a **signal, not a merge blocker**: it is deliberately kept
   off the required branch-protection status checks (same pattern as
   `linux-crash-repro-preload.yml`), because GitHub-hosted runners can and do change their
   `perf_event_paranoid` default / available tracepoints between images without any code change
   on our side.

This job intentionally does **not** run inside a container, so it does not need `--cap-add`
capability grants — hosted `ubuntu-latest` runners default `perf_event_paranoid` to a value that
permits same-UID per-process CPU/off-CPU/native-alloc/native-lock sampling for a spawned child
process. If a future runner image tightens that default, the job's own capability report step
will show it, and the affected `collect` invocation will show up as a documented gap in the job
summary rather than a mysterious CI failure.

Container/Kubernetes-sidecar topologies (the production-representative topology) are **not**
covered by this workflow — they need `--cap-add PERFMON` / `SYS_PTRACE` wiring that already has
a documented, human-verified walkthrough in
[`docs/local-docker-sidecar.md`](./local-docker-sidecar.md); re-validate perf compatibility there
manually when changing the perf-backed collectors, rather than duplicating that capability
plumbing into a second automated job.

## WSL2: documented/manual environment

WSL2 is the environment that originally exposed the "kernel-matching wrapper with no usable
binary" failure mode (issue [#830](https://github.com/pedrosakuma/dotnet-diagnostics/issues/830)).
There is no stable, hosted GitHub Actions runner image for WSL2, so it is **not** part of any
automated workflow in this repo. The wrapper-detection logic itself (`PerfBinaryResolver`) is
unit-tested against a simulated WSL topology (see `PerfCompatSmokeTests`), but a full live smoke
run against WSL2 remains manual:

1. On a Windows host with WSL2, install the matching `linux-tools-$(uname -r)` package (or accept
   that `perf` silently falls back to the unusable wrapper — this is the exact failure #830
   found).
2. Confirm `perf --version` prints a real version banner (not the
   `WARNING: perf not found for kernel ...` message) before trusting any collector output.
3. `kernel.perf_event_paranoid` on WSL2 defaults to `2`, which is enough for `perf stat`
   (`cpu-efficiency`) but not `sched:sched_switch` tracing (`off_cpu`) — see AGENTS.md's "WSL2
   perf quirks" section for the exact sysctl to relax on a personal/isolated dev box only, never
   on a shared host.
4. Run the same `dotnet-diagnostics-cli collect --kind …` commands the automated smoke workflow
   uses (see step 4 above) and compare output against the native-Linux baseline.

## Failure mode reference

`PerfFailureClassifier.Classify` (see
`src/DotnetDiagnostics.Core/Capabilities/PerfFailureClassifier.cs`) is a pure function over a
failed perf invocation's combined stdout/stderr that distinguishes:

| `PerfFailureKind` | Trigger | Typical environment |
|---|---|---|
| `MissingPerf` | No perf binary resolves at all (every candidate failed `--version`), or the shell reports `command not found` | Minimal container images without `linux-perf` installed |
| `UnusableWrapper` | The Debian/Ubuntu/WSL kernel-matching wrapper prints `WARNING: perf not found for kernel ...` and exits non-zero | WSL2 without a matching `linux-tools-*` package |
| `MissingTracepoint` | The requested tracepoint/uprobe/event does not exist on this kernel (`event not found`, `Error: File ... not found`) | Minimal/hardened kernels without `sched:sched_switch` or without `debugfs`/`tracefs` mounted for uprobes |
| `PermissionDenied` | `perf_event_paranoid` too restrictive, or the process lacks `CAP_PERFMON`/`CAP_SYS_ADMIN` | Containers without the capability added; hosts with a hardened `perf_event_paranoid` |
| `UnsupportedCallGraph` | The requested unwind mode (`--call-graph dwarf`) is not supported by this perf build/kernel | Older perf builds, or kernels built without frame-pointer/DWARF unwind support |

Callers should classify perf failures with `PerfFailureClassifier.Classify(combinedStdoutStderr)`
before surfacing a generic error to the LLM, so the response can point at the actionable fix
(install `linux-tools-$(uname -r)`, add a capability, relax `perf_event_paranoid`, etc.) instead
of a bare "perf record exited with code 1".

## Resource bounds preserved

Every collector's `--max-size` cap (see [`docs/resource-boundedness.md`](./resource-boundedness.md))
is unchanged by this work — `PerfNativeAotCpuSampler.FormatPerfFileSize` only changes how the same
byte count is *rendered* on the command line, never the byte count itself, and the smoke workflow
does not raise or bypass any existing cap. No new interactive `sudo` prompts were introduced;
`apt-get install` in the smoke workflow runs non-interactively under `sudo apt-get -y`, and the
workflow makes no attempt to relax `perf_event_paranoid` or `kernel.yama.ptrace_scope`.
