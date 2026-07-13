# Group A — event-stream collector hotpaths

Benchmarks were run with `BenchmarkDotNet 0.15.8` in `Release`, `[MemoryDiagnoser]`, and `[EventPipeProfiler(EventPipeProfile.CpuSampling)]` against a published `samples/CoreClrSample` child process launched as `dotnet CoreClrSample.dll`.

- Collection window: ~6s per benchmark (BenchmarkDotNet mean wall-clock lands around 7.1–7.7s including harness overhead).
- Load generation: external helper process so the profiler mostly samples collector-side work, not the HTTP driver itself.
- CPU tables below come from parsing the generated `.nettrace` files with `Microsoft.Diagnostics.Tracing.TraceEvent`.
- A blank method name row means the sample landed in an unresolved/native runtime frame in the EventPipe profile.
- Across almost every collector, exclusive CPU is dominated by runtime wait/parking frames. That is the main finding: these collectors are mostly callback/IPC bound, not obviously CPU-bound in their own managed parsing code.

## EventPipeActivityCollector

- **What it does:** Captures ActivitySource stop events and reconstructs spans.
- **Synthetic load:** 12 concurrent workers cycling `/activity?delayMs=25`, `/activity?delayMs=50`, and `/weatherforecast`.
- **Allocated:** `13099.8 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `177449`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 45752 | 25.8% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 37581 | 21.2% |
| 3 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 27478 | 15.5% |
| 4 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 25085 | 14.1% |
| 5 | `` | 18892 | 10.6% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 12570 | 7.1% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 8741 | 4.9% |
| 8 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 400 | 0.2% |
| 9 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 120 | 0.1% |
| 10 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 110 | 0.1% |

- **Interpretation:** Despite activity-heavy traffic, the benchmark process spends most sampled time parked in runtime waits while the collector’s background EventPipe reader drains events. No `DotnetDiagnostics.Core` frame entered the top-10 self-cost list, so there is no obvious small CPU hotpath to optimize.

## EventPipeLogCollector

- **What it does:** Collects Microsoft.Extensions.Logging EventPipe events with optional JSON payload stitching.
- **Synthetic load:** 16 concurrent workers cycling `/weatherforecast`, `/render?count=1500`, and `/activity?delayMs=25` to trigger routine ASP.NET Core request logging.
- **Allocated:** `87950.03 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `165145`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 55896 | 33.8% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 34496 | 20.9% |
| 3 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 20796 | 12.6% |
| 4 | `` | 17337 | 10.5% |
| 5 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 13164 | 8.0% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 11476 | 6.9% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 7272 | 4.4% |
| 8 | `System.Private.CoreLib!System.Threading.Thread.<PollGC>g__PollGCWorker\|67_0()` | 2948 | 1.8% |
| 9 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 488 | 0.3% |
| 10 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.Wait(int32,bool)` | 117 | 0.1% |

- **Interpretation:** After removing the pathological regex input, the benchmark completed reliably. Allocation is higher than the counter/request collectors, but exclusive CPU is still dominated by runtime waits rather than by collector-local message parsing/redaction work.

## EventPipeExceptionCollector

- **What it does:** Counts managed ExceptionStart events and retains recent exceptions.
- **Synthetic load:** 12 concurrent workers repeatedly calling `/parse` to force repeated `FormatException` activity.
- **Allocated:** `808593.79 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `199695`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 101483 | 50.8% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 30330 | 15.2% |
| 3 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 16823 | 8.4% |
| 4 | `` | 14829 | 7.4% |
| 5 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 9989 | 5.0% |
| 6 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 8614 | 4.3% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 5506 | 2.8% |
| 8 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.Wait(int32,bool)` | 3648 | 1.8% |
| 9 | ``System.Net.Sockets!System.Net.Sockets.SocketPal.TryCompleteReceiveFrom(class System.Net.Sockets.SafeSocketHandle,value class System.Span`1<unsigned int8>,class System.Collections.Generic.IList`1<value class System.ArraySegment`1<unsigned int8>>,value class System.Net.Sockets.SocketFlags,value class System.Span`1<unsigned int8>,int32&,int32&,value class System.Net.Sockets.SocketFlags&,value class System.Net.Sockets.SocketError&)`` | 1044 | 0.5% |
| 10 | `System.Private.CoreLib!System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()` | 926 | 0.5% |

- **Interpretation:** Driving pure `/parse` traffic makes this benchmark very allocation-heavy because the sample throws continuously, but exclusive CPU is still dominated by runtime wait/monitor frames. The collector’s own accounting logic does not surface as a top hotspot.

## EventPipeCrashGuardCollector

- **What it does:** Collects exception events plus crash-adjacent runtime signals for a possibly dying process.
- **Synthetic load:** Same exception-storm load as the plain exception collector (`/parse`).
- **Allocated:** `889409.13 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `235871`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 110153 | 46.7% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 38894 | 16.5% |
| 3 | `` | 19501 | 8.3% |
| 4 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 18290 | 7.8% |
| 5 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 13538 | 5.7% |
| 6 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 10641 | 4.5% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Enter_Slowpath(class System.Object)` | 9729 | 4.1% |
| 8 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 6827 | 2.9% |
| 9 | `System.Private.CoreLib!System.Runtime.EH.DispatchEx(value class System.Runtime.StackFrameIterator&,value class ExInfo&)` | 4309 | 1.8% |
| 10 | `System.Private.CoreLib!System.String.StrCns(unsigned int32,int)` | 933 | 0.4% |

- **Interpretation:** Crash-guard allocates even more than the plain exception collector because it keeps richer recent-exception/final-exception state, but sampled CPU is still mostly waiting/coordination rather than parsing work.

## EventPipeGcCollector

- **What it does:** Aggregates CLR GC start/stop/pause events over the collection window.
- **Synthetic load:** 10 concurrent workers cycling `/render?count=4000`, `/render?count=6000`, `/weatherforecast`, and `/parse` for sustained transient allocation churn.
- **Allocated:** `12053.3 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `163583`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 52953 | 32.4% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 35389 | 21.6% |
| 3 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 23109 | 14.1% |
| 4 | `` | 17790 | 10.9% |
| 5 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 12511 | 7.6% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 11372 | 7.0% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 8520 | 5.2% |
| 8 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 508 | 0.3% |
| 9 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.Wait(int32,bool)` | 371 | 0.2% |
| 10 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 132 | 0.1% |

- **Interpretation:** The allocation headline is large because the benchmark exercises a high-volume GC event stream, but sampled CPU again points to background waiting rather than a hot managed parser loop.

## EventPipeGcDatasCollector

- **What it does:** Collects GC events to a temporary nettrace, converts to TraceLog, then extracts DATAS tuning payloads.
- **Synthetic load:** Same GC-heavy load mix as `EventPipeGcCollector`.
- **Allocated:** `28373.57 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `170282`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 74771 | 43.9% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 36601 | 21.5% |
| 3 | `` | 18415 | 10.8% |
| 4 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 18026 | 10.6% |
| 5 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 11593 | 6.8% |
| 6 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 8424 | 4.9% |
| 7 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 444 | 0.3% |
| 8 | `System.Private.CoreLib!System.Threading.Thread.<PollGC>g__PollGCWorker\|67_0()` | 244 | 0.1% |
| 9 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.Wait(int32,bool)` | 127 | 0.1% |
| 10 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 114 | 0.1% |

- **Interpretation:** Allocation is materially higher because this collector writes a trace to disk and re-opens it through `TraceLog`; CPU samples are still overwhelmingly runtime waiting frames, so the extra cost is mostly trace materialization rather than a hot per-event parser loop.

## EventPipeCounterCollector

- **What it does:** Subscribes to EventCounters/System.Diagnostics.Metrics and snapshots latest values.
- **Synthetic load:** 12 concurrent workers mixing `/render?count=4000`, `/cpu-burn?ms=200`, `/weatherforecast`, and `/activity?delayMs=30`.
- **Allocated:** `611.05 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `157576`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 45049 | 28.6% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 36306 | 23.0% |
| 3 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 24250 | 15.4% |
| 4 | `` | 18270 | 11.6% |
| 5 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 11930 | 7.6% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 11901 | 7.6% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 8471 | 5.4% |
| 8 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 460 | 0.3% |
| 9 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 93 | 0.1% |
| 10 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 60 | 0.0% |

