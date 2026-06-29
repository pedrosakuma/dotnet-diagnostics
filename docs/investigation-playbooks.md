# Investigation playbooks

Concrete, tool-by-tool recipes for the most common diagnostics scenarios an
LLM (or a human) can drive through `dotnet-diagnostics-mcp`. Each playbook starts from
a symptom and walks through the tool calls in order.

> **Always start with these two calls:**
>
> 1. `inspect_process(view="list")` — discover the target's PID
> 2. `inspect_process(view="capabilities")` — confirm runtime flavor (CoreCLR vs
>    NativeAOT) and which tools are usable
>
> The capability matrix gates the rest of the investigation. Skipping it leads
> to "CPU sampling returned nothing" surprises on NativeAOT targets.

---

## 1. "The app feels slow / high latency"

**Hypothesis tree:** CPU bound → GC bound → I/O / downstream bound → contention.

### Step 0 — Cold-start sweep (one call, parallel)
Before stepping through vitals manually, run `collect_events(kind="sweep")`. It fans out the
five EventPipe-safe collectors (counters + gc + exceptions + threadpool + resource) **concurrently**
in ~6 s and returns a classified `triage` verdict, each sub-summary, and per-collector drill-down
handles in `data.handles`. Follow the verdict's top hint (and `data.handles[...]` for `query_snapshot`)
instead of re-collecting. Drop to the manual steps below only when the sweep verdict is ambiguous or
you need a longer window for a specific signal.

### Step 1 — Quick vitals
Call `collect_events(kind="counters")` with default providers for 5 s. If the target emits
Meter data, prefer `http.server.request.duration` p95 from `Meters[]`; otherwise fall back to
legacy EventCounters. Look at:

- `System.Runtime/cpu-usage`
- `System.Runtime/working-set`
- `System.Runtime/gen-2-gc-count` and `time-in-gc`
- `Meters[].Instrument == "http.server.request.duration"` (`Histogram.P95`), or `Microsoft.AspNetCore.Hosting/request-duration` if the Meter is absent
- `Microsoft-AspNetCore-Server-Kestrel/connection-queue-length`

### Step 2 — Quick app-level signal
`collect_events(kind="logs", minLevel="Warning")` for 10–15 s. If the warning/error stream already names the slow dependency, timeout, or retry loop, follow that lead before escalating.

### Step 3 — Branch on what's elevated

- **CPU near 100% in one or two cores** → `collect_sample(kind="cpu")` for 10–30 s,
  inspect `topHotspots` by `exclusiveSamples`. Look for unexpected user code
  near the top; hot framework methods often point to allocation pressure
  rather than algorithmic cost.
- **`time-in-gc` > 20% or rising gen-2 count** → `collect_events(kind="gc")` for
  10 s, look at `maxPauseTime` and the generation distribution. Gen-2 spikes
  with `WithHeap` dumps are the next step.
- **High request duration but low CPU** → `collect_events(kind="event_source")` with
  `providerName = "System.Net.Http"` to see outbound call timing, or
  `Microsoft.AspNetCore.Hosting` for in-pipeline latency. Often the answer is
  a downstream dependency, not the app itself.
- **Connection queue growing** → thread-pool starvation. `collect_events(kind="threadpool")`
  for 6–10 s, then inspect `query_snapshot(handle, view="timeline")` for worker/IOCP growth,
  `view="hillClimbing"` for `Starvation` / `ThreadTimedOut` transitions, and
  `view="workItemOrigins"` for the hottest enqueue origins when call stacks are available.

---

## 1a. "ThreadPool starvation / sync-over-async"

1. Run `collect_events(kind="threadpool", durationSeconds=6)` **before** the suspected blocking workload starts.
2. Drive the workload (for example `GET /threadpool-starve?blockers=50` in `BadCodeSample`) while the window is open.
3. Read the inline summary first:
   - `starvationAdjustments > 0` or `hillClimbingEvents > 0` + rising worker timeline → starvation confirmed.
   - `effectiveSettings` near `workerMinThreads` with a flat worker timeline → the pool may not be injecting quickly enough.
