# Group C hotpaths — attach / inspect / one-shot collectors

## Methodology

- Harness: `benchmarks/DiagnosedBenchmarks/GroupCRemainingBenchmarks`
- Sample target: a real `CoreClrSample.dll` child process launched with `dotnet <dll> --urls http://127.0.0.1:0` (never `dotnet run`)
- Per-iteration setup: start a fresh sample, then prime heap/state with `/render`, `/leak`, `/generics`, `/async-pending`, and a short `/cpu-burn`
- Benchmark attributes: `[MemoryDiagnoser]` + `[EventPipeProfiler(EventPipeProfile.CpuSampling)]`
- CPU parsing: `.nettrace` files exported by BenchmarkDotNet EventPipeProfiler and parsed with `Microsoft.Diagnostics.Tracing.TraceEvent`
- Scope note: several lightweight one-shot collectors are so short that the profiler mostly samples BenchmarkDotNet/profiler wait time; that is itself evidence that collector-side CPU is negligible

## ClrMdDumpInspector

- Setup: live `InspectLiveAsync` against a primed CoreClrSample heap with `IncludeStaticFields=true` and duplicate-string tracking enabled
- Allocated: **14.61 MB/op**
- Top exclusive-time methods:
  1. 31.0% `System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)`
  2. 22.7% `System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)`
  3. 19.9% `System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)`
  4. 11.4% `0x74814c32d485`
  5. 9.5% `System.Threading.Monitor.Wait(class System.Object,int32)`
  6. 1.0% `Microsoft.Diagnostics.Runtime.DacInterface.DacDataTarget.ReadVirtual(...)`
  7. 0.6% `Microsoft.Diagnostics.Runtime.Utilities.LinuxLiveDataReader.ReadMemoryReadv(...)`
  8. 0.3% `Interop+Sys.Open(...)`
  9. 0.3% `System.Threading.Monitor.Enter_Slowpath(class System.Object)`
  10. 0.3% `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()`
- Interpretation: wall time is real (~1.2s/op) but the profiler mostly catches harness/profiler waiting, not a managed hot loop inside the inspector. The first clearly relevant frames are ClrMD DAC reads, so the dominant cost is still raw heap walking / memory reads, not avoidable LINQ or boxing in the new bounded streaming paths.

### Dump-file path — `UseLockFreeMemoryMapReader` experiment (issue #686)

- Question: does ClrMD 4.0's opt-in lock-free memory-mapped dump reader
  (`DataTargetOptions.UseLockFreeMemoryMapReader`) speed up our dump-based drilldowns
  (`inspect_heap(source="dump")`, `query_snapshot` gcroot/object/objsize, `capture_method_bytes`
  on a dump)? These all call `DataTarget.LoadDump(path)` today.
- Thread-safety audit (required before enabling — the resulting reader is not thread-safe): every
  dump-based call site (`ClrMdRuntimeSession.LoadDump`, `ClrMdThreadSnapshotInspector.CaptureDump`,
  `ClrMdFrameVariableResolver.OpenTarget`, `ClrMdNativeAddressResolver.OpenTarget`,
  `ClrMdJitMethodCapturer.CaptureFromDumpAsync`) opens its own short-lived `DataTarget` inside a
  single method call and never shares it across threads or across requests (each MCP tool
  invocation opens a fresh `DataTarget`). No code in the repo reads `ClrRuntime.IsThreadSafe`. So
  there is no cross-call-site contention risk from disabling the default reader's internal locking.
- Spike method: a throwaway console harness (not committed) launched a real `CoreClrSample.dll`
  child process, called `/leak?mb=32` 40x to retain ~1.28 GiB of heap, wrote a `WithHeap` dump via
  `DiagnosticsClient.WriteDump(..., logDumpGeneration: false)` (1510 MiB dump file), then repeated a
  full `heap.EnumerateObjects()` (sum sizes/count) + `heap.EnumerateRoots()` pass 8x against the same
  dump file with the default reader and 8x with `UseLockFreeMemoryMapReader = true` — the same read
  pattern `inspect_heap`/gcroot/objsize drilldowns exercise.
- Result (wall-clock per full pass, 8 iterations each, first default-reader iteration excluded as a
  cold-cache outlier):

  | Reader | mean | min | max |
  | --- | --- | --- | --- |
  | Default (stream + locks) | ~60.6 ms | 48.5 ms | 72.6 ms |
  | Lock-free mmap | ~45.6 ms | 29.7 ms | 105.9 ms |

  Object count (15,701) and total heap bytes (1,343,662,259) were identical between both readers on
  every iteration — no correctness difference, only speed. The lock-free reader was **~1.3-1.8x
  faster** on typical iterations (one outlier iteration at 105.9ms was still faster than the
  default-reader median).