- **Interpretation:** Very low allocation (~600 KB/op) and a wait-dominated CPU profile suggest the counter collector is already proportionate to the amount of work it performs.

## EventPipeEventSourceCollector

- **What it does:** Captures every event+payload from one chosen EventSource provider.
- **Synthetic load:** 20 concurrent workers driving Kestrel traffic with `/weatherforecast`, `/render?count=1500`, and `/activity?delayMs=15`; provider benchmarked: `Microsoft-AspNetCore-Server-Kestrel`.
- **Allocated:** `3948.8 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `157176`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 47652 | 30.3% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 35090 | 22.3% |
| 3 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 23614 | 15.0% |
| 4 | `` | 17652 | 11.2% |
| 5 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 12044 | 7.7% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 11611 | 7.4% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 8120 | 5.2% |
| 8 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 480 | 0.3% |
| 9 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 93 | 0.1% |
| 10 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 75 | 0.0% |

- **Interpretation:** Capturing full payloads from the Kestrel provider raises allocation, but exclusive CPU still does not expose any collector-local hot frame worth a safe micro-optimization.

## EventPipeEventCatalogCollector

- **What it does:** Catalogs provider/event-name metadata only across several providers.
- **Synthetic load:** 16 concurrent workers mixing `/weatherforecast`, `/render?count=2500`, `/parse`, `/activity?delayMs=25`, and `/cpu-burn?ms=150`; providers benchmarked: runtime, DiagnosticSource, and Kestrel.
- **Allocated:** `131589.34 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `183864`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 75289 | 40.9% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 35911 | 19.5% |
| 3 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 24398 | 13.3% |
| 4 | `` | 17961 | 9.8% |
| 5 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 11760 | 6.4% |
| 6 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 8908 | 4.8% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 6749 | 3.7% |
| 8 | `System.Private.CoreLib!System.Threading.Monitor.Enter_Slowpath(class System.Object)` | 768 | 0.4% |
| 9 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 421 | 0.2% |
| 10 | `System.Private.CoreLib!System.Threading.Thread.<PollGC>g__PollGCWorker\|67_0()` | 222 | 0.1% |

