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
- **CPU sampling is supported; allocation sampling is not.** The `cpu` kind runs the EventPipe CPU
  sampler in-process and attributes cost per stack frame (exclusive/inclusive samples + call tree).
  Allocation *sampling* remains MCP-server-only; for per-type allocation here use the `gc` kind or
  BDN's native `MemoryDiagnoser`.
- **Linux/WSL:** EventPipe collectors do **not** need `CAP_SYS_PTRACE`.

## Files

| File | Role |
| --- | --- |
| `DiagnosedConfig.cs` | Monitoring job + the diagnoser + native `MemoryDiagnoser`. |
| `WorkloadBenchmarks.cs` | `GcChurn` and `LockStorm` diagnosis fixtures. |
| `Program.cs` | `BenchmarkSwitcher` entry point. |

The diagnoser, `[DiagnosticKind]` attribute, and the offenders report exporter all live in the
reusable [`dotnet-diagnostics-benchmarkdotnet`](https://www.nuget.org/packages/dotnet-diagnostics-benchmarkdotnet)
NuGet package — add it to your own benchmark project with
`dotnet add package dotnet-diagnostics-benchmarkdotnet`.
