# NativeAOT diagnostic coverage

This document maps every diagnostic tool to its runtime × OS support and points
to the gap-filling tool when the canonical one doesn't apply. **NativeAOT
parity (meta [#91](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/91))
is functionally complete since v0.3.1**, with the remaining honest non-goals
(managed-type retention, lock-graph identity) called out below.

- **CoreCLR** — JIT-based runtimes (`Microsoft.NETCore.App`). Full managed
  metadata at runtime → ClrMD-backed tools (thread snapshot, heap walk) work.
- **NativeAOT** — published with `PublishAot=true`. No JIT and no DAC →
  ClrMD-backed paths fall back to OS-native unwinders (eu-stack / ETW kernel
  stacks / perf-replay) and EventPipe is the only managed signal.

See also:
[`tool-reference.md`](./tool-reference.md) ·
[`investigation-playbooks.md`](./investigation-playbooks.md) ·
[`windows-sidecar-service.md`](./windows-sidecar-service.md) ·
[`local-docker-sidecar.md`](./local-docker-sidecar.md).

## Symbol sources (legend)

The capability digest returned by `get_diagnostic_capabilities` and
`collect_thread_snapshot` reports which symbol/stack source it used.

These ids are what `get_diagnostic_capabilities` returns in
`data.threadSnapshotSource` and what `collect_thread_snapshot` stamps on its
artifact.

| Source | Backend | Resolves to | Where it applies |
|---|---|---|---|
| `clrmd-thread-walk` | DAC over the diagnostic socket | Managed `Type.MethodName`, lock owner, SyncBlock identity | CoreCLR (Linux + Windows) |
| `linux-native-stack` | `eu-stack -p <pid>` + libdw DWARF unwind | Native frames; managed names come from `.symbols.map` when present | NativeAOT/Linux with `CAP_SYS_PTRACE` |
| `etw-native-stack` | Kernel Logger `Thread/Stack` events (TraceEvent) | Native frames; managed names come from PDB export table | NativeAOT/Windows elevated |
| `perf-replay-approx` | `perf record -e sched:sched_switch --call-graph dwarf` | "Last stack seen per TID" — not point-in-time | AOT fallback when ptrace is blocked |
| `symbols.map` | NativeAOT symbol sidecar emitted at publish | Demangled managed names for native frames | NativeAOT (both OS) |
| `pdb-export` | PE export table + Portable PDB | Demangled managed names for native frames | NativeAOT/Windows |

`perf-replay-approx` is a **best-effort** source: it replaces a hard `❌` with a
`⚠️`. Its weakness is staleness, not accuracy — the frames are real, they just
reflect "where this TID last context-switched" instead of "where it is right
now". When `ptrace_scope=0` and `CAP_SYS_PTRACE` is held the router prefers the
live source automatically.

## Tool × runtime × OS matrix

Legend: `✅` works · `⚠️` works with caveats (footnote) · `❌` unavailable

| Tool | CoreCLR / Linux | CoreCLR / Windows | NativeAOT / Linux | NativeAOT / Windows |
|---|---|---|---|---|
| `list_dotnet_processes` | ✅ | ✅ | ✅ [^stale] | ✅ |
| `get_process_info` | ✅ | ✅ | ✅ | ✅ |
| `get_diagnostic_capabilities` | ✅ | ✅ | ✅ | ✅ |
| `get_container_signals` | ✅ | ⚠️ Linux only | ✅ | ⚠️ Linux only |
| `get_memory_trend` | ✅ | ✅ | ✅ | ✅ |
| `snapshot_counters` | ✅ | ✅ | ✅ | ✅ |
| `collect_gc_events` | ✅ | ✅ | ✅ | ✅ |
| `collect_exceptions` | ✅ | ✅ | ✅ | ✅ |
| `collect_event_source` | ✅ | ✅ | ⚠️ [^aot-eventsource] | ⚠️ [^aot-eventsource] |
| `collect_cpu_sample` | ✅ EventPipe | ✅ EventPipe | ✅ `perf` (`symbols.map`) | ✅ ETW (`pdb-export`) [^win-etw-elev] |
| `collect_off_cpu_sample` | ✅ `perf` | ⚠️ ETW kernel logger, elevated [^win-etw-elev] | ✅ `perf` [^perf-install] | ⚠️ ETW kernel logger, elevated [^win-etw-elev] |
| `collect_allocation_sample` | ✅ TypeName populated | ✅ TypeName populated | ⚠️ TypeName empty [^aot-typename] | ⚠️ TypeName empty [^aot-typename] |
| `collect_thread_snapshot` | ✅ `clrmd-thread-walk` | ✅ `clrmd-thread-walk` | ✅ `linux-native-stack` ([#92](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/92)) | ✅ `etw-native-stack` ([#93](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/93)) |
| `query_thread_snapshot` | ✅ full lock graph | ✅ full lock graph | ⚠️ no managed lock graph [^lock-graph] | ⚠️ no managed lock graph [^lock-graph] |
| `inspect_live_heap` / `query_heap_snapshot` | ✅ | ✅ | ❌ [^heap] | ❌ [^heap] |
| `inspect_dump` (heap) | ✅ | ✅ | ❌ [^heap] | ❌ [^heap] |
| `collect_process_dump` | ✅ | ✅ | ✅ native dump | ✅ native dump |
| `capture_method_bytes` | ✅ JIT code-heap | ✅ JIT code-heap | ❌ [^jit-only] | ❌ [^jit-only] |
| `start_investigation` / `export_investigation_summary` / `compare_to_baseline` | ✅ | ✅ | ✅ | ✅ |
| `get_collection_status` / `cancel_collection` | ✅ | ✅ | ✅ | ✅ |

[^stale]: Long-lived sidecars whose `/tmp` accumulates stale diagnostic sockets across restarts may see `list_dotnet_processes` return `[]`. Pass `processId` explicitly. Tracking: [#108](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/108).
[^aot-eventsource]: The provider must be embedded in the AOT binary at publish time. Sources added via assembly load after publish are not reachable.
[^perf-install]: Sidecar Dockerfile installs `perf` only when built with `--build-arg INSTALL_PERF=true`. See [`local-docker-sidecar.md`](./local-docker-sidecar.md) and [#104](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/104).
[^win-etw-elev]: NT Kernel Logger sessions require administrative elevation (or `SeSystemProfilePrivilege`). Tracked under [#59](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/59) for the Windows sidecar service profile.
[^aot-typename]: NativeAOT does not populate `GCAllocationTick.TypeName`. Total events and bytes are correct; rollup is `<unknown>`. Cross-reference with `collect_cpu_sample` for native allocation-site frames (`RhNewObject`, `RhNewArray`, `RhAllocateObject`). Improvement tracked in [#100](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/100).
[^lock-graph]: There is no native equivalent to a managed `SyncBlock`. Thread states and stacks are accurate; ownership/waiter edges between managed objects are not recoverable without runtime cooperation. Pure-native locks (futex, srwlock) still show up as off-CPU stacks via `collect_off_cpu_sample`.
[^heap]: ClrMD's DAC has no NativeAOT implementation; there is no public design for one upstream. See **Honest non-goals** below.
[^jit-only]: `capture_method_bytes` reads the JIT code-heap of a live process. On NativeAOT and pure ReadyToRun there is no code-heap — the code is in the on-disk binary. Use the `dotnet-native-mcp.disassemble` companion server against the published ELF/PE.

## Honest non-goals

These gaps are not bugs and not on any roadmap. They require runtime cooperation
that does not exist today.

- **Type-level retained-byte walking of an AOT heap.** ClrMD's DAC reads
  in-process type tables; NativeAOT strips them at publish. There is no public
  design in `dotnet/runtime` for an AOT-equivalent DAC. The pragmatic
  alternative is **allocation-rate diagnosis** (`collect_allocation_sample` +
  `collect_cpu_sample` for native allocation-site frames) plus **growth-rate
  observation** (`get_memory_trend`).
- **Managed lock graph (SyncBlock identity, owner→waiter edges) on AOT.** Same
  root cause. Native lock primitives (`futex`, `pthread_mutex`, `srwlock`)
  still show up as off-CPU waits.
- **`Thread.Name` on AOT when the app does not call `pthread_setname_np`.**
  CoreCLR and AOT both call it by default; the gap only appears in
  hand-rolled native threads.

## Recipes

### "My NativeAOT process is leaking"

`inspect_live_heap` is unavailable. The growth-then-attribution flow:

```
1. get_memory_trend(pid, durationSeconds=30)
   → verdict (growing / stable / shrinking), RSS/PSS/private-anon deltas
2. snapshot_counters(pid, durationSeconds=10)
   → gc-heap-size, gen counts, threadpool — confirm it's managed
3. collect_gc_events(pid, durationSeconds=30)
   → GC frequency + per-gen counts; if Gen2 collections are rare and heap
     keeps growing, suspect LOH or long-lived rooted objects
4. collect_allocation_sample(pid, durationSeconds=30)
   → total bytes/events (TypeName is empty on AOT — that's expected)
5. collect_cpu_sample(pid, durationSeconds=30)
   → get_call_tree → look for RhNewObject / RhNewArray / RhAllocateObject
     frames; the parents are the allocation sites
```

This trades type-level resolution for site-level resolution. It answers "where
is the allocation pressure coming from?" instead of "what objects are retained
right now?".

### "My NativeAOT process is hung"

`collect_thread_snapshot` works since v0.3.1 — the router dispatches to
`linux-native-stack` (eu-stack + DWARF) or `etw-native-stack` (ETW kernel
stacks) automatically. The managed lock graph is the only thing missing.

```
1. collect_thread_snapshot(pid)
   → returns ThreadSnapshotArtifact with osThreadId, state, stack, IsLikelyBlocked
   → caveats: partial-unwind warnings on the AOT entrypoint frame are benign
2. query_thread_snapshot(handle, view="top-blocked")
   → ranks threads by IsLikelyBlocked then LockCount
3. query_thread_snapshot(handle, view="stack", threadId=<TID>)
   → full native frames
4. (optional) collect_off_cpu_sample(pid, durationSeconds=10)
   → if the snapshot is ambiguous, off-CPU sampling shows where the thread
     spent its blocked time (futex, IO, sleep) — works on AOT/Linux
```

### "Is this a NativeAOT app?"

```
1. get_diagnostic_capabilities(pid)
   → data.runtime ∈ {CoreClr, NativeAot}
   → data.threadSnapshotSource ∈ {clrmd-thread-walk, linux-native-stack, etw-native-stack, perf-replay-approx}
   → data.canAttachClrMD (live heap walking needs this + CoreClr)
   → data.canSampleCpu, canSampleOffCpu, canCollectThreadSnapshot, canCollectProcessDump
```

A single capability call gives the LLM the complete usable-tool set for the
target before any data is collected.

## Related issues

- Meta: [#91 NativeAOT coverage parity](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/91)
- Slice 1 (thread snapshot): [#92](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/92) Linux · [#93](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/93) Windows · [#94](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/94) perf-replay fallback
- Slice 2 (allocation): [#95](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/95) collector · [#100](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/100) TypeName projection
- Slice 3 (memory trend): [#96](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/96)
- Open follow-ups: [#59](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/59) Windows off-CPU elevation · [#104](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/104) perf install default · [#108](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/108) stale-socket enumeration