- **Interpretation:** The metadata-only catalog path is still wait-dominated. It allocates noticeably less than the exception/crash collectors, which is acceptable for a broad “what events exist?” sweep.

## EventPipeContentionCollector

- **What it does:** Captures CLR contention start/stop pairs and summarizes longest waits.
- **Synthetic load:** 8 concurrent workers cycling `/threadpool/queue?globalItems=192&localItems=192&blockMs=3500`, `/cpu-burn?ms=250`, and `/activity?delayMs=150`.
- **Allocated:** `155.31 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `159594`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 56364 | 35.3% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 36871 | 23.1% |
| 3 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 33574 | 21.0% |
| 4 | `` | 18552 | 11.6% |
| 5 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 12144 | 7.6% |
| 6 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 987 | 0.6% |
| 7 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 388 | 0.2% |
| 8 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 97 | 0.1% |
| 9 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 71 | 0.0% |
| 10 | `System.Private.CoreLib!Interop+Sys.Read(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32)` | 30 | 0.0% |

- **Interpretation:** The contention workload generated the expected wait-heavy runtime profile. Collector-side bookkeeping did not surface in the top-10 self-cost list, so no targeted CPU fix stood out.

## EventPipeThreadPoolCollector

- **What it does:** Collects runtime thread-pool timeline / hill-climbing events.
- **Synthetic load:** Same thread-pool-pressure mix as `EventPipeContentionCollector`.
- **Allocated:** `430.31 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `167206`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 44872 | 26.8% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 37097 | 22.2% |
| 3 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 33311 | 19.9% |
| 4 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 19469 | 11.6% |
| 5 | `` | 18666 | 11.2% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 12242 | 7.3% |
| 7 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 418 | 0.2% |
| 8 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 378 | 0.2% |
| 9 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 101 | 0.1% |
| 10 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 60 | 0.0% |

- **Interpretation:** Thread-pool collection stays modest in allocation and largely idle between EventPipe callbacks; again there is no obvious collector-local CPU hotspot in the sampled top-10.

## EventPipeJitCollector

