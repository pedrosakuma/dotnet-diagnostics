# Tool reference

Every tool exposed by `dotnet-diagnostics-mcp` is listed here with its purpose, parameters,
return shape, runtime requirements, and a sample invocation. All tools are
delivered over Streamable HTTP at `POST /mcp` and require an
`Authorization: Bearer <token>` header (see [client-setup.md](./client-setup.md)).

> Return shapes link back to the C# record definitions in
> [`src/DotnetDiagnostics.Core`](../src/DotnetDiagnostics.Core), which are the source of
> truth for field names and types.

### Common response envelope

Every structured tool response is a `DiagnosticResult<T>` envelope with:

- `summary`: short human-readable outcome.
- `hints`: ordered `NextActionHint[]`; each hint carries `nextTool`, `reason`,
  optional `suggestedArguments`, and `priority` (`high`, `normal`, or `low`;
  default `normal`).
- `data`: the tool-specific payload on success.
- `signals`: optional ranked `SignalGroup[]` — engine-derived, **diagnosis-agnostic**
  groupings of the collected data (each with a `signal` grouping-id, a `summary`,
  a `salience` in `[0,1]`, and `buckets[]` referencing a handle, plus an optional
  `nextAction`). Leads the response so the consumer sees *where a signal
  concentrates* without re-deriving it from `data`. Omitted from the wire when
  nothing is salient (no noise). See [Signal-grouping layer](#signal-grouping-layer).
- `error`: `DiagnosticError` on classified failures.
- `handle` / `handleExpiresAt` / `handleExpiresInSeconds`: present when the
  tool minted a drilldown handle. `handleExpiresInSeconds` is computed when the
  response is serialized and is floored at `0` after expiry.

### Signal-grouping layer

Some collectors reduce the raw data they just captured into a compact **"vector"**
of salient signal groupings — think edge / IoT: a huge volume of raw signal is
captured, but only the dimensions that *stand out* are forwarded in the envelope's
`signals[]`, so the consumer does not have to re-derive them from the raw payload.
Each `SignalGroup` carries:

- `signal`: stable id of the **grouping dimension** — not a diagnosis (e.g.
  `cpu.self-time.concentration`, `cpu.self-time.by-namespace`, `exceptions.by-type`,
  `exceptions.by-throw-site`, `allocations.by-type`, `allocations.by-site`,
  `gc.pause-time-share`, `gc.gen2-share`, `gc.loh-growth`).
- `summary`: one-line description of what stands out.
- `salience`: `0`–`1`, how far the grouping stands out (magnitude / concentration).
- `buckets[]`: the top members of the grouping, each `{ key, magnitude, unit,
  handle }`, referencing a drilldown `handle` (not inlined blobs).
- `nextAction`: an optional neutral `NextActionHint` to drill in.

Signals **group and correlate; they do not diagnose**. They surface *where* a
signal concentrates / *how* signals co-move (e.g. "89% of self-time in
`System.Globalization`"), never *what* the bug is or how to fix it — the consumer
draws the conclusion and can always drill and disagree. This is transparent
grouping, never a trained model: the ground-truth label only ever comes from the
consumer that already saw the signal, so any accumulated dataset would be
contaminated (consumer-side leakage). Ranking is by `salience` descending, capped
so the payload stays small.

**Resource.** `collect_sample(kind="cpu")` signals are also exposed as a
read-only MCP Resource `signals://cpu-sample/{handle}`, so a client can re-pull the
current signals for a handle without re-running the sampler. The providers run over
the full merged call tree stored under the handle, so the namespace roll-up is
faithful and nothing is lost to the inline top-N cap.

**Exceptions.** `collect_events(kind="exceptions")` and `collect_events(kind="crash-guard")`
surface exception groupings inline: `exceptions.by-type` (does one exception type
dominate the stream vs. spread thin — off the exact per-type counts, both collectors)
and `exceptions.by-throw-site` (roll-up by `type × innermost frame`). The throw-site
roll-up needs resolved managed stacks, which only the crash-guard collector captures,
and even there it is best-effort (live EventPipe stack resolution can be empty), so its
shares are relative to the stack-resolved events and it simply produces nothing when no
stacks were resolved. The standard exception stream carries no stack, so it only ever
emits `exceptions.by-type`.

**Allocations & GC.** `collect_sample(kind="allocation")` surfaces byte-weighted
concentration groupings: `allocations.by-type` (does one type dominate the allocated
bytes) and `allocations.by-site` (does one call-site — the leaf allocating frame —
dominate them). `allocations.by-type` skips the NativeAOT `<unknown>` placeholder (an
attribution gap, not a real type concentrating). `allocations.by-site` simply produces
nothing when no allocation stacks resolved. `collect_events(kind="gc")` surfaces three
neutral trend/magnitude signals over the full (untrimmed) window: `gc.pause-time-share`
(fraction of the window spent paused), `gc.gen2-share` (fraction of collections that
were gen2, elevated vs. the gen0-dominated norm) and `gc.loh-growth` (LOH size growth
across the window, from the `GCHeapStats` time series) — each a magnitude the consumer
interprets, never a verdict.

### Implicit bootstrap (`processId` is optional)

Since issue #42 every tool that targets a live .NET process accepts `processId`
as optional. When the caller omits it the server lists the visible .NET
processes via the diagnostic IPC and:

- **0 candidates** → structured error `NoDotnetProcessFound`.
- **1 candidate** → auto-selects it, marks the response's
  `resolvedProcess.autoResolved = true`.
- **N candidates** → structured error `AmbiguousDotnetProcess` with the
  candidate list inline; re-issue the call with `processId` set explicitly.

Every successful response now carries a `resolvedProcess` digest on the
envelope alongside `data` / `summary` / `hints`:

```json
{
  "resolvedProcess": {
    "processId": 1234,
    "runtime": "CoreClr",
    "runtimeVersion": "10.0.0",
    "canSampleCpu": true,
    "canCollectGcDump": true,
    "autoResolved": true
  }
}
```

This means the previously-obligatory opener of
[`inspect_process(view="list")`](#inspect_process) → `inspect_process(view="capabilities")` → `<tool>` collapses to
a single `<tool>` call when there is only one .NET process visible to the
sidecar. The capability digest is cached per pid for 60 seconds so back-to-back
tool calls within an investigation pay the probe cost once.

### Verbosity (`depth`)

Issue [#41 slice 2c](https://github.com/pedrosakuma/dotnet-diagnostics/issues/41)
adds a uniform `depth` parameter to every windowed collector. Values:
`Summary` (default), `Detail`, `Raw`. Contract:

- `Summary` returns a small, decision-grade payload inline (the smallest piece
  of evidence the LLM needs to choose the next tool). This is the default.
- `Detail` returns the historical pre-#41 payload (top-N hotspots, full
  `Events[]` lists, full `Notes`, etc.).
- `Raw` is reserved for parity with the artifact handle; today equivalent to
  `Detail` for every tool.

**Key invariant — the handle store always carries the FULL artifact**, regardless
of `depth`. The depth knob only filters the *inline* response. Drilldown is now
unified behind a single verb — **[`query_snapshot(handle, view, …)`](#query_snapshot)**
— which dispatches on the handle's recorded artifact kind
and re-projects everything the original collection captured.

Per-tool `Summary` semantics:

| Tool | What `Summary` drops inline |
| --- | --- |
| `collect_events(kind="counters")` | All non-headline counters (keeps ~14: cpu-usage, working-set, gc-heap-size, gen-2-gc-count, time-in-gc, alloc-rate, threadpool-thread-count, threadpool-queue-length, exception-count, monitor-lock-contention-count + ASP.NET Core requests/failed/current + Kestrel connections-per-sec). **Auto-hints** trigger on: `cpu > 70%` (CPU hotspot), `threadpool-queue-length > 50` (starvation), `time-in-gc > 15%` (GC pressure), `alloc-rate > 50 MB/s` + Gen2 (allocation), `contention > 10` (lock storms), low CPU + queue buildup (I/O bound). |
| `inspect_process(view="container")` | The `Notes[]` (caveats about cgroup v1 / missing PSI). Cgroup values themselves remain. |
| `collect_sample(kind="cpu")` | `TopHotspots` truncated to the top 3 (handle keeps `topN`, default 25). |
| `collect_sample(kind="off_cpu")` | `TopBlockingStacks` truncated to the top 3 (handle keeps `topN`). |
| `collect_events(kind="exceptions")` | The `Recent[]` list. `Total` and `ByType` remain exact (counts at every depth). |
| `collect_events(kind="crash-guard")` | The retained `Exceptions[]` list. Final exception, exit status, by-type counts, and notes remain inline. |
| `collect_events(kind="gc")` | The `Events[]` list. Totals, max pause, per-gen counts remain exact. |
| `collect_events(kind="datas")` | The full `Samples[]`, `TuningEvents[]` and `FullGcTuningEvents[]` lists. Drill in with `query_snapshot(handle, view=overview\|tuning\|samples\|gen2)`. |
| `collect_events(kind="catalog")` | The metadata-only `Sample[]` occurrence list. The ranked `Catalog[]` remains inline; payload values are never captured. |
| `collect_events(kind="event_source")` | The `Events[]` list. Provider + total count remain. Drill in with `query_snapshot(handle, view=byEventName)`. |
| `collect_events(kind="logs")` | The `Recent[]` list. Level counts + per-category rollups remain exact for the window. |
| `collect_events(kind="jit")` | Method rows beyond the hottest 10. Healthcheck + tier counts remain exact for the window. |
| `collect_events(kind="threadpool")` | The full worker/IOCP timelines and hill-climbing sequence. Summary keeps headline counts + top origins; drill in with `query_snapshot(handle, view=timeline|hillClimbing|workItemOrigins)`. |
| `collect_events(kind="contention")` | The raw contention event list. Summary keeps headline wait totals + percentiles; drill in with `query_snapshot(handle, view=byCallSite|byOwner)`. |
| `collect_events(kind="db")` | The long `ByCommand[]` / `NPlusOne[]` lists. Summary keeps the headline aggregates + pool slice. |
| `collect_events(kind="kestrel")` | The `byOperation[]` list, queue-length timeline, and `configurationJson`. Summary keeps the headline connection/request/TLS aggregates + latency tail. |
| `collect_events(kind="networking")` | The full `ByOperation[]` list. Summary keeps headline HTTP/DNS/TLS/socket counts + latency tails; drill in with `query_snapshot(handle, view=byOperation|queue|tls|dns)`. |
| `collect_events(kind="requests")` | The full in-flight request list. Summary keeps the headline counts + the oldest requests inline; drill in with `query_snapshot(handle, view=requests|longRunning)`. |
| `collect_events(kind="startup")` | The loader/DI event lists and full timeline. Summary keeps headline counts, top assembly/module aggregates, and notes. |
| `collect_events(kind="sweep")` | The five sub-snapshots' bulky lists (counters, gc, exceptions, threadpool, resource). Summary keeps the triage verdict + per-collector handles. Each sub-collector's full payload stays behind its handle (`data.handles`). |
| `collect_thread_snapshot` | The lock graph + threads beyond the top 3 most-blocked. Drill in with `query_snapshot(view=lock-graph\|deadlocks\|unique-stacks\|async-stalls\|wait-chains)`. |

Explicit `topN` always wins over the depth default — if you pass
`topN=10, depth=Summary` you get up to 10 hotspots inline (the LLM knows what
it asked for).

`collect_events(kind="activities")` does **not** currently expose `depth`; it always returns the
retained `Activities[]` inline (bounded by `maxActivities`) and relies on
`query_snapshot(handle, view=...)` for narrower drilldown views.

### Parallel initial triage (`collect_events(kind="sweep")`)

`collect_events(kind="sweep")` is the recommended **first** call when triaging an unfamiliar
process. Instead of issuing five sequential collections (~25–40 s), it fans out the five
EventPipe-safe collectors — `counters`, `gc`, `exceptions`, `threadpool` and `resource` — **concurrently**
in a single round-trip and returns one consolidated envelope:

- `data.triage` — the classified verdict + severity + evidence (same shape as the legacy triage).
- `data.counters` / `data.gc` / `data.exceptions` / `data.threadpool` / `data.resource` — each sub-snapshot's summary inline.
- `data.handles` — per-collector drill-down handles (`counters`, `gc`, `exceptions`, `threadpool`); pass these to `query_snapshot` to follow up without re-collecting.
- `data.failures` — per-collector failure notes; empty when every collector succeeded (one slow/failed collector never blocks the rest).

`durationSeconds` defaults to 6 and is floored at 6 s so each EventPipe session has time to start and
emit at least one interval. The top-level `Hints[]` point at the next best drill-down for the verdict.

### Distributed trace correlation (`collect_events(kind="distributed_trace")`)

When you are attached to several replicas of the same service (orchestrator mode — one
`attach_to_pod` per Pod), `collect_events(kind="distributed_trace")` follows **one** W3C trace
across all of them and stitches the per-Pod spans into a single timeline. It is the distributed
counterpart of `kind="activities"`: instead of capturing activities on one process it **fans out** a
bounded `collect_events(kind="activities")` to every attached Pod, matches the spans whose
`trace-id` equals the supplied `traceId`, and joins parent→child spans **by span link, never by
wall-clock** (so clock skew between nodes cannot scramble the order).

| Parameter | Meaning |
| --- | --- |
| `traceId` | **Required.** The 32-hex W3C trace-id to correlate (the `trace-id` field of the slow request's `traceparent` header). |
| `durationSeconds` | Capture window applied to each Pod's fan-out collection (default 10). Correlation targets **in-flight** traces — run it while the trace is live. |
| `maxActivities` | Per-Pod cap on retained activities (the same bound as `kind="activities"`). |
| `sources` | Optional ActivitySource name filter forwarded to each Pod. |

Requirements: orchestrator mode (`Orchestrator:Enabled=true`), the `eventpipe` **and**
`orchestrator-attach` scopes, and at least one **Active** investigation handle (i.e. you must
`attach_to_pod` to the replicas first). The call always runs **locally on the orchestrator** even
when your session is bound to a single Pod — it never proxies the whole fan-out into one replica.

The result envelope carries a `DistributedTrace` timeline: the stitched `Spans[]` (each tagged with
its `PodName`, `Depth`, `ParentResolved`, and **self-time** = own duration minus the time attributed
to its direct children), the flagged `SlowestHop` (the span with the largest self-time — the hop
that is actually slow, not merely *waiting* on a slow downstream child), per-Pod `Coverage`, and
`Warnings` for orphan parents, zero-match Pods, or clock skew. Per-Pod failures are isolated: one
unreachable replica is reported in `data.podErrors` (and the summary) and does not sink the rest of
the correlation; if **every** attached Pod fails to collect, the call returns a
`DistributedTraceFanoutFailed` error carrying those per-Pod messages.

```text
# after attach_to_pod against each replica:
collect_events(kind="distributed_trace")(traceId="0af7651916cd43dd8448eb211c80319c", durationSeconds=15)
```

### Replica counter skew (`collect_events(kind="replica_counters")`)

When you are attached to several replicas of the same service (orchestrator mode — one
`attach_to_pod` per Pod), `collect_events(kind="replica_counters")` captures the headline
EventCounters from **every** attached Pod **simultaneously** and flags the outlier — answering
"which replica is hot/leaking right now?" in one round-trip. It fans out a bounded
`collect_events(kind="counters")` to each attached Pod in parallel (so the windows overlap), parses
each `gc-heap-size` / `cpu` / `threadpool-queue` reading, and computes per-metric dispersion plus the
single most-deviant replica. This is distinct from `compare_to_baseline`, which contrasts
pre-collected **serial** snapshots — this is **live + simultaneous**.

| Parameter | Meaning |
| --- | --- |
| `durationSeconds` | Counter window applied to each Pod's simultaneous fan-out collection (default 5). |
| `intervalSeconds` | Counter refresh interval forwarded to each Pod (default 1). |

Requirements: orchestrator mode (`Orchestrator:Enabled=true`), the `read-counters` **and**
`orchestrator-attach` scopes, and at least one **Active** investigation handle. Like
`distributed_trace`, the call always runs **locally on the orchestrator** and is never proxied into a
single Pod. The result envelope carries a `ReplicaCounters` skew: per-replica `Replicas[]` readings,
per-metric `Metrics[]` dispersion (min/max/mean/stddev, absolute + relative spread, min/max Pod), the
flagged `OutlierPod` + `OutlierScore` (summed z-score), and `Warnings`. Per-Pod failures are isolated
in `data.podErrors`; if **every** attached Pod fails, the call returns a `ReplicaCounterFanoutFailed`
error carrying those messages.

```text
# after attach_to_pod against each replica:
collect_events(kind="replica_counters")(durationSeconds=5)
```


### Threshold-gated capture (`collect_events` + `triggerWhen`)

`collect_events(kind="counters")` can arm a **bounded** watch that captures a heavier artifact the
moment a single metric threshold trips — the threshold-gated, LLM/human-driven equivalent of
DebugDiag `collect`. It is **not** a daemon: the call polls one `System.Runtime` EventCounter for at
most `windowSeconds`, fires `captureKind` up to `maxCaptures` times, then returns synchronously.
Nothing persists server-side.

| Parameter | Meaning |
| --- | --- |
| `triggerWhen` | Single predicate `<metric><op><value>` — e.g. `cpu>85`, `gcHeapMb>=1500`, `rssMb>2000`, `threadCount>400`, `activeTimerCount>1000`. Operators: `>` `>=` `<` `<=`. Metrics map to `System.Runtime` EventCounters (`rssMb`=`working-set`, `threadCount`=`threadpool-thread-count`). |
| `captureKind` | What to capture on trip: `dump`, `cpu-sample`, `heap`, `thread-snapshot`. |
| `windowSeconds` | Required. Hard upper bound on how long the watch is armed (1–300). |
| `maxCaptures` | Stop after N captures (default 1, max 10). |
| `sampleIntervalSeconds` | Metric poll interval (1–`windowSeconds`, default 2). |
| `confirmDump` | Required `true` when `captureKind=dump` (writes a dump to disk; mirrors `collect_process_dump`). |

The captured artifact registers under the existing drilldown handle kinds (`cpu-sample` /
`heap-snapshot` / `thread-snapshot`) so the high-priority `query_snapshot(handle, …)` hint reaches
it without re-collecting; `dump` writes to disk and returns the path. Per-`captureKind` scopes are
re-checked on top of `read-counters`/`eventpipe`: `cpu-sample`=`eventpipe`; `heap`=`heap-read`+`ptrace`;
`thread-snapshot`=`ptrace`; `dump`=`dump-write`+`ptrace`. The result envelope carries a `GatedCapture`
block (samples observed, peak value, whether the predicate tripped, and one record per capture).

```text
collect_events(kind="counters")(processId=4242, triggerWhen="cpu>85", captureKind="cpu-sample", windowSeconds=60)
```

### Long-running collects: MCP Tasks

As of the `2025-11-25` protocol bump, the server registers an
`IMcpTaskStore`, advertises `capabilities.tasks.{list,cancel,requests.tools.call}`
and marks these tools with `execution.taskSupport: "optional"` in `tools/list`:

- `collect_sample` (every `kind` — cpu, off_cpu, allocation, native-alloc)
- `collect_events` (every `kind` — counters, exceptions, crash-guard, gc, …)
- `inspect_heap` (both `source="live"` and `source="dump"`)

`tools/list` also annotates every tool with authorization metadata under
`_meta.dotnetDiagnostics.auth`:

```json
{
  "requiredScopes": ["eventpipe"],
  "semantics": "all",
  "authorized": true
}
```

`semantics` is `all` for `[RequireScope]` and `any` for `[RequireAnyScope]`;
`authorized` is evaluated for the current bearer token (or the synthetic root
principal in stdio / legacy-root mode). Runtime branches may still tighten
access based on parameters or handle kind; see [authorization](./authorization.md).

**Spec-compliant clients should use MCP Tasks** for long windows:

1. send `tools/call` with `params.task` (or use `McpClient.CallToolAsTaskAsync`)
2. poll `tasks/get`
3. fetch the terminal `CallToolResult` via `tasks/result`
4. cancel via `tasks/cancel`

### MCP-native progress and cancellation (issue #211)

In addition to MCP Tasks, long-running collectors emit standard MCP
`notifications/progress` messages and honor `notifications/cancelled` on the
same `tools/call` request — no second round-trip, no polling. This is the
**preferred** path for clients that don't implement the full Tasks lifecycle.

Tools wired up:

- `collect_sample` (every `kind` — cpu, off_cpu, allocation, native-alloc)
- `collect_events` (every `kind` — counters, exceptions, crash-guard, gc, datas, catalog, event_source, activities, logs, jit, threadpool, contention, db, kestrel, networking, requests, startup)
- `inspect_heap` (both `source="live"` and `source="dump"` — emits an **indeterminate** heartbeat, since a ClrMD heap walk has no a-priori duration, plus a terminal `progress=100` on success)

How it works:

- The client sends a normal `tools/call` request with `_meta.progressToken` set
  (most C# / TypeScript SDKs do this automatically when an `IProgress<…>` is passed
  to `CallToolAsync`).
- The server emits `notifications/progress` on a ~1s cadence while the collector
  is running, plus a terminal `progress=100` on success.
- If the client cancels the in-flight `tools/call` request (its SDK
  `CancellationToken` trips, or it sends an MCP `notifications/cancelled`
  scoped to that **request id** — not to the progress token), the underlying
  EventPipe / sampler session is torn down and the server returns a
  `DiagnosticResult<T>` envelope with `cancelled: true` and empty data.
  Depending on which side of the race wins, some MCP client SDKs surface
  the cancellation as an `OperationCanceledException` instead of returning
  the envelope — both shapes are spec-conformant.

> **Removed in Stage B (issue #211).** The legacy polling
> bridge — `collect_sample(kind="cpu")(runAsJob=true)`, `get_collection_status(handle)`,
> `cancel_collection(handle)` — has been removed. Clients must use MCP Tasks or
> the in-request progress/cancel notifications described above.

### Prompts (curated playbooks)

In addition to tools, the server exposes 6 MCP **Prompts** that pre-package the
investigation strategies from [`investigation-playbooks.md`](./investigation-playbooks.md)
so the LLM can opt into a baked recipe instead of re-planning the next call
after every step. Prompts do not consume the tool-slot budget — clients
discover them via `prompts/list` and request a specific one via `prompts/get`.

| Prompt | Source playbook | Required inputs |
|---|---|---|
| `diagnose-high-latency` | "The app feels slow / high latency" | none (all optional: `processId?`, `durationSeconds?`, `symptom?`) |
| `diagnose-memory-growth` | "Memory keeps growing" | none (`processId?`, `windowSeconds?`, `symptom?`) |
| `diagnose-5xx-errors` | "We're seeing 5xxs in production" | none (`processId?`, `symptom?`) |
| `diagnose-slow-outbound-http` | "Slow outbound HTTP calls" | none (`processId?`, `durationSeconds?`, `symptom?`) |
| `triage-nativeaot` | "Is this a NativeAOT app?" | none (`processId?`) |
| `diagnose-safely-in-prod` | "Safest investigation in production" | none (`processId?`) |

Every prompt returns a single `user`-role message whose content is annotated
with `audience: ["assistant"]` so MCP clients that distinguish user-facing
templates from assistant-facing context route them directly into the LLM's
context window. Each prompt embeds the hypothesis tree from the playbook plus
exact tool-call examples (with placeholder args reflecting the implicit bootstrap).
The LLM may always ignore a prompt and drive ad-hoc.

### Handle chaining in the collectors (`query_snapshot`)

The windowed collectors — every `collect_events(kind=…)` variant (`counters`,
`exceptions`, `crash-guard`, `gc`, `datas`, `catalog`, `activities`,
`event_source`, `logs`, `jit`, `threadpool`, `contention`, `db`, `kestrel`,
`networking`, `requests`, `startup`) — return, alongside the inline summary
+ top-N, an opaque `handle` (Crockford-base32, TTL ~10 min) registered in an
in-memory store. The LLM can then re-project the same artifact under a
different view **without re-running EventPipe** by calling `query_snapshot`:

```jsonc
// 1. collect once
collect_events(kind="exceptions")(processId=4242, durationSeconds=10)
  → { summary: "30 exceptions (3 types)", handle: "01H...XY", data: { … top-N } }

// 2. drill down N times within the TTL window
query_snapshot(handle="01H...XY", view="recent", topN=20)
query_snapshot(handle="01H...XY", view="byType")
```

`query_snapshot` is the single drilldown verb — it dispatches on the `kind` the
handle carries and covers every kind emitted by the collectors above plus heap
(`heap-snapshot`), thread (`thread-snapshot`), off-CPU (`off-cpu-snapshot`) and
call-tree (`cpu-sample` / `allocation-sample` / `native-alloc-sample`).

Views available per `kind`:

| Kind | Emitted by | Accepted views |
|---|---|---|
| `counters` | `collect_events(kind="counters")` | `summary` (default), `byProvider` |
| `exception-snapshot` | `collect_events(kind="exceptions")` | `summary` (default = `byType.Take(topN)`), `byType`, `recent` |
| `crash-guard-snapshot` | `collect_events(kind="crash-guard")` | `summary` (default), `exceptions`, `stack` |
| `gc-events` | `collect_events(kind="gc")` | `summary` (default), `events`, `pauseHistogram`, `timeline`, `longestPauses`, `byGeneration`, `heap-stats` |
| `gc-datas` | `collect_events(kind="datas")` | `overview` (default), `tuning` (honours `changesOnly`), `samples`, `gen2` |
| `event-catalog` | `collect_events(kind="catalog")` | `catalog` (default), `byProvider`, `events` |
| `activities` | `collect_events(kind="activities")` | `summary` (default), `bySource`, `byOperation`, `activities` |
| `event-source` | `collect_events(kind="event_source")` | `summary` (default), `byEventName`, `events` |
| `log-snapshot` | `collect_events(kind="logs")` | `summary` (default), `byCategory`, `byLevel`, `recent`, `errors` |
| `jit-snapshot` | `collect_events(kind="jit")` | `summary` (default), `topMethods`, `tierDistribution`, `reJIT` |
| `threadpool-snapshot` | `collect_events(kind="threadpool")` | `summary` (default), `timeline`, `hillClimbing`, `workItemOrigins` |
| `contention-snapshot` | `collect_events(kind="contention")` | `summary` (default), `byCallSite`, `byOwner` |
| `db-snapshot` | `collect_events(kind="db")` | `summary` (default), `byCommand`, `n+1`, `connectionPool` |
| `kestrel-snapshot` | `collect_events(kind="kestrel")` | `summary` (default), `byOperation`, `queues`, `tls`, `config` |
| `networking-snapshot` | `collect_events(kind="networking")` | `summary` (default), `byOperation`, `queue`, `tls`, `dns` |
| `in-flight-requests` | `collect_events(kind="requests")` | `summary` (default), `requests`, `longRunning` |
| `startup-snapshot` | `collect_events(kind="startup")` | `summary` (default), `assemblies`, `modules`, `di`, `timeline` |
| `heap-snapshot` | `inspect_heap` / `inspect_heap(source="live")` / `inspect_heap(source="dump")` / `inspect_heap(source="gcdump")` | `top-types` (default), `retention-paths`, `roots-by-kind`, `finalizer-queue`, `fragmentation`, `static-fields`, `delegate-targets`, `duplicate-strings`, `gchandles`, `timers`, `alc`, `object`, `gcroot`, `objsize`, `async`, `diff`, `growth` |
| `thread-snapshot` | `collect_thread_snapshot` | `top-blocked` (default), `threads-summary`, `stack`, `lock-graph`, `deadlocks`, `unique-stacks`, `async-stalls`, `wait-chains`, `threadpool`, `resolve-address`, `frame-vars` |
| `off-cpu-snapshot` | `collect_sample(kind="off_cpu")` | `topStacks` (default), `byThread`, `stack` |
| `cpu-sample` / `allocation-sample` / `native-alloc-sample` | `collect_sample(kind="cpu")` / `collect_sample(kind="allocation")` / `collect_sample(kind="native-alloc")` | `call-tree`, `top-methods`, `by-module`, `by-namespace`, `hot-path`, `caller-callee`, `diff` |

Authorization is applied per kind at the dispatcher (`heap-read` for heap,
`ptrace` for thread, `eventpipe` for off-CPU, `investigation-export` for
cpu/allocation call-tree + diff, `heap-read` for heap diff,
`read-counters`|`eventpipe` for collection) — the static gate accepts any of
those scopes for the tool surface, and the per-kind boundary preserves each
former verb's contract verbatim.

`view="diff"` accepts `baselineHandle` or ordered `comparisonHandles`, `minDeltaPct`
(default `5.0`), `topN` (default `25`), `depth` (`"full"` default, or `"compact"`),
and `mode` (`"trend"` default, or `"dispersion"`). Trend treats captures as ordered over
time. Dispersion treats captures as unordered replicas and reports `uniform`, `dispersed`,
`no_overlap`, or `incomparable`; it requires N-way comparable captures via `comparisonHandles`
and is rejected for the legacy pairwise `baselineHandle` sample diffs.
For comparable journey diffs (`gc-datas`, `counters`, `gc-events`, `contention-snapshot`,
`threadpool-snapshot`), `depth="compact"` returns verdict + headline + counts + notes +
top-N metric/key deltas. `depth="full"`
returns the full `SnapshotJourneyDiff` only while it stays below the 32 KiB inline threshold;
larger matrices are retained in memory and the inline payload includes `journey://diff/{handle}`
so the assistant can pull the full matrix as an MCP Resource. Pairwise sample diffs remain
inline and accepted pairs are `cpu-sample × cpu-sample`, `heap-snapshot × heap-snapshot` and
`allocation-sample × allocation-sample`. Allocation diffs normalize totals to per-second rates

`heap-snapshot` `view="growth"` is the retention-aware **live heap leak hunt** (issue #463).
Capture two live heap snapshots N seconds apart — `inspect_heap(source="live", includeRetentionPaths=true)` —
then call `query_snapshot(handle=<later>, view="growth", baselineHandle=<earlier>)`. It ranks the
managed types that *grew* by retained `bytes` (default) or `instances` (`rankBy`), reporting per-type
baseline/current/delta for both dimensions, and attaches the retention chains recorded on the *later*
snapshot to the top growers so the model sees "which types grew, **and what's holding them**" in one
round-trip. Only positive growth at or above `minDeltaPct` (default `5.0`) surfaces; `topN` (default `25`)
caps the ranked rows while `totalGrowers` reports the full count. The verdict is `leak_suspected` when any
type grew, else `stable`. Unlike `view="diff"` — which ranks by percentage and can bury a large
absolute-but-modest-% leak — `growth` ranks strictly by absolute growth, the signal that matters for a
steady-state leak. Both handles must be `heap-snapshot` kind; a missing/expired `baselineHandle` returns
the standard `InvalidArgument` / `HandleExpired` envelope. Requires the `heap-read` scope.

`heap-snapshot` `view="timers"` projects the already-walked heap into a task/timer leak
drilldown: total live `System.Threading.Timer` / `TimerQueueTimer` objects, total live
`Task` and `TaskCompletionSource` objects, timers grouped by callback target/method, and
top task/TCS concrete runtime types. Use it after `collect_events(kind="counters")` shows
`active-timer-count` growth to identify the callback or async state-machine type being leaked. Allocation diffs normalize totals to per-second rates
when the two capture windows use different durations and surface both raw + normalized metrics
in each row.

`heap-snapshot` `view="alc"` projects the already-walked CoreCLR heap into an
AssemblyLoadContext leak drilldown: live ALC instances, collectible/default state,
assemblies observed under each context, and bounded GC-root retention hints for suspected
collectible leaks. Retention hints are computed during the heap walk for at most 16
collectible ALCs per snapshot, using the same bounded root-search machinery as `gcroot`
(64 frames / 250,000 visited objects); additional contexts are still listed without a
path. NativeAOT has no DAC/ClrMD heap walk, so this view is CoreCLR-only.

`thread-snapshot` `view="wait-chains"` builds ranked, multi-hop **wait-chains** that span the
three ways a .NET thread stalls, all from the already-captured snapshot (no re-collection):
(1) **sync monitor lock** — a thread waiting on a contended SyncBlock → the thread that *owns* it
(the same waiter→owner edges `view="deadlocks"` walks); (2) **async continuation** — a thread parked
sync-over-async (`Task.Wait`/`.Result`/`GetResult`) or awaiting an incomplete construct
(`SemaphoreSlim.WaitAsync`, channel reads/writes, `TaskCompletionSource`, a generic `MoveNext`) → the
construct it is blocked on (classified by the same recognizer as `async-stalls`); (3) **ThreadPool
starvation** — a sync-over-async chain that terminates in "waiting for a ThreadPool worker that isn't
available", detected when the snapshot's ThreadPool has pending work, no idle workers, and is at its
maximum. Chains are ranked longest / most-blocked first; true **cycles** are flagged distinctly
(`isCycle=true`, `terminalKind="cycle"`) from open chains that sink in starvation, an async construct,
or a running lock owner. Each hop reports `edgeKind`, a human `waitReason`, and the target node.
**Honesty about async ownership:** monitor hops carry a concrete `ownerThreadId` (recorded in the
snapshot), but async-continuation resumption ownership is generally **not** recoverable from a
point-in-time snapshot — nothing in thread state records which thread/task will complete an
outstanding await — so async hops emit an explicit `note` and `ownerThreadId=null` rather than
guessing. Requires the `ptrace` scope (same as every thread view).

The address-addressed views `object` (SOS `!do`), `gcroot` (SOS `!gcroot`) and `objsize` (SOS
`!objsize`) take an `address` and re-open the snapshot's origin with ClrMD to answer the question. They
work over **both** live and dump origins: a live handle briefly re-attaches behind the attach guard,
while a **dump-origin** handle re-reads the recorded `.dmp` DataTarget — so an offline dump can still
answer "what roots this object" without re-attaching to the (possibly gone) process. Authorization is the
same kind-wide `heap-read` scope for either origin. The standalone Core CLI `session` REPL serves
`gcroot`/`object` for dump-origin handles too (it has no live-attach guard), with `object` previews
redacted to metadata-only.

`compare_to_baseline(snapshotsJson=[...])` accepts the same comparable journey knobs:
`topN`, `depth`, and `mode="trend"|"dispersion"`. Legacy `InvestigationSummary` JSON
comparison ignores journey mode because it still returns the older two-summary `SummaryDiff`.

For compact dispersion summaries, metric series are ranked by their dispersion coefficient of
variation. Key-set rows are likewise ranked by coefficient of variation. In `dispersion` mode each
`KeyMatrixRow` persists a per-row `Dispersion` (`min`/`max`/`median`/`mean`/`stdDev`/
`coefficientOfVariation`/`outlierIndex`) computed once over the row's per-capture values, mirroring
`MetricSeries.Dispersion`; it is `null` in `trend` mode. Ranking and verdict reuse this persisted
value rather than recomputing it.

For an end-to-end comparative workflow (before/after and N-way trend journeys, verdict and
trend interpretation, and the two doors) see
[investigation-playbooks.md §1d](./investigation-playbooks.md#1d-did-my-fix-actually-help--comparative--n-way-trend-journeys).

The CPU drilldown views (`top-methods`, `by-module`, `by-namespace`, `hot-path`,
`caller-callee`, issue #313) re-aggregate the already-collected merged call tree — no new
sampling. They reuse the existing `query_snapshot` parameters: `topN` caps the number of rows
(default `20`), `rankBy` chooses the sort/credit metric (`inclusive` selects inclusive samples;
any other value, including the default, selects exclusive samples), and `rootMethodFilter`
supplies the focus method substring for `caller-callee`. `hot-path` additionally accepts
`hotPathThresholdPercent` (default `50`, range `0 < x <= 100`): the path descends into the
heaviest child while each step still carries at least that percentage of its parent's inclusive
samples. `top-methods`/`by-module`/`by-namespace` return ranked exclusive+inclusive sample
stats with percentages; `caller-callee` returns the focus method's aggregated cost plus its
direct callers and callees. The synthetic `<root>` frame is excluded from the ranked/grouped
views (`top-methods`/`by-module`/`by-namespace`/`hot-path`); in `caller-callee` it appears as a
caller named `<root>` to mark a top-level entry point (matching PerfView's ROOT pseudo-node).
A `caller-callee` filter that matches zero methods returns `NotFound`; one that matches more
than one distinct method returns `InvalidArgument` with the candidate list.

The GC drilldown views (`timeline`, `longestPauses`, `byGeneration`, issue #314) re-aggregate the
GC events already retained behind a `gc-events` handle — no new collection. `timeline` orders the
collections by start time and returns the earliest `topN` rows, each with a 0-based `Index`, the
GC `Generation`/`Reason`/`Type`, the `PauseDuration` (GCStart→GCStop elapsed), and
`GapSincePreviousStart` (start-to-start gap from the previous collection). `longestPauses` ranks
the same rows by pause descending and returns the top `topN` (each keeps its timeline `Index` for
cross-reference). `byGeneration` reports `Count` + total/mean/max pause per generation bucket
(`gen0`/`gen1`/`gen2`/`background`); background GCs form their own mutually-exclusive bucket, so
`gen2` counts non-background gen2 collections only. Note these views describe only the events
retained on the artifact (the collector caps at `maxEvents`).

The `heap-stats` view (issue #384) re-projects the per-collection `GCHeapStats` samples retained
behind the same `gc-events` handle — no new collection. Each sample carries the per-generation heap
sizes (`Gen0`/`Gen1`/`Gen2`/`Loh`/`Poh`), total heap and promoted bytes, finalization survivors, and
the `PinnedObjectCount` / `GcHandleCount`. The view returns the chronological samples (earliest
`topN`) plus a `Trend` block with the first→last deltas for gen2, LOH, POH, total heap, pinned-object
count, and GC-handle count — the classic signal for a slow managed leak or pinning pressure that pause
data alone misses. `Poh*` fields are populated only by the V2 event (pinned object heap) and are 0 on
runtimes that emit the V1 event.

The event-catalog views (`catalog`, `byProvider`, `events`) answer "what events does this app
emit?" without exposing EventSource payload values. `collect_events(kind="catalog")` enables a
broad curated provider set at Informational level (`Microsoft-Windows-DotNETRuntime`,
`System.Runtime`, `Microsoft-Diagnostics-DiagnosticSource`, `Microsoft-Extensions-Logging`, and
`System.Threading.Tasks.TplEventSource`); pass `providers` to replace that set when you need custom
EventSources, because EventPipe has no wildcard provider subscription. The catalog records only
provider name, event name, level and timestamp: `catalog` ranks distinct `(provider,eventName,level)`
rows by count, `byProvider` rolls counts up per provider, and `events` returns the bounded
metadata-only occurrence sample (`maxEvents`). Use `topN` for caps, `providerFilter` for a
case-insensitive provider substring, and `rootMethodFilter` as the event-name substring filter. If
you need payload field values, use the targeted `event_source` collector, which carries the
allowlist/redaction/unsafe-provider machinery.

The DATAS views (`overview`, `tuning`, `samples`, `gen2`) expose **D**ynamic **A**daptation **T**o
**A**pplication **S**izes — the Server GC's adaptive heap-count/gen0-budget tuning loop (default-on in
.NET 9+). `collect_events(kind="datas")` collects `Microsoft-Windows-DotNETRuntime` at `GCKeyword`
(`0x1`) / Informational and decodes the three DATAS `GCDynamicEvent` payloads
(`SizeAdaptationSample`, `SizeAdaptationTuning`, `SizeAdaptationFullGCTuning`). `overview` rolls up the
heap-count range, the number of heap-count changes, throughput-cost-percent (TCP) statistics and mean
gen0 budget / SOH stable size. `tuning` is the per-decision heap-count timeline (pass `changesOnly` to
collapse it to just the transitions plus a baseline row); `samples` returns the per-GC measurements
behind those decisions; `gen2` returns the gen2 "backstop" tuning events. Requires **Server GC** —
Workstation GC emits no DATAS events, so the collector returns a graceful `NoDatasEvents` result rather
than an error. The default collection window is 15 s (DATAS decisions accrue over time).

`view="resolve-address"` (thread-snapshot, issue #275) re-opens the snapshot origin (dump file or
live pid) and classifies one or more addresses passed via `address` (comma-separated, decimal or
`0x`-hex) into `module` (with `module`, `rva`, `buildId`), `managed` (with a `MethodIdentity`
handoff), `mapped-non-module` (readable but outside any loaded module — JIT stub / anonymous map),
or `unmapped-or-not-captured` (a freed hole or a region the dump did not capture). Numeric fields
are rendered as hex strings and `Display` is always safe to show verbatim — the diagnostics surface
never returns a bare pointer. Native/unresolved frames on every thread snapshot are enriched the
same way at capture time (`AddressKind` / `Rva` / `BuildId` on each frame, `DisplayName` becomes
`module+0x<rva>` or `<unmapped-or-not-captured 0x…>`). Hand the `(buildId, rva)` to
`dotnet-native-mcp` for symbolication.

`view="frame-vars"` (thread-snapshot, issue #449) is the ClrMD `!clrstack -a` equivalent. It
re-opens the snapshot origin (dump file or live pid — same ptrace / dump-read footprint as
`inspect_heap` live/dump, gated by `heap-read` on top of the kind-wide ptrace scope) and walks one
managed thread's stack roots, attributing the object-typed locals/parameters alive on each frame to
the frame that owns them, so an exception throw site can be inspected in-tool without a round-trip
to offline `dotnet-dump analyze`. Pass the `ManagedThreadId` via `threadId`. Each variable reports
`TypeFullName`, the object `Address` (hex), the register/stack `Location`, and pin/interior flags;
the current managed exception type is surfaced when the thread is faulting. It is **best-effort**:
ClrMD 3.x exposes object references but not source-level names, and value-type (struct/primitive) or
optimized-away locals are not enumerable. Raw string previews and the exception message require
`includeSensitiveValues` AND `Diagnostics:AllowSensitiveHeapValues` or the `sensitive-heap-read`
scope.

> **Note — `event-source` truncation:** the collector stops storing events
> once it reaches `maxEvents`, but keeps counting the total. The
> `summary`/`byEventName` views now carry `capturedCount` and `truncated`; when
> `truncated=true` the groups reflect only the captured prefix — re-run
> `collect_events(kind="event_source")` with a larger `maxEvents` for exact aggregates.

Handles are invalidated when: the TTL expires, the target process dies
(automatic eviction), or a server restart clears the store. Accessing an
unknown handle returns `DiagnosticError { Kind: "HandleExpired" }` with a
`NextActionHint` pointing at the original collector. Responses with handles
include both absolute `handleExpiresAt` and relative `handleExpiresInSeconds`
so clients can refresh without parsing timestamps.

This contract is the "split collector, unified drilldown" pattern
(documented in [`AGENTS.md`](../AGENTS.md)) applied to *all* collectors
— the same pattern as `inspect_heap(source="dump")`/`inspect_heap(source="live")` and
`collect_thread_snapshot`, now collapsed into a single query verb.

### Kernel-side signals (`inspect_process(view="container")`)

Kills the most common blind-spot in K8s: "the app is slow, but EventCounters
say CPU/memory are ok" — most of the time it's **CPU throttling at the
cgroup**, invisible to the runtime. `inspect_process(view="container")` reads cgroup v2 +
`/proc/<pid>/oom_score` and returns:

- `Cpu`: `usage_usec`, `nr_periods`, `nr_throttled`, `throttled_usec`,
  `ThrottlePercent` (canonical signal) and `QuotaCores` (null = unlimited).
- `Memory`: `current`, `max`, `high`, `UsageFraction`, plus
  `oom_kill` / `max-hit` counters extracted from `memory.events`.
- `Pressure` (PSI): `cpu.some.avg10`, `memory.some/full.avg10`, `io.some/full.avg10`.
- `Pids` and `oom_score`.

All best-effort: missing files (PSI on an old kernel, no memory limit,
a container without read access to `memory.events`) become entries in `Notes`, not
a fatal error. On Windows / cgroup v1 / no cgroup, it returns `InContainer=false`
+ the correct `CgroupVersion` and an explanatory `Notes` (job-object metrics are not
yet wired).

`inspect_process(view="capabilities")` gained the kernel-side flags so you know
whether it's worth attempting the collection first: `InContainer`, `CgroupV2`,
`CanSeeThrottle` (true iff a quota is configured → throttling is observable),
`PsiAvailable`, `PerfInstalled`, `HasCapPerfmon`, `PerfEventParanoid`,
`HasCapSysPtrace`, `PtraceScope` and `EtwKernelOk`. It also exposes
**`CanSampleOffCpu`** — true when the sidecar already meets the backend's
prerequisites (Linux: perf + sufficient privilege for `sched_switch`; Windows:
elevated process). When false, `Notes` carries the concrete hint for the reason before
the LLM attempts `collect_sample(kind="off_cpu")` on an unprivileged sidecar.

NextActionHints: throttle > 5% suggests `collect_sample(kind="cpu")` directly; memory >
85% of the limit suggests `inspect_heap(source="live")` before the OOM-kill.

### Off-CPU sampling (`collect_sample(kind="off_cpu")` + `query_snapshot`)

> **DEPRECATED (0.9.0).** Call [`collect_sample(kind="off_cpu", …)`](#collect_sample) instead; the legacy `collect_sample(kind="off_cpu")` remains registered behind a deprecation banner during the window (issue #210).

Complements `collect_sample(kind="cpu")` (which shows **on-CPU** — where the app
spends time executing) with **off-CPU** — where threads were **blocked**
(I/O, locks, condvars, monitor wait). It resolves the classic blind-spot "low CPU
but high latency": on-CPU sampling can't see it because the threads aren't
running.

- **Linux:** uses `perf record -a -e sched:sched_switch --call-graph dwarf`
  system-wide (the `sched_switch` tracepoint only fires on the thread leaving
  CPU, so restricting by PID misses the IN event). Spans are filtered
  post-collection by the target's `/proc/<pid>/task/*`. Requires `CAP_PERFMON` (kernel
  ≥ 5.8) or `perf_event_paranoid <= -1`, and `perf` installed
  (`linux-tools-common` / `linux-tools-$(uname -r)` on Debian/Ubuntu).
  `SymbolSource: "perf-sched-dwarf"`.
- **Windows:** uses the NT Kernel Logger session via `TraceEvent` with
  `ContextSwitch + Dispatcher + ImageLoad/Process/Thread`, with a stack walk on
  `ContextSwitch` (the stack captured at switch-out time is exactly the
  blocking call). The kernel wait reason
  (`UserRequest` / `WrLpcReceive` / `WrQueue`...) becomes the span's `PrevState`,
  a direct mirror of Linux's `S/D/I`. Spans still pending at the end of the window become
  censored (`IsCensored=true`) with a lower-bound duration, same as Linux.
  Requires **BUILTIN\\Administrators** or `SeSystemProfilePrivilege`; without it
  it returns `PermissionDenied` with a hint pointing at the two supported paths
  (`Administrators` **or** `Profile system performance`). For production, see
  [`windows-sidecar-service.md`](./windows-sidecar-service.md)
  (Windows Service with `LocalSystem` or a dedicated account + a single privilege).
  `SymbolSource: "etw-cswitch-pdb"` (resolves local PDBs + `_NT_SYMBOL_PATH`).
- **Managed↔kernel stack merge:** not yet — frames are purely native /
  kernel on both platforms.

`collect_sample(kind="off_cpu")(pid, durationSeconds=10, topN=10)` returns `{handle,
summary, top}` with the stacks that spent the most time off-CPU.
`query_snapshot(handle, view, ...)` follows the **split collector,
unified drilldown** pattern: `view="topStacks"` (default), `view="byThread"`
(aggregated by TID with `TopBlockingLeaf` + dominant state), or
`view="stack"` with `stackRank=N` (1-based) to export the full stack.


## Quick index

> NativeAOT coverage detail (which symbol source per tool, per OS): see
> [`aot-coverage.md`](./aot-coverage.md).

| Tool | Cost | Requires CoreCLR? | NativeAOT? | Side effects |
|---|---|---|---|---|
| [`inspect_process`](#inspect_process) | depends on `view` | no | ✅ | union of the five legacy bootstrap tools below |
| [`inspect_process(view="list")`](#inspect_process(view="list")) *(deprecated — use `inspect_process(view="list")`)* | cheap | no | ✅ | none |
| [`inspect_process(view="info")`](#inspect_process(view="info")) *(deprecated — use `inspect_process(view="info")`)* | cheap | no | ✅ | none |
| [`inspect_process(view="capabilities")`](#inspect_process(view="capabilities")) *(deprecated — use `inspect_process(view="capabilities")`)* | ~2 s | no | ✅ | opens a short EventPipe probe |
| [`inspect_process(view="container")`](#inspect_process(view="container")) *(deprecated — use `inspect_process(view="container")`)* | cheap | no | ✅ (Linux) | reads `/sys/fs/cgroup` + `/proc` files |
| [`inspect_process(view="memory_trend")`](#inspect_process(view="memory_trend")) *(deprecated — use `inspect_process(view="memory_trend")`)* | window-bound | no | ✅ | reads `/proc/<pid>/smaps_rollup` + `/proc/<pid>/stat` (Linux) or `GetProcessMemoryInfo` (Windows) |
| [`inspect_process(view="runtime-config")`](#inspect_process(view="runtime-config")) | cheap | no | ✅ (Windows env partial) | ClrMD GC / ThreadPool probe + filtered `/proc/<pid>/environ` (Linux) |
| [`inspect_process(view="resources")`](#inspect_process(view="resources")) | cheap / window-bound | no | ✅ (Linux/Windows partial) | reads `/proc/<pid>/fd`, `/proc/<pid>/net/tcp{,6}`, `/proc/<pid>/limits`, `VmRSS` + a short `gc-heap-size` counter probe (Linux) or `GetProcessHandleCount` / `WorkingSet64` (Windows) |
| [`inspect_process(view="requests-now")`](#inspect_process(view="requests-now")) | ~2 s | no | ✅ (ptrace required) | short EventPipe request window + live thread snapshot |
| [`inspect_process(view="triage")`](#inspect_process(view="triage")) | ~5 s | no | ✅ | **Phase 12 IoT-style triage.** Collects counters (5s), classifies workload (cpu-bound/gc-pressure/memory-pressure/threadpool-starvation/lock-contention/io-bound/healthy), returns actionable hints. The LLM just follows the first hint — no interpretation needed. |
| [`inspect_process(view="preflight")`](#inspect_process(view="preflight")) | cheap | no | ✅ | **Phase 13 environment self-diagnosis.** Target-optional, remediation-first readiness checks (diagnostic-socket UID, ClrMD attach/ptrace, perf off-CPU, native-alloc). Answers *"why can't I attach to this PID and how do I fix it?"* before paying for a failed collect. |
| `collect_sample(kind="off_cpu")` (Linux/Windows) | window-bound | no | ✅ (Linux) | **Deprecated — use `collect_sample(kind="off_cpu")`.** system-wide `perf record` (Linux) / NT Kernel Logger CSwitch (Windows, admin) |
| `query_snapshot` | cheap | no | ✅ | drilldown on handle from `collect_sample(kind="off_cpu")` |
| [`collect_events`](#collect_events) | window-bound | no | ✅ (mostly — see kind) | **Canonical EventPipe collector.** Dispatches by `kind` to counters/exceptions/crash-guard/gc/datas/catalog/event_source/activities/logs/jit/threadpool/contention/db/kestrel/networking/requests/startup. |
| [`collect_sample`](#collect_sample) | window-bound | depends on kind | ✅ (mostly — see kind) | **Canonical bounded-time sampler.** Dispatches by `kind` to cpu/off_cpu/allocation/native-alloc. |
| [`collect_events(kind="counters")`](#collect_events(kind="counters")) | window-bound | no | ✅ | **Deprecated — use `collect_events(kind="counters")`.** opens an EventPipe session |
| [`collect_sample(kind="cpu")`](#collect_sample(kind="cpu")) | window-bound | no | ✅ (perf/ETW, native frames) | **Deprecated — use `collect_sample(kind="cpu")`.** EventPipe + temp `.nettrace` on disk |
| [`collect_sample(kind="allocation")`](#collect_sample(kind="allocation")) | window-bound | no | ⚠️ TypeName empty | **Deprecated — use `collect_sample(kind="allocation")`.** EventPipe session |
| [`collect_events(kind="exceptions")`](#collect_events(kind="exceptions")) | window-bound | no | ✅ | **Deprecated — use `collect_events(kind="exceptions")`.** EventPipe session |
| [`collect_events(kind="crash-guard")`](#collect_events(kind="crash-guard")) | window-bound (returns on exit) | no | ✅ | Runtime exception/crash guard; emits dump hint on unhandled exception |
| [`collect_events(kind="gc")`](#collect_events(kind="gc")) | window-bound | no | ✅ | **Deprecated — use `collect_events(kind="gc")`.** EventPipe session |
| [`collect_events(kind="activities")`](#collect_events(kind="activities")) | window-bound | no | ✅ | **Deprecated — use `collect_events(kind="activities")`.** EventPipe session |
| [`collect_events(kind="event_source")`](#collect_events(kind="event_source")) | window-bound | no | ⚠️ provider must be embedded at publish | **Deprecated — use `collect_events(kind="event_source")`.** EventPipe session |
| `collect_thread_snapshot` / `query_snapshot` | seconds | no | ✅ via `linux-native-stack` / `etw-native-stack` | ptrace attach (Linux) / kernel logger (Windows) |
| `inspect_heap` (canonical) / `inspect_heap(source="live")` / `inspect_heap(source="dump")` (deprecated aliases, 0.7.0) / `query_snapshot` | seconds | **yes** | ❌ | ClrMD walks managed heap (heap drilldown values metadata-only by default — see [Security gates](#security-gates-b4)) |
| `inspect_heap(source="gcdump")` | seconds | no (EventPipe, no ptrace) | ❌ | Induced GC heap snapshot over EventPipe — **production-safe**, no dump file; per-type byte/instance totals only (ClrMD-only views empty) |
| [`collect_process_dump`](#collect_process_dump) | seconds–minutes | no | ✅ (native dump) | **writes a dump file to disk** |
| [`capture_method_bytes`](#capture_method_bytes) | cheap | **yes** | ❌ (use `dotnet-native-mcp.disassemble`) | reads JIT code-heap |
| `get_bytes(kind="module")` | cheap | **yes** (live module attach) | ❌ (materialize locally, then hand off) | streams PE / PDB bytes over MCP chunks |
| `get_bytes(kind="dump")` | cheap | no | ❌ (materialize locally, then hand off) | streams dump bytes from `MCP_ARTIFACT_ROOT` |
| `list_orchestrator(kind=pods\|investigations)` (orchestrator) | cheap | n/a | n/a | Successor to `list_orchestrator(kind="pods")` + `list_orchestrator(kind="investigations")`. `kind=pods` → Kubernetes `pods.list` (scope `orchestrator-list`); `kind=investigations` → in-memory handle snapshot (scope `orchestrator-attach`). **Opt-in**, registered only when `Orchestrator:Enabled=true`. Legacy tool names remain accepted for one deprecation window (removed in 0.7.0). |

"Window-bound" means the duration is the dominant cost; the tool will block for
~`durationSeconds`.

### Linux runtime requirements

EventPipe-based tools (including `collect_events(kind="activities")`, alongside the ones listed in the index above) only need the
diagnostic IPC socket, which works as long as the MCP server runs as the
**same UID** as the target process. ClrMD-backed tools added since the MVP —
`collect_thread_snapshot`, `inspect_heap(source="live")`, `inspect_heap(source="dump")` against a live
PID, `collect_process_dump`, and `get_bytes(kind="module")` — additionally call
`ptrace(PTRACE_ATTACH, …)` under the hood. On Linux, matching UIDs is **not**
sufficient when the host's
`kernel.yama.ptrace_scope` is `1` (the Debian/Ubuntu/WSL default): the kernel
blocks same-UID peer attach.

If a request lands in that state you'll get a structured error envelope (see
issue #32):

```json
{ "error": { "kind": "PermissionDenied",
             "message": "Could not PTRACE_ATTACH to any thread of the process N." } }
```

Mitigations:

- **Docker:** add `--cap-add SYS_PTRACE` to the **sidecar** container.
- **Kubernetes:** set `capabilities.add: ["SYS_PTRACE"]` on the sidecar
  container's `securityContext` (see [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml)).
- **Bare host / local dev:** `sudo sysctl -w kernel.yama.ptrace_scope=0`, or
  run the MCP server as root.

When ptrace cannot be granted, fall back to `collect_process_dump` +
`inspect_heap(source="dump")` (the dump capture runs in the target's own process, so it does
not require ptrace from the sidecar — but writing the dump file is still
gated on the diagnostic socket UID).

For **NativeAOT on Linux**, `collect_thread_snapshot` now routes to
`eu-stack -p <pid>` (elfutils) instead of ClrMD. The snapshot payload carries
`source: "linux-native-stack"` and maps wait reason from
`/proc/<pid>/task/<tid>/{status,wchan}` (`BlockedOnLock`, `BlockedOnIO`,
`BlockedOnUninterruptibleIO`, `Stopped`, `Running`). This path still requires
same-UID + ptrace gate; when denied the `PermissionDenied` envelope includes a
hint to the perf-replay fallback tracked in issue #92.

---

## `inspect_process`

**Canonical bootstrap tool.**
Consolidates the five legacy metadata tools — `inspect_process(view="list")`,
`inspect_process(view="info")`, `inspect_process(view="capabilities")`, `inspect_process(view="container")`,
`inspect_process(view="memory_trend")` — behind one `view` discriminator, and adds the
Phase 11 `inspect_process(view="runtime-config")` projection for GC / ThreadPool / tiered-comp startup settings,
plus the Phase 10.3 `inspect_process(view="resources")` FD / handle / socket inspector and
Phase 10.4 `inspect_process(view="requests-now")` for an in-flight ASP.NET Core request snapshot.
Each legacy view delegates to the same implementation as the removed tool of the same name, so
the payload under `data` is byte-identical to the legacy envelope's `data`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `view` | `"list" \| "info" \| "capabilities" \| "container" \| "memory_trend" \| "runtime-config" \| "resources" \| "requests-now"` | `"list"` | Which bootstrap projection to compute. |
| `processId` | `int?` | auto | Target PID. **Ignored when `view="list"`** (the list view is process-agnostic). When omitted on `view="memory_trend"` or `view="resources"` the server auto-resolves the lone reachable .NET process; `view="runtime-config"` and `view="requests-now"` also auto-resolve but still require a real .NET process because they open a live diagnostics path. |
| `durationSeconds` | `int` | `10` / `0` | Used by `view="memory_trend"` and `view="resources"`. Memory trend requires `>= 2`; resources uses `0` for a single snapshot (default) or `>= 2` for trend mode. |
| `sampleEverySeconds` | `int` | `2` | Used only by `view="memory_trend"` / `view="resources"`. Must be ≥ 1. |
| `depth` | `SamplingDepth?` | `Summary` | Used only by `view="container"`; forwarded to `inspect_process(view="container")`. |

**Returns:** `InspectProcessReport` — a standard envelope (`summary` / `hints` /
`error` / `resolvedProcess`) wrapping a `data` object that contains exactly one
populated field matching the requested view:

| `view` | `data` shape |
|---|---|
| `list` | `DotnetProcess[]` (see [`inspect_process(view="list")`](#inspect_process(view="list"))) |
| `info` | `DotnetProcess` (see [`inspect_process(view="info")`](#inspect_process(view="info"))) |
| `capabilities` | `DiagnosticCapabilities` (see [`inspect_process(view="capabilities")`](#inspect_process(view="capabilities"))) |
| `container` | `ContainerSignals` (see [`inspect_process(view="container")`](#inspect_process(view="container"))) |
| `memory_trend` | `MemoryTrend` (see [`inspect_process(view="memory_trend")`](#inspect_process(view="memory_trend"))) |
| `runtime-config` | `RuntimeConfigView` (see [`inspect_process(view="runtime-config")`](#inspect_process(view="runtime-config"))) |
| `resources` | `ProcessResources` (see [`inspect_process(view="resources")`](#inspect_process(view="resources"))) |
| `requests-now` | `InFlightHttpRequest[]` (see [`inspect_process(view="requests-now")`](#inspect_process(view="requests-now"))) |

**Recommended bootstrap sequence:**

```text
inspect_process(view="list")          # discover candidate PIDs (or rely on auto-resolve)
inspect_process(view="capabilities")  # confirm CoreCLR vs NativeAOT + ptrace/PSI/perf gates
inspect_process(view="container")       # cheap cgroup/PSI signals before any EventPipe session
inspect_process(view="memory_trend")    # lightweight leak signal — any OS process, no IPC
inspect_process(view="runtime-config")  # GC / ThreadPool / tiered-comp startup settings + filtered env vars
inspect_process(view="resources")       # unmanaged FD / socket / handle signal when heap is flat
inspect_process(view="requests-now")    # in-flight ASP.NET Core requests + current thread stacks
```

Unknown view values surface as the standard discriminator-dispatch error
(`error.kind = "InvalidArgument"`, `error.detail = "view"`).

---

## `inspect_process(view="list")`

> **Deprecated** — use [`inspect_process(view="list")`](#inspect_process). The
> payload is unchanged; the legacy tool remains registered and emits a
> `Deprecated` flag on `tools/list`.

Lists every .NET process on the local machine that exposes a Diagnostic IPC
endpoint (Unix socket on Linux, named pipe on Windows).

**Parameters:** none.

**Returns:** array of `DotnetProcess`:

```json
[
  {
    "processId": 12345,
    "commandLine": "/usr/bin/dotnet /app/MyApi.dll",
    "operatingSystem": "linux",
    "processArchitecture": "x64",
    "runtimeVersion": "10.0.0",
    "managedEntrypointAssemblyName": "MyApi"
  }
]
```

**Notes:** processes that respond too slowly or whose IPC endpoint is
unreachable are silently omitted.

---

## `inspect_process(view="info")`

> **Deprecated** — use [`inspect_process(view="info")`](#inspect_process).

Returns metadata for a single PID.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |

**Returns:** a single `DotnetProcess` (same shape as above) or `null` if the
process is gone / unreachable.

---

## `inspect_process(view="capabilities")`

> **Deprecated** — use [`inspect_process(view="capabilities")`](#inspect_process).

Probes the target by opening a short EventPipe session against the
`Microsoft-DotNETCore-SampleProfiler` provider. The presence/absence of sample
events is used to classify the runtime as **CoreCLR** vs **NativeAOT**.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |

**Returns:** `DiagnosticCapabilities`:

```json
{
  "processId": 12345,
  "runtime": "CoreClr",
  "runtimeVersion": "10.0.0",
  "canReadEventCounters": true,
  "canSampleCpu": true,
  "canCollectGcDump": true,
  "canCollectExceptions": true,
  "canCollectHttpActivity": true,
  "canCollectCustomEventSource": true,
  "canCollectProcessDump": true,
  "notes": "CoreCLR runtime detected via SampleProfiler events."
}
```

**Notes:** always call this **first** in a session. The result tells the LLM
(or human) which other tools can be used on the target. NativeAOT will return
`runtime = "NativeAot"` and `canSampleCpu = false`.

---

## `inspect_process(view="preflight")`

**Environment self-diagnosis (Phase 13 / issue #436).** Unlike `view="capabilities"`
(a per-target boolean matrix), this view is **target-optional** and **remediation-first**:
every non-OK finding carries a copy-pasteable fix (docker flag / k8s `securityContext`
snippet / `sysctl`). Use it to answer *"why can't I attach to this PID and how do I fix
it?"* before paying for a failed collect — it reuses the cheap host probes (ptrace, perf)
and a `/proc/*/status` UID read, opens no EventPipe session, and never fails.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | — | Optional. With a target, also validates the diagnostic-socket UID match against that pid. Omit for host-only diagnosis. |

**Returns:** `PreflightReport`:

```json
{
  "processId": 4242,
  "os": "linux",
  "overall": "Blocked",
  "checks": [
    {
      "id": "clrmd-attach",
      "title": "ClrMD live attach (ptrace)",
      "status": "Blocked",
      "reason": "Linux: kernel.yama.ptrace_scope=1 … and sidecar lacks CAP_SYS_PTRACE — same-UID peer attach is blocked.",
      "remediation": "Grant the capability (container: --cap-add SYS_PTRACE / capabilities.add: ['SYS_PTRACE']) or relax the host (sudo sysctl -w kernel.yama.ptrace_scope=0).",
      "affectedTools": ["collect_thread_snapshot", "inspect_heap(source=\"live\")", "inspect_heap(source=\"dump\")", "collect_process_dump"]
    }
  ]
}
```

**Checks:**

| `id` | Severity when failing | Affects |
|---|---|---|
| `socket-uid` | **Blocked** (UID mismatch) / Degraded (unreadable) | **all tools** — the diagnostic IPC socket is owned by the target UID |
| `clrmd-attach` | **Blocked** | `collect_thread_snapshot`, `inspect_heap`, `collect_process_dump` |
| `offcpu-perf` | Degraded | `collect_sample(kind="off_cpu")` |
| `native-alloc` | Degraded | `collect_sample(kind="native-alloc")` |

**Status ladder:** `Ok` < `Degraded` (optional capability missing; core diagnostics still
work) < `Blocked` (hard blocker). `NotApplicable` checks (Linux-only checks on Windows, the
socket-UID check with no target) are excluded from `overall`. The most severe check is
surfaced first.

**Notes:** the standalone CLI exposes the same engine as
[`dotnet-diagnostics doctor`](./cli-reference.md#doctor), which additionally exits non-zero
on a hard blocker for CI gating.

---

## `inspect_process(view="memory_trend")`

> **Deprecated** — use [`inspect_process(view="memory_trend")`](#inspect_process).

Samples OS-level memory metrics at regular intervals over a configurable window
and computes per-second deltas and a growth verdict. Works on **any** runtime
(CoreCLR, NativeAOT, even non-.NET processes) — no EventPipe session required.

Use this as a **lightweight memory-leak signal** before reaching for heap dumps.
It answers "is the process growing and how fast?" without walking the heap.

**Sources:**
- **Linux**: `/proc/<pid>/smaps_rollup` (Rss, Pss, Anonymous) and
  `/proc/<pid>/stat` fields 10 & 12 (minflt / majflt). Pure file reads —
  no privileges, no EventPipe.
- **Windows**: `GetProcessMemoryInfo(PROCESS_MEMORY_COUNTERS_EX)`:
  `WorkingSetSize` (RSS), `PrivateUsage` (private committed bytes),
  `PageFaultCount`. Requires `PROCESS_QUERY_INFORMATION` access to the target.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id |
| `durationSeconds` | `int` | `10` | Observation window length in seconds. Must be ≥ 2. |
| `sampleEverySeconds` | `int` | `2` | Interval between consecutive samples in seconds. Must be ≥ 1. |

**Returns:** `MemoryTrend`:

```json
{
  "processId": 12345,
  "windowStart": "2026-05-18T20:00:00Z",
  "windowEnd": "2026-05-18T20:00:10Z",
  "samples": [
    {
      "timestamp": "2026-05-18T20:00:00Z",
      "rssBytes": 104857600,
      "pssBytes": 52428800,
      "privateAnonBytes": 83886080,
      "heapRegionBytes": null,
      "majorFaults": 12,
      "minorFaults": 50000
    }
  ],
  "deltas": {
    "rssBytesPerSec": 1200000.0,
    "pssBytesPerSec": 600000.0,
    "majorFaultsPerSec": 0.2
  },
  "verdict": "growing",
  "notes": []
}
```

**Verdict heuristic:** RSS growth > 1 MiB/s → `growing`; RSS decrease > 1
MiB/s → `shrinking`; otherwise → `stable`. All three values are
stable-but-informative labels — they do not distinguish between heap and
stack allocations.

**Field notes:**
- `pssBytes` is Linux-only (Proportional Set Size — shared pages charged
  proportionally). Always `null` on Windows.
- `heapRegionBytes` is `null` on both platforms (requires a full
  `/proc/<pid>/smaps` walk; omitted for cost reasons).
- On Windows, `majorFaults` is always `0` — Windows does not separate
  major/minor faults; the combined count appears in `minorFaults`.

**Next-action hints:**
- `verdict = "growing"` → suggests `inspect_heap(source="live")` (identify dominant
  retainers) and `inspect_process(view="container")` (cross-check against cgroup limits).
- `verdict = "stable"` or `"shrinking"` → suggests `collect_events(kind="counters")`.

---

## `inspect_process(view="runtime-config")`

Cheap startup-configuration snapshot for questions like "is this Server GC?", "what are the ThreadPool min/max settings?", and "did someone override tiered compilation?".

- **GC / ThreadPool**: best-effort ClrMD live attach. On Linux, ptrace restrictions degrade to `notes[]` instead of failing the whole view.
- **Tiered compilation**: sourced from startup env overrides (`DOTNET_TieredCompilation`, `DOTNET_TC_QuickJit`, `DOTNET_TieredPGO`, plus `COMPlus_` aliases when present).
- **Environment variables**: Linux reads `/proc/<pid>/environ`; Windows currently returns an explanatory note and an empty `envVars[]`.
- **Security boundary**: `envVars[]` is strictly filtered to `DOTNET_`, `COMPlus_`, `ASPNETCORE_`, and `DOTNET_SYSTEM_` prefixes. Everything else is intentionally dropped.
- **AppContext switches**: parsed offline from the target's `<app>.runtimeconfig.json` (`runtimeOptions.configProperties`) located next to the main module via the absolute cmdline DLL or the self-contained apphost — AppContext switches (`Switch.System.*`, `System.Net.*`, HTTP/3 / TLS / gRPC / metrics opt-ins) and runtime knobs. Only known runtime namespaces (`System.`, `Microsoft.`, `Switch.`, `Windows.`, `Internal.`) are surfaced; custom configProperties keys are dropped so app secrets can't leak. No ClrMD attach; post-startup `AppContext.SetSwitch` overrides are not reflected. Empty with a note when the file cannot be located.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target .NET process id. When omitted the server auto-resolves the lone reachable .NET process. |

**Returns:** `RuntimeConfigView`:

```json
{
  "processId": 12345,
  "gc": {
    "isServerGc": false,
    "isConcurrent": true,
    "isBackground": true,
    "heapCount": 1,
    "largeObjectHeapCompactionMode": null
  },
  "threadPool": {
    "minWorkerThreads": 1,
    "maxWorkerThreads": 32767,
    "minIocpThreads": 1,
    "maxIocpThreads": 1000,
    "hillClimbingEnabled": true
  },
  "tieredCompilation": {
    "enabled": true,
    "quickJitEnabled": true,
    "dynamicPgoEnabled": true
  },
  "envVars": [
    { "name": "DOTNET_TieredCompilation", "value": "1" },
    { "name": "ASPNETCORE_URLS", "value": "http://127.0.0.1:0" }
  ],
  "appContextSwitches": [
    { "name": "System.GC.Server", "value": "false" },
    { "name": "System.Net.SocketsHttpHandler.Http3Support", "value": "true" }
  ],
  "notes": [
    "Environment variables are filtered to known runtime prefixes (DOTNET_ / COMPlus_ / ASPNETCORE_ / DOTNET_SYSTEM_); all other process env vars are intentionally omitted as a security boundary.",
    "AppContext switches were read offline from /app/MyApp.runtimeconfig.json (runtimeOptions.configProperties); post-startup AppContext.SetSwitch overrides are not reflected."
  ]
}
```

---

## `inspect_process(view="resources")`

Cheap OS-level resource inspector for the classic "RSS grows but `gc-heap-size` stays flat" case.

- **Linux**: counts `/proc/<pid>/fd`, classifies symlink targets (`socket:[...]`, `/...`, `pipe:[...]`, `anon_inode:[eventfd]`), aggregates TCP states from `/proc/<pid>/net/tcp{,6}`, and parses `Max open files` from `/proc/<pid>/limits`.
- **Windows**: calls `GetProcessHandleCount`; FD/socket breakdowns stay `null` with a note.
- **Managed/native split**: reads RSS (`VmRSS` on Linux, `WorkingSet64` on Windows) and
  samples the `System.Runtime/gc-heap-size` EventCounter to populate `managedVsNative`.
  If RSS is far larger than the GC heap, the response adds a note/hint to investigate
  native allocations, fragmentation, pinned LOH/POH, mmap/file caches, or unmanaged libraries.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id. Explicit values bypass .NET IPC resolution, so any OS pid is accepted. |
| `durationSeconds` | `int` | `0` | `0` = single snapshot; values `>= 2` enable trend mode. |
| `sampleEverySeconds` | `int` | `2` | Interval between trend samples. Must be ≥ 1. Ignored when `durationSeconds = 0`. |

**Returns:** `ProcessResources`:

```json
{
  "processId": 12345,
  "capturedAt": "2026-05-25T22:40:00Z",
  "fdCount": 186,
  "handleCount": null,
  "fd": { "sockets": 42, "regular": 96, "pipes": 16, "eventfds": 2, "other": 30 },
  "sockets": { "established": 12, "timeWait": 51, "closeWait": 0, "listen": 2, "other": 1 },
  "limits": { "noFileSoft": 1024, "noFileHard": 1024, "noFileUsageFraction": 0.1816 },
  "managedVsNative": {
    "rssBytes": 536870912,
    "gcHeapBytes": 67108864,
    "rssMinusGcHeapBytes": 469762048,
    "gcHeapToRssRatio": 0.125,
    "rssDominated": true,
    "interpretation": "RSS is much larger than the managed GC heap; investigate native allocations, fragmentation, pinned LOH/POH, mmap/file caches, or unmanaged libraries."
  },
  "notes": [],
  "trend": null
}
```

`trend.samples[]` repeats the same OS headline fields (`fdCount`, `handleCount`, `fd`, `sockets`, `limits`) per sample, with the top-level properties set to the latest sample. `managedVsNative` is populated on the top-level/latest sample from a best-effort GC heap probe near the end of the window; if the target is not a reachable .NET process, `managedVsNative.gcHeapBytes` is `null` and `notes[]` explains why.

**Next-action hints:**
- `closeWait > 100` and rising → `collect_events(kind="event_source", providerName="System.Net.Http")` to confirm undisposed responses / client misuse.
- `noFileUsageFraction > 0.85` → consider `collect_process_dump` before the process hits `EMFILE` / "Too many open files".
- huge `timeWait` with flat `fdCount` → connection churn / pooling issue, again best cross-checked with `System.Net.Http` events.
- `managedVsNative.rssDominated = true` → `inspect_heap(source="live")` to rule out pinned/fragmented managed heap; if the GC heap remains flat, pivot to native allocation or mmap investigation.

---

## `inspect_process(view="requests-now")`

Short ASP.NET Core request snapshot for the "which requests are hanging right now?" question.

- Opens a ~2 s EventPipe window against the ASP.NET Core `HttpRequestIn` activity stream.
- Keeps only requests whose start event was observed **without** a matching stop before the window closed.
- Captures one live thread snapshot and maps the observed OS thread id back to top managed frames.
- Requires the `ptrace` scope because the enrichment step uses the same live-attach path as `collect_thread_snapshot`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target .NET process id. When omitted the server auto-resolves the lone reachable .NET process. |

**Returns:** `InFlightHttpRequest[]`:

```json
[
  {
    "traceId": "4b89c4e2f7c4b0d7b34d2d9739f52f01",
    "endpoint": "/slow-hang",
    "method": "GET",
    "startedAtMs": 1840.0,
    "threadId": 12345,
    "topFrames": [
      "System.Threading.Tasks.Task.Delay(Int32, CancellationToken)",
      "BadCodeSample.Program+<>c.<<Main>$>b__0_11>d.MoveNext()"
    ]
  }
]
```

`method` and `endpoint` are best-effort projections from the request activity metadata. If ASP.NET Core did not stamp those fields before the snapshot, the server returns `"(unknown)"` rather than dropping the request row.

---

## `collect_events`

**Canonical EventPipe collector.** A single tool that dispatches by `kind` to
the underlying counters / exceptions / crash-guard / gc / datas / catalog /
event_source / activities / logs / jit / threadpool / contention / db /
kestrel / networking / startup collectors. New clients should call
`collect_events` instead of the legacy entrypoints; the legacy tools remain
registered and behaviorally identical, but each carries a `DEPRECATED` notice
and will be removed in `0.7.0`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `kind` | `string` | — | One of `counters`, `exceptions`, `crash-guard`, `gc`, `datas`, `catalog`, `event_source`, `activities`, `logs`, `jit`, `threadpool`, `contention`, `db`, `kestrel`, `networking`, `requests`, `startup`. Case-sensitive. |
| `processId` | `int?` | auto | Target process id. |
| `durationSeconds` | `int` | 5 (counters) / 15 (datas) / 10 (others) | Collection window. |
| `providers` / `meters` / `intervalSeconds` / `maxInstrumentTimeSeries` | counters only | — | Same as [`collect_events(kind="counters")`](#collect_events(kind="counters")). |
| `maxRecent` | exceptions / crash-guard only | 100 | Maximum retained exception records. |
| `maxEvents` | gc / datas / catalog / event_source / logs only | 200 (`gc`, `event_source`) / 1000 (`datas`) / 500 (`logs`) | Same as the underlying tool. |
| `providerName` / `keywords` / `eventLevel` / `depth` / `unsafeProvider` | event_source only | — | Same as [`collect_events(kind="event_source")`](#collect_events(kind="event_source")). |
| `sources` / `maxActivities` | activities only | — | Same as [`collect_events(kind="activities")`](#collect_events(kind="activities")). |
| `categories` / `minLevel` / `maxMessageBytes` / `depth` | logs only | — | Same as [`collect_events(kind="logs")`](#collect_events(kind="logs")). |
| `depth` | exceptions / crash-guard / jit / threadpool / contention / startup only | `Summary` | Inline verbosity for the curated runtime views. |
| `intervalSeconds` / `depth` | db / kestrel / networking only | `1` / `Summary` | EventCounter refresh interval + inline verbosity for curated views. |

**Returns:** `CollectEventsEnvelope` — a polymorphic record that carries the
`kind` discriminator plus exactly one populated payload field
(`counters` / `exceptions` / `crashGuard` / `gc` / `datas` / `catalog` /
`eventSource` / `activities` / `logs` / `jit` / `threadPool` / `contention` /
`db` / `kestrel` / `networking` / `startup`). The envelope's `summary`, `hints`,
`handle`, `handleExpiresAt`, and `resolvedProcess` are passed through from the
underlying collector verbatim, so `query_snapshot` drilldowns continue to work
unchanged.

**Authorization.** The dispatcher is gated by `RequireAnyScope("read-counters","eventpipe")`
and re-checks the per-kind scope inside the call so the scope boundaries
are preserved: `kind="counters"` requires `read-counters`, every other
kind requires `eventpipe` (`event_source` additionally honors the existing
`eventsource-any` modifier).

---

## `collect_sample`

**Canonical bounded-time sampler.** A single tool that
dispatches by `kind` to the underlying CPU / off-CPU / allocation / native-alloc sampler.
New clients should call `collect_sample` instead of the three legacy entry
points; the legacy tools remain registered and behaviorally identical, but
each carries a `DEPRECATED` notice and will be removed in `0.9.0`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `kind` | `string` | `cpu` | One of `cpu`, `off_cpu`, `allocation`, `native-alloc`. Case-sensitive. |
| `processId` | `int?` | auto | Target process id. |
| `durationSeconds` | `int` | `10` | Sampling window. ≥ 1. |
| `topN` | `int` | `25` | Top hotspots / blocking stacks / types. |
| `depth` | `SamplingDepth` | `Summary` | Verbosity; applies to `cpu` / `off_cpu`. Ignored by `allocation`. |
| `symbolPath` | `string?` | `null` | `cpu` / `off_cpu` only. Symbol search path; remote `srv*http(s)://…` segments are denied unless allowlisted (issue #165 / M3). |
| `resolveSourceLines` | `bool` | `true` | `cpu` only. Same as [`collect_sample(kind="cpu")`](#collect_sample(kind="cpu")). |
| `maxResolvedSources` | `int?` | `topN` | `cpu` only. |
| `resolveMethodInstantiations` / `maxResolvedMethodInstantiations` | — | — | `cpu` only. Same as `collect_sample(kind="cpu")`. |
| `nativeAotMapFile` | `string?` | `null` | `cpu` on NativeAOT only. Path to the ILC `*.map.xml` (`<IlcGenerateMapFile>true</IlcGenerateMapFile>`). Emits a name-based `MethodIdentity` (TypeFullName + MethodName; MVID/token `null`) for hot managed AOT methods so the `dotnet-native-mcp` disassembly handoff works. Ignored on CoreCLR. See [`aot-coverage.md`](./aot-coverage.md) and [`handoff-contract.md`](./handoff-contract.md#nativeaot-identity--name-based-issue-395). |
| `nativeAllocSamplePeriod` | `long` | `1000` | `native-alloc` on **Linux** only. Record one callchain per N allocator hits (throttles recorded samples, not the per-call uprobe trap cost). Ignored by the Windows ETW VirtualAlloc backend, which records every committed allocation. |
| `exportTrace` | `bool` | `false` | `cpu` only. When `true`, the raw `.nettrace` (normally deleted after parsing) is kept under `MCP_ARTIFACT_ROOT/traces/` and its relative path returned on the result. Fetch the bytes with `get_bytes(kind="trace")` for offline PerfView/Speedscope/Perfetto analysis. |

**Returns:** `CollectSampleEnvelope` — a polymorphic record carrying the
`kind` discriminator plus exactly one populated payload field
(`cpu` / `offCpu` / `allocation` / `nativeAlloc`). The envelope's `summary`, `hints`,
`handle`, `handleExpiresAt`, and `resolvedProcess` are passed through from
the underlying sampler verbatim, so `query_snapshot(view="call-tree")` and
`query_snapshot` drilldowns continue to work unchanged.

**Platform notes.** `kind="off_cpu"` requires Linux (`perf record -e sched_switch`)
or Windows admin (NT Kernel Logger ContextSwitch); on unsupported hosts the
unified tool returns the same `NotSupported` / `PermissionDenied` envelope the
legacy `collect_sample(kind="off_cpu")` returns. `kind="allocation"` works on CoreCLR
and NativeAOT, but on NativeAOT GCAllocationTick events carry an empty
TypeName — surfaced via the envelope summary so the LLM knows to fall back
to `kind="cpu"` for per-site attribution.

**`kind="native-alloc"` (issue #279, Phase 15 Windows parity #466).** Attributes
**native/unmanaged** allocations (off the GC heap — P/Invoke, native libraries, the
runtime itself) to a call site. Companion to `kind="allocation"`, which only sees the
managed GC heap. Two backends emit the **identical** call-tree handle:

- **Linux** uprobes the target's libc allocator (`malloc`/`calloc`/`realloc`) with
  `perf probe` + `perf record --call-graph dwarf`. Needs the `perf` binary plus permission
  to create a uprobe (`CAP_SYS_ADMIN` / tracefs write access — strictly more than off-CPU's
  `CAP_PERFMON`). The `nativeAllocSamplePeriod` knob throttles recorded callchains.
- **Windows** captures the NT Kernel Logger **`VirtualAlloc`** ETW provider with stack walks
  (the libc allocator's underlying OS commit path — what PerfView's "Net Virtual Alloc
  Stacks" view is built on). Needs administrative elevation / `SeSystemProfilePrivilege`;
  `nativeAllocSamplePeriod` is ignored (every committed allocation is recorded).

Both are gated by `inspect_process(view="capabilities")`'s `CanSampleNativeAlloc`.
**Hotspot-only:** counts are *allocator-call hits, not bytes*, and neither backend does
alloc/free retention matching — it shows who allocates most, not what leaks. Drill into the
merged call tree with `query_snapshot(view="call-tree")`; compare two windows with
`query_snapshot(view="diff")`. Escalate to it from `inspect_process(view="memory_trend")`
when RSS / anonymous pages climb while the managed heap stays flat. On an unsupported host
(e.g. macOS, or a Linux host without `perf`) the unified tool returns a structured
`NotSupported` envelope — never a crash; a missing `CAP_SYS_ADMIN` (Linux) or denied ETW
access (Windows) instead surfaces as `PermissionDenied`.

**Authorization.** Gated by `RequireScope("eventpipe")`, matching the three
legacy samplers verbatim.

---

## `collect_events(kind="counters")`

> **Deprecated — call [`collect_events`](#collect_events) with `kind="counters"`.**
> Behaviorally identical; will be removed in `0.7.0`.


Subscribes to one or more legacy EventCounter providers and, optionally, one or
more Meter names through `System.Diagnostics.Metrics`. Returns the latest
EventCounter value per counter plus the latest Meter time series / histogram
snapshot seen over a fixed window.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `5` | Collection window. Must be ≥ 1. |
| `providers` | `string[]?` | see below | Legacy EventCounter provider names. `null` uses defaults; `[]` disables legacy EventCounters. |
| `meters` | `string[]?` | `null` | Meter names forwarded to `System.Diagnostics.Metrics`. Null/empty disables Meter collection. |
| `intervalSeconds` | `int` | `1` | Refresh interval for both EventCounters and Meter aggregation. |
| `maxInstrumentTimeSeries` | `int` | `1000` | Max Meter time series / histograms retained before the collector caps results and emits a `Notes[]` warning. |

When `providers` is null the defaults are:
`System.Runtime`, `Microsoft.AspNetCore.Hosting`, `Microsoft-AspNetCore-Server-Kestrel`.

**Returns:** `CounterSnapshot`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:05",
  "counters": [
    {
      "provider": "System.Runtime",
      "name": "cpu-usage",
      "displayName": "CPU Usage",
      "value": 23.4,
      "unit": "%",
      "kind": "Mean"
    }
  ],
  "meters": [
    {
      "meter": "Microsoft.AspNetCore.Hosting",
      "instrument": "http.server.request.duration",
      "unit": "s",
      "kind": "Histogram<double>",
      "tags": {
        "method": "GET"
      },
      "lastValue": null,
      "rate": null,
      "histogram": {
        "count": 42,
        "sum": 1.84,
        "p50": 0.031,
        "p95": 0.084,
        "p99": 0.120
      }
    }
  ],
  "notes": [
    "TimeSeriesLimitReached: capped at 1000 series."
  ]
}
```

When Meter data is present, `SamplingDepth.Summary` keeps the headline EventCounters
and also includes `http.server.request.duration` p95 when available.

---

## `collect_sample(kind="cpu")`

> **DEPRECATED (0.9.0).** Call [`collect_sample(kind="cpu", …)`](#collect_sample) instead. The legacy tool remains registered and behaviorally identical during the deprecation window (issue #210).

Captures a CPU sample via the `Microsoft-DotNETCore-SampleProfiler` provider,
writes a temporary `.nettrace`, parses it with `TraceLog` and aggregates the
top-N hotspots by inclusive and exclusive sample counts.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `10` | Sampling window. ≥ 1. |
| `topN` | `int` | `25` | Maximum hotspots returned. ≥ 1. |
| `resolveSourceLines` | `bool` | `true` | Resolve top hotspots to source file:line via PDB / SourceLink. |
| `symbolPath` | `string?` | `null` | Optional symbol search path used when `resolveSourceLines=true`. **Remote symbol servers are denied by default** (issue #165 / M3): any `srv*http(s)://…` segment must point at a host listed under `Diagnostics:SymbolServerAllowlist`, otherwise the call fails with a `SymbolServerNotAllowed` envelope. Local paths always pass through. See [Security gates](#security-gates-b4). |
| `maxResolvedSources` | `int?` | `topN` | Cap on how many hotspots get source resolution. |
| `resolveMethodInstantiations` | `bool` | `false` | Opt-in ClrMD attach after sampling to recover closed generic method signatures for the hottest managed frames. CoreCLR only; on Linux requires `CAP_SYS_PTRACE` (or `ptrace_scope=0`) and briefly suspends the target. |
| `maxResolvedMethodInstantiations` | `int?` | `topN` | Cap on how many hotspots get ClrMD generic-instantiation enrichment. |
| `depth` | `SamplingDepth` | `Summary` | `Summary` returns the top 3 hotspots inline; `Detail` / `Raw` return the requested `topN`. |

**Returns:** `CpuSample`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalSamples": 4218,
  "topHotspots": [
    {
      "frame": { "module": "MyApi", "method": "MyApi.Service.DoWork(int)" },
      "inclusiveSamples": 1820,
      "exclusiveSamples": 320
    }
  ],
  "symbolSource": "ElfDemangled"
}
```

`symbolSource` is populated for **NativeAOT** samples only (see #35) and
reports the aggregate symbol-resolution quality of `topHotspots`:

- `ElfDemangled` — every managed frame went through the demangler. Trust the
  names as-is.
- `ElfMangled` — perf returned managed-looking symbols but demangling did not
  apply (e.g. lookup table missing). Names are still usable but may be `S_P_…`-style.
- `Native` — frames are non-managed (libc / P/Invoke / kernel). Expected for
  threadpool/GC threads.
- `Stripped` — perf returned `[unknown]` or raw addresses; names are not
  actionable. Likely missing build-id / PDB on the host.
- `Mixed` — quality varies across `topHotspots`. Inspect per-frame.
- `Unknown` / omitted — CoreCLR sample (the EventPipe path resolves managed
  names directly; this field does not apply).

**Signals.** CPU samples are reduced into ranked, diagnosis-agnostic
[signal groupings](#signal-grouping-layer) surfaced in the envelope's `signals[]`:
`cpu.self-time.concentration` (how concentrated on-CPU time is, and in which
frames) and — on the Resource path — `cpu.self-time.by-namespace` (which namespace
the self-time rolls up into, e.g. `System.Globalization` or
`System.Text.RegularExpressions`, without naming the cause). The same signals are
readable as the `signals://cpu-sample/{handle}` Resource, re-derived over the full
call tree.

**Routing.** `collect_sample(kind="cpu")` dispatches based on
`inspect_process(view="capabilities")`:

- **CoreCLR (Linux + Windows)** — EventPipe `SampleProfiler` over the
  diagnostic socket; managed frames carry the `(mvid, token)` handoff.
- **NativeAOT / Linux** — system-wide `perf record` (frames are native;
  managed names recovered from the AOT `.symbols.map` sidecar when present).
- **NativeAOT / Windows** — NT Kernel Logger `PerfInfo/SampledProfile` via
  ETW; admin elevation (or `SeSystemProfilePrivilege`) required. Frames are
  native; managed names recovered from the PE export table + PDB.

Confirm the dispatch path up front with `inspect_process(view="capabilities")` →
`data.canSampleCpu`. Coverage and AOT caveats are summarized in
[`aot-coverage.md`](./aot-coverage.md).

**NativeAOT/Linux perf install.** On Debian/Ubuntu/WSL the distro ships a
wrapper at `/usr/bin/perf` that fails unless the matching
`linux-tools-$(uname -r)` package is installed. The sampler auto-discovers a
working binary by probing `/usr/lib/linux-tools-*/perf` (kernel-matched first,
then newest-first); when nothing usable is found, `IsAvailable` returns false
and the tool reports `not_supported`. Install with:

```bash
sudo apt install linux-tools-$(uname -r) linux-tools-generic
```

**Sampling rate** is the runtime default (~1 kHz). A 10-second window typically
yields a few thousand samples; bump `durationSeconds` for sparse workloads.

**Long-running pattern:** this tool supports MCP Tasks (`execution.taskSupport:
"optional"`). Spec clients should use task-augmented `tools/call` + `tasks/get` /
`tasks/result`; for clients that don't implement Tasks, use the in-request
`notifications/progress` + `notifications/cancelled` flow described under
[MCP-native progress and cancellation](#mcp-native-progress-and-cancellation-issue-211).

## Symbol resolution

Tools that resolve external symbols now share the same precedence chain:

1. explicit tool parameter `symbolPath`
2. server startup env `MCP_SYMBOL_PATH`
3. host env `_NT_SYMBOL_PATH`
4. local fallback paths (typically the target `MainModule` directory; `collect_sample(kind="cpu")`
   also appends module directories discovered in the trace)

`symbolPath` values use TraceEvent / `SymbolReader`'s NT-style syntax on every OS.
Common examples:

- `srv*C:\\symbols*https://msdl.microsoft.com/download/symbols`
- `cache*/tmp/sym;srv*https://nuget.smbsrc.net`
- local PDB-only default: omit `symbolPath` and keep the PDB next to the target binary

The same override shape is exposed by `collect_sample(kind="cpu")`, `collect_sample(kind="off_cpu")`,
`collect_thread_snapshot`, `inspect_heap(source="dump")`, and `inspect_heap(source="live")`.

**Opt-in closed generics (`resolveMethodInstantiations`).** On Linux, EventPipe alone only knows the
open `MethodDef` for generic methods like `Echo<T>`. When you enable this flag, the server performs
an additional ClrMD attach after the trace ends, resolves the hottest instruction pointers back to
closed runtime methods, and stamps `MethodIdentity.ClosedSignature` plus
`MethodIdentity.GenericTypeArguments.Method`. This keeps the default EventPipe path lightweight while
making LINQ / MediatR / serializer hotspots far more operator-friendly when you explicitly need the
closed form.

---

## `collect_sample(kind="allocation")`

> **DEPRECATED (0.9.0).** Call [`collect_sample(kind="allocation", …)`](#collect_sample) instead. The legacy tool remains registered and behaviorally identical during the deprecation window (issue #210).

Captures allocation samples from the target process via `GCAllocationTick`
events from `Microsoft-Windows-DotNETRuntime` (keyword `GCKeyword=0x1`, level
Verbose). The GC fires this event roughly every **100 KB of total managed
allocations** and carries the TypeName of the most recently allocated object
plus a call stack. The call stack is accessible via `query_snapshot(view="call-tree")` using the
handle returned by this tool.

**CoreCLR**: TypeName is fully populated with managed type names. The call tree
resolves to managed method names via rundown events. `MethodIdentity` (MVID +
metadata token) is emitted for top-N frames, enabling the assembly-mcp handoff.

**NativeAOT**: `GCAllocationTick` events fire, but the runtime **does not**
populate the `TypeName` field — managed type metadata is stripped at compile
time. All events roll up under `<unknown>`. The call tree is captured but
contains native frame addresses only. See [`aot-coverage.md`](./aot-coverage.md)
for the full NativeAOT diagnostic matrix.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id (optional — auto-selects when only one .NET process is visible) |
| `durationSeconds` | `int` | `10` | Sampling window. Must be ≥ 1. |
| `topN` | `int` | `25` | Maximum types per ranked list. Must be ≥ 1. |

**Returns:** `AllocationSample` with a drilldown `handle`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalEvents": 14250,
  "totalBytes": 1469161472,
  "topByBytes": [
    { "typeName": "System.String", "totalBytes": 1400000000, "eventCount": 14000, "dominantKind": "Small" },
    { "typeName": "System.Byte[]", "totalBytes": 60000000, "eventCount": 200, "dominantKind": "Large" }
  ],
  "topByCount": [
    { "typeName": "System.String", "totalBytes": 1400000000, "eventCount": 14000, "dominantKind": "Small" }
  ]
}
```

`TopByBytes` ranks by total allocated bytes — the dominant signal for allocation
pressure. `TopByCount` ranks by sampling event count — useful when many small
types compete with one large-object type.

**Notes on sampling semantics:** `GCAllocationTick` is a sampled event, not
an instrumented one. It samples the *most recently allocated* type when the
total allocation counter crosses each 100 KB threshold. High-frequency types
are sampled proportionally more often, making the top-N ranking statistically
accurate for steady workloads.

**Run after** `collect_events(kind="counters")` shows elevated `gen-0-gc-count`,
`gen-1-gc-count`, or growing `gc-heap-size`. Use `query_snapshot(view="call-tree")` with the
returned handle to find which allocation sites are responsible.

---

## `collect_events(kind="exceptions")`

> **Deprecated — call [`collect_events`](#collect_events) with `kind="exceptions"`.**
> Behaviorally identical; will be removed in `0.7.0`.

Collects every exception thrown by the process during the window.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `maxRecent` | `int` | `100` | Maximum exception details to return |

**Returns:** `ExceptionSnapshot`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalExceptions": 42,
  "byType": [
    { "exceptionType": "System.InvalidOperationException", "count": 30 },
    { "exceptionType": "System.TimeoutException", "count": 12 }
  ],
  "recent": [
    {
      "timestamp": "2026-05-18T20:00:01.123Z",
      "exceptionType": "System.InvalidOperationException",
      "exceptionMessage": "Sequence contains no elements",
      "exceptionHResult": "0x80131509",
      "threadId": 17
    }
  ],
  "recentCap": 100
}
```

**Notes:** also catches "first-chance" exceptions caught by the app — useful
for detecting error rates much higher than the response logs suggest.

`totalExceptions` and `byType` are always exact for the window. `recent` is
capped to `maxRecent` (default `100`, echoed back as `recentCap`); when
`totalExceptions > recentCap` it contains the first `recentCap` exceptions
observed, not a random sample. Raise `maxRecent` for storms where the tail
matters; lower it when you only want a quick signal.

**Long-running pattern:** this tool supports MCP Tasks (`execution.taskSupport:
"optional"`). Spec clients should use task-augmented `tools/call` + `tasks/get` /
`tasks/result`. Clients that don't implement Tasks should use the in-request
`notifications/progress` + `notifications/cancelled` flow.

---

## `collect_events(kind="crash-guard")`

Starts a crash/unhandled-exception guard window. It subscribes to the runtime
exception keyword (including `ExceptionThrown_V1`) plus crash-adjacent runtime
events and returns early when the target process exits. Use it before triggering
a suspected fatal path, or during an incident where the process is about to die.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id |
| `durationSeconds` | `int` | `10` | Guard window; returns earlier if the process exits |
| `maxRecent` | `int` | `100` | Maximum exception events to retain |
| `depth` | `summary\|detail\|raw` | `Summary` | Summary keeps the final exception/headline inline; detail/raw include retained exceptions |

**Returns:** `CrashGuardSnapshot` with `processExited`, `exitCode`,
`unhandledExceptionObserved`, `finalException`, exact `byType` counts, retained
`exceptions[]`, and `notes[]`. The handle accepts:

- `query_snapshot(handle, view="summary")` — final exception + by-type counts.
- `query_snapshot(handle, view="exceptions")` — retained exception stream.
- `query_snapshot(handle, view="stack")` — managed stack for the final exception
  when the runtime/event payload exposed one.

When an unhandled exception is observed, the result emits a next-action hint
toward `collect_process_dump(dumpType="Mini")` so the LLM can correlate
exception type/message/stack with dump state. The dump tool still requires its
normal explicit confirmation before writing a dump file.

**Pairing with runtime-written crash dumps.** If the target is configured with
`DOTNET_DbgEnableMiniDump=1` (and companion `DOTNET_DbgMiniDumpType` /
`DOTNET_DbgMiniDumpName` when needed), the runtime may write a crash dump as the
process terminates. Use `collect_events(kind="crash-guard")` to capture the
exception stream and final managed stack, then correlate its `startedAt`,
`finalException.timestamp`, `processId`, and `exitCode` with the dump file name
or crash-report metadata. In that mode, `collect_process_dump` is optional: use
the runtime-written dump if it already exists, or follow the hint when the
process is still alive long enough for an explicit dump.

---

## `collect_events(kind="gc")`

> **Deprecated — call [`collect_events`](#collect_events) with `kind="gc"`.**
> Behaviorally identical; will be removed in `0.7.0`.


Subscribes to the runtime `GC` keyword, pairs `GCStart`/`GCStop` events and
returns aggregate + per-collection details.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `maxEvents` | `int` | `200` | Cap on individual GC events returned |

**Long-running pattern:** this tool supports MCP Tasks (`execution.taskSupport:
"optional"`). Spec clients should use task-augmented `tools/call`; clients that
don't implement Tasks should use the in-request `notifications/progress` +
`notifications/cancelled` flow.

**Returns:** `GcSummary`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalCollections": 18,
  "totalPauseTime": "00:00:00.0420000",
  "maxPauseTime": "00:00:00.0150000",
  "generations": [
    { "generation": 0, "count": 14 },
    { "generation": 1, "count": 3 },
    { "generation": 2, "count": 1 }
  ],
  "events": [
    {
      "timestamp": "2026-05-18T20:00:01.500Z",
      "generation": 0,
      "reason": "AllocSmall",
      "type": "NonConcurrentGC",
      "pauseDuration": "00:00:00.0021000"
    }
  ]
}
```

**Notes:** to capture a full gcdump (heap snapshot), use `collect_process_dump`
with `dumpType = "WithHeap"` and analyze offline with `dotnet-dump`.

---

## `collect_events(kind="activities")`

> **Deprecated — call [`collect_events`](#collect_events) with `kind="activities"`.**
> Behaviorally identical; will be removed in `0.7.0`.


Captures `ActivitySource` spans through the `Microsoft-Diagnostics-DiagnosticSource`
EventPipe bridge, keeping completed span records inline and grouped rollups behind
`query_snapshot`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `sources` | `string[]?` | `null` | Optional `ActivitySource` filters (`*` / `?` wildcards supported) |
| `durationSeconds` | `int` | `10` | Window length |
| `maxActivities` | `int` | `200` | Cap on captured span records retained inline + in the handle artifact |

**Returns:** `ActivityCapture`:

```json
{
  "processId": 12345,
  "sourceFilters": ["MyCompany.Checkout*"],
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalActivities": 12,
  "completedActivities": 12,
  "activities": [
    {
      "sourceName": "MyCompany.Checkout",
      "operationName": "POST /checkout",
      "id": "00-3b2dc9c6a0b7dc27ba8e290f198d98f4-9f10a33a49390375-01",
      "parentId": null,
      "traceId": "3b2dc9c6a0b7dc27ba8e290f198d98f4",
      "spanId": "9f10a33a49390375",
      "parentSpanId": null,
      "startedAt": "2026-05-18T20:00:00.120Z",
      "stoppedAt": "2026-05-18T20:00:00.188Z",
      "duration": "00:00:00.0680000",
      "tags": { "http.method": "POST", "db.system": "sqlserver" }
    }
  ],
  "bySource": [
    {
      "sourceName": "MyCompany.Checkout",
      "count": 12,
      "completedCount": 12,
      "averageDurationMs": 32.7,
      "maxDurationMs": 68.0
    }
  ],
  "byOperation": [
    {
      "sourceName": "MyCompany.Checkout",
      "operationName": "POST /checkout",
      "count": 12,
      "completedCount": 12,
      "averageDurationMs": 32.7,
      "maxDurationMs": 68.0
    }
  ]
}
```

**Drilldown:** `query_snapshot(handle, view="bySource" | "byOperation" | "activities")`
re-projects the same capture window without reopening EventPipe.

**Notes:**

- The collector listens to `Activity/Stop` bridge events, so every returned row is a
  completed span with duration + tags already populated.
- `sources` matches `ActivitySource.Name`, not operation names.
- The provider supports a single Activity listener per session; this tool claims it for
  the duration of the capture window.

---

## `collect_events(kind="logs")`

Collects a curated `ILogger` view from the `Microsoft-Extensions-Logging`
EventSource, keeping per-level counts, per-category rollups, a bounded recent
ring buffer, and redacted scope / exception detail when `depth != "Summary"`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `categories` | `string[]?` | `null` | Optional case-insensitive glob filters for logger categories |
| `minLevel` | `string` | `Information` | Minimum retained level: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` |
| `maxEvents` | `int` | `500` | Cap on retained recent log entries |
| `maxMessageBytes` | `int` | `4096` | Per-message / scope / exception UTF-8 truncation cap |
| `depth` | `SamplingDepth` | `Summary` | `Summary` drops `recent`; `Detail` / `Raw` also enable `MessageJson` for exception + scope detail |

**Returns:** `LogSnapshot` with:

- `totalEvents`
- `eventsByLevelTrace|Debug|Information|Warning|Error|Critical`
- `byCategory` (`LogCategoryGroup[]` sorted by count)
- `recent` (`LogEntry[]`, bounded by `maxEvents`)
- `truncated` + `notes`

`LogEntry` carries `timestamp`, `level`, `category`, `eventId`, `eventName`,
`message`, optional `exceptionType` / `exceptionMessage`, and optional redacted
`scopes`.

**Drilldown:** `query_snapshot(handle, view="summary" | "byCategory" | "byLevel" | "recent" | "errors")`.

**Notes:**

- `MessageJson` is enabled only when `depth != "Summary"` to reduce collector overhead.
- Messages and scope values always pass through `SensitiveDataRedactor` before they are retained.
- When `truncated=true`, the collector dropped oldest retained entries after `maxEvents`.

---

## `collect_events(kind="jit")`

Collects CLR JIT / tiered-compilation activity from `Microsoft-Windows-DotNETRuntime`,
reconstructing inclusive JIT time from `MethodJittingStarted` → `MethodLoadVerbose`
pairs and tracking Tier0 vs Tier1, ReadyToRun hits/miss-then-jit, ReJIT, OSR,
and IL-map counts.
## `collect_events(kind="threadpool")`

Collects a curated ThreadPool starvation view from the runtime `ThreadingKeyword`
(`Microsoft-Windows-DotNETRuntime`, `0x10000`): per-second worker + IOCP timelines,
hill-climbing transitions/reasons, best-effort effective min/max settings when the
runtime emits `ThreadPoolMinMaxThreadsChanged`, and top work-item origins when EventPipe
exposes enqueue call stacks.

## `collect_events(kind="contention")`

Collects a curated CLR lock-contention view from the runtime `Contention` keyword
(`Microsoft-Windows-DotNETRuntime`, `0x4000`). The collector pairs `ContentionStart`
/ `ContentionStop` events by contending thread, computes wait duration percentiles,
and groups the captured waits by contended call site and owner thread.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `depth` | `SamplingDepth` | `Summary` | `Summary` drops the raw event list inline; `Detail` / `Raw` keep the captured events inline |

**Returns:** `ContentionSnapshot` with:

- `totalEvents`, `distinctMonitors`
- `totalContentionDuration`, `p50ContentionDuration`, `p95ContentionDuration`, `maxContentionDuration`
- `events` (`ContentionEventSample[]` sorted by `duration` descending)
- `notes`

**Drilldown:** `query_snapshot(handle, view="summary" | "byCallSite" | "byOwner")`.

**Notes:**

- `byCallSite` is best-effort and depends on EventPipe call stacks being available in the session.
- On current Linux runtimes, `ContentionStart` / `ContentionStop` may not be emitted over EventPipe even when `monitor-lock-contention-count` rises; the collector surfaces that caveat in `notes` when the window is empty.


## `collect_events(kind="startup")`

Collects startup and cold-start contributors that are visible during an EventPipe
window: runtime loader events from `Microsoft-Windows-DotNETRuntime`
`LoaderKeyword` (`0x8`) and DependencyInjection events from
`Microsoft-Extensions-DependencyInjection`. Loader events include
`AssemblyLoad` / `AssemblyLoad_V1`, `ModuleLoad` / `ModuleLoad_V2`, and any
DC/load variants the runtime emits during the session. DI events are based on the
provider's current source (`ServiceProviderBuilt`, `ServiceProviderDescriptors`, `CallSiteBuilt`,
`ServiceResolved`, `ExpressionTreeGenerated`, `DynamicMethodBuilt`, and
`ServiceRealizationFailed`; older/newer runtimes may vary).

**Critical timing caveat:** attaching to an already-running process captures only
loader/DI events emitted **during the collection window**. Events before attach —
usually the most important part of initial cold-start — are missed. True
cold-start capture requires enabling EventPipe before or at process start via a
suspended/reverse-connect startup diagnostic port (for example `DOTNET_DiagnosticPorts`
with the `suspend` modifier). Attaching after launch — including the CLI `--launch`
child mode, which waits for the diagnostic endpoint to come up before collecting —
does **not** recover pre-attach events. The collector always includes this
caveat in `notes`; it does not pretend to recover pre-attach events.

JIT-at-startup is not duplicated here; use `collect_events(kind="jit")` for JIT
and tiered-compilation startup work. Static-constructor duration is not exposed
as a clean EventPipe signal in this collector, so it is documented in `notes`
rather than inferred.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `depth` | `SamplingDepth` | `Summary` | `Summary` keeps headline counts and short loader/DI slices inline; `Detail` / `Raw` keep the captured lists inline |

**Returns:** `StartupSnapshot` with assembly/module load counts, DI event counts,
observed DI activity span, loader event lists, DI event list, merged timeline,
and explanatory notes.

**Drilldown:** `query_snapshot(handle, view="summary" | "assemblies" | "modules" | "di" | "timeline")`.

## `collect_events(kind="db")`

Collects a curated database view by combining EF Core command activities with
SqlClient command/pool telemetry. The collector groups commands by
`(CommandTextHash, ConnectionStringSanitized)`, computes `count`, `totalMs`,
`maxMs`, `p95Ms`, flags N+1 patterns when the same command repeats more than 10
 times under the same parent activity / trace, and snapshots SqlClient pool
 counters when available.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `depth` | `SamplingDepth` | `Summary` | `Summary` keeps only the hottest 10 methods inline; `Detail` / `Raw` return every observed method row |

**Returns:** `JitSnapshot` with:

- `jitStartCount`, `completedCompilations`, `uniqueMethods`
- `distribution` (`tier0`, `tier1`, `readyToRun`, `r2rHit`, `r2rMissThenJit`)
- `reJitCount`, `osrCount`, `ilMapCount`, `r2rLookupCount`
- `tier1Percent`, `r2rHitRatePercent`, `healthCheck`
- `methods` (`JitMethodSummary[]` sorted by `inclusiveJitTimeMs` descending)
- `notes`

`JitMethodSummary` carries `methodNamespace`, `methodName`, `methodSignature`,
`displayName`, `inclusiveJitTimeMs`, `compilationCount`, `lastOptimizationTier`,
per-tier counts, `reJitCount`, `osrCount`, and `hasIlMap`.

**Drilldown:** `query_snapshot(handle, view="summary" | "topMethods" | "tierDistribution" | "reJIT")`.

**Notes:**

- The collector enables the runtime's JIT + JIT tracing keywords **plus** IL-map / compilation-diagnostic keywords so ReadyToRun lookup and IL-map events are visible in the same window.
- `R2R hit rate` is computed over all observed `r2rLookupCount` lookups; `R2RMissThenJit` remains a separate correlation metric for misses that fell back to JIT within the same window.
- `OSR` is surfaced from `OptimizationTier=OptimizedTier1OSR` on `MethodLoadVerbose`.
| `depth` | `SamplingDepth` | `Summary` | `Summary` keeps headline counts + top origins inline; `Detail` / `Raw` keep full timelines + hill-climbing samples inline |

**Returns:** `ThreadPoolEventSnapshot` with:

- `workerThreadTimeline` / `iocpThreadTimeline`
- `hillClimbing` (`ThreadPoolHillClimbingSample[]`)
- `workItemOrigins` (`ThreadPoolWorkItemOrigin[]`)
- `effectiveSettings` (`workerMinThreads`, `workerMaxThreads`, `iocpMinThreads`, `iocpMaxThreads`) when the runtime emits `ThreadPoolMinMaxThreadsChanged`
- `totalEnqueueEvents` / `totalDequeueEvents`
- `notes`

**Drilldown:** `query_snapshot(handle, view="summary" | "timeline" | "hillClimbing" | "workItemOrigins")`.

**Notes:**

- The runtime does not always publish named ThreadPool adjustment payloads on every platform; when that happens the collector annotates `notes` and infers the transition reason from the timing / direction of worker growth.
- Work-item origins require EventPipe call stacks on `ThreadPoolEnqueueWork`; when stacks are unavailable the collector returns a note and leaves `workItemOrigins` empty.
- Effective min/max counts are best-effort: the collector stays EventPipe-only and fills `effectiveSettings` only when the runtime emits `ThreadPoolMinMaxThreadsChanged`; otherwise it falls back to a note and points callers at `collect_thread_snapshot(view="threadpool")` for a ptrace-backed snapshot.
| `intervalSeconds` | `int` | `1` | Refresh interval requested from SqlClient EventCounters |
| `depth` | `SamplingDepth` | `Summary` | `Summary` keeps only the top command/N+1 slices inline; `Detail` / `Raw` keep the full capture |

**Returns:** `DbSnapshot` with:

- `totalCommands`
- `byCommand` (`DbCommandAggregate[]` with `commandTextHash`, sanitized SQL,
  sanitized connection string, `count`, `totalMs`, `maxMs`, `p95Ms`)
- `nPlusOne` (`DbNPlusOneIncident[]`)
- `connectionPool` (`DbConnectionPoolStats[]`)
- `notes`

**Drilldown:** `query_snapshot(handle, view="summary" | "byCommand" | "n+1" | "connectionPool")`.

**Notes:**

- `SensitiveDataRedactor` redacts connection-string secrets and inline SQL
  literal values before the snapshot is retained.
- SqlClient pool stats depend on provider support; when the target only emits EF
  activities the `connectionPool` slice may be empty.

## `collect_events(kind="kestrel")`

Collects a curated Kestrel HTTP-server view by subscribing to the
`Microsoft-AspNetCore-Server-Kestrel` EventSource. The collector pairs
connection / request / TLS-handshake start+stop events to compute request and
TLS latency percentiles and connection durations, tracks the
`connection-queue-length` and `request-queue-length` EventCounters over the
window to localize head-of-line blocking, and captures the live
`KestrelServerOptions` JSON emitted by the `Configuration` event when the
session is enabled (TLS, limits, keep-alive, HTTP protocol versions).

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `intervalSeconds` | `int` | `1` | Refresh interval requested from Kestrel EventCounters |
| `depth` | `SamplingDepth` | `Summary` | `Summary` trims the by-operation list and drops the queue timeline + config JSON inline; `Detail` / `Raw` keep the full capture |

**Returns:** `KestrelSnapshot` with:

- `connectionsStarted` / `connectionsStopped` / `connectionsRejected`
- `requestsStarted` / `requestsStopped`
- `tlsHandshakesStarted` / `tlsHandshakesStopped` / `tlsHandshakesFailed`
- `peakConnectionQueueLength` / `peakRequestQueueLength`
- request latency `requestP50` / `requestP95` / `requestMax`
- TLS latency `tlsHandshakeP50` / `tlsHandshakeP95` / `tlsHandshakeMax`
- connection duration `connectionDurationP50` / `connectionDurationP95` / `connectionDurationMax`
- `counters` (`KestrelCounterSample[]`), `queuePoints` (`KestrelQueuePoint[]`)
- `byOperation` (`KestrelRequestGroup[]` keyed by HTTP method + path + version)
- `tlsProtocols`, `configurationJson`, `notes`

**Drilldown:** `query_snapshot(handle, view="summary" | "byOperation" | "queues" | "tls" | "config")`.

**Notes:**

- The `Configuration` event fires once when the EventPipe session is enabled, so
  `configurationJson` reflects the server options at the moment of collection.
- When no traffic flows during the window the collector returns a note and empty
  aggregates — start the session **before** the load you want to observe.

---

## `collect_events(kind="networking")`

Collects a curated outbound-networking view by subscribing to the stable .NET
networking EventSources: `System.Net.Http` (HttpClient request lifecycle,
connection pool, time-in-queue), `System.Net.NameResolution` (DNS),
`System.Net.Security` (TLS handshakes) and `System.Net.Sockets` (socket
connects). Request / DNS / TLS Start and Stop events are paired by EventSource
activity id to compute latency percentiles, time-in-queue is read directly from
`RequestLeftQueue`, outbound HTTP is grouped by `scheme://host:port` + method,
and each provider's EventCounters are snapshotted.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `intervalSeconds` | `int` | `1` | Refresh interval requested from the networking EventCounters |
| `depth` | `SamplingDepth` | `Summary` | `Summary` keeps only the top by-operation slice inline; `Detail` / `Raw` keep the full by-operation list |

**Returns:** `NetworkingSnapshot` with:

- HTTP: `httpRequestsStarted`/`Stopped`/`Failed`, `httpConnectionsEstablished`/`Closed`,
  `httpRequestsLeftQueue`, `httpRequestP50`/`P95`/`Max`, `timeInQueueP50`/`P95`/`Max`
- DNS: `dnsLookupsStarted`/`Stopped`/`Failed`, `dnsP50`/`P95`/`Max`
- TLS: `tlsHandshakesStarted`/`Stopped`/`Failed`, `tlsP50`/`P95`/`Max`, `tlsProtocols`
- Sockets: `socketConnectsStarted`/`Stopped`/`Failed`
- `counters` (`NetworkingCounterSample[]`), `byOperation` (`NetworkingHttpGroup[]`), `notes`

**Drilldown:** `query_snapshot(handle, view="summary" | "byOperation" | "queue" | "tls" | "dns")`.

**Notes:**

- Latency percentiles are best-effort: when Start/Stop events cannot be
  correlated by activity id in the window the counts are still reported and a
  note explains the gap.
- Rising `timeInQueue` (the `queue` view) is the #1 outbound-HTTP saturation
  signal — it means requests are waiting for a free pooled connection.

---

## `collect_events(kind="requests")`

Enumerates the **in-flight** ASP.NET Core requests — the ones that started but
had not finished when the collection window closed. This is the first move for
*"the app is hung, what's it doing?"*: counters expose a *current-requests*
number, but this kind lists **which** requests are stuck, with path, verb,
elapsed time and trace-id, sorted oldest-first and flagging long-runners.

The collector subscribes to the `Microsoft.AspNetCore.Hosting HttpRequestIn`
Activity start/stop pairs through the `Microsoft-Diagnostics-DiagnosticSource`
EventPipe bridge; a request observed as *started* but never *stopped* within the
window is reported as in-flight, with `elapsedMs` measured from its start to the
moment the window closed. It is **pure EventPipe — no `ptrace`** — so it is safe
to run against a hung production process.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `longRunningThresholdMs` | `double` | `1000` | Elapsed-time threshold above which an in-flight request is flagged `isLongRunning` |
| `maxRequests` | `int` | `100` | Cap on in-flight requests returned inline (oldest-first); the full set stays behind the handle |
| `depth` | `SamplingDepth` | `Summary` | `Summary` keeps only the oldest requests inline; `Detail` / `Raw` keep the full captured list |

**Returns:** `InFlightRequestSnapshot` with:

- `requestsStarted` / `requestsCompleted`
- `inFlightCount` / `longRunningCount` / `longRunningThresholdMs` / `oldestElapsedMs`
- `requests` (`InFlightRequest[]`: `traceId`, `spanId`, `method`, `path`, `startedAt`, `elapsedMs`, `isLongRunning`)
- `notes`

**Drilldown:** `query_snapshot(handle, view="summary" | "requests" | "longRunning")`.

**Notes:**

- EventPipe sessions take ~500 ms–1 s to start; begin collection **before** (or
  while) the stall is happening so the slow request's start event is captured.
- Status code is only known when a request *completes*, so it is intentionally
  not reported for in-flight requests.
- For the **live thread stack** behind a stuck request (what line it is blocked
  on), follow up with [`inspect_process(view="requests-now")`](#inspect_process(view="requests-now")),
  which adds ClrMD-backed stacks and **requires the `ptrace` scope**. This kind
  is the prod-safe, attach-free counterpart.

---

## `collect_events(kind="event_source")`

> **Deprecated — call [`collect_events`](#collect_events) with `kind="event_source"`.**
> Behaviorally identical; will be removed in `0.7.0`.


Generic passthrough that opens an EventPipe session for any EventSource by
name and captures the events it emits in the window. Use for HTTP activity
(`System.Net.Http`), Kestrel/Hosting/Logging events, or app-defined sources.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `providerName` | `string` | — | EventSource provider name. **Must be on the curated allowlist** (issue #165 / M2) — see [Security gates](#security-gates-b4); the deny path returns an `EventSourceProviderNotAllowed` envelope listing the curated set. |
| `durationSeconds` | `int` | `10` | Window length |
| `keywords` | `long` | `-1` | Keyword mask. `-1` = all (clamped to `0` for opt-in non-allowlisted providers when left at `-1`). |
| `eventLevel` | `int` | `5` | 0=LogAlways…5=Verbose (clamped to `4` for opt-in non-allowlisted providers when left above `4`). |
| `maxEvents` | `int` | `200` | Cap on captured events |
| `unsafeProvider` | `bool` | `false` | Opt-in for non-allowlisted providers (issue #165 / M2). Only honoured when the server has `Diagnostics:AllowSensitiveHeapValues=true`. |

**Returns:** `EventSourceCapture`:

```json
{
  "processId": 12345,
  "provider": "System.Net.Http",
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalEvents": 128,
  "events": [
    {
      "timestamp": "2026-05-18T20:00:00.500Z",
      "provider": "System.Net.Http",
      "eventName": "RequestStart",
      "level": "Informational",
      "payload": { "scheme": "https", "host": "api.example.com", "port": "443" }
    }
  ]
}
```

**Tips:**

- `System.Net.Http` — outbound HTTP request/response timing
- `Microsoft.AspNetCore.Hosting` — request pipeline events
- `Microsoft-AspNetCore-Server-Kestrel` — connection lifecycle
- `Microsoft-Extensions-Logging` — structured app logs flowing through ILogger

---

## `inspect_heap`

Inspects a managed heap and returns the top retained types plus optional
retention paths, roots, static-field owners, delegate targets, and duplicate
strings. Registers a `heap-snapshot` drilldown handle so follow-up questions go
through [`query_snapshot`](#query_snapshot) without re-walking the heap.

**Backend discriminator (`source`, required):**

| `source` | Backend | ptrace / dump | Notes |
|---|---|---|---|
| `live` | ClrMD attach to a running process | needs `CAP_SYS_PTRACE` on Linux | suspends the target for the walk |
| `dump` | Offline walk of a captured `.dmp` | neither | `dumpFilePath` required |
| `gcdump` | GC heap snapshot over EventPipe | neither — **production-safe** | CoreCLR only; NativeAOT returns a friendly `NotSupported` (issue #471) |

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `source` | `string` | — | `live` \| `dump` \| `gcdump`. See table above |
| `processId` | `int?` | auto-select | Required for `source="live"` (auto-resolved when one .NET process is reachable); forbidden for `source="dump"` |
| `dumpFilePath` | `string?` | — | Absolute path to a captured `.dmp`. Required for `source="dump"`; forbidden for `source="live"` |
| `topTypes` | `int` | `20` | Types returned in each top-N (bytes / instances) list |
| `includeRetentionPaths` | `bool` | `false` | Walk a short GC retention chain for the top types (slower; lengthens the live suspend window) |
| `retentionPathLimit` | `int` | `8` | Retention-chain depth cap when retention paths are enabled |
| `includeStaticFields` | `bool` | `false` | Rank loaded types' static reference fields by referenced size — surfaces "singleton grew forever" leaks |
| `includeDelegateTargets` | `bool` | `false` | Group `MulticastDelegate` invocation lists by (target type, method) — surfaces "event handler never unsubscribed" leaks |
| `includeDuplicateStrings` | `bool` | `false` | Hash every `System.String` and rank by aggregate retained bytes — surfaces missing interning |
| `symbolPath` | `string?` | — | NT_SYMBOL_PATH-style search path. Remote symbol servers are **off by default** (issue #165) — `srv*http(s)://…` must be on `Diagnostics:SymbolServerAllowlist` |
| `exportTrace` | `bool` | `false` | `source="gcdump"` only. Persist the raw `.nettrace` under the artifact root and return its relative path for `get_bytes(kind="trace")` |

**Returns:** a `HeapInspectionResult` summary plus a `heap-snapshot` `handle`
(~10 min TTL). Drill further via [`query_snapshot`](#query_snapshot) with any of
the heap views: `top-types`, `retention-paths`, `roots-by-kind`,
`finalizer-queue`, `fragmentation`, `static-fields`, `delegate-targets`,
`duplicate-strings`, `gchandles`, `timers`, `alc`, `object`, `gcroot`, `objsize`,
`async`, `diff`, `growth`.

**Scope:** `heap-read`. `source="live"` additionally requires the runtime
`ptrace` scope on the bearer (root/wildcard tokens satisfy it; dedicated bearers
must hold the literal `ptrace` scope). **Requires:** `source="live"` needs
`CAP_SYS_PTRACE` on Linux; `source="gcdump"` requires a CoreCLR target (NativeAOT
is refused, not crashed).

---

## `collect_thread_snapshot`

Captures managed thread states plus the SyncBlock lock graph (holder address,
owning thread, waiter count) from a live process or a dump. Returns the top-3
blocked threads inline plus a `thread-snapshot` `handle` (~10 min TTL) for
deadlock / unique-stack / wait-chain drilldown. Dump-origin handles are **not**
evicted when the producer PID exits.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto-select | Live PID. Mutually exclusive with `dumpFilePath`; auto-selects when both are null |
| `dumpFilePath` | `string?` | — | Path to a captured `.dmp`. Mutually exclusive with `processId` |
| `maxFramesPerThread` | `int` | `64` | Max stack frames captured per thread |
| `includeRuntimeFrames` | `bool` | `false` | Include PInvoke trampolines / runtime frames with no managed method |
| `includeNativeFrames` | `bool` | `false` | Include pure native frames ClrMD cannot resolve |
| `symbolPath` | `string?` | — | NT_SYMBOL_PATH-style path (same remote-server allowlist rule as `inspect_heap`) |
| `depth` | `string` | `summary` | `summary` (top-3 blocked, no lock graph) \| `detail` (top-25 threads + top-25 locks) \| `raw` (= detail). The full snapshot is always retained behind the handle |

**Returns:** `ThreadSnapshotQueryResult` + `thread-snapshot` handle. Drill via
[`query_snapshot`](#query_snapshot) thread views: `threads-summary`, `stack`,
`lock-graph`, `deadlocks`, `top-blocked`, `unique-stacks`, `async-stalls`,
`wait-chains`, `threadpool`, `resolve-address`, `frame-vars`.

**Scope:** `ptrace`. **Requires:** live attach needs `CAP_SYS_PTRACE` on Linux.

---

## `query_snapshot`

The single **drilldown surface**. Every collector that captures a reusable
artifact (heap, thread, off-CPU, event collection, CPU/allocation/native-alloc
sample) registers a handle in the shared handle store; `query_snapshot` answers
parameterized follow-up questions against that handle without re-paying the
collection cost. It replaces the five legacy per-family query tools
(`query_heap_snapshot`, `query_thread_snapshot`, `query_off_cpu_snapshot`,
`query_collection`, `get_call_tree`) behind one `(handle, view)` contract; the
dispatcher reads the artifact kind and forwards to the matching implementation so
response envelopes stay byte-identical (asserted by
`QuerySnapshotCompatibilityTests`).

**Core parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `handle` | `string` | — | Drilldown handle from a prior collector |
| `view` | `string?` | per-kind default | Kind-specific view (catalog below). Omit for the kind's default |
| `topN` | `int?` | 50 heap/thread/collection, 25 off-CPU | Max entries in a ranked-list view |

**View catalog (by handle kind):**

- **heap** (`inspect_heap`): `top-types` (default), `retention-paths`,
  `roots-by-kind`, `finalizer-queue`, `fragmentation`, `static-fields`,
  `delegate-targets`, `duplicate-strings`, `gchandles`, `timers`, `alc`,
  `object`, `gcroot`, `objsize`, `async`, `diff`, `growth`.
- **thread** (`collect_thread_snapshot`): `top-blocked` (default),
  `threads-summary`, `stack`, `lock-graph`, `deadlocks`, `unique-stacks`,
  `async-stalls`, `wait-chains`, `threadpool`, `resolve-address`, `frame-vars`.
- **off-CPU** (`collect_sample(kind="off_cpu")`): `topStacks` (default),
  `byThread`, `stack`.
- **collection** (`collect_events(kind=…)`): `summary` (default), plus
  per-kind views such as `byProvider`, `byType`, `exceptions`, `pauseHistogram`,
  `byGeneration`, `heap-stats`, `n+1`, `connectionPool`, `queues`, `dns`,
  `config`, `timeline`, `hillClimbing`, `requests`, `longRunning`, …
- **cpu-sample / allocation-sample / native-alloc-sample**: `call-tree`
  (default), `top-methods`, `by-module`, `by-namespace`, `hot-path`,
  `caller-callee`, `diff`.

**Common view-specific parameters** (each ignored outside its view):
`rankBy` (`bytes`/`instances`), `typeFullName`, `address`,
`includeSensitiveValues`, `threadId`, `framesToHash`, `minCount`, `stackRank`,
`rootMethodFilter`, `providerFilter`, `changesOnly`, `maxDepth`, `maxNodes`,
`baselineHandle`, `comparisonHandles`, `minDeltaPct`, `depth`, `mode`,
`hotPathThresholdPercent`. See the tool's parameter descriptions for the exact
view→parameter mapping.

**Authorization.** The static gate accepts any drilldown-capable bearer; after
resolving the handle kind the tool re-applies the exact legacy scope at runtime
(heap → `heap-read`, thread → `ptrace`, off-CPU → `eventpipe`, call-tree →
`investigation-export`, collections → `read-counters`/`eventpipe`). Unknown
handle kinds, unknown views, and parameter-shape violations return structured
`InvalidArgument` / `UnsupportedHandleKind` / `HandleExpired` envelopes — never a
500.

---

## `collect_process_dump`

Writes a process dump to disk via the diagnostic IPC channel.

> **Human approval is required (defense in depth — [authorization](./authorization.md#per-call-confirmation)).**
> Approval is obtained one of two ways, depending on the client's negotiated capabilities:
>
> 1. **Native MCP Elicitation (preferred).** When the client advertised the
>    `elicitation` capability at initialize, the server **always** issues an
>    `elicitation/create` request describing the dump that *would* be written
>    (PID, dump type, output path, disk-cost / heap-contents warning) and a single
>    boolean `approve` field. The dump is written only on an explicit approve —
>    even if the caller also passed `confirm=true`; a decline writes nothing and
>    returns an `approval_declined` envelope that does **not** invite a retry.
>    `confirm=true` cannot bypass a human decline on a capable client.
> 2. **`confirm=true` fallback.** Clients that did **not** negotiate elicitation
>    keep the legacy two-call contract: without `confirm=true` the tool returns a
>    `{ "kind": "confirmation_required", ... }` envelope (`targetPid`, `dumpType`,
>    `outputDirectory`) and writes nothing; surface the preview to a human and
>    re-issue with `confirm=true` after approval.
>
> The `dump-write` + `ptrace` scopes are still required on top of approval. Fallback
> two-call pattern (non-elicitation client):
>
> ```text
> # 1. Preview — no dump written.
> collect_process_dump(processId=12345, dumpType="WithHeap")
> # → { "kind": "confirmation_required", "targetPid": 12345, "dumpType": "WithHeap", ... }
>
> # 2. Surface the preview to a human, then re-issue with confirm=true.
> collect_process_dump(processId=12345, dumpType="WithHeap", confirm=true)
> # → { "kind": "dump_written", "dump": { "filePath": "...", ... } }
> ```

> **Sandbox (issue #163).** `outputDirectory` is interpreted as a **relative**
> sub-path under the operator-configured artifact root. The root is set by the
> `MCP_ARTIFACT_ROOT` environment variable (default
> `{TempPath}/dotnet-diagnostics-mcp`). Absolute paths, `..` traversal, and
> symlink escapes are rejected with a structured `InvalidArtifactPath` error.
> Files are written with POSIX mode `0600`; the parent directory is `0700`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `dumpType` | `string` | `"Mini"` | `Mini` / `Triage` / `WithHeap` / `Full` |
| `outputDirectory` | `string?` | artifact root | **Relative** sub-path under `MCP_ARTIFACT_ROOT`. Must not be absolute. |
| `confirm` | `bool` | `false` | Approval fallback for clients **without** the MCP elicitation capability. **Required `true` to write the dump when elicitation is unavailable.** Elicitation-capable clients are **always** prompted natively and this flag is ignored for them (it cannot bypass a human decline). See [authorization](./authorization.md#per-call-confirmation). |

**Returns:** `DumpToolResult` — a discriminated envelope:

```json
// confirm=false (default) — no file written:
{
  "kind": "confirmation_required",
  "message": "collect_process_dump writes a heap dump to disk. Pass confirm=true to proceed.",
  "targetPid": 12345,
  "dumpType": "Mini",
  "outputDirectory": "dumps/oncall-20260518"
}

// confirm=true — file written:
{
  "kind": "dump_written",
  "targetPid": 12345,
  "dumpType": "Mini",
  "outputDirectory": "dumps/oncall-20260518",
  "dump": {
    "processId": 12345,
    "dumpType": "Mini",
    "filePath": "/tmp/dotnet-diagnostics-mcp/dumps/oncall-20260518/dump_pid12345_Mini_20260518T200000Z.dmp",
    "fileSizeBytes": 28311552,
    "createdAt": "2026-05-18T20:00:00Z"
  }
}
```

**Cost / size:**

| Type | Approx. size for a 200 MB workload | Use when |
|---|---|---|
| `Mini` | ~30 MB | crash triage, thread state |
| `Triage` | ~30 MB | minimal, strings stripped |
| `WithHeap` | full workload + heap (200+ MB) | leak/heap investigation |
| `Full` | largest | last resort, full address space |

**Side effects:** **writes to disk** on the server. In a sidecar topology the
file lives on the sidecar container's filesystem — mount a PVC if you expect
to capture more than transient dumps.

## `capture_method_bytes`

Reads the JIT-emitted (or ReadyToRun-baked) native machine code for a single
managed method out of a live .NET process (or `WithHeap`/`Full` dump) and
writes the raw bytes to a file on disk. Closes the only disasm coverage gap:
NativeAOT and R2R binaries live on disk and are already covered by
`dotnet-native-mcp`; JIT-emitted code lives only in the target process memory.

The bytes are emitted via a **file side-channel** (mirroring `collect_process_dump`)
so binary payloads never enter the LLM context. Each captured region returns a
`NextActionHint` for `dotnet-native-mcp.disassemble(rawBlob=true)` carrying the
file path, size, architecture and load-base — feed that hint verbatim to
disassemble.

**Backend:** ClrMD `HotColdInfo`. **Requires:** CoreCLR target (NativeAOT
returns an error envelope — use `dotnet-native-mcp.load_native_binary` against
the binary on disk instead). On Linux also requires `CAP_SYS_PTRACE` for live
attach.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `moduleVersionId` | `string` (GUID) | — | MVID of the method's declaring module (from a sampler hotspot's `MethodIdentity`) |
| `metadataToken` | `string` | — | MethodDef token (`0x06000123` or decimal) |
| `processId` | `int?` | auto-select | Live PID. Mutually exclusive with `dumpFilePath` |
| `dumpFilePath` | `string?` | — | Path to a `WithHeap`/`Full` dump. Mutually exclusive with `processId` |
| `codeAddress` | `string?` | — | Optional native IP (hex or decimal) for the fast `GetMethodByInstructionPointer` path; verified against `(mvid, token)` |
| `tier` | `string?` | — | Informational label (`Tier0`/`Tier1`/etc.) echoed into the output file name. ClrMD does not expose tier metadata, so this is **not** a filter |
| `outputDirectory` | `string?` | `method-bytes/{pid}` | **Relative** sub-path under `MCP_ARTIFACT_ROOT` (default `{TempPath}/dotnet-diagnostics-mcp`). Same sandbox rules as `collect_process_dump`: absolute paths, `..` traversal, and symlink escapes are rejected with `InvalidArtifactPath`. `.bin` files are written `0600`. |

**Returns:** `CapturedMethodBytes`:

```json
{
  "origin": "Live",
  "processId": 12345,
  "runtimeName": "coreclr",
  "runtimeVersion": "10.0.0",
  "architecture": "X64",
  "method": { "moduleVersionId": "…", "metadataToken": 100663297, "methodName": "…", "typeFullName": "…" },
  "regions": [
    { "filePath": "/tmp/…/My.Type.Method-Hot--0x06000001.bin", "size": 412, "baseAddress": 140234567890, "architecture": "X64", "region": "Hot", "tier": null, "compilationType": "Jit" }
  ],
  "outputDirectory": "/tmp/…",
  "warnings": []
}
```

**Handoff:** every region carries a `NextActionHint` for
`dotnet-native-mcp.disassemble` with `imagePath`, `rawBlob: true`, `rva: 0`,
`size`, `architecture` and `baseAddress` — pass those through unchanged.

**Side effects:** writes one `.bin` file per region (Hot, plus Cold when the
JIT split the method). Suspend window on live attach is typically < 100 ms.
**NativeAOT/R2R targets are rejected** with an explanatory error envelope.

## `get_bytes`

**Successor to `get_bytes(kind="module")` + `get_bytes(kind="dump")`.** Single
byte-fetch entrypoint that dispatches on a `kind` discriminator:

- `kind: "module"` — same shape as the legacy `get_bytes(kind="module")`. Required
  `moduleVersionId`; optional `asset` (`"pe"`/`"pdb"`), `processId`.
- `kind: "dump"` — same shape as the legacy `get_bytes(kind="dump")`. Required
  `dumpFilePath` (under `MCP_ARTIFACT_ROOT`).
- `kind: "trace"` — streams a raw `.nettrace` exported by `collect_sample(kind="cpu", exportTrace=true)`
  or `inspect_heap(source="gcdump", exportTrace=true)`. Required `traceFilePath`
  (under `MCP_ARTIFACT_ROOT`); identical validation/chunking to `kind="dump"`.
- `kind: "list"` — read-only inventory of every artifact under `MCP_ARTIFACT_ROOT`
  (recursive, newest first). Returns `{ root, count, totalSizeBytes, artifacts[] }`
  where each entry has `relativePath`, `absolutePath`, `sizeBytes`, `lastModifiedUtc`,
  `ageSeconds`. Use it to find dumps/traces to prune.
- `kind: "delete"` — removes a single artifact named by `artifactPath` (relative to
  `MCP_ARTIFACT_ROOT`; `..`, absolute, and symlink escapes rejected with
  `InvalidArtifactPath`). Returns the deleted artifact's metadata. **Requires the
  literal `delete-artifact` scope** in addition to `module-bytes-read`.

Both branches share `offset` / `maxBytes` and return the same
`ByteFetchEnvelope` documented below. Unknown `kind` returns a structured
`InvalidArgument` error envelope listing the allowed values — never throws.

> **Scope:** `module-bytes-read` (literal modifier — same enforcement as the
> legacy tools). `kind="delete"` additionally requires the literal `delete-artifact`
> scope; root/`*` does not auto-grant it.
>
> **Artifact TTL reaper.** A background reaper prunes artifacts older than
> `MCP_ARTIFACT_TTL_HOURS` (default 24h; `0`/negative disables it) so a sidecar doing
> repeated WithHeap dumps does not fill `/tmp`. `kind="delete"` is the manual override.

The legacy `get_bytes(kind="module")` and `get_bytes(kind="dump")` entrypoints remain available
during the deprecation window and emit byte-for-byte identical envelopes — see
`GetBytesCompatibilityTests` for the asserted contract.

## `get_bytes(kind="module")`

Streams a loaded managed module's PE or PDB in repeated `CallTool` chunks so a
client-side sibling MCP can materialize the bytes locally in orchestrator mode.
The tool resolves the module by **MVID** inside a live process, then returns a
`ByteFetchEnvelope` carrying the full-artifact SHA-256, the current chunk, and a
`NextActionHint` for the follow-up `offset` call when more bytes remain.

> **Scope:** `module-bytes-read` is a **literal modifier scope**. A root/`*`
> bearer passes the outer `[RequireScope]` gate but is still rejected in-method
> unless the token literally carries `module-bytes-read`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `moduleVersionId` | `string` (GUID `D`) | — | MVID of the loaded module to stream |
| `asset` | `string` | `"pe"` | `"pe"` or `"pdb"` |
| `offset` | `long` | `0` | Chunk start offset |
| `maxBytes` | `int` | `4_194_304` | Requested chunk size; capped at `16 MiB` |
| `processId` | `int?` | auto-select | Live PID. Omit to use the normal resolver |

**Returns:** `ByteFetchEnvelope`:

```json
{
  "kind": "module",
  "asset": "pe",
  "identifier": "6f5c9bf0-1e0b-4f3b-9a8e-...",
  "sourcePath": "/app/MyService.dll",
  "totalSize": 1835008,
  "sha256": "4d9d...",
  "offset": 0,
  "chunkSize": 4194304,
  "base64Chunk": "TVqQ...",
  "nextOffset": 4194304,
  "companionPdbPath": "/app/MyService.pdb",
  "pdbIsEmbedded": null,
  "processId": 12345
}
```

**When to use:** cross-MCP handoff in orchestrator mode when `dotnet-assembly-mcp`
or `dotnet-native-mcp` cannot be co-located with the diagnostics sidecar.

**When NOT to use:** local / twin-sidecar topologies where the sibling MCP can
already see the pod-local filesystem directly.

## `get_bytes(kind="dump")`

Streams a dump file already living under `MCP_ARTIFACT_ROOT` (or an absolute
path that still resolves under that root after symlink resolution). The shape is
identical to `get_bytes(kind="module")`, but the `asset` is always `"dump"` and the
`identifier` is the canonical dump path under the sandbox.

> **Scope:** same literal `module-bytes-read` requirement as `get_bytes(kind="module")`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `dumpFilePath` | `string` | — | Relative path under `MCP_ARTIFACT_ROOT`, or an absolute path that still resolves under that root |
| `offset` | `long` | `0` | Chunk start offset |
| `maxBytes` | `int` | `4_194_304` | Requested chunk size; capped at `16 MiB` |

**Notes:**

- `dumpFilePath` is re-validated on **every** call via the artifact-root sandbox.
  `..`, symlink escape, and absolute paths outside the root return
  `InvalidArtifactPath`.
- Artifacts larger than `256 MiB` are rejected with `InvalidArgument` rather
  than partially streamed.
- The returned `sha256` is for the **entire** dump, not just the current chunk.

**When to use:** after `collect_process_dump(confirm=true)` when a client-side
sibling MCP needs the dump bytes locally.

**When NOT to use:** as a generic file reader — the sandbox intentionally only
covers dump artifacts under `MCP_ARTIFACT_ROOT`.

## `get_bytes(kind="trace")`

Streams a raw `.nettrace` capture already living under `MCP_ARTIFACT_ROOT`. The
file is produced by `collect_sample(kind="cpu", exportTrace=true)` (CPU sampling)
or `inspect_heap(source="gcdump", exportTrace=true)` (induced-GC heap snapshot) —
those tools keep the otherwise-deleted `.nettrace` under `traces/` and return its
relative path. Hand the bytes off to PerfView, Speedscope, or Perfetto for fully
offline analysis. The shape is identical to `get_bytes(kind="dump")`, but the
`asset` is always `"trace"`.

> **Scope:** same literal `module-bytes-read` requirement as `get_bytes(kind="dump")`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `traceFilePath` | `string` | — | Relative path under `MCP_ARTIFACT_ROOT`, or an absolute path that still resolves under that root |
| `offset` | `long` | `0` | Chunk start offset |
| `maxBytes` | `int` | `4_194_304` | Requested chunk size; capped at `16 MiB` |

**Notes:**

- `traceFilePath` is re-validated on **every** call via the artifact-root sandbox
  (same gate as `kind="dump"`); `..`, symlink escape, and absolute paths outside
  the root return `InvalidArtifactPath`.
- Artifacts larger than `256 MiB` are rejected with `InvalidArgument`.

**When to use:** after `collect_sample(kind="cpu", exportTrace=true)` or
`inspect_heap(source="gcdump", exportTrace=true)` when a client needs the raw
trace bytes locally for PerfView/Speedscope/Perfetto.

---

## `list_orchestrator`

Consolidation of the orchestrator listing surface (issue #212). One
read-only tool that dispatches on `kind`:

| `kind` | Replaces | Required scope | Returns |
|---|---|---|---|
| `pods` | `list_orchestrator(kind="pods")` | `orchestrator-list` | `PodCandidatePage` under `data.pods` |
| `investigations` | `list_orchestrator(kind="investigations")` | `orchestrator-attach` | `InvestigationListPage` under `data.investigations` |

Per-kind parameters are preserved verbatim:

- **`kind="pods"`** — `namespace`, `labelSelector`, `fieldSelector`,
  `containerName`, `preparedOnly` (default `true`), `includeNotReady`
  (default `false`), `limit` (default `100`, clamped to
  `Orchestrator:MaxListLimit`), `cursor`.
- **`kind="investigations"`** — `includeTerminal` (default `false`),
  `includeAllSessions` (default `false`; requires
  `Orchestrator:AllowCrossSessionAdmin=true` **or** the bearer's
  `orchestrator-admin` modifier scope).

**Result envelope:**

```json
{
  "summary": "...",
  "hints": [ ... ],
  "data": {
    "kind": "pods",                  // discriminator echo
    "pods":            { "items": [...], "nextCursor": null },   // when kind=pods
    "investigations":  null                                       // null when not selected
  }
}
```

Exactly one of `data.pods` / `data.investigations` is populated, matching `data.kind`.
Errors (unknown `kind`, orchestrator disabled, scope mismatch) surface as the
standard `DiagnosticError` envelope with kinds `InvalidArgument`,
`OrchestratorDisabled`, or `PermissionDenied` respectively.

**Authorization.** The MCP scope filter accepts either of `orchestrator-list` /
`orchestrator-attach`. The tool re-checks scopes per `kind` so a token holding
only `orchestrator-list` cannot enumerate investigation handles by switching the
discriminator.

**Why `attach_to_pod` / `detach_from_pod` are NOT folded in.** Those
verbs have side-effect boundaries (ephemeral-container injection, handle close,
session unbind) that are distinct from read-only listing. They remain explicit.

**Deprecation.** `list_orchestrator(kind="pods")` and `list_orchestrator(kind="investigations")` are still
registered and behave unchanged, but each carries `[DeprecatedTool]` metadata
pointing at `list_orchestrator` and will be removed in **0.7.0**.

**Examples**

```jsonc
// Enumerate prepared Pods in a namespace:
{ "name": "list_orchestrator", "arguments": {
    "kind": "pods", "namespace": "checkout", "labelSelector": "app=api" } }

// List active handles for the current MCP session:
{ "name": "list_orchestrator", "arguments": {
    "kind": "investigations", "includeTerminal": false } }
```

---

## `attach_to_pod`

Injects a diagnostic **ephemeral container** into a target Kubernetes Pod so the
sidecar shares the target's PID namespace and diagnostic IPC socket, then returns
an investigation handle bound to the current MCP session. This is a side-effecting
verb (deliberately **not** folded into `list_orchestrator`).

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `namespace` | `string?` | `Orchestrator:DefaultNamespace` | Pod namespace |
| `podName` | `string` | — | Pod name. **Required** |
| `containerName` | `string?` | first container in the Pod spec | Target container inside the Pod |
| `ttlSeconds` | `int?` | `Orchestrator:DefaultInvestigationTtlSeconds` (1800) | Per-investigation TTL |
| `requirePreparedTarget` | `bool` | `true` | When true, refuses to attach to Pods that don't carry the prepared opt-in label |
| `allowReuseExistingSession` | `bool` | `true` | When true, returns an existing investigation for the same target instead of injecting a second ephemeral container |

**Returns:** `AttachSession` (investigation handle + resolved target). Use the
handle with the diagnostic tools, then release it with
[`detach_from_pod`](#detach_from_pod). **Scope:** `orchestrator-attach`.
Requires the orchestrator to be enabled; disabled servers return
`OrchestratorDisabled`.

---

## `detach_from_pod`

Closes an active investigation handle: tears down the cached MCP client, stops
the port-forward, unbinds every MCP session still pointed at the handle, and
marks it `Closed` so subsequent tool calls fall back to local execution.
**NOTE:** the ephemeral diagnostics container **cannot** be removed (a Kubernetes
constraint) — it stays on the Pod's spec until the Pod is recreated, so detach
only releases the orchestrator-side transport, it does not roll the Pod back.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `handleId` | `string?` | handle bound to the current session | Investigation handle id returned by `attach_to_pod` |

**Returns:** `DetachResult`. **Scope:** `orchestrator-attach`. Idempotent —
calling on a missing / already-terminal handle is a no-op and returns Ok.

---

## `discover_azure`

Azure discovery v1 (issue #232, parent #230). Single `kind`-discriminated tool that
enumerates .NET workload candidates in an Azure subscription across three platforms.

| `kind` | Required scope | Returns |
|---|---|---|
| `webapps` (default) | `azure-discovery` | `AzurePagedResult<AzureWebAppCandidate>` under `data.webapps` |
| `containerapps` | `azure-discovery` | `AzurePagedResult<AzureContainerAppCandidate>` under `data.containerapps` |
| `aksclusters` | `azure-discovery` | `AzurePagedResult<AzureAksClusterCandidate>` under `data.aksclusters` |

**Parameters**

- `subscriptionId` *(required)* — Azure subscription id (string GUID).
- `kind` — discriminator, see table above. Case-sensitive.
- `resourceGroup` — optional resource-group filter; null lists across the whole subscription.
- `includeStopped` *(default false)* — when true, backends include stopped / failed resources.
- `limit` *(default 100)* — page size; clamped to `200`.
- `cursor` — opaque continuation token from a prior page; null for the first page.
- `includeKubeconfig` *(default false)* — `aksclusters` only. When true, the AKS
  backend returns an opaque kubeconfig handle (`AzureAksHandoff`) — never raw
  kubeconfig content.

**Result envelope**

```json
{
  "summary": "...",
  "hints": [],
  "data": {
    "kind": "containerapps",
    "webapps":        null,
    "containerapps":  { "items": [...], "nextCursor": null },
    "aksclusters":    null
  }
}
```

Exactly one of `data.webapps` / `data.containerapps` / `data.aksclusters` is populated,
matching `data.kind`. Errors (missing subscription id, unknown `kind`, Azure discovery
disabled, scope mismatch) surface as the standard `DiagnosticError` envelope with kinds
`InvalidArgument`, `AzureDiscoveryDisabled`, or `PermissionDenied` respectively.

**`readinessWarnings`.** Each candidate carries a best-effort `readinessWarnings[]` so the
LLM can rank attach targets without an extra round-trip (empty does *not* prove attach-ready):
- `webapps` — Windows sites are flagged (`Windows OS — sidecar not supported`); function apps
  are excluded entirely.
- `containerapps` — flags `No second container detected` (sidecar topology not deployed) and
  `Scale=0` (may be scaled to zero and unreachable).

**RBAC.** All kinds need **Reader** on the subscription (or a tighter resource-group scope).
`aksclusters` with `includeKubeconfig=true` additionally needs the **Azure Kubernetes Service
Cluster User Role** per cluster; missing it leaves `handoff` null on that row with a warning.

**Registration.** Gated on the `AzureDiscovery:Enabled` configuration flag — a server
with the master switch off looks identical to a pre-#232 build (the tool is not
registered and the Azure SDK is never reached).

**Backends.** The contract is shipped in #232; the real backends arrive in:
- **#233** — App Service (`webapps`) + Container Apps (`containerapps`).
- **#234** — AKS (`aksclusters`), including the kubeconfig-handle store.

Until those PRs merge, calling the tool with `AzureDiscovery:Enabled=true` throws
`NotImplementedException` through the backend stubs.

---

## `start_investigation`

Plans a .NET performance investigation as a decision tree **before** any collector
runs, so the LLM executes a bounded, prioritized sequence instead of guessing.
Returns an `InvestigationPlan` (ordered steps + rationale + a tool-call budget).
The mode is inferred from which inputs are supplied:

- **cold** — a `symptom` only → full triage decision tree.
- **hypothesis** — a `hypothesis` → a targeted plan confirming/refuting it.
- **warm** — a prior `baseline` → resume from a known-good comparison.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto-select | Target PID (auto-selects when one .NET process is visible) |
| `symptom` | `string?` | — | Plain-language symptom (e.g. `high latency on /checkout since v2025.10`). Required for cold mode |
| `hypothesis` | `string?` | — | Specific hypothesis to test → hypothesis mode |
| `baseline` | `BaselineHandle?` | — | Baseline from a prior investigation → warm mode |
| `maxToolCalls` | `int` | `8` | Hard cap on tool calls before forcing summarization |
| `dumpRequiresApproval` | `bool` | `true` | Mark `collect_process_dump` steps as approval-gated |

**Scope:** `investigation-export`. See
[investigation-playbooks.md](./investigation-playbooks.md) for worked cold /
warm / hypothesis journeys.

---

## `export_investigation_summary`

Reads a prior `collect_sample(kind="cpu")` drilldown handle and produces a
portable, versioned investigation summary the LLM can persist externally
(server stays stateless) and later diff with `compare_to_baseline`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `handle` | `string` | — | Handle from a prior `collect_sample(kind="cpu")` call. **Required** |
| `format` | `SummaryFormat` | `json` | `json` (portable) or `markdown` (human-readable for PRs) |
| `topHotspots` | `int` | `10` | Max hotspots included |
| `buildAssemblyName` | `string?` | — | Managed assembly name of the target |
| `previousInvestigationId` | `string?` | — | Link lineage to a previous summary |
| `fixCommitSha` / `fixPullRequestUrl` / `fixDescription` | `string?` | — | Optional proposed-fix metadata |
| `notes` | `string?` | — | Free-form notes appended to the summary |

**Returns:** `ExportedInvestigationSummary`. An expired/unknown handle returns a
`HandleExpired` envelope with a hint to re-run the sampler. **Scope:**
`investigation-export`.

---

## `compare_to_baseline`

Diffs a current investigation summary against a baseline (or compares an ordered
journey of `ComparableSnapshot` bodies) and returns a verdict + headline + ranked
deltas. Large matrices return a compact inline payload plus a
`journey://diff/{handle}` Resource link.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `baselineSummaryJson` | `string?` | — | Baseline summary JSON (from a prior `export_investigation_summary`). Optional when `snapshotsJson` is supplied |
| `currentSummaryJson` | `string?` | — | Current summary JSON. Optional when `snapshotsJson` is supplied |
| `snapshotsJson` | `string[]?` | — | Ordered `ComparableSnapshot` JSON bodies for an N-way journey diff (bodies, not file paths) |
| `topN` | `int` | `25` | Max metric series / key rows in compact inline payloads |
| `depth` | `string` | `full` | `full` (whole matrix when small) or `compact` (verdict/headline/top deltas) |
| `mode` | `string?` | `trend` | `trend` (ordered captures over time) or `dispersion` (unordered replicas → outliers) |

**Scope:** `investigation-export`. Pairs with `export_investigation_summary` for
"did my fix actually help?" journeys — see
[investigation-playbooks.md](./investigation-playbooks.md).

---

## Security gates (B4)

Issue #165 introduced three opt-in security gates that change the default behaviour of
`query_snapshot`, `collect_events(kind="event_source")` and `collect_sample(kind="cpu")`. All three are bound
from the `Diagnostics:` configuration section and can be set via env vars
(`Diagnostics__AllowSensitiveHeapValues=true`, `Diagnostics__EventSourceAllowlist__0=…`,
`Diagnostics__SymbolServerAllowlist__0=msdl.microsoft.com`).

> **B5.4 — modifier scopes preferred.** All three gates now accept a modifier
> scope on the bearer principal as an alternative authorisation path:
> `sensitive-heap-read`, `eventsource-any`, `symbols-remote`. The scope-first predicate is
> `principal.HasExplicitScope("<scope>") OR <legacy-flag-or-allowlist-allows>` — either
> path is sufficient, so existing deployments keep working. The legacy paths now emit a
> once-per-process deprecation warning when they are the mechanism that unlocked the call.
>
> Scope membership is **literal**: a `root`/`*` token does **not** auto-grant the modifier
> scopes (this preserves least-surprise for the SSRF / sensitive-data gates — operators
> must deliberately mint a scoped token). The
> `Diagnostics:EventSourceAllowlist` and `Diagnostics:SymbolServerAllowlist` policies
> themselves are **retained** as fallback value-shaping. Only
> `Diagnostics:AllowSensitiveHeapValues` is slated for removal in a future release —
> prefer minting a token with the `sensitive-heap-read` scope today.

### H4 — heap drilldown defaults to metadata-only

`query_snapshot` with `view=duplicate-strings` and `view=object` no longer returns raw
string previews or field/array element values by default. Instead each value site is replaced
with `<redacted:metadata-only>` and the LLM gets length / type / address metadata only.

To opt-in (**scope-first path, recommended**):

1. mint a bearer token with the `sensitive-heap-read` scope (see
   [`authorization.md`](./authorization.md#modifier-scopes) and
   `deploy/helm/README.md` for the chart-level shape), **and**
2. pass `includeSensitiveValues=true` on the per-call invocation.

Legacy fallback (deprecated — emits a once-per-process warning):

1. set `Diagnostics:AllowSensitiveHeapValues=true` on the server, **and**
2. pass `includeSensitiveValues=true` on the per-call invocation.

When the gate opens via either path, values flow through `SensitiveDataRedactor`, which
replaces any substring matching the default patterns (Bearer/Basic tokens, JWT-shaped
triples, `password=`/`secret=`/`api_key=` query-string syntax, AWS access keys, GitHub PATs,
PEM blocks) with `<redacted:sensitive>`. Add custom patterns via
`Diagnostics:RedactionPatterns[]`.

The `heap-snapshot://` MCP resource projection is **always metadata-only** — it has no
per-call opt-in surface, so neither the scope nor the server flag can unlock raw values
through that path. Operators who need the redacted-but-present view should call
`query_snapshot view=duplicate-strings includeSensitiveValues=true` (which honours
both gates).

### M2 — `collect_events(kind="event_source")` provider allowlist

Arbitrary user-defined EventSource providers were the easiest way for an attacker who
gained MCP access to siphon application-defined logging (which routinely contains tokens,
PII, SQL parameters). The tool now refuses any `providerName` that is not on the curated
default allowlist (System.Net.Http, Microsoft.AspNetCore.Hosting,
Microsoft-AspNetCore-Server-Kestrel, Microsoft-Extensions-Logging,
Microsoft-Windows-DotNETRuntime, System.Threading.Tasks.TplEventSource, …) or under
`Diagnostics:EventSourceAllowlist[]`.

To capture a custom provider:

- **Scope-first path (recommended).** Grant the bearer the `eventsource-any` scope; the
  tool will then accept any `providerName` regardless of the curated allowlist when the
  caller passes `unsafeProvider=true`. The keyword/level clamping below still applies.
- Add the provider to `Diagnostics:EventSourceAllowlist[]` (preferred over the legacy
  flag — survives across calls). When a call is authorised by the allowlist alone (no
  `eventsource-any` scope on the bearer) the tool emits a once-per-process deprecation
  warning so operators see they should be distinguishing callers with scopes rather
  than relying on a deployment-wide allowlist.
- Legacy fallback (deprecated — emits a once-per-process warning): set
  `Diagnostics:AllowSensitiveHeapValues=true` on the server **and** pass
  `unsafeProvider=true` on the call.

On any `unsafeProvider=true` path `keywords=-1` is clamped to `0` and `eventLevel>4` is
clamped to `Informational` unless the caller passed explicit safer values.

### M3 — symbol-server SSRF guard

`symbolPath` historically accepted any `srv*http(s)://…` segment, which let a malicious
caller turn the sidecar into an outbound HTTP client to any host on the cluster network.
Caller-supplied `symbolPath` values are now parsed and every `srv*` / `symsrv*` segment's
`http://` / `https://` URL must host-match `Diagnostics:SymbolServerAllowlist[]`, **or**
the principal must hold the `symbols-remote` modifier scope (scope-first path —
recommended). Local filesystem paths and bare directory entries always pass through. The
deny path returns a `SymbolServerNotAllowed` envelope. When a call is authorised by the
allowlist alone (no `symbols-remote` scope on the bearer) the tool emits a once-per-process
deprecation warning. Tools covered:

- `collect_sample(kind="cpu")`
- `collect_sample(kind="off_cpu")`
- `collect_thread_snapshot`
- `inspect_heap` (and its deprecated aliases `inspect_heap(source="dump")` / `inspect_heap(source="live")`)

`MCP_SYMBOL_PATH` and `_NT_SYMBOL_PATH` from the **operator-set environment** are *not*
validated — they are treated as trusted by the deployment.

### D2 — per-pid attach concurrency gate

Live-attach tools suspend their target through the .NET diagnostic pipeline / ptrace, and a
given process can be suspended by only one attacher at a time. Two simultaneous attaches
against the same pid (`collect_thread_snapshot`, `inspect_heap(source="live")`,
`collect_process_dump`, `capture_method_bytes`) therefore collide. A per-pid concurrency gate
serializes them: while one attach holds the pid, a second attach against that same pid returns
a retriable `Busy` envelope (`NextActionHint` to retry the same tool) instead of failing hard.
Attaches against *different* pids and dump-based work (no live pid) are never gated.

- `MCP_ATTACH_MAX_PER_PID` — permits in flight per pid (default `1`).
- `MCP_ATTACH_WAIT_MS` — how long to wait for a permit before reporting busy (default `0`, fail fast).