4. Drill down with `query_snapshot`:
   - `view="timeline"` → worker vs IOCP bucketed counts.
   - `view="hillClimbing"` → exact transition sequence (`Warmup`, `Starvation`, `ThreadTimedOut`, …).
   - `view="workItemOrigins"` → hottest enqueue origins when EventPipe call stacks are available.
5. If the pool keeps growing but the app stays slow, pair the result with `collect_sample(kind="cpu")` or `collect_thread_snapshot(view="threadpool")` to identify the blocking code.

## 1a.1. "Lock contention / monitor storm"

1. Start `collect_events(kind="contention", durationSeconds=6)` **before** driving the workload.
2. Hit `GET /lock-storm?seconds=3&blockers=8` in `BadCodeSample` while the window is open.
3. Read `summary` first:
   - `totalEvents > 0` + high `p95ContentionDuration` → lock waits are a likely latency root cause.
   - `distinctMonitors == 1` → one hot gate is serializing the path.
4. Drill with `query_snapshot(handle, view="byCallSite")` to find the hottest contended method, then `view="byOwner"` to see which owner thread is repeatedly holding the monitor.
5. If contention is severe but the call site remains framework-heavy, pair it with `collect_thread_snapshot(view="lock-graph")` while the incident is live.

- **Endpoint-specific latency with DB suspicion** → `collect_events(kind="db")`
  for 10–15 s while driving the slow request. Check `summary` / `byCommand` for
  hot SQL shapes, `n+1` for repeated command bursts under one parent activity,
  and `connectionPool` for open-connection pressure or exhaustion signals.
- **Connection queue growing** → thread-pool starvation. `collect_events(kind="event_source")`
  with `Microsoft-System-Threading` (or `Microsoft-System-Threading-Tasks-TplEventSource`)
  shows queued/completed tasks per second.

---

## 1b. "Did this deploy regress CPU or allocation hot spots?"

1. Capture a baseline window on the healthy / previous deploy: `collect_sample(kind="cpu")`
   or `collect_sample(kind="allocation")`.
2. Capture the same window on the current deploy.
3. Diff them with `query_snapshot(handle="<current>", view="diff", baselineHandle="<baseline>")`.
4. Look at `Changed[]` first:
   - `Direction="up"` + `Verdict="regression"|"mixed"` → hot path / type got worse.
   - `Direction="down"` → improvement.
   - `Notes[]` mentioning normalization → allocation windows had different durations; use the
     per-second metrics instead of raw totals.
5. If the diff points at a CPU hotspot, follow up with `query_snapshot(view="call-tree")` on the
   current handle to walk callers/callees for the regressed method.

---

## 1c. "Post-deploy cold-start is slow"

1. Start `collect_events(kind="jit", durationSeconds=10)` **before** sending the first real request after deploy / rollout.
2. During the window, hit the cold path once or a small handful of times.
3. Inspect `summary` first:
   - high `distribution.tier0` with low `tier1Percent` → the process is still mostly running first-pass codegen
   - low `r2rHitRatePercent` or high `distribution.r2rMissThenJit` → ReadyToRun coverage is poor for this startup path
   - non-zero `reJitCount` / `osrCount` → tiered recompilation is already happening during warmup
4. Drill in with `query_snapshot(handle, view="topMethods")` to see which methods paid the largest inclusive JIT cost.
5. If the same endpoints stay slow after the cold window, pivot to `collect_sample(kind="cpu")` — the problem is no longer just startup compilation.

---

## 1d. "Did my fix actually help?" — comparative + N-way trend journeys

Use this when you have **two or more captures of the same kind** and want a verdict instead of
eyeballing two payloads. It covers the current comparable projector kinds — `gc-datas`,
`counters`, `gc-events`, `contention-snapshot`, and `threadpool-snapshot` — across
**before/after** (N=2) and **N-way trend** (N≥3) journeys. The engine and verdict semantics
are identical regardless of which "door" you use; only the transport differs.

### Two doors, one engine

- **MCP, in-session handles** — capture each window, keep the handles, then
  `query_snapshot(handle="<last>", view="diff", comparisonHandles=["<t0>","<t1>", …])`.
  For the classic before/after, pass `baselineHandle="<baseline>"` instead. The current
  handle is always the **last** capture in the journey.
