# CPU/allocation hotpath profiling

Companion to [`docs/resource-boundedness.md`](../resource-boundedness.md). That doc bounds
**memory growth** (resident entries) for long/high-volume captures; this profiling pass asks a
different question: **where do CPU cycles and allocations actually go** inside each collector's own
code, so future perf work is guided by measurement instead of guesswork. Tracked in
[issue #616](https://github.com/pedrosakuma/dotnet-diagnostics/issues/616).

## Method (common to all three groups)

- Benchmarks live in `benchmarks/DiagnosedBenchmarks/` and call the real `DotnetDiagnostics.Core`
  collector/sampler/inspector method **in-process**, against a real published `samples/CoreClrSample`
  (or `samples/NativeAotSample`) child process launched the same way the live tests do (`dotnet
  <dll>`, never `dotnet run`), under sustained synthetic load matched to what each collector observes.
- Each benchmark class is decorated with `[MemoryDiagnoser]` (allocation totals) and
  `BenchmarkDotNet.Diagnosers.EventPipeProfiler` (cross-platform CPU stack sampling via `.nettrace`,
  no `perf`/ptrace required for the profiler itself — some sampler backends still shell out to `perf`
  on their own, see Group B).
- `.nettrace` files are parsed with `Microsoft.Diagnostics.Tracing.TraceEvent` to rank the top
  exclusive-(self)-time methods as a percentage of total sampled stacks.
- Run via `dotnet run -c Release --project benchmarks/DiagnosedBenchmarks -- --filter '*<ClassName>*'`.

## Groups

| Doc | Scope | Headline finding |
| --- | --- | --- |
| [`group-a-event-stream.md`](./group-a-event-stream.md) | 19 `EventPipe*Collector` classes (Activities, Logs, Exceptions, Gc, Counters, EventSources, Contention, ThreadPool, Jit, Db, Kestrel, Networking, Requests, MethodParameters, GatedCapture, Startup, …) | Across nearly every collector, exclusive CPU is dominated by runtime wait/parking frames (`LowLevelLifoSemaphore.WaitForSignal`, `WaitHandle.WaitOneNoCheck`, …), not collector-owned parsing code. No `DotnetDiagnostics.Core` frame entered any top-10 self-cost list. No fix applied. |
| [`group-b-sampling-native.md`](./group-b-sampling-native.md) | CPU/allocation/off-CPU/native-alloc samplers, memory-trend collector | Managed EventPipe CPU/allocation/memory-trend samplers are wait-bound with negligible collector self-time. `PerfNativeAllocSampler`/`PerfSchedOffCpuSampler` could not be measured in this sandbox (missing tracefs/`CAP_SYS_ADMIN` permissions), reported rather than skipped silently. One real bug fixed: `PerfNativeAotCpuSampler`'s `perf record --max-size` argument used raw-byte formatting that some `perf` builds reject; switched to perf-accepted size suffixes (e.g. `512M`). |
| [`group-c-remaining.md`](./group-c-remaining.md) | Attach/inspect/one-shot classes: `ClrMdDumpInspector`, `GcDumpHeapSnapshotCollector`, thread-snapshot inspectors, `RequestsNowCollector`, `RuntimeConfigInspector`, `PreflightInspector`, `CgroupV2SignalsCollector` | The only materially expensive classes are the ClrMD-backed ones, and their visible cost is DAC attach/read time, not an avoidable managed hot loop. Everything else is allocation-light and CPU-negligible. `LinuxNativeThreadSnapshotInspector` and `PerfReplayThreadSnapshotInspector` were blocked by sandbox ptrace/tracepoint permissions (documented, not silently skipped). No fix applied. |

## Overall takeaway

None of the three passes found a high-confidence, safe CPU/allocation hotpath fix worth making on
its own — the dominant cost in almost every collector is waiting on the runtime/EventPipe pipe, not
avoidable managed work in `DotnetDiagnostics.Core`. Combined with the `resource-boundedness.md`
memory audit, this gives a reasonably complete picture: our collectors are already proportionate to
the event volume they observe, and further perf work should be re-triggered by a *new* profiler-backed
finding rather than speculative micro-optimization.
