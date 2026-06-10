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

`counters`, `exceptions`, `gc`, `cpu`, `allocation`, `datas`, `catalog`, `activities`, `logs`,
`jit`, `threadpool`, `contention`, `db`. (`event_source` is intentionally excluded — it needs an
explicit provider name.)

The `cpu` kind runs the EventPipe **CPU sampler** (CoreCLR only) against the benchmark child and
attributes cost **per stack frame**: each hotspot carries its *exclusive* (self) and *inclusive*
(self + callees) sample counts, and the consolidated `*-dotnet-diagnostics-report.md` headlines the
hottest self-cost frame (e.g. `Hottest self-cost: MyApp.Serialize (59.7% exclusive)`). The full
caller→callee call tree is retained behind the envelope's drill-down handle. Source-line and
generic-instantiation resolution are disabled in-process (no PDB/SourceLink I/O, no ClrMD attach).

The `allocation` kind runs the EventPipe **allocation sampler** (`GCAllocationTick`) and attributes
allocated bytes **per managed type** (top-N by bytes and by event count, SOH vs LOH) **and per
call site** (`TopBySite` — the leaf frame that allocated, byte-weighted, with a `MethodIdentity`
for the assembly-mcp handoff), with the full merged allocation call-site tree behind the handle. It
is the per-type *and per-origin* complement to `MemoryDiagnoser`'s `Allocated` column — *which*
types and *where* they came from, not just how many bytes. The headline reports the top type and
the top site (e.g. `Top site: System.Private.CoreLib!System.String.Ctor(wchar,int32) (… bytes)`).
On NativeAOT the runtime emits the event without a `TypeName`, so types roll up under `<unknown>`
(flagged in the summary); drill the call-tree handle for native allocation sites.

> **Does the diagnostic count as allocation?** No. The sampler is *observe-only* — it enables a
> native EventPipe keyword and reads events the target runtime already emits; it performs **no
> managed allocation in the measured process**, so it cannot inflate the benchmark's `Allocated`
> counter. With the default (out-of-process) toolchain the sampler additionally runs in the
> orchestrator process, never the measured child — `MemoryDiagnoser` reads the child's own GC
> counters across a different run. The one exception is the **in-process toolchain** (`[InProcess]`):
> there the benchmark shares this process, `GC.GetTotalAllocatedBytes()` is process-wide, and a
> co-located capture is **not** isolated — the `allocation` summary flags this explicitly. Prefer the
> default toolchain (or a dedicated diagnostic job) for clean allocation numbers.

EventPipe collectors must not run concurrently against one PID, so multiple kinds on a single method
are collected **sequentially** within the measurement window. Keep the count small (1–2) and the
per-kind duration short relative to the job's actual-run length.

## Stability

While this package is `0.x` its public API carries no SemVer stability guarantee.