- **MCP, persisted JSON bodies** — `compare_to_baseline(snapshotsJson=[<json0>, <json1>, …])`.
  Pass **JSON bodies only** (never file paths — the sidecar is stateless). Dispatch is by the
  `Schema` field, so the same tool still compares two `InvestigationSummary` documents.
- **CLI** — `collect --kind datas|counters|gc|contention|threadpool --save <file>` writes a `ComparableSnapshot`,
  then `compare a.json b.json …` runs the same engine locally. Works one-shot and inside
  `session` (`--save` then `compare`).

### Before/after recipe (N=2)

1. Capture a **baseline** window on the healthy / pre-fix build:
   `collect_events(kind="datas")` (or `kind="counters"` / `kind="gc"` / `kind="contention"` /
   `kind="threadpool"`).
2. Apply the fix / config change and re-capture the **same** window on the new build.
3. Compare — MCP: `query_snapshot(view="diff", baselineHandle="<baseline>")`; CLI:
   `compare before.json after.json`.
4. Read the top-level **`Verdict`**:
   - `improvement` — the primary metric(s) moved the better-direction way.
   - `regression` — the primary metric(s) moved the wrong way.
   - `mixed` — some primaries improved, others regressed; read the per-metric `Direction`.
   - `no_change` — primaries within `minDeltaPct`.
   - `no_overlap` — the two captures share no comparable metric/key (e.g. different kinds of
     work); re-capture comparable windows.
   - `incomparable` — fewer than two captures or mixed kinds.
5. `Pairwise.Headline` is `first→last`; `MetricSeries[].DeltaPct` and `KeyMatrix[].DeltaPct`
   carry the per-metric / per-row deltas. `Notes[]` flags caveats (cross-process, unit
   mismatch, top-N truncation).

### N-way trend recipe (N≥3)

Capture `t0..tn` of the **same kind** (especially `gc-datas`, `counters`, `contention`, or
`threadpool`) while a workload ramps or a tuning loop runs, then compare all of them in order.
The headline verdict is still
`first→last`, but the real signal is each metric's **`Trend`**:

- `MonotonicUp` / `MonotonicDown` — moving steadily one way (still trending).
- `Converged` — moved early then settled; the system **adapted and stabilised** (e.g. DATAS
  heap-count settling after load change).
- `Oscillating` — flipping back and forth without settling; the configuration is **hunting**.
- `Flat` — no meaningful movement across the series.

`Pairwise.BaselineEach[]` (t0→ti) and `Pairwise.Adjacent[]` (ti→ti+1) let you locate **when**
the change happened. This is how you tell "settled" from "still adapting" — a `Converged`
series with a quiet tail is healthy; a `MonotonicUp` allocation-rate series at the tail is not.

### Replica consistency recipe (dispersion mode)

Use this when the question is **"are my N replicas consistent right now?"** rather than
"did one process change over time?" Capture the same kind/window from each pod or replica,
then compare them as an unordered fleet:

- MCP handles: `query_snapshot(handle="<pod-c>", view="diff", comparisonHandles=["<pod-a>","<pod-b>"], mode="dispersion")`.
- Persisted JSON: `compare_to_baseline(snapshotsJson=[<pod-a-json>, <pod-b-json>, <pod-c-json>], mode="dispersion")`.
- CLI: `compare pod-a.json pod-b.json pod-c.json --mode dispersion`.

Read the dispersion verdicts as:

- `uniform` — shared metrics/key rows are close enough across captures.
- `dispersed` — at least one metric/key row has a high coefficient of variation; inspect the
  `Dispersion` stats on metric series or compact top key rows for the outlier.
- `no_overlap` — captures do not share comparable metrics/key rows.
- `incomparable` — fewer than two captures or mixed kinds.

For compact summaries, metric series are ranked by their dispersion coefficient of variation.
Key-set row ranking computes coefficient of variation from the row values at presentation time
because `KeyMatrixRow` does not yet persist per-row dispersion stats. Richer row-level dispersion
stats are a future enhancement.

