# Group B — sampling/native collector hotpaths

Linux profiling pass (`uname -s` = `Linux`) for issue [#616](https://github.com/pedrosakuma/dotnet-diagnostics/issues/616).

## Method

- Benchmarks live in `benchmarks/DiagnosedBenchmarks/SamplingNativeHotpathBenchmarks.cs`.
- Each benchmark launches a real sample child process from published output:
  - `CoreClrSample` for managed EventPipe/native-alloc/off-CPU/memory-trend collectors.
  - `NativeAotSample` for `PerfNativeAotCpuSampler`.
- BenchmarkDotNet config: `RunStrategy.Monitoring`, 1 warmup, 2 measured iterations, 6s collection window.
- Allocation headline numbers below come from BenchmarkDotNet `Allocated`.
- CPU hotpaths come from `EventPipeProfiler` `.nettrace` files parsed by `benchmarks/DiagnosedBenchmarks/NettraceSelfTimeAnalyzer.cs`.

## EventPipeCpuSampler

- Load: repeated `/cpu-burn?ms=350`, `/generics?iterations=200000`, `/render?count=3000`.
- Allocated: **136,793.94 KB** (~133.6 MB/op).
- Top exclusive methods (whole benchmark process): `LowLevelLifoSemaphore.WaitForSignal` 63–68%, `WaitNative` ~9–10%, `WaitOneNoCheck` ~9–11%, `<unknown>` ~5%, `Monitor.Wait` ~5%.
- Top collector self-time methods: `EventPipeCpuSampler.AggregateHotspots` **0.02%** of total samples; everything else was effectively noise (`SampleCoreAsync`, `BuildHotspots`, `FormatFrame`, `BuildMethodIdentities` at ~0.00%).
- Interpretation: the benchmark process is overwhelmingly wait-bound while EventPipe records and the target app burns CPU. The measurable self-cost sits in the post-collection trace parse (`AggregateHotspots`), but it is tiny relative to the end-to-end window. No profiler-backed hotpath fix stood out.

## RoutingCpuSampler

- Load: same as `EventPipeCpuSampler`.
- Allocated: **95,454.18 KB** (~93.2 MB/op).
- Top exclusive methods (whole benchmark process): same wait-heavy shape as `EventPipeCpuSampler`.
- Top routing/collector self-time methods: `EventPipeCpuSampler.AggregateHotspots` **0.02%**; `RoutingCpuSampler.SampleAsync` showed only a single exclusive sample (<0.01%).
- Interpretation: the router is effectively free; all meaningful work is still the managed EventPipe sampler.

## EventPipeAllocationSampler

- Load: repeated `/render?count=5000` and `/generics?iterations=120000`.
- Allocated: **510,834.98 KB** (~498.9 MB/op).
- Top exclusive methods (whole benchmark process): `LowLevelLifoSemaphore.WaitForSignal` 50.50%, `WaitNative` 16.78%, `WaitOneNoCheck` 14.25%, `<unknown>` 7.17%, `Monitor.Wait` 6.91%.
- Top collector self-time methods: `EventPipeAllocationSampler.Aggregate` **0.02%**; `SampleAsync` and `TypeAccumulator.ToRecord` were ~0.00%.
- Interpretation: like the CPU sampler, the benchmark process mostly waits while the target generates allocation ticks. The measurable collector cost is concentrated in the final aggregation pass, but it is still a rounding error against the full window. No obvious per-event boxing/LINQ/string-split hotspot surfaced.

## PerfNativeAotCpuSampler

- Load: repeated `NativeAotSample /weatherforecast` requests. Note: the current sample responds `500` because of an existing NativeAOT JSON metadata issue (`JsonTypeInfo metadata for type 'WeatherForecast[]' was not provided...`), but the requests still generated real work/exceptions in the target process.
- Allocated: **298.73 MB/op**.
- Top exclusive methods (whole benchmark process): `LowLevelLifoSemaphore.WaitForSignal` 63.37%, `WaitNative` 10.02%, `WaitOneNoCheck` 8.85%, `<unknown>` 5.20%, `WaitAnyMultiple` 4.36%.
- Top collector self-time methods: `PerfNativeAotCpuSampler.AggregateAsync` **0.00%** (5 samples); `SampleAsync`/`RunScriptAsync` were also ~0.00%.
- Interpretation: parsing `perf script` output was visible but extremely small. The benchmark process again spent almost all of its time blocked on the external tool / request traffic, not burning CPU in the parser itself.
- Benchmark unblocker applied: `PerfNativeAotCpuSampler` was passing `perf record --max-size` as raw bytes (`536870912`), which the installed `perf` rejected with a usage error. Switching to perf-accepted size formatting (`512M`) let the benchmark run.

## PerfNativeAllocSampler

- Load attempted: same allocation-heavy CoreCLR load as `EventPipeAllocationSampler`.
- Result: **not profiled in this sandbox**.
- Actual failure:

  ```text
  perf probe could not create a uprobe on the target libc allocator.
  This usually means the sidecar lacks CAP_SYS_ADMIN / tracefs write access.
  Last perf stderr: No permission to write tracefs.
  Please run this command again with sudo.
    Error: Failed to add events.
  ```

- Interpretation: the existing `perf probe` permission gate prevented any hotpath measurement here.

## RoutingNativeAllocSampler

- Result: **not profiled in this sandbox**.
- Failure: identical to `PerfNativeAllocSampler` because the router delegates directly to that backend on Linux.
- Interpretation: routing overhead is not the limiting factor; environment permissions are.

## PerfSchedOffCpuSampler

- Load attempted: 48 concurrent `/activity?delayMs=900|1200` requests to create real waiting/blocking.
- Result: **not profiled in this sandbox**.
- Actual failure:

  ```text
  perf record (sched) exited with code 129. stderr: event syntax error: 'sched:sched_switch'
                       \___ can't access trace events

  Error: No permissions to read /sys/kernel/tracing//events/sched/sched_switch
  Hint:  Try 'sudo mount -o remount,mode=755 /sys/kernel/tracing/'
  ```

- Interpretation: off-CPU profiling is blocked by kernel tracing permissions, not by collector CPU cost.

## RoutingOffCpuSampler

- Result: **not profiled in this sandbox**.
- Failure: identical to `PerfSchedOffCpuSampler`; the Linux router delegates straight to that backend.

## MemoryTrendCollector

- Load: one `/leak?mb=1` request every 200ms during the 6s window.
- Allocated: **218.06 KB**.
- Top exclusive methods (whole benchmark process): `LowLevelLifoSemaphore.WaitForSignal` 33.19%, `WaitOneNoCheck` 22.71%, `WaitNative` 20.90%, `<unknown>` 11.47%, `Monitor.Wait` 11.19%.
- Top collector self-time methods: `MemoryTrendCollector.CollectAsync`, `TakeSample`, `ReadSmapsRollup`, `ReadStatFaults`, and `ComputeDeltas` each showed only **1 sample** (~0.00% each).
- Interpretation: this collector is extremely cheap; the benchmark mostly sleeps between 1-second polls. No hotpath work is worth tuning here from this profile.

## Windows-only classes skipped on Linux

- `EtwNativeAotCpuSampler`
- `EtwNativeAllocSampler`
- `EtwOffCpuSampler`

These are ETW-only implementations and were intentionally left out of this Linux pass.
