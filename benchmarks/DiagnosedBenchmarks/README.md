# DiagnosedBenchmarks — perf indicators + BenchmarkDotNet

A sample that combines a [BenchmarkDotNet](https://benchmarkdotnet.org/) run with the perf
indicators captured by this repo's engine, via the
[`dotnet-diagnostics-benchmarkdotnet`](https://www.nuget.org/packages/dotnet-diagnostics-benchmarkdotnet)
NuGet package.

It answers *"can we capture perf indicators combined with a BenchmarkDotNet run?"* — the answer is
**yes, for diagnosis** (explaining *why* a workload behaves the way it does), not as a replacement
for clean measurement.

## What it does

The `DotnetDiagnosticsDiagnoser`
runs the diagnostic engine **in-process** (in the BenchmarkDotNet orchestrator, not the measured
child) and attaches its EventPipe collectors to the per-benchmark child process while it runs. No
shell-out, no CLI on disk — the package references the engine directly.

The resulting EventPipe envelope (GC stats, lock contention, counters, …) is written to
`BenchmarkDotNet.Artifacts/diagnostics/<case>.<seq>.<kind>.json`, and a consolidated
`*-dotnet-diagnostics-report.md` offenders report lands next to BDN's own results. Each benchmark
declares which collector explains it via `[DiagnosticKind]`:

```csharp
[Benchmark]
[DiagnosticKind("gc", durationSeconds: 5)]
public long GcChurn() { /* allocation churn */ }

[Benchmark]
[DiagnosticKind("contention", durationSeconds: 5)]
public long LockStorm() { /* lock storm */ }

[Benchmark]
[DiagnosticKind("cpu", durationSeconds: 5)]
public long CpuHotPath() { /* tight numeric loop — per-frame self/inclusive cost */ }

[Benchmark]
[DiagnosticKind("allocation", durationSeconds: 5)]
public long AllocChurn() { /* per-type allocation churn — bytes by managed type */ }
```

The [`DiagnosedConfig`](DiagnosedConfig.cs) pairs the diagnoser with the **native**
`MemoryDiagnoser` so you can read BDN's own allocation numbers alongside the deeper EventPipe view.
(You can instead annotate the class with `[DotnetDiagnosticsDiagnoser]` — just like
`[MemoryDiagnoser]` — when you don't need a custom job.)

## Run it

BenchmarkDotNet's CsProj toolchain regenerates and compiles a per-benchmark project, so it needs the
sample's `.csproj` on the working-directory search path — run from the project directory:

```bash
# BDN must run in Release, outside a debugger.
cd benchmarks/DiagnosedBenchmarks
dotnet run -c Release -- --filter '*GcChurn*'
```

Then inspect the captured indicators (under the project's `BenchmarkDotNet.Artifacts/`):

```bash
cat BenchmarkDotNet.Artifacts/results/*-dotnet-diagnostics-report.md
cat BenchmarkDotNet.Artifacts/diagnostics/*.gc.json
```

## CI regression spike

The `perf-regression` command is a dedicated issue #647 fixture. It is intentionally separate from
the collector-hotpath benchmarks above:

```bash
# Run this from the repository root. Repeat measure at least three times.
dotnet run --project benchmarks/DiagnosedBenchmarks -c Release -- \
  perf-regression measure \
  --run-id local-1 \
  --output artifacts/perf/measurement-1.json \
  --artifacts artifacts/perf/clean-1 \
  --runner-class local \
  --baseline-build-id fixture-baseline \
  --candidate-build-id fixture-candidate

dotnet run --project benchmarks/DiagnosedBenchmarks -c Release -- \
  perf-regression diagnose \
  --output artifacts/perf/diagnostic.json \
  --artifacts artifacts/perf/diagnostic \
  --runner-class local \
  --candidate-build-id fixture-candidate

dotnet run --project benchmarks/DiagnosedBenchmarks -c Release -- \
  perf-regression report \
  --run artifacts/perf/measurement-1.json \
  --run artifacts/perf/measurement-2.json \
  --run artifacts/perf/measurement-3.json \
  --diagnostic artifacts/perf/diagnostic.json \
  --output-json artifacts/perf/report.json \
  --output-markdown artifacts/perf/report.md
```

The clean run stores runner/runtime provenance, workload parameters, BenchmarkDotNet mean,
standard deviation, sample count, and allocations per operation. The diagnostic run stores only
bounded normalized signals plus content-addressed references to short-lived raw captures. The final
report can be regenerated from those immutable compact inputs and refuses incompatible or
duplicate-capture comparisons. A gate recommendation also requires a complete, stable unchanged
control and compatible runner-image provenance.

### Paired-ref experiment

Issue #651 adds a manual, advisory-only workflow at
`.github/workflows/paired-performance-experiment.yml`. It checks out `main` and a PR ref into
separate directories on one GitHub-hosted VM, builds each ref once, then runs three clean pairs in
alternating order (`main -> PR`, `PR -> main`, `main -> PR`). The existing `measure` and `diagnose`
commands remain unchanged. A separate `paired-report` command consumes their immutable JSON:

Before the workflow exists on the default branch, GitHub cannot dispatch it by name. A maintainer
can add the exact `run-paired-performance` label to a PR to start the same human-triggered
experiment against that PR's base and head SHAs. Other labels and ordinary PR activity do not run
the job. After merge, `workflow_dispatch` defaults to `main` and the dispatched ref.

```bash
dotnet run --project benchmarks/DiagnosedBenchmarks -c Release --no-build -- \
  perf-regression paired-report \
  --pair '1|main_then_pr|main-1.json|pr-1.json' \
  --pair '2|pr_then_main|main-2.json|pr-2.json' \
  --pair '3|main_then_pr|main-3.json|pr-3.json' \
  --diagnostic diagnostic.json \
  --stage-metrics stages.tsv \
  --job-start-unix-ms 1784361600000 \
  --compact-root artifacts/compact \
  --raw-root artifacts/raw \
  --output-manifest manifest.json \
  --output-feasibility feasibility.json \
  --output-json report.json \
  --output-markdown report.md
```

Only matching workload identity, version, parameters, control designation, and variant sets are
compared. PR-only workloads are `new_unbaselined`, main-only workloads are `removed`, and changed
contracts are `contract_changed`; none receives a regression verdict. The policy-neutral manifest,
per-ref measurements, normalized diagnostic signals, real commit SHAs, runner image, alternating
order, stage durations, and artifact bytes remain separate from the versioned policy-derived
report. Diagnostic attribution starts only after all clean pairs and its elapsed time is excluded
from every metric verdict.

One workflow cohort measures within-VM order and operating cost only. It cannot establish
multi-runner/day stability, provides no dedicated-runner evidence, and is never gate-eligible.

The follow-up `paired-performance-calibration.yml` workflow reuses that exact protocol rather
than introducing another benchmark path. Each of its three hosted matrix jobs is a separate
GitHub-hosted job/allocation and emits one self-contained, policy-neutral `cohort.json`.
The aggregate job groups cohorts only when runner kind/label, SDK, runtime, hosted image, ref
SHAs, and workload contracts match exactly. Different groups are reported but never pooled.
The report separates each cohort's three within-VM pairs from cross-allocation CV and, when
prior scheduled/manual runs are supplied, cross-day CV. Detection and unchanged-control
false-positive rates include Wilson 95% intervals. Runner minutes and compact/raw input bytes
are summed independently from metric verdicts.

Scheduled runs execute on three adjacent UTC days and automatically include up to five previous
successful scheduled runs, maximizing the chance of retaining one exact hosted image version.
A manual dispatch can include explicit prior workflow IDs through `prior_run_ids`. Three
parallel hosted jobs on one date are cross-allocation evidence, not multi-day evidence; image
revisions still form separate groups rather than being pooled.
All reports remain advisory and `eligibleForGate: false`.

The repository had no registered self-hosted runner when this calibration was added. The
dedicated job therefore stays skipped unless an operator first verifies an online runner with
all labels `self-hosted`, `linux`, `x64`, and `dotnet-diagnostics-perf`, then sets repository
variable `PERF_DEDICATED_RUNNER_ENABLED=true`. The exact verification and dispatch sequence is:

```bash
gh api repos/pedrosakuma/dotnet-diagnostics/actions/runners \
  --jq '.runners[] | select(.status == "online") | {name, labels: [.labels[].name]}'
gh variable set PERF_DEDICATED_RUNNER_ENABLED \
  --repo pedrosakuma/dotnet-diagnostics --body true
gh workflow run paired-performance-calibration.yml \
  --repo pedrosakuma/dotnet-diagnostics --ref main \
  -f baseline_ref=main \
  -f candidate_ref=main
```

Do not set the variable until the exact label set is visible and online. GitHub can otherwise
leave a self-hosted job queued before `timeout-minutes` starts. The dedicated path accepts only
scheduled or default-branch manual main-vs-main calibration; it never runs pull-request code on
the persistent runner.

The waiting pilot gives both variants the same eight delayed operations and verifies the same
summed result. The baseline awaits them concurrently; the candidate synchronously blocks on each
operation in sequence. Its physically separate EventPipe fixture delays load activation for two
seconds so the ThreadPool session is established first, then performs three independent candidate
and control launches. Attribution matches only parsed `Starvation` or `CooperativeBlocking`
adjustment events; generic summaries and unrelated hill-climbing do not qualify.

## Important caveats

This pattern **diagnoses** benchmarks; it does not produce publication-grade timings.

- **Observation has a cost.** EventPipe collectors are observe-only (no ptrace, no code injection)
  but still add modest overhead. The config therefore uses a dedicated `RunStrategy.Monitoring`
  job; treat its timing numbers as diagnostic, not authoritative. For clean measurement use the
  default config and BDN's native diagnosers (`MemoryDiagnoser`, `ThreadingDiagnoser`,
  `EventPipeProfiler`).
- **No parallel EventPipe on one PID.** The runtime has no start-session timeout, so two concurrent
  EventPipe sessions against the same process can wedge the IPC. The diagnoser collects multiple
  kinds **sequentially**; keep `[DiagnosticKind]` to 1–2 kinds with short durations.
- **CPU and allocation sampling are supported.** The `cpu` kind runs the EventPipe CPU sampler
  in-process and attributes cost per stack frame (exclusive/inclusive samples + call tree); the
  `allocation` kind attributes allocated bytes per managed type (SOH/LOH + allocation call-site
  tree) — the per-type complement to `MemoryDiagnoser`'s `Allocated` column. Both are observe-only
  and, with the default out-of-process toolchain, run in the orchestrator process, so they don't
  perturb the benchmark's measured allocation numbers.
- **Linux/WSL:** EventPipe collectors do **not** need `CAP_SYS_PTRACE`.

## Files

| File | Role |
| --- | --- |
| `DiagnosedConfig.cs` | Monitoring job + the diagnoser + native `MemoryDiagnoser`. |
| `WorkloadBenchmarks.cs` | `GcChurn`, `LockStorm`, `CpuHotPath`, and `AllocChurn` diagnosis fixtures. |
| `Program.cs` | `BenchmarkSwitcher` entry point. |

The diagnoser, `[DiagnosticKind]` attribute, and the offenders report exporter all live in the
reusable [`dotnet-diagnostics-benchmarkdotnet`](https://www.nuget.org/packages/dotnet-diagnostics-benchmarkdotnet)
NuGet package — add it to your own benchmark project with
`dotnet add package dotnet-diagnostics-benchmarkdotnet`.