`mode="dispersion"` is available only on N-way comparable journeys; legacy pairwise
`baselineHandle` sample diffs (`cpu-sample`, `heap-snapshot`, `allocation-sample`,
`native-alloc-sample`) are rejected because their pairwise diff shape cannot represent dispersion.

### Token guidance (compact verdict in context, full matrix on demand)

Large journeys must not flood the LLM context:

- Keep `depth="compact"` (the helpful default for triage): you get verdict + headline + counts
  + `Notes[]` + the **top-N** metric/key deltas inline. Raise `topN` to widen the inline slice.
- `depth="full"` inlines the entire `SnapshotJourneyDiff` **only** while it stays under the
  32 KiB inline threshold. Past that, the server retains the full matrix in memory and the
  inline payload carries a `journey://diff/{handle}` **Resource** link — pull it only when you
  need the whole matrix.
- The CLI mirror of that lever is `--save <file>` (writes the full matrix to disk) while the
  terminal keeps the compact verdict + headline.

See [tool-reference.md](./tool-reference.md) (`query_snapshot(view="diff")` /
`compare_to_baseline`) and [cli-reference.md](./cli-reference.md) (`collect --save`, `compare`)
for the full parameter and output contracts.

### Streaming summaries to OpenTelemetry / Application Insights (opt-in)

When the operator sets `MCP_INVESTIGATION_OTEL=1` (or
`Observability:InvestigationTelemetry:Enabled=true`), every
`export_investigation_summary` call additionally emits an `investigation.summary`
OpenTelemetry span on the `DotnetDiagnostics.Mcp.Investigations` activity source —
so a diagnostic run leaves a durable, queryable trail without changing the portable
JSON the LLM owns (the server stays stateless: emit-and-forget). The span carries the
investigation id, pid, build/container provenance, total samples, duration, and the
top-N hotspots (`investigation.hotspot.{i}.method|module|exclusive_percent|inclusive_percent`,
capped by `Observability:InvestigationTelemetry:MaxHotspotAttributes`, default 5).

- **Off by default**; zero behavior change when unset (no span is produced).
- Rides the **existing** OTLP tracing exporter — set `OTEL_EXPORTER_OTLP_ENDPOINT`
  to ship spans to any OTLP backend (Grafana/Tempo, Honeycomb, …).
- **Azure Application Insights**: no bespoke SDK needed — point
  `OTEL_EXPORTER_OTLP_ENDPOINT` at the Application Insights OTLP ingestion endpoint
  (or run the OpenTelemetry Collector with the Azure Monitor exporter). Query the
  spans under `dependencies` / `traces` by `investigation.id`.

This makes "show me every investigation against pod X over the last week" answerable
in your telemetry backend, while `compare_to_baseline` remains the in-context diff path.

---

## 2. "Memory keeps growing"

### Step 1
`collect_events(kind="counters")` for 15 s. Compare:

- `System.Runtime/working-set`
- `System.Runtime/gc-heap-size`
- `System.Runtime/gen-2-size`
- `System.Runtime/loh-size`
- `System.Runtime/poh-size`

A steadily-growing `gen-2-size` with a flat `working-set` is leak-shaped; both
growing is more like fragmentation or unmanaged growth.

### Step 2
If `working-set` / RSS is growing **without** corresponding `gc-heap-size` growth,
branch to `inspect_process(view="resources")` before taking a dump. This catches the
classic unmanaged-FD leak shape:

- rising `fdCount` + `noFileUsageFraction` → file/socket leak approaching `ulimit -n`
- rising `sockets.closeWait` → likely undisposed `HttpResponseMessage` / pooled HTTP misuse
- huge `sockets.timeWait` with flat `fdCount` → connection churn / pooling misconfiguration

If `resources` looks clean, continue with GC-focused investigation.

