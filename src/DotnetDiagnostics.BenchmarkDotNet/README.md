# dotnet-diagnostics-benchmarkdotnet

A [BenchmarkDotNet](https://benchmarkdotnet.org) `IDiagnoser` that attaches the
[`dotnet-diagnostics`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp) engine **in-process**
to a benchmark's child process while it runs and captures EventPipe perf indicators (GC, contention,
thread pool, exceptions, JIT, counters, …) as JSON artifacts plus a per-run **"biggest offenders"**
markdown report.

> **Diagnose, don't measure.** EventPipe collectors are observe-only (no ptrace, no code injection)
> but add modest overhead. Run this on a dedicated diagnostic job (e.g. a `RunStrategy.Monitoring`
> job) and treat its timing as non-publication-grade. Keep `MemoryDiagnoser`/`ThreadingDiagnoser`
> for clean measurement; this adds the drill-down the MCP server / CLI provide.

## Usage

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DotnetDiagnostics.BenchmarkDotNet;

[DotnetDiagnosticsDiagnoser]   // attach the diagnoser + offenders report (like [MemoryDiagnoser])
public class Workload
{
    [Benchmark]
    [DiagnosticKind("gc", durationSeconds: 5)]   // which collect kind best explains this method
    public void AllocateLots() { /* ... */ }

    [Benchmark]
    [DiagnosticKind("contention,threadpool")]    // multiple kinds run sequentially
    public void LockStorm() { /* ... */ }
}

BenchmarkRunner.Run<Workload>();
```

The diagnoser runs in the BenchmarkDotNet **orchestrator** process (not the measured child), so the
heavy ClrMD/TraceEvent dependencies it pulls in never contaminate the benchmark's timing or
allocations. Each `[DiagnosticKind]` method gets one EventPipe collection per kind against the
child PID; results land in `<artifacts>/diagnostics/*.json` and a consolidated
`*-dotnet-diagnostics-report.md`.

## Supported collect kinds

`counters`, `exceptions`, `gc`, `datas`, `catalog`, `activities`, `logs`, `jit`, `threadpool`,
`contention`, `db`. (`event_source` is intentionally excluded — it needs an explicit provider name.)

EventPipe collectors must not run concurrently against one PID, so multiple kinds on a single method
are collected **sequentially** within the measurement window. Keep the count small (1–2) and the
per-kind duration short relative to the job's actual-run length.

## Stability

While this package is `0.x` its public API carries no SemVer stability guarantee.