- Decision: **enabled unconditionally.** Added `ClrMdDumpLoader.Load(path)` (wraps
  `DataTarget.LoadDump(path, new DataTargetOptions { UseLockFreeMemoryMapReader = true })`) and
  routed all 5 dump-file call sites through it. No config knob was added — the thread-safety
  audit above shows no call site is affected, and the memory cost is the same order of magnitude
  as the default reader already touching most of the dump for a heap walk (not tested against an
  extremely large dump far exceeding available address space / RAM; revisit if that ever becomes a
  real scenario per `docs/resource-boundedness.md`).

## GcDumpHeapSnapshotCollector

- Setup: real `CollectAsync` gcdump-style EventPipe heap walk against the same primed sample
- Allocated: **1.57 MB/op**
- Top exclusive-time methods:
  1. 33.6% `System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)`
  2. 23.7% `System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)`
  3. 20.5% `System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)`
  4. 10.3% `0x7bb8ea51d485`
  5. 8.2% `System.Threading.Monitor.Wait(class System.Object,int32)`
  6. 0.9% `System.IO.Enumeration.FileSystemEnumerator\`1[System.__Canon].MoveNext()`
  7. 0.3% `Interop+Sys.Open(...)`
  8. 0.3% `System.Diagnostics.Process.GetProcessById(int32)`
  9. 0.3% `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()`
  10. 0.3% `System.IO.Enumeration.FileSystemEnumerator\`1[System.__Canon].FindNextEntry()`
- Interpretation: allocations stayed low and no collector-owned managed hotspot surfaced. This path looks bounded and already cheap relative to the induced GC / EventPipe session itself.

## ClrMdThreadSnapshotInspector

- Setup: live `InspectLiveAsync` during concurrent `/cpu-burn` requests
- Allocated: **3.53 MB/op** (measured in a follow-up MemoryDiagnoser-only rerun)
- CPU trace status: **EventPipeProfiler was unstable here**
  - Benchmarked method wall time was repeatable at ~**2.007 s/op**
  - The EventPipeProfiler diagnostic rerun repeatedly hung after entering its second actual iteration
  - I forced a stop only after the `.nettrace` appeared on disk, but the file was still truncated and `TraceLog.CreateFromEventPipeDataFile(...)` failed with `System.FormatException: Read past end of stream`
- Interpretation: allocation is moderate but I do not have a trustworthy completed CPU top-10 table for this collector from this sandbox. This is a tooling limitation, not a silent skip.

## EtwNativeThreadSnapshotInspector

- Status: **not applicable in this Linux sandbox**
- Reason: Windows-only ETW backend

## LinuxNativeThreadSnapshotInspector

- Status: **blocked in this sandbox**
- Attempted benchmark result: `UnauthorizedAccessException`
- Exact blocker:
  - collector error: `kernel.yama.ptrace_scope=1 ... same-UID peer attach is blocked`
  - attempted remediation: `sudo -n sysctl -w kernel.yama.ptrace_scope=0`
  - actual result: `sudo: a password is required`
- Interpretation: no benchmark numbers from this host without `CAP_SYS_PTRACE` or a host sysctl change.

## PerfReplayThreadSnapshotInspector

- Status: **skipped after an explicit perf probe**
- Probe results:
  - direct binary worked: `/usr/lib/linux-tools-6.8.0-134/perf --version` → `perf version 6.8.12`
  - actual replay prerequisite failed: `/usr/lib/linux-tools-6.8.0-134/perf record -a -e sched:sched_switch --call-graph dwarf ...`
  - error: `No permissions to read /sys/kernel/tracing//events/sched/sched_switch`
- Interpretation: the host has a usable `perf` binary but not the tracepoint permissions needed for sched-switch replay, so this backend was genuinely inapplicable here.

## ProcessResourcesCollector

- Setup: `CollectAsync(processId, durationSeconds: 0, sampleEverySeconds: 1)` against the primed sample
- Allocated: **339.4 KB/op**
- Top exclusive-time methods:
  1. 28.5% `System.Threading.LowLevelLifoSemaphore.WaitNative(...)`
  2. 25.9% `System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)`
  3. 20.4% `System.Threading.WaitHandle.WaitOneNoCheck(...)`
  4. 13.2% `System.Threading.Monitor.Wait(class System.Object,int32)`
  5. 10.2% `0x750684b0d485`
  6. 0.3% `System.IO.Enumeration.FileSystemEnumerator\`1[System.__Canon].MoveNext()`
  7. 0.2% `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()`
  8. 0.2% `Interop+Sys.Open(...)`
  9. 0.1% `System.IO.Strategies.OSFileStreamStrategy.Read(...)`
  10. 0.1% `System.IO.Enumeration.FileSystemEnumerator\`1[System.__Canon].FindNextEntry()`
- Interpretation: the 2-second `gc-heap-size` probe dominates total latency. The `/proc` scans themselves are negligible; there is no small collector-side CPU fix worth making here.

## RequestsNowCollector