> **Escalating to native-allocation attribution (Linux).** When RSS / anonymous
> pages climb while the managed `gc-heap-size` stays flat, you have *native*
> (unmanaged) growth — `inspect_process(view="resources")` and `view="memory_trend"`
> *detect* it but don't say **where** it comes from. On a Linux host/sidecar whose
> capability matrix reports `CanSampleNativeAlloc: true`, escalate to
> `collect_sample(kind="native-alloc")`: it uprobes the libc allocator and attributes
> native `malloc`/`calloc`/`realloc` calls to a code path, drilled into with
> `query_snapshot(view="call-tree")`. It is **hotspot-only** (sampled allocator-call
> hits, not bytes, and not alloc/free retention) so it shows who allocates most, not
> what leaks — but the hottest native call site is usually where to look first. Needs
> `CAP_SYS_ADMIN` (uprobe creation); a `PermissionDenied` envelope with the perf stderr
> comes back when the sidecar lacks it.

### Step 3
`collect_events(kind="gc")` for 15–30 s. If gen-2 collections happen but `gen-2-size`
doesn't drop, you have surviving objects (leak or long-lived cache).

### Step 3a — Live heap growth diff (retention-aware leak hunt)
Once Step 3 confirms surviving objects, pin down **which types** are growing and **what's
holding them** without two offline dumps + manual SOS `!objsize` / `!gcroot`. Take two live
heap snapshots N seconds apart (30–120 s under steady load), then diff them:

```text
inspect_heap(source="live", processId=12345, includeRetentionPaths=true)   # → baseline handle H1
# …wait while the leak accrues…
inspect_heap(source="live", processId=12345, includeRetentionPaths=true)   # → current handle H2
query_snapshot(handle=H2, view="growth", baselineHandle=H1)                 # ranked growth + retention
```

`view="growth"` ranks the types whose retained `bytes` (or `instances`, via `rankBy`) grew
between the two captures, attaching the retention chains from the *later* snapshot to the top
growers — so the verdict `leak_suspected` comes with the static cache / event-handler / collection
that is keeping each growing type alive. It ranks by **absolute** growth (not percentage), so a
large-but-modest-% leak does not get buried under noisy small types. Pass `includeRetentionPaths=true`
on both captures to populate the "what's holding them" drill-down (the view notes it and suggests a
re-capture otherwise). This is the fastest path from "memory keeps growing" to a named leaking type
plus its root.

### Step 4
If `working-set` keeps climbing but the managed heap still looks deceptively small,
run `query_snapshot(handle, view="gchandles")` on a recent `inspect_heap` handle.
A growing `Pinned` / `Normal` bucket is the classic forgotten-`GCHandle.Alloc(...)`
shape; `Dependent` often points at `ConditionalWeakTable`-style leaks.

### Step 5
For plugin hosts, scripting workloads, or "metadata / Loader heap keeps growing"
reports, run `query_snapshot(handle, view="alc")` on the same `inspect_heap` handle.
Collectible ALC rows with `suspectedLeak=true` are still rooted; inspect the
`retentionPath` to find the static cache/event-handler/object graph keeping a type from
that ALC alive. The view is CoreCLR-only (NativeAOT has no ClrMD heap walk) and computes
retention hints for at most 16 collectible ALCs per snapshot with the bounded
64-frame / 250,000-object root search to avoid an O(contexts × heap) walk.