- **What it does:** Captures MethodJittingStarted/MethodLoadVerbose and tiered-compilation activity.
- **Synthetic load:** 8 concurrent workers repeatedly calling `/generics?iterations=180000` and `/generics?iterations=120000`.
- **Allocated:** `186.37 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `156944`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 41041 | 26.2% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 36258 | 23.1% |
| 3 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 24180 | 15.4% |
| 4 | `` | 18260 | 11.6% |
| 5 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 15179 | 9.7% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 11996 | 7.6% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 8777 | 5.6% |
| 8 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 471 | 0.3% |
| 9 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 82 | 0.1% |
| 10 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 73 | 0.0% |

- **Interpretation:** JIT collection is one of the lightest successful benchmarks by allocation and the CPU profile is dominated by wait states, not by method-key parsing or aggregation.

## EventPipeDbCollector

- **What it does:** Aggregates EF Core / SqlClient DiagnosticSource and EventSource activity.
- **Synthetic load:** Mixed request traffic (`/weatherforecast`, `/render?count=2500`, `/parse`, `/activity?delayMs=25`, `/cpu-burn?ms=150`) against the stock sample; the sample does not issue real DB calls, so this effectively measures the idle/no-event path.
- **Allocated:** `271.15 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `171399`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 67972 | 39.7% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 36772 | 21.5% |
| 3 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 33290 | 19.4% |
| 4 | `` | 18513 | 10.8% |
| 5 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 12128 | 7.1% |
| 6 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 1367 | 0.8% |
| 7 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 508 | 0.3% |
| 8 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 134 | 0.1% |
| 9 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 81 | 0.0% |
| 10 | `System.Private.CoreLib!Interop+Sys.FLock(int,value class LockOperations)` | 45 | 0.0% |

- **Interpretation:** Because the sample does not emit real DB provider activity, this benchmark effectively measures the no-event path. The low allocation and wait-dominated samples indicate the idle path is cheap.

## EventPipeKestrelCollector

- **What it does:** Aggregates Kestrel connection/request/TLS events and queue counters.
- **Synthetic load:** 20 concurrent workers driving `/weatherforecast`, `/render?count=1500`, and `/activity?delayMs=15`.
- **Allocated:** `8751.66 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `165713`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 53944 | 32.6% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 35796 | 21.6% |
| 3 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 23838 | 14.4% |
| 4 | `` | 18003 | 10.9% |
| 5 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 12739 | 7.7% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 11652 | 7.0% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 8267 | 5.0% |
| 8 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 486 | 0.3% |
| 9 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 99 | 0.1% |
| 10 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 82 | 0.0% |

- **Interpretation:** Kestrel event aggregation stays below 10 MB/op and the benchmark process is mostly blocked waiting for EventPipe/HTTP progress, which is a good sign for collector overhead.

## EventPipeNetworkingCollector

- **What it does:** Aggregates System.Net.* EventSource activity/counters.
- **Synthetic load:** Same Kestrel traffic mix as `EventPipeKestrelCollector`; because the sample is inbound-only, this mostly exercises server-side socket activity and the collector’s mostly-idle path.
- **Allocated:** `329.84 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `151747`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 42721 | 28.2% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 35069 | 23.1% |
| 3 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 31782 | 20.9% |
| 4 | `` | 17658 | 11.6% |
| 5 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 11724 | 7.7% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 11526 | 7.6% |
| 7 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 482 | 0.3% |
| 8 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 105 | 0.1% |
| 9 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 77 | 0.1% |
| 10 | `System.Private.CoreLib!Interop+Sys.Read(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32)` | 31 | 0.0% |

- **Interpretation:** The inbound-only sample produces only a light networking signal, so the collector mostly idles. That is reflected in both the low allocation total and the wait-heavy top-10 frames.

## EventPipeInFlightRequestCollector

