# Resource-boundedness of long/high-volume captures

> Companion to [`AGENTS.md`](../AGENTS.md). This document exists because several
> EventPipe/ClrMD collectors used to accumulate state proportional to capture
> duration or event volume with no upper bound — a real OOM/hang risk for long
> or high-throughput captures. Issues
> [#604](https://github.com/pedrosakuma/dotnet-diagnostics/issues/604),
> [#605](https://github.com/pedrosakuma/dotnet-diagnostics/issues/605), and
> [#606](https://github.com/pedrosakuma/dotnet-diagnostics/issues/606) tracked
> the audit and fixes (PRs #607–#614). This page records **what each fix
> actually trades away** so nobody has to re-derive it from a diff later.

## Why this matters (and what it doesn't fix)

Bounding a collector's memory is not free: past the configured cap, a
collector either (a) merges the excess into an aggregate/overflow bucket, (b)
evicts the oldest/least-interesting entries, or (c) drops new entries outright.
These fixes surface a cap hit in the result (`Notes`/similar fields carry an
explicit message with the cap and the drop/eviction count). If you are
diagnosing a workload that plausibly exceeds these caps (e.g. thousands of
distinct SQL shapes or a sustained request flood), read the returned
snapshot's `Notes` before trusting the numbers as exhaustive.

Two different fix shapes exist in this codebase; they have very different
implications:

- **Pure efficiency refactors (zero behavioral change).** The result is
  mathematically identical to the old unbounded implementation — the fix only
  changed *when* aggregation happens (incrementally during collection instead
  of materializing everything then truncating at the end), so peak memory
  drops but nothing is lost.
- **Real retention trade-offs.** Past the cap, some detail is genuinely
  merged/dropped. These caps were deliberately set high enough that normal
  workloads never hit them. Some collectors rank what they keep (longest
  duration, oldest-still-pending); others simply keep the **first N distinct**
  entries observed and aggregate/drop the rest — see the table below for which
  strategy each collector uses.

## Per-collector reference

### Pure efficiency refactors — no data loss at any cap

| Collector | What changed | Result vs. before |
|---|---|---|
| `ClrMdDumpInspector.WalkGcHandles` (`Dump/`) | GC handle aggregation is now streamed into `GcHandleAggregation` incrementally instead of materializing every handle into a `List<GcHandleSample>` first | Identical — same buckets/counts, lower peak memory during heap inspection |
| `ClrMdDumpInspector.WalkStaticFields` (`Dump/`) | Maintains a bounded top-N structure while walking instead of collecting every static reference then `Take(topN)` | Identical — same top-N rows for the same `topN` value |
| `GcActivityCorrelator.Correlate` (`Collection/`) | Sorted-window interval scan instead of an O(activities × GC events) all-pairs loop, with a bounded top-N heap while scanning | Identical — same `topN` impacted activities, much less CPU/memory for large artifacts |

### Real retention trade-offs — bounded with explicit notes

| Collector | Cap | What's kept | What's dropped/merged | Signal |
|---|---|---|---|---|
| `EventPipeInFlightRequestCollector` (`Requests/`) | `maxRequests` (caller-supplied) | The **oldest** still-pending requests (most likely to explain a stall) | Newer request starts once the cap is hit | `notes`: `"Dropped {n} newer in-flight request start event(s) after reaching maxRequests=..."` — `Requests`/`InFlightCount` become lower bounds |
| `EventPipeContentionCollector` (`Contention/`) | `MaxTrackedEvents = 200` | The **longest-duration** contention events | Shorter events past the cap | `notes`: `"Retained the N longest contention event(s) after reaching the in-memory cap of 200; M shorter event(s) were dropped."` |
| — same collector, distinct monitor ids | `MaxTrackedMonitorIds = 4096` | First 4096 distinct monitor identities | `DistinctMonitors` becomes a lower bound past the cap | `notes`: distinct-monitor overflow note |
| `EventPipeJitCollector` (`Jit/`) | `MaxTrackedMethods = 2048`, plus separate caps for pending starts (4096 ids × 8 starts/id), IL-map correlation (4096), R2R-miss correlation (4096) | Top-line JIT counters always stay exact; per-method detail for the **first** 2048 distinct methods observed (first-come, not ranked) | Per-method rows for methods observed after the cap (still counted in aggregate totals) | `notes`: one message per exhausted cap, each naming the exact limit and drop count |
| `DbEventAggregationState` (`Db/`) | `MaxTrackedCommandAggregates = 256`, `MaxTrackedNPlusOneIncidents = 256`, `MaxTrackedPendingCommands = 2048` (+ a TTL for pending commands) | The **first** 256 distinct command shapes / N+1 keys observed (first-come, not ranked); up to 2048 in-flight (unmatched `BeginExecute`) commands, each expiring after a fixed TTL if never completed | Additional distinct command shapes fold into a single overflow bucket; pending commands beyond the cap evict oldest-first; pending commands stuck past the TTL are dropped (assumed abandoned) | `notes`: `"Aggregated N additional distinct DB command shape(s) into the overflow bucket..."`, `"Evicted N oldest pending SqlClient command(s)..."`, `"Expired N pending SqlClient command(s) that exceeded the M-minute completion TTL."` |
| `EventPipeNetworkingCollector` (`Networking/`) | `MaxTrackedOperationGroups = 256` (host+path buckets), `MaxPendingActivities = 4096` per correlation kind (HTTP/DNS/TLS) + a TTL | The **first** 256 distinct host/path operation buckets observed (first-come, not ranked); up to 4096 pending activities per kind, expiring after the TTL | Extra operations fold into a synthetic `(other)`/`(overflow)` bucket; pending correlation state past the cap/TTL is evicted, producing unmatched failure counts | `notes`: overflow-bucket note, plus expire/evict notes per correlation kind |
| `EventPipeKestrelCollector` (`Kestrel/`) | Same shape as Networking: `MaxTrackedOperationGroups = 256`, `MaxPendingActivities = 4096` (connections/requests/TLS), `MaxQueuePoints = 4096` | The **first** 256 distinct method/path/version groups observed (first-come, not ranked); bounded pending correlation state; bounded queue-depth timeline | Extra request groups fold into `(other)`/`(overflow)`; queue-length samples beyond the cap are dropped (timeline becomes sparser, not wrong) | `notes`: overflow/evict/drop messages mirroring Networking |
| `EventPipeThreadPoolCollector` (`ThreadPool/`) | `MaxTimelineSamples = 4096` per series (worker, IOCP, hill-climbing) | The most recent 4096 samples per series (ring buffer — oldest evicted first) | Older timeline samples once a series exceeds 4096 points | `notes`: `"Dropped N worker-thread timeline sample(s) after reaching the in-memory cap of 4096."` (one per series) |
| `EventPipeStartupCollector` (`Startup/`) | `MaxRetainedAssemblyLoads`/`MaxRetainedModuleLoads`/`MaxRetainedDiEvents = 1,000` each, `MaxRetainedTimelineEvents = 2,000`; capture duration hard-capped at `MaxStartupDurationSeconds = 30` | The **first** 1,000 assembly/module/DI events observed (first-come, not ranked); first 2,000 timeline rows; at most 30s of capture | Additional loader/DI events past the per-category cap; capture duration requests above 30s are rejected outright | Truncation surfaced via the snapshot's own notes; duration cap enforced at the use-case validation layer |
| `MethodParameterCaptureCollector` (`MethodParameters/`) | `MaxEvents`/`CaptureLimit` (caller-supplied), now enforced **locally** in the observer, not just sent to the profiler | Events up to the caller's requested cap | Additional invocations once the local cap is reached, even if the profiler over-delivers or duplicates | `StopReason = "max_events_reached"` + `DroppedCount` in the summary (note: `"value-cap"` is a *different* signal, used only when an individual captured value is truncated for exceeding the per-value byte limit, not for the event-count cap) |
| `ClrMdTaskTimerAnalyzer` (`Dump/`) | `MaxTrackedTimerAddresses` (`DefaultMaxTrackedTimerAddresses = 200,000`) | Exact dedup for the first 200k distinct timer addresses seen during a heap walk | Beyond that, dedup becomes approximate (a timer leak large enough to hit this is itself the finding) — totals may slightly over-count duplicate timer containers | `TimerAddressTrackingTruncated` flag → note: *"Timer address de-duplication hit its safety cap ({cap}); totals may slightly over-count duplicate timer containers beyond that point."* (a separate, unrelated note — *"Timer callback rows are truncated to the top groups retained in the snapshot."* — covers ordinary top-N row truncation, not the address-dedup cap) |
| `RequestsNowCollector` (`ProcessDiscovery/`) | Snapshot-capture queue bounded at `SnapshotQueueCapacity = 256`, non-blocking `TryWrite` | Up to 256 concurrently-queued thread-snapshot captures per window | When the queue is full, the newly-started request is **removed from the result entirely** (not just its stack) — the final snapshot only includes requests with a captured call stack (`TopFrames.Length > 0`) | `notes`: `"Dropped N request thread snapshot capture(s) after reaching SnapshotQueueCapacity=256; request rows and counts are incomplete lower bounds..."`; the existing debug log is also preserved |
| `MvidReader` singleton cache (`CpuSampling/`) | `DefaultCapacity = 128` (FIFO eviction) | The 128 most-recently-resolved module identities | Older module→MVID mappings are evicted and re-resolved on next use (cache miss, not correctness loss) | N/A — pure cache, re-resolution is transparent |
| Perf/ETW off-CPU & CPU samplers (`OffCpu/`, `CpuSampling/`) | `perf record --max-size` (already capped pre-fix) + the parser/aggregator now streams `perf script`/ETW output instead of buffering the full text/span list before aggregating | Same final aggregate (call tree / stack rollups) as before | Nothing — this was a pure streaming refactor (same category as the Dump/Collection rows above), listed here because it shares the "buffer full output" root cause with the other perf-tooling fixes | N/A |
| Perf native-allocation sampler (`NativeAlloc/PerfNativeAllocSampler.cs`) | Same streaming parser as CPU/off-CPU, **plus** a hard `PerfScriptSampleBudget = 250,000` samples | The first 250,000 parsed perf-script samples | Parsing stops once the budget is hit; hotspots reflect only the processed prefix, not the full allocator-hot run | `aggregate.Truncated` → note: *"Stopped parsing perf script after 250,000 samples to keep allocator-hot captures bounded; hotspots reflect the processed prefix only."* — **this is a real trade-off, not a pure refactor**, since a single very allocation-heavy run can exceed the budget and silently-in-spirit (though not silently-in-notes) miss later hotspots |

## Measured impact (synthetic 300k-event load)

A throwaway benchmark (not committed — reproducible by feeding N synthetic
events directly into the internal bounded structures) measured **steady-state
resident entries** — the number that matters for OOM risk on a long capture —
before vs. after, for four representative collectors. The benchmark used a
capacity of 256 for the In-Flight-Requests, Contention, and DB structures to
keep those three comparable, and the ThreadPool queue's actual production
`MaxTimelineSamples = 4096`. The 256 figure matches
`EventPipeInFlightRequestCollector`'s typical `maxRequests` usage and
`DbEventAggregationState`'s exact production cap (`MaxTrackedCommandAggregates
= 256`), but is *not* `EventPipeContentionCollector`'s actual production cap
(`MaxTrackedEvents = 200`). The point of the benchmark is the *shape* of the
improvement (unbounded → constant), not matching every collector's exact
production cap — see the table above for each collector's real default.

| Collector | Resident entries before | Resident entries after (benchmark capacity) | Reduction |
|---|---|---|---|
| In-flight requests | 300,000 | 256 | 99.9% |
| Contention top-N (production cap is 200, not 256) | 300,000 | 256 | 99.9% |
| DB command aggregation (matches production cap) | 300,000 | 257 (256 + 1 overflow bucket) | 99.9% |
| ThreadPool timeline queue (matches production cap) | 300,000 | 4,096 | 98.6% |

**Honest caveat:** allocated-bytes-during-processing (a proxy for GC/CPU
pressure, not peak resident memory) was a mixed result — Contention and
ThreadPool improved (12%/100% less garbage), in-flight requests were roughly
neutral, and DB aggregation actually did **more** allocation per event
(-48%) because the TTL/eviction/overflow bookkeeping costs more per call than
a bare `Dictionary` insert. This is an accepted trade-off: slightly more
CPU/GC work per event in exchange for memory that no longer scales with
capture duration or event volume — which was the actual goal (no OOM, no
hang), not raw throughput.

## Conventions for adding a new bounded structure

If you're adding a new collector or extending an existing one:

1. **Cap during collection, not after.** Enforce the limit at the point of
   insertion (the EventPipe callback), never by accumulating everything and
   calling `.Take(n)` at the end — that defeats the purpose of the cap.
2. **Pick a retention strategy that keeps the most useful data**, not just
   "first N": longest-duration, oldest-still-pending, or most-frequent are
   usually more diagnostically valuable than arrival order. Match what a human
   investigating this signal would actually want to see.
3. **Always add a `notes` entry when a cap is hit**, naming the exact
   constant and the drop/eviction count — never truncate silently.
4. **Prefer an overflow/aggregate bucket over a hard drop** when the
   collector's job is aggregation (sums/counts survive; only per-key detail is
   lost). Use hard drops only for state that's inherently per-item (e.g. a
   timeline sample, a raw event snapshot).
5. **TTL-expire pending/correlation state** (started-but-not-yet-completed
   activities) in addition to capping its size — a stalled downstream (a
   request that never returns `Stop`) is exactly the scenario resource caps
   need to survive.