### Step 6
`collect_process_dump` with `dumpType = "WithHeap"`. **Defense in depth
([per-call confirmation](./authorization.md#per-call-confirmation)):** call it once
first *without* `confirm` to preview the dump that would be
written (returns a `{ kind: "confirmation_required", targetPid, dumpType,
outputDirectory }` envelope and writes nothing); then re-issue with `confirm=true`
once a human has approved. The `dump-write` + `ptrace` scopes are still required on
top of `confirm=true`. Analyze offline:

```bash
dotnet dump analyze /tmp/dotnet-diagnostics-mcp/dump_pid12345_WithHeap_*.dmp
> dumpheap -stat
> gcroot <addr>
```

For a sidecar deployment, copy the dump out of the container first
(`kubectl cp`), since dotnet-dump usually runs alongside symbol/source paths
that aren't in the sidecar image.

---

## 2b. "Is this Server GC / did someone override ThreadPool or tiered compilation?"

1. Call `inspect_process(view="runtime-config")` against the target PID.
2. Read `gc` first:
   - `isServerGc=true` + `heapCount > 1` → Server GC is active.
   - `isConcurrent=false` / `isBackground=false` → expect longer stop-the-world pauses than the default CoreCLR workstation profile.
3. Read `threadPool` next:
   - unexpectedly low `minWorkerThreads` or `hillClimbingEnabled=false` → keep starvation in mind before blaming downstream I/O.
4. Check `tieredCompilation` / `envVars`:
   - `DOTNET_TieredCompilation=0` / `DOTNET_TieredPGO=0` overrides explain surprising cold-start or steady-state perf behavior.
   - `notes[]` explicitly says when a field is unavailable (for example ptrace-gated ClrMD attach on Linux).

---

## 3. "We're seeing 5xxs in production"

### Step 1
`collect_events(kind="exceptions")` for 30 s. Inspect `byType` to find the dominant exception
type, then look at `recent` to read messages and HRESULTs.

### Step 2
For first-chance vs unhandled differentiation, also call `collect_events(kind="event_source")`
with `providerName = "Microsoft-Extensions-Logging"`. This catches structured
log entries the app considers "handled" so you can correlate.

### Step 3 (optional)
If the exception only repros under specific code paths, capture a Mini dump at
the moment of an alert with `collect_process_dump dumpType=Mini` to inspect
thread stacks and locals.

---

## 3b. "The process crashes with an unhandled exception"

### Step 1
Start `collect_events(kind="crash-guard", durationSeconds=30)` **before**
triggering the suspect path. EventPipe sessions are not retroactive; events
thrown before the session opens are missed.

### Step 2
If the guard returns `unhandledExceptionObserved=true`, inspect
`query_snapshot(handle, view="stack")` for the final exception type, message,
and managed stack. Use `query_snapshot(handle, view="exceptions")` when the
process threw multiple exceptions before the crash.

### Step 3
Correlate with dumps. If the environment has `DOTNET_DbgEnableMiniDump=1`, match
the runtime-written crash dump by PID/timestamp with `finalException.timestamp`.
If no runtime dump exists and the process is still alive, follow the
`collect_process_dump(dumpType="Mini")` hint emitted by the guard; the dump tool
will still require the normal explicit confirmation before writing the file.

### Step 4
For destructive fixtures such as stack overflow or OOM, prefer reproducing in an
isolated sample/replica. The `BadCodeSample` `/crash?mode=unhandled` fixture is
used in CI for the reliable unhandled-exception path; `stackoverflow` and `oom`
are available for manual validation but are intentionally not asserted in the
live test because they terminate the sample process more abruptly.

---

## 4. "Slow outbound HTTP calls"

### Step 1
`collect_events(kind="event_source")` with `providerName = "System.Net.Http"`,
`durationSeconds = 30`. Look at `events` for `RequestStart` / `RequestStop`
pairs — most clients emit timing on the stop event payload.

### Step 2
Cross-reference with `Microsoft-AspNetCore-Server-Kestrel` for inbound
connection lifecycle to confirm the latency is downstream-induced, not
client-induced.

---

## 4b. "One endpoint is hanging right now"

1. Call `inspect_process(view="requests-now")` while the incident is happening. It opens a short (~2 s) request window and returns only in-flight ASP.NET Core requests, with the current thread id and top stack frames.
2. Sort mentally by `startedAtMs` — the oldest request is your best candidate.
3. Look at `topFrames[]`:
   - app code near the top → you already have the first suspect method
   - `Task.Delay`, timers, waits, or `Monitor.Enter` → likely async hang / lock contention
   - framework I/O (`Socket`, `SslStream`, `HttpClient`) → pivot to `collect_events(kind="event_source", providerName="System.Net.Http")`
4. If the single-thread view is not enough, escalate to `collect_thread_snapshot` for the full thread + lock graph while the same request is still hanging.
5. Reproduce locally with `samples/BadCodeSample`'s `/slow-hang?seconds=N` fixture.

## 4c. "This looks like a parked async continuation"

1. Capture `collect_thread_snapshot` while the hang is happening.
2. Run `query_snapshot(handle, view="async-stalls")`.
3. Read `byBucket[]` first:
   - `SyncOverAsync` → somebody blocked on `Task.Result`, `Task.Wait`, or `GetResult()`.
   - `ChannelAwait` → a `ChannelReader` is waiting for a producer.
   - `TcsPending` → a `TaskCompletionSource`-backed handoff never completed.
   - `SemaphoreAwait` → an async semaphore waiter needs a matching `Release()`.
   - `Delay` → timer/backoff noise; often low priority.
   - `Unknown` → async-looking stack, but inspect `query_snapshot(handle, view="stack", threadId=...)` before concluding.
4. Reproduce locally with `samples/BadCodeSample`'s `/async-stall?bucket=tcs|channel|sync-over-async|semaphore&seconds=N` fixture.

## 4d. "Slow query / N+1 suspected"

1. Start `collect_events(kind="db", durationSeconds=10-15)` **before** driving the
   slow endpoint.
2. Hit the endpoint while the collection window is open.
3. Read `summary` first:
   - `topCommands[0].p95Ms` high → slow query shape
   - `nPlusOneCount > 0` → repeated SQL under one parent activity / trace
   - `connectionPool.poolExhaustedCount > 0` → pool starvation or leaked
     connections
4. Drill with `query_snapshot(handle, view="byCommand")` to inspect the worst SQL
   shapes, then `query_snapshot(handle, view="n+1")` to confirm the repeated
   pattern and how many times it fired.
5. If the DB snapshot is quiet, fall back to `collect_events(kind="event_source",
   providerName="System.Net.Http")` or `collect_sample(kind="cpu")` — the latency
   may be downstream or CPU-bound rather than database-bound.

## 4e. "The spike is intermittent — I can't catch it manually"

When the symptom (CPU spike, heap balloon, thread explosion) only appears for a few seconds at
unpredictable times, a one-shot `collect_sample` / `inspect_heap` almost always misses it. Arm a
**bounded threshold-gated capture** so the heavy artifact is taken the instant the metric trips:

1. Pick the metric + threshold from the symptom: `cpu>85`, `gcHeapMb>=1500`, `rssMb>2000`,
   `threadCount>400`, or `activeTimerCount>1000`.
2. Arm `collect_events(kind="counters", triggerWhen="cpu>85", captureKind="cpu-sample", windowSeconds=120)`.
   The call polls the counter and returns the moment it captures (or when the window elapses). Use
   `captureKind="heap"` for memory spikes, `"thread-snapshot"` for thread/lock explosions, `"dump"`
   (with `confirmDump=true`) for a full post-mortem.
3. On a trip, follow the high-priority `query_snapshot(handle, …)` hint to drill into the captured
   cpu-sample / heap / thread-snapshot without re-collecting.
4. If `tripped=false`, re-arm with a longer `windowSeconds`, a lower threshold, or while you drive
   the workload. Raise `maxCaptures` (≤10) to catch several breaches in one window.
5. From the CLI / CI: `dotnet-diagnostics-cli collect --kind counters --pid <id> --capture-when 'cpu>85' --capture cpu-sample --window 120`.

---

## 4f. "One distributed trace is slow — which replica/hop is to blame?"

When a request fans out across several services (or several replicas of one service) and the
**end-to-end** latency is high, the question is *which hop* is slow, not *which process*. Use
distributed trace correlation to follow one W3C trace across every attached Pod:

1. Grab the `trace-id` from the slow request — the 32-hex `trace-id` field of its `traceparent`
   header (`00-<trace-id>-<span-id>-01`), or from your APM / the response's `traceparent`.
2. In orchestrator mode, `attach_to_pod` to **each** replica that could serve the trace (you need an
   Active investigation handle per Pod). Confirm with `list_orchestrator(kind="investigations")`.
3. While the trace is still **in-flight** (correlation captures live spans, not historical replays),
   run `collect_events(kind="distributed_trace", traceId="<trace-id>", durationSeconds=15)`. It fans
   out a bounded `collect_events(kind="activities")` to every attached Pod and stitches the spans.
4. Read `DistributedTrace.SlowestHop` — it is the span with the largest **self-time** (own duration
   minus time attributed to its direct children), so a parent that merely *waits* on a slow
   downstream child is not mis-blamed. `Spans[]` gives the full causal order (parent before child,
   `ParentResolved` shows cross-Pod links that resolved), and `Coverage`/`Warnings` tell you which
   Pods matched and whether any were unreachable or showed clock skew.
5. Drill into the culprit on the flagged `PodName`: bind/attach to that replica and run
   `collect_sample(kind="cpu", durationSeconds=10)` (the auto-hint suggests exactly this) to see what
   its CPU is doing during the slow hop.

If `SpanCount=0` but Pods were reached, the trace had already completed — re-run while it is live, or
widen `durationSeconds`. Requires the `eventpipe` + `orchestrator-attach` scopes.

---

## 4g. "All my replicas should look the same — which one is the outlier right now?"

When a service runs N replicas behind a load balancer, an unhealthy replica (heap leak, hot CPU,
saturated thread pool) is often masked by the healthy ones. The question is *which replica is the
outlier*, sampled at the **same moment** — not "did one process change over time" (that's
`compare_to_baseline` on serial snapshots). Use the simultaneous counter fan-out:

1. In orchestrator mode, `attach_to_pod` to **each** replica. Confirm with
   `list_orchestrator(kind="investigations")` — you need an Active handle per Pod.
2. Run `collect_events(kind="replica_counters", durationSeconds=5)`. It fans out a bounded
   `collect_events(kind="counters")` to every attached Pod **in parallel** so the windows overlap,
   then compares `cpu` / `gc-heap-size` / `threadpool-queue` across replicas.
3. Read `ReplicaCounters.OutlierPod` (the most-deviant replica by summed z-score) and `Metrics[]`
   for the per-metric spread (which counter skews, min/max Pod). `Replicas[]` is the raw per-Pod
   readings; `data.podErrors` lists any replica that could not be collected.
4. Drill into the flagged replica: bind/attach to `OutlierPod` and run
   `collect_sample(kind="cpu")` or `inspect_heap` (the auto-hint suggests exactly this).

If no clear outlier stands out, the replicas are balanced — look upstream. Requires the
`read-counters` + `orchestrator-attach` scopes.

---

## 5. "Is this a NativeAOT app?"

`inspect_process(view="capabilities")` returns `runtime: "NativeAot"`. On NativeAOT:

- ✅ counters, exceptions, GC events, custom EventSources, dumps all work
- ❌ `collect_sample(kind="cpu")` returns no hotspots on EventPipe (SampleProfiler is CoreCLR-only); the NativeAOT Linux fallback routes through `perf record`
- ⚠️ EventSource support is opt-in via `<EventSourceSupport>true</EventSourceSupport>`
  on the target; if disabled at publish time even counters won't flow

For CPU sampling on NativeAOT, fall back to `perf record -p <pid>` on the
host. We may add a `perf`-based collector in the future (see plan, Phase 7).

---

## 6. "I'm investigating in production — what's the safest thing I can do?"

Order of escalation from cheapest to most disruptive:

1. `inspect_process(view="info")`, `inspect_process(view="capabilities")` — passive metadata
2. `collect_events(kind="counters")` — small EventPipe session, low overhead
3. `collect_events(kind="event_source")` / `collect_events(kind="exceptions")` / `collect_events(kind="gc")` — EventPipe sessions sized by `durationSeconds`
4. `collect_sample(kind="cpu")` — same family as 3 but specifically uses the SampleProfiler at ~1 kHz
5. `collect_process_dump dumpType=Mini` — pauses the process briefly while the kernel reads memory
6. `collect_process_dump dumpType=WithHeap` or `Full` — can pause the process for seconds and writes hundreds of MB to disk

In hot path scenarios, prefer windowed EventPipe tools (1–5) over dumps. Dumps
should be reserved for post-mortem or "we've already isolated this to one
instance" investigations.

---

## Adapting playbooks for the LLM

When wiring `dotnet-diagnostics-mcp` into an LLM-driven agent, encode this priority as
a system message:

> Always call `inspect_process(view="capabilities")` before any window-bound tool.
> Prefer `collect_events(kind="counters")` as the first observation; only escalate to CPU
> sampling, GC events, or dumps when the counters point in that direction.
> Never call `collect_process_dump` with `dumpType=Full` without explicit
> human approval.