- **What it does:** Tracks ASP.NET Core request start/stop pairs and reports requests still in flight.
- **Synthetic load:** 8 concurrent workers issuing intentionally long requests: `/activity?delayMs=1800` and `/cpu-burn?ms=1800`.
- **Allocated:** `206.88 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `162468`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 45809 | 28.2% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 37516 | 23.1% |
| 3 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 34178 | 21.0% |
| 4 | `` | 18873 | 11.6% |
| 5 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 12552 | 7.7% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 12416 | 7.6% |
| 7 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 366 | 0.2% |
| 8 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 100 | 0.1% |
| 9 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 63 | 0.0% |
| 10 | `System.Private.CoreLib!Interop+Sys.Read(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32)` | 33 | 0.0% |

- **Interpretation:** Even when intentionally holding requests open, the collector spends most sampled CPU in runtime waits. Allocation is tiny, so the current oldest-request tracker does not look like a hotpath issue.

## MethodParameterCaptureCollector

- **What it does:** Attaches the vendored dotnet-monitor profiler payload, ReJITs selected methods, and captures live parameter values.
- **Synthetic load:** 6 concurrent workers repeatedly calling `/cpu-burn?ms=123`, `/cpu-burn?ms=456`, and `/cpu-burn?ms=789`.
- **Allocated:** n/a (benchmark did not complete).
- **CPU trace:** n/a (no `.nettrace` emitted because the benchmark failed before the measurement run finished).
- **Interpretation:** I attempted the real benchmark twice; the collector failed in this environment with `Microsoft.Diagnostics.NETCore.Client.ServerErrorException: AttachProfilerAsync failed - HRESULT: 0x80004005` while attaching the profiler payload. This is an environment-specific limitation, not a measured hotpath, so I left the benchmark in place and documented the failure instead of inventing a synthetic substitute.

## ThresholdGatedCaptureCollector

- **What it does:** Polls one EventCounter and fires a callback when a threshold trips.
- **Synthetic load:** 12 concurrent workers mixing `/render?count=4000`, `/cpu-burn?ms=200`, `/weatherforecast`, and `/activity?delayMs=30`; predicate benchmarked: `cpu > 1`, synthetic callback only.
- **Allocated:** `171.59 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `177168`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 58899 | 33.2% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 36384 | 20.5% |
| 3 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 24188 | 13.7% |
| 4 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 20969 | 11.8% |
| 5 | `` | 18332 | 10.3% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 17191 | 9.7% |
| 7 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 433 | 0.2% |
| 8 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 97 | 0.1% |
| 9 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 67 | 0.0% |
| 10 | `System.Private.CoreLib!Interop+Sys.FLock(int,value class LockOperations)` | 30 | 0.0% |

- **Interpretation:** This is primarily a polling/co-ordination benchmark; low allocation and wait-dominated samples are expected, and no small safe optimization stands out.

## EventPipeStartupCollector

- **What it does:** Collects loader/DependencyInjection startup-related events from an already-running process.
- **Synthetic load:** 10 concurrent workers mixing `/weatherforecast`, `/render?count=1500`, `/generics?iterations=60000`, and `/activity?delayMs=20`. This is explicitly the live-attach path, not the cold-start suspended-launch path.
- **Allocated:** `3958.05 KB` per operation (MemoryDiagnoser).
- **Total sampled stacks:** `170170`.
- **Top 10 exclusive self-cost methods:**

| Rank | Method | Exclusive samples | % total |
| --- | --- | ---: | ---: |
| 1 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)` | 53830 | 31.6% |
| 2 | `System.Private.CoreLib!System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)` | 36685 | 21.6% |
| 3 | `System.Private.CoreLib!System.Threading.ManualResetEventSlim.Wait(int32,value class System.Threading.CancellationToken)` | 24693 | 14.5% |
| 4 | `` | 18468 | 10.9% |
| 5 | `System.Private.CoreLib!System.Threading.LowLevelLifoSemaphore.WaitNative(class Microsoft.Win32.SafeHandles.SafeWaitHandle,int32)` | 14566 | 8.6% |
| 6 | ``System.Private.CoreLib!System.Threading.WaitHandle.WaitAnyMultiple(value class System.ReadOnlySpan`1<class Microsoft.Win32.SafeHandles.SafeWaitHandle>,int32)`` | 12137 | 7.1% |
| 7 | `System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)` | 8543 | 5.0% |
| 8 | ``System.Private.CoreLib!System.IO.Enumeration.FileSystemEnumerator`1[System.__Canon].FindNextEntry()`` | 425 | 0.2% |
| 9 | `System.Private.CoreLib!Interop+Sys.Open(class System.String,value class OpenFlags,int32)` | 84 | 0.0% |
| 10 | `System.Private.CoreLib!Interop+Sys.PRead(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32,int64)` | 77 | 0.0% |

- **Interpretation:** This live-attach startup benchmark mainly measures the collector’s steady-state loader/DI observation path. It allocates a few MB to build the snapshot, but sampled CPU still does not reveal a collector-specific self-cost hotspot.