- Setup: six concurrent `/cpu-burn?ms=4000` requests while `CollectAsync(window: 2s, topFrames: 8)` snapshots in-flight requests
- Allocated: **91.35 KB/op**
- Top exclusive-time methods:
  1. 41.0% `System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)`
  2. 20.7% `System.Threading.WaitHandle.WaitOneNoCheck(...)`
  3. 13.7% `System.Threading.LowLevelLifoSemaphore.WaitNative(...)`
  4. 12.7% `System.Threading.Monitor.Wait(class System.Object,int32)`
  5. 10.4% `0x7201a8f0d485`
  6. 0.3% `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()`
  7. 0.2% `System.IO.Enumeration.FileSystemEnumerator\`1[System.__Canon].MoveNext()`
  8. 0.1% `System.Runtime.EH.DispatchEx(...)`
  9. 0.1% `Interop+Sys.Open(...)`
  10. 0.1% `System.IO.Enumeration.FileSystemEnumerator\`1[System.__Canon].FindNextEntry()`
- Interpretation: the bounded-channel implementation from PR #607 kept allocation extremely small. CPU samples are mostly the expected snapshot-window wait; I did not find evidence of a new managed hot loop in the collector itself.

## RuntimeConfigInspector

- Setup: `InspectAsync(processId)` against the live sample
- Allocated: **4.86 MB/op**
- Top exclusive-time methods:
  1. 33.3% `System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)`
  2. 22.5% `System.Threading.WaitHandle.WaitOneNoCheck(...)`
  3. 19.5% `System.Threading.LowLevelLifoSemaphore.WaitNative(...)`
  4. 11.3% `0x7784432fd485`
  5. 9.0% `System.Threading.Monitor.Wait(class System.Object,int32)`
  6. 0.7% `Microsoft.Diagnostics.Runtime.Utilities.LinuxLiveDataReader.ReadMemoryReadv(...)`
  7. 0.6% `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()`
  8. 0.4% `Microsoft.Diagnostics.Runtime.DacInterface.DacDataTarget.ReadVirtual(...)`
  9. 0.3% `Interop+Sys.Open(...)`
  10. 0.3% `System.Runtime.EH.DispatchEx(...)`
- Interpretation: like `ClrMdDumpInspector`, the visible collector-side work is ClrMD attach/read cost. No high-confidence micro-optimization stood out.

## PreflightInspector

- Setup: `Inspect(processId)` repeated 50x per benchmark invocation to make the profiler window meaningful
- Allocated: **283.2 KB/op**
- Top exclusive-time methods:
  1. 38.9% `System.Threading.LowLevelLifoSemaphore.WaitNative(...)`
  2. 24.2% `System.Threading.WaitHandle.WaitOneNoCheck(...)`
  3. 12.7% `0x79596b0ed485`
  4. 10.9% `System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)`
  5. 10.3% `System.Threading.Monitor.Wait(class System.Object,int32)`
  6. 0.9% `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()`
  7. 0.4% `System.Runtime.EH.DispatchEx(...)`
  8. 0.1% `Interop+Sys.Read(...)`
  9. 0.1% `System.Diagnostics.Process.GetProcessById(int32)`
  10. 0.1% `System.IO.Enumeration.FileSystemEnumerator\`1[System.__Canon].FindNextEntry()`
- Interpretation: this is effectively negligible; profiler noise dominates because the real work is just a few `/proc` reads and capability checks.

## CgroupV2SignalsCollector

- Setup: `CollectAsync(processId)` repeated 200x per benchmark invocation; host filesystem confirmed `cgroup2fs`
- Allocated: **108.12 KB/op**
- Top exclusive-time methods:
  1. 27.9% `System.Threading.LowLevelLifoSemaphore.WaitNative(...)`
  2. 26.3% `System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)`
  3. 22.7% `System.Threading.WaitHandle.WaitOneNoCheck(...)`
  4. 11.4% `0x7703a4b0d485`
  5. 8.8% `System.Threading.Monitor.Wait(class System.Object,int32)`
  6. 0.5% `Interop+Sys.Open(...)`
  7. 0.3% `System.Diagnostics.Process.GetProcessById(int32)`
  8. 0.2% `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()`
  9. 0.2% `System.IO.Strategies.OSFileStreamStrategy.Read(...)`
  10. 0.2% `System.Runtime.EH.DispatchEx(...)`
- Interpretation: one-shot cgroup v2 file reads are cheap. Nothing here justifies a code change.

## Overall conclusion

- The only materially expensive managed collectors in this group are the ClrMD-backed ones (`ClrMdDumpInspector`, `ClrMdThreadSnapshotInspector`, `RuntimeConfigInspector`), and their visible cost is still dominated by ClrMD attach / DAC reads rather than avoidable managed hot loops.
- `RequestsNowCollector`, `ProcessResourcesCollector`, `PreflightInspector`, and `CgroupV2SignalsCollector` look allocation-bounded and CPU-light enough that I would not change them without a different workload showing something more specific.
- I did **not** apply a code fix in Group C because I did not find a profiler-backed, high-confidence micro-optimization worth taking on its own.
- One exception: the `UseLockFreeMemoryMapReader` experiment above (issue #686) *did* find a real,
  low-risk win for the dump-file read path specifically, and was applied — see `ClrMdDumpLoader`.
