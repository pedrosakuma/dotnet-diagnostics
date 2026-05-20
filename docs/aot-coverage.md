# NativeAOT diagnostic coverage

This document maps common diagnostic questions to the tools that can answer them,
distinguishing **CoreCLR** (JIT-based, full metadata at runtime) from **NativeAOT**
(ahead-of-time compiled, no JIT metadata at runtime).

## Capability matrix

| Question | CoreCLR tool | NativeAOT tool | Notes |
|---|---|---|---|
| Is the process .NET? | `list_dotnet_processes` | `list_dotnet_processes` | Both expose a diagnostic IPC socket |
| What runtime is this? | `get_diagnostic_capabilities` | `get_diagnostic_capabilities` | Detects CoreCLR vs NativeAOT |
| Runtime counters (CPU, GC, heap, threads) | `snapshot_counters` | `snapshot_counters` | EventPipe works on both |
| GC pause frequency and duration | `collect_gc_events` | `collect_gc_events` | GC keyword on both runtimes |
| Exception rate and types | `collect_exceptions` | `collect_exceptions` | CLR exception events on both |
| Custom EventSource events | `collect_event_source` | `collect_event_source` | Provider must be embedded in AOT binary |
| **Allocation volume (bytes + events)** | **`collect_allocation_sample`** | **`collect_allocation_sample`** | GCAllocationTick fires on both — **see TypeName caveat below** |
| **Who is allocating? (type names)** | **`collect_allocation_sample`** | ❌ **not available** — TypeName is empty | GCAllocationTick TypeName is not populated by NativeAOT |
| What types dominate the heap? | `inspect_live_heap`, `inspect_dump` | ❌ not available | ClrMD requires JIT metadata |
| What method is hot (CPU)? | `collect_cpu_sample` (EventPipe) | `collect_cpu_sample` (perf/ETW) | AOT: native frames only |
| Off-CPU blocking stacks | `collect_off_cpu_sample` | `collect_off_cpu_sample` | perf/ETW |
| Thread stacks | `collect_thread_snapshot` | ❌ not available | ClrMD requires JIT metadata |
| Process dump | `collect_process_dump` | `collect_process_dump` | Dump is native-only on AOT |
| Container throttling / cgroup | `get_container_signals` | `get_container_signals` | Reads `/sys/fs/cgroup`, not runtime-specific |

## AOT heap diagnostics — what works and what doesn't

### `collect_allocation_sample` on NativeAOT

`GCAllocationTick` events **do fire** on NativeAOT — the GC threshold (~100 KB of total
managed allocations) still triggers. However, the runtime does **not** populate the
`TypeName` field in the event payload on NativeAOT, because managed type metadata is stripped
during AOT compilation. All events roll up under `<unknown>`.

What you **get** from `collect_allocation_sample` on NativeAOT:
- `TotalEvents` — total GC sampling events (a proxy for allocation rate)
- `TotalBytes` — aggregate allocation volume estimate
- A call-tree artifact (via the returned handle) with **native** frame addresses

What you **don't get**:
- Per-type breakdown (TypeName is empty)
- Managed method names in call stacks (all frames are native)

### Recommended AOT flow for memory pressure

```
1. get_diagnostic_capabilities   → confirm NativeAOT, note available tools
2. snapshot_counters             → baseline GC heap, gen counts, CPU, allocation rate counter
3. collect_gc_events(30)         → GC frequency, generation distribution, pause times
4. collect_allocation_sample(10) → total events/bytes (even without type names, shows allocation rate)
5. collect_cpu_sample            → perf/ETW native frames to find hot allocation sites
```

If `gc-heap-size` is growing in step 2 but `collect_allocation_sample` shows `<unknown>` types,
the allocation is happening via managed code compiled to native. Cross-reference with
`collect_cpu_sample` to find the native frames responsible for the allocation load. Look for
frames like `RhNewObject`, `RhNewArray`, `RhAllocateObject` in the call tree — the frames
above those are the allocation sites.

### Comparison: what AllocationSampled would have offered

The issue specification (#91) requested the `AllocationSampled` event from
`Microsoft-DotNETCore-SampleProfiler` (keyword `0x80000000`). Empirical testing on
.NET 10 confirmed this event is **not emitted** in practice on either CoreCLR or NativeAOT
on Linux — the `Microsoft-DotNETCore-SampleProfiler` provider only emits `Thread/Sample`
(CPU thread samples) with that keyword combination. The TraceEvent library v3.1.8 does not
include a parser for `AllocationSampled`. `GCAllocationTick` was chosen as the implementation
basis because it fires reliably, carries TypeName on CoreCLR, and produces call stacks.
