# dotnet-diagnostics-benchmarkdotnet

A [BenchmarkDotNet](https://benchmarkdotnet.org) `IDiagnoser` that attaches the
[`dotnet-diagnostics`](https://github.com/pedrosakuma/dotnet-diagnostics) engine **in-process**
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
    // Prefer the type-safe, IntelliSense-discoverable enum overload:
    [DiagnosticKind(BenchmarkDiagnosticKind.Gc, DurationSeconds = 5)]
    public void AllocateLots() { /* ... */ }

    [Benchmark]
    [DiagnosticKind(BenchmarkDiagnosticKind.Contention, BenchmarkDiagnosticKind.ThreadPool)]  // run sequentially
    public void LockStorm() { /* ... */ }

    [Benchmark]
    [DiagnosticKind("gc,threadpool")]   // the free-text string overload still works (validated at run time)
    public void Legacy() { /* ... */ }
}

BenchmarkRunner.Run<Workload>();
```

> **Discoverability.** Pass `BenchmarkDiagnosticKind` values for compile-time-checked, IntelliSense-
> completed kinds. The `string` overload remains for back-compat, but a typo there is only caught at
> BenchmarkDotNet validation time.

The diagnoser runs in the BenchmarkDotNet **orchestrator** process (not the measured child), so the
heavy ClrMD/TraceEvent dependencies it pulls in never contaminate the benchmark's timing or
allocations. Each `[DiagnosticKind]` method gets one EventPipe collection per kind against the
child PID; results land in `<artifacts>/diagnostics/*.json` and a consolidated
`*-dotnet-diagnostics-report.md`.

## CI regression comparisons

Performance gates need two physically separate runs:

1. **Clean measurement:** ordinary BenchmarkDotNet jobs plus native diagnosers produce timing and
   allocation observations. Persist at least three independent run documents.
2. **Diagnostic attribution:** a monitoring job with `DotnetDiagnosticsDiagnoser` produces
   EventPipe artifacts that explain where cost moved. Its timing is never benchmark-quality and
   never enters the regression calculation.

`DotnetDiagnostics.BenchmarkDotNet.Regression` provides the versioned
`PerfMeasurementRun`, `PerfDiagnosticRun`, and `PerfRegressionReport` contracts plus an analyzer.
It checks runtime, OS/RID, architecture, GC mode, runner class/image, workload version, parameters,
and build identity before comparing runs. Duplicate capture IDs/timestamps, fewer than three
compatible repetitions, missing or unstable unchanged controls, excessive run-level coefficient of
variation, or an environment mismatch produce `inconclusive` or `environment_changed`, not a
gate-shaped result. Allocation movement from a zero baseline additionally requires an absolute
32 B/op effect floor.

The issue #647 pilot under `benchmarks/DiagnosedBenchmarks` demonstrates the full
`measure` → `diagnose` → `report` flow. Its GitHub Actions workflow is advisory-only and uploads
the immutable input documents, raw BenchmarkDotNet output, EventPipe artifacts, and regenerable
JSON/Markdown report.

The issue #651 follow-up adds `PerfPairedMeasurement`, `PerfPairedExperimentManifest`, and
`PerfPairedRegressionReport`. It pairs clean captures from two refs on the same VM, requires at
least three alternating pairs, and compares only exact workload contracts. Workload-set changes
are explicit: `new_unbaselined`, `removed`, and `contract_changed` entries retain evidence but do
not receive metric verdicts. The paired report is policy-versioned and always advisory for a
single cohort; its feasibility section records checkout, restore/build, clean-pair, diagnostic,
report, and bulk-upload duration and bytes separately.

Storage is intentionally tiered. Keep the compact normalized signals (metric name, stable
method/type/site identity, value, unit, direction, sampling metadata, and provenance) for baseline
history. Keep raw EventPipe captures only for a bounded investigation window; the compact document
references each raw artifact by relative path, byte size, SHA-256, and retention period. The final
verdict is derived output and can be regenerated rather than becoming the only retained evidence.

## Supported collect kinds

`counters`, `exceptions`, `gc`, `cpu`, `allocation`, `datas`, `catalog`, `activities`, `logs`,
`jit`, `threadpool`, `contention`, `db`, `kestrel`, `networking`, `requests`, `gcdump`.
(`event_source` is intentionally excluded — it needs an explicit provider name.)

The `kestrel`, `networking` and `requests` kinds collect the ASP.NET Core / HTTP pipeline views —
Kestrel server request timings, `HttpClient`/socket/DNS/TLS activity, and in-flight ASP.NET requests
respectively — so a web/API benchmark can attribute its own server-side and outbound-I/O cost, not
just CPU and allocations.

The `gcdump` kind captures a managed-heap **retention** snapshot (per-type instance counts and byte
totals) via the EventPipe `GCHeapSnapshot` keyword — the same mechanism `dotnet-gcdump` uses, with
**no ptrace, no ClrMD attach and no dump file**. It is the *what survives* complement to
`allocation`'s *what churns*: `allocation` (and `MemoryDiagnoser`) tell you how many bytes a method
allocated; `gcdump` tells you which types are still **retained** on the heap. The full type table is
retained behind the envelope's `heap-snapshot` handle for `query_snapshot` drill-down. `gcdump` is
**CoreCLR-only** — requesting the `GCHeapSnapshot` keyword crashes .NET 10 NativeAOT targets, so on a
NativeAOT child the capability is withheld and surfaces as a `NotSupported` entry rather than a crash.

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

## Not captured (intentionally out of scope)

These diagnostic kinds exist in the engine but are deliberately **not** exposed by the diagnoser,
each for a concrete reason — they are not gaps:

- `event_source` — needs an explicit provider name, so it can't be selected by a bare kind token.
- `startup` — the diagnoser attaches after the benchmark host is already running, so it cannot
  observe cold-start loader/DI events.
- `crash-guard` — a crash/first-chance guard for a failing process, not a benchmark perf view.
- `sweep` — a combined multi-kind sweep; redundant here since a method can already list multiple
  kinds on its `[DiagnosticKind]`.
- `method-params` — captures sensitive values through an explicit dynamic profiler attach and
  ReJIT instrumentation; it remains MCP-server-only and is never enabled by the diagnoser.
- **off-CPU** sampling (`off_cpu`) — attributes *blocked/wait* time, but needs host `perf` + kernel
  tracepoints + elevated privilege, so it is not portable in-process for a benchmark. Feasibility via
  the child-launch/container angle is tracked separately (issue #501).
- **native-alloc** (`native-alloc`) — native (unmanaged) allocation sampling is Windows/ETW-only and
  niche for managed benchmarks.

## Stability

While this package is `0.x` its public API carries no SemVer stability guarantee.

## License

MIT — see the repository.
