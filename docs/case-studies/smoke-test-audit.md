# Smoke-test UX audit — steps to diagnosis, re-measured

This audit re-measures how many MCP tool calls a symptom-to-root-cause
investigation takes against `samples/BadCodeSample`, using the current tool
surface (including the signal-grouping/findings layer and `collect_batch`).
It complements, but does not replace, the narrated
[case studies](./README.md) and the
[bad-code scenario matrix](../bad-code-scenarios.md).

**Method.** A "step" is one MCP tool invocation, counted from the reported
symptom to a causal hypothesis naming a method/resource. Fix verification
reports the actual number of additional MCP tool invocations used against a
`*-fixed` endpoint. Each scenario runs in its own Docker network/port pair
so independent scenarios can execute concurrently; CPU-sensitive scenarios
(`cpu-burn`, `culture-lookup`, `sync-over-async`, `lock-storm`) are capped at
two concurrent runs to avoid cross-container CPU contention skewing the
CPU/queue thresholds the evidence depends on.

Scope: **troubleshooting scenarios only** (symptom caused by a real
anti-pattern in `BadCodeSample`). Pure performance-improvement workflows
without a defect (capacity tuning, `compare_to_baseline` as a CI regression
gate, the BenchmarkDotNet diagnoser) are explicitly out of scope for this
audit. The separate `/lock-contention` scenario was not included in this run;
`lock-storm` covered the lock-owner/waiter correlation path instead.

## Execution topology and preflight

The audit used freshly rebuilt `badcode-sample:dev` and
`dotnet-diagnostics-mcp:dev` images from the same repository HEAD. Checking
the image creation timestamp is part of the preflight: the first attempted
run found a two-month-old local `:dev` image whose baked configuration and
tool surface no longer represented the current source. All observations from
that attempt were discarded before measuring steps.

Each concurrent scenario received its own Docker network, target container,
sidecar container, `/tmp` volume, and host ports:

| Scenario | App port | MCP port | Scheduling lane |
|---|---:|---:|---|
| sync-over-async | 18181 | 18881 | CPU-sensitive |
| culture-lookup | 18182 | 18882 | CPU-sensitive |
| lock-storm | 18183 | 18883 | CPU-sensitive |
| cpu-burn | 18184 | 18884 | CPU-sensitive |
| leak | 18185 | 18885 | parallel-safe |
| exceptions | 18186 | 18886 | parallel-safe |
| loh-alloc | 18187 | 18887 | parallel-safe |
| slow-http | 18188 | 18888 | parallel-safe |
| crash | 18189 | 18889 | parallel-safe |

The five parallel-safe scenarios ran concurrently. CPU-sensitive scenarios
ran at most two at a time. This isolates ports and diagnostic sockets while
limiting host CPU contention that could invalidate relative CPU and queue
signals.

## Negative-path UX

### Invalid bearer and missing `CAP_SYS_PTRACE`

An invalid bearer was rejected before tool invocation with HTTP 401 and:

```json
{"error":{"kind":"unauthenticated","message":"invalid bearer token"}}
```

With the correct bearer, `collect_thread_snapshot(processId=1)` against a
sidecar intentionally started without `SYS_PTRACE` returned HTTP 200 with a
structured `PermissionDenied` error. Its summary explicitly named
`CAP_SYS_PTRACE`, `kernel.yama.ptrace_scope=1`, Docker and Kubernetes
capability syntax, and the host `sysctl` alternative. A redundant
`collect_events(kind="counters")` call succeeded afterward, confirming that
diagnostic IPC worked and only the ptrace-dependent path was blocked.

**UX note.** The permission envelope was actionable, but the MCP result did
not set `isError: true`; server logs likewise reported `IsError=False`.
Consumers that inspect only the protocol error bit could misclassify this as a
successful tool result.

## Workflow observations

### Managed investigation and baseline comparison — 10 calls versus 4

The managed before/after path required ten MCP calls:

1. `start_investigation` for the broken endpoint.
2. `collect_events(kind="counters")`.
3. `collect_sample(kind="cpu")`.
4. `query_snapshot(view="call-tree")`.
5. `export_investigation_summary`.
6. `start_investigation` for the fixed endpoint.
7. `collect_events(kind="counters")`.
8. `collect_sample(kind="cpu")`.
9. `export_investigation_summary` with the previous investigation ID and fix
   description.
10. `compare_to_baseline` with both exported summary documents.

The broken run completed 0 of 50 requests, with queue length 236, 141
ThreadPool threads, and 0.16% CPU. Its stack connected the application lambda
to `TaskAwaiter.GetResult`, while `ManualResetEventSlim.Wait` accounted for
51.48% of samples. The fixed run completed 50 of 50 requests with 3.04-second
p95 latency, queue length zero, and 22 threads.

This is six calls more than the shortest ad-hoc path (three calls to diagnose
and one fix-check call). The planner/playbook is understandable, but the
server remains stateless: the client must retain handles, substitute
placeholders, persist full exported JSON, and carry investigation IDs, lineage,
and tool budgets. Export currently accepts only CPU handles, forcing a fixed
side CPU capture primarily to satisfy the handoff contract.

**UX/correctness note.** Despite starvation and the blocking hotspot
disappearing, `compare_to_baseline` classified the result as
`regression_new_hotspot`. The comparison was therefore not a meaningful
summary of this fix and could direct the investigation backward.

### Live heap versus dump/offline — 3 calls versus 5

Both paths produced the same decisive retention chain:

`Stack root -> List<byte[]> -> byte[][] -> byte[]`

and the object drilldown identified `List<byte[]>._items` as the reference to
the backing `System.Byte[][]`.

The minimum live path was three calls:

1. `inspect_heap(source="live", includeRetentionPaths=true)`.
2. `query_snapshot(view="gcroot")` for a retained `byte[]`.
3. `query_snapshot(view="object")` for the list retainer.

The dump path took five calls when the client handled confirmation manually:

1. `collect_process_dump(type="WithHeap")` to receive
   `confirmation_required`.
2. Repeat with `confirm=true`.
3. `inspect_heap(source="dump", dumpFilePath=...,
   includeRetentionPaths=true)`.
4. `query_snapshot(view="gcroot")`.
5. `query_snapshot(view="object")`.

Protocol elicitation can combine the approval interaction with the original
dump request, reducing that path to four MCP calls. Dump collection therefore
adds one or two calls over live inspection, plus approval and artifact
lifecycle management. In return, it creates reusable offline evidence with
stable addresses after the target exits.

Both inspections measured a 102,626,920-byte managed heap that was
approximately 98.6% `byte[]`. The dump file itself was 317,476,864 bytes,
uses an approximately 24-hour file cleanup lifecycle, and produces diagnostic
handles with an approximately ten-minute lifetime.

**UX notes.** The first dump inspection and a later post-analysis period both
coincided with sidecar exit 139; restarting the sidecar allowed the same dump
workflow to succeed.

### Suspended cold-start — 1 CLI command

The public CLI path completed in one diagnostic command:

```bash
TMPDIR=.s dotnet-diagnostics collect --kind startup --duration 8 \
  --depth summary --json --suspend-startup --launch -- \
  dotnet samples/CoreClrSample/bin/Release/net10.0/CoreClrSample.dll \
  --urls http://127.0.0.1:0
```

The launcher generated a short reverse diagnostic socket with `suspend`,
accepted the runtime connection, armed EventPipe, and then resumed execution.
It captured 115 dependency-injection startup events, including 57
`CallSiteBuilt` and 52 `ServiceResolved` events. An ordinary attach after
launch saw only four replayed events and none of those two event types.

`ServiceProviderBuilt` appeared once in both captures because that event is
replayed, so it is not a reliable cold-start differentiator. No assembly or
module events appeared in this collector.

**UX notes.** Using a long `TMPDIR` exceeded Unix's 108-character socket-path
limit and surfaced an unhandled `ArgumentOutOfRangeException`. Terminated
children also left normal runtime diagnostic socket/FIFO artifacts requiring
manual cleanup. The MCP surface now exposes the same startup launch flow
through `collect_events(kind="startup", launch=...)` in stdio mode with
`Diagnostics__AllowProcessLaunch=true`; documentation and source comments
that still describe it as CLI-only are stale.

### CLI parity — one-shot versus session REPL

For sync-over-async, the one-shot CLI reached the diagnosis in two commands
and checked the fix in one:

1. `inspect --view triage --pid ... --duration 6` reported queue length 212
   with 0.12% CPU.
2. `collect --kind thread-snapshot --depth detail --json --launch -- ...`
   found 59 of 64 threads blocked with `GetResult`,
   `SpinThenBlockingWait`, `ManualResetEventSlim.Wait`, and `Task.WaitAll`.
3. A fixed-side triage check reported queue length zero and no backlog
   hypothesis.

The fixed verdict remained `inconclusive` because the expected three-second
request latency was still present, despite the starvation signal disappearing.

The session REPL took three diagnosis commands plus one fix check, matching the
MCP count. Binding the PID once removed repeated process arguments and enabled
handle reuse; its largest unique-stack group contained 75 of 132 threads with
the same blocking frames.

Lock-storm required one command in both CLI modes, matching MCP detail mode.
The one-shot run correlated one sleeping owner with 15 waiters; the REPL run
found one with 17 waiters.

**UX notes.** Human-readable `--depth detail` output prints only a headline;
the decisive inline evidence is available only in the much larger JSON
response. One-shot handles cannot be queried, so the REPL materially improves
multi-step investigations but adds no value to a one-call lock-storm
diagnosis. Session hints redundantly include `--pid` after a process is bound,
and `query --top 3` returned six of six groups rather than three.

### Kubernetes orchestrator — 5 calls for one pod, 9 for two

A disposable kind cluster ran the repository's canonical KindIntegration
test successfully (1/1 in 12 seconds). The prepared-pod listing returned two
replicas and label selection such as `p6-target=b` narrowed the result to one.

The minimum one-pod flow took five MCP calls:

1. `list_orchestrator(kind="pods", namespace="p6-sample",
   labelSelector="app=p6-sample,p6-target=a", preparedOnly=true)`.
2. `attach_to_pod(podName=..., containerName="app",
   requirePreparedTarget=true, ttlSeconds=600)`.
3. Proxied `inspect_process(view="list")` to select CoreClrSample PID 1.
4. Proxied `collect_events(kind="counters", processId=1,
   durationSeconds=6, providers=["System.Runtime"], intervalSeconds=1)`.
5. `detach_from_pod(handleId=...)`.

Explicit counters across both pods took nine calls: one listing, two attaches,
two process listings, two counter captures, and two detaches. Both captures
succeeded.

The automatic `replica_counters` path took six calls including setup and
cleanup, but failed:

```text
ReplicaCounterFanoutFailed:
replica_counters: every one of the 2 attached Pod(s) failed to collect
counters -- no replicas could be compared.
```

Each pod-local error said:

```text
2 .NET processes visible -- pass processId explicitly.
```

The explicit per-pod path can provide that PID, but the fan-out call has no
per-replica PID input, so it cannot operate in this canonical topology.

**UX notes.** A detach followed by reattach also exposed stale ephemeral
container state: the old process still occupied port 5130, while the new proxy
received `401 invalid bearer token`. All five detach calls nevertheless moved
their handles from Active to Closed, and final active-handle count was zero.
The run used 28 MCP calls in total while exercising retries and variants:
eight listings, five attaches, three process listings, five event collections,
one snapshot query, five detaches, and one gated `discover_azure` probe.
Without Azure configuration, `discover_azure` was absent from the 16-tool
catalog and a direct call correctly returned `Unknown tool`.

## Results

| Scenario | Steps to root cause | Fix-verify step | Notes / deltas vs prior baseline | Status |
|---|---|---|---|---|
| sync-over-async (re-measure) | 3 | 1 | Down from the prior 4-step path; triage routed directly to a thread snapshot and unique-stack drilldown. | pass with friction |
| culture-lookup (re-measure, incl. signal-grouping) | 3 (2 from known CPU symptom) | 3 | No reduction from the prior baseline: triage misrouted toward activities; CPU sample + narrowed call-tree remained necessary. | pass with friction |
| lock-storm-correlation (re-measure) | 1 | N/A | `depth="detail"` now returns the sleeping owner and all 19 waiters inline; summary mode still needs drilldowns. | pass with friction |
| cpu-burn | 4 (3 from known CPU symptom) | N/A | Located the hot endpoint lambda, but the documented `SHA256` frame was absent even from a rooted call-tree. | partial / doc drift |
| leak | 5 (6 calls executed) | N/A | Live retention reached `List<byte[]>._items`; the sixth `gcroot` query was redundant. Triage/counter summaries understated obvious growth. | pass with friction |
| exceptions | 1 | N/A | One exception capture attributed all 2,000 events to the same `FormatException`; suggested follow-ups were unnecessary. | pass |
| loh-alloc | 3 | N/A | Batch + drilldown established 32 Gen2 GCs and 1.311 s of pauses; one batch retry was needed after inline output overflow. | pass with friction |
| slow-http | 1 | N/A | One `event_source` capture correlated the request lifecycle and measured 3.097 s end to end; counters were unnecessary. | pass |
| crash (unhandled) | no diagnosis returned (1 failed call) | N/A | The target crash destroyed the Docker PID-sharing sidecar, so `crash-guard` returned a transport `IncompleteRead` instead of its structured envelope. | fail |

## Follow-up issues

The audit findings are tracked in
[#691–#704](https://github.com/pedrosakuma/dotnet-diagnostics/issues/691).
The external-Docker passthrough design is #704, the repeated Linux exit-139
evidence was added to #147, and the real-MCP isolated scenario runner was
added to #681.

## Raw run log

Entries below are appended as each scenario completes, with the exact tool
calls (arguments trimmed), the observed evidence, and the step count.

### `sync-over-async` — 3 steps (previously 4), plus 1 fix check

1. `inspect_process(view="triage", processId=1, durationSeconds=6)`.
2. `collect_thread_snapshot(processId=1, maxFramesPerThread=32,
   depth="Summary")`.
3. `query_snapshot(view="unique-stacks", topN=3, framesToHash=12,
   minCount=2)`.

Triage reported 0.10% CPU with a critical ThreadPool queue of 713 and a
`threadpool.backlog` hypothesis. The snapshot found 166 of 172 threads likely
blocked. Its three largest unique-stack groups contained 65, 49, and 32
threads, with `BadCodeSample` lambda frames above
`TaskAwaiter<T>.GetResult()`, `Task.SpinThenBlockingWait`, and
`ManualResetEventSlim.Wait`. Together, these establish sync-over-async
blocking of ThreadPool workers.

A client capable of rendering the decisive grouped stacks directly from the
snapshot response could stop after two calls. In this run, response size and
client clipping made the explicit unique-stack query necessary.

The equivalent load against `/sync-over-async-fixed` required one verification
call:

1. `inspect_process(view="triage", processId=1, durationSeconds=6)`.

It reported a healthy process, queue length zero, 0.17% CPU, and no observed
signals or hypotheses.

**`collect_batch` check.** A separate batch combined counters and ThreadPool
events in one eight-second invocation. It returned 0.1% CPU, 32 hill-climbing
events, and 28 explicit starvation reasons. That is useful confirmation, but
the batch cannot include a thread snapshot and therefore does not beat the
triage-to-snapshot path for reaching the blocking frames.

**UX notes.** A first attempt against an already-warmed worker pool appeared
healthy; a cold target restart was needed for deterministic reproduction. A
detail snapshot produced 716 KB and was followed by sidecar exit 139. Summary
mode still produced 59.7 KB. Nine successful diagnostic calls were executed
while comparing paths and retrying, but only the three calls above belong to
the shortest defensible diagnosis.

### `culture-lookup` — 3 steps, plus 3 fix checks

1. `inspect_process(view="triage", processId=1, durationSeconds=6)`.
2. `collect_sample(kind="cpu", processId=1, durationSeconds=8, topN=15,
   depth="Summary")`.
3. `query_snapshot(view="call-tree", rootMethodFilter="b__12", maxDepth=3,
   maxNodes=20)`.

Triage observed 64.67% process CPU and 5164 ms p95 latency, but routed the
investigation toward activities rather than CPU sampling. The CPU capture
contained 71,388 samples. `IcuGetHashCodeOfString` owned 47,657 exclusive
samples: 66.8% of all samples and 91.6% of running self-time. The narrowed
call tree connected the endpoint lambda (47,308 inclusive samples) directly
to the ICU hashing child (47,092 inclusive and exclusive samples), proving
that culture-aware dictionary hashing dominated the endpoint. Without the
initial triage test, the reported CPU symptom reaches this result in two
calls.

The fixed endpoint was checked with the same three tools under equivalent
load. Its p95 fell to 722 ms (approximately 7.2 times lower), ICU hashing was
no longer the self-time leader, and the narrowed endpoint tree had no ICU
child.

**UX notes.** The signal-grouping/triage expectation was not met: process CPU
normalized over 16 host cores remained in the 43-67% range while Docker
reported 745-1157%, so triage prioritized latency. A broad call-tree response
was 103.6 KB and truncated, requiring a narrowed query. Eight calls were
executed against the dedicated topology while calibrating load and comparing
views, but the shortest complete paths were three calls for diagnosis and
three for like-for-like fix verification.

### `cpu-burn` — 4 steps (3 from the known CPU symptom)

1. `inspect_process(view="triage", processId=1, durationSeconds=5)`.
2. `collect_sample(kind="cpu", processId=1, durationSeconds=5, topN=10,
   depth="Summary", resolveSourceLines=false)`.
3. `query_snapshot(view="top-methods", rankBy="exclusive", topN=10)`.
4. `query_snapshot(view="call-tree",
   rootMethodFilter="Program+<>c.<<Main>$>b__0_4", maxDepth=3,
   maxNodes=20)`.

Triage reported only 6.51% normalized process CPU and routed toward allocation
analysis rather than recognizing one saturated core. The sample contained
45,857 observations, split into 11,452 running and 34,405 waiting. The
exclusive ranking identified the `BadCodeSample` endpoint lambda with 3,800
running samples (33.18% of running samples). The rooted call tree contained
only that node: 3,800 inclusive and exclusive samples, no children, and no
truncation.

This localizes the CPU cost to the endpoint but does not reproduce the
`System.Security.Cryptography.SHA256` frame promised by
`bad-code-scenarios.md`. The expected signal therefore appears stale as an
observable profiling expectation. Starting from the explicit CPU symptom
avoids the misrouting triage call, for a three-call path. There is no fixed
variant endpoint.

**UX notes.** The inline CPU summary ranked `WaitForSignal` despite all of its
samples being waiting; the running/waiting-aware `top-methods` drilldown was
required to expose the actual running hot method.

### `lock-storm-correlation` — 1 step

1. `collect_thread_snapshot(processId=1, depth="detail")`.

The response contained 95 threads, 25 likely blocked threads, and four
contended SyncBlocks. Its inline `correlation.thread-overlap` finding named
thread 86 as sleeping in `Thread.Sleep` while owning a `System.Object`
monitor with 19 waiters; the lock row included the owner and all 19 waiter
thread IDs. No snapshot query was required to establish the sleeping-owner
relationship.

For comparison, default summary mode required three calls: the snapshot,
`query_snapshot(view="lock-graph")` to identify the owner with 19 waiters,
and `query_snapshot(view="stack")` to show that owner's `Thread.Sleep` frame.
There is no fixed lock-storm endpoint.

**UX notes.** Summary output was still 28.5 KB and could omit the decisive
correlation. Detail mode is materially better, although it named owner thread
86 in the correlation while omitting that thread from the 25 inline thread
rows. The response recommended a lock-graph drilldown even though detail had
already provided the decisive relation. A guessed
`query_snapshot(view="correlation.thread-overlap")` failed because that signal
name is not a valid view.

### `slow-http` — 1 step

1. `collect_events(kind="event_source", processId=1, durationSeconds=10,
   depth="detail", providerName="System.Net.Http", maxEvents=200)`

The workload called `/slow-http` with the loopback
`/slow-hang?seconds=3` endpoint while collection was active. The response
reported `elapsedMs=3112`. The capture returned ten correlated events:
`Request/Start` at `19:00:37.043Z`, `ResponseHeaders/Start` at
`19:00:40.126Z`, and `Request/Stop` at `19:00:40.140Z`, for 3.097 seconds
end to end (including 28.34 ms of queue time).

No counter capture or snapshot query was needed. The event payload redacted
the path to `?*`, but the correlation and timestamps were sufficient to
establish that the outbound HTTP request accounted for the reported delay.
There is no dedicated `/slow-http-fixed` endpoint.

**UX notes.** The container healthcheck reported unhealthy because the image
does not contain `wget`, although `/health` and the MCP endpoint were both
responsive. This did not affect collection, but it is misleading operational
noise during setup.

### `exceptions` — 1 step

1. `collect_events(kind="exceptions", processId=1, durationSeconds=5,
   depth="Detail", maxRecent=3)`

The workload triggered `/exceptions?count=2000` two seconds after collection
started. All 2,000 observed exceptions were `System.FormatException`, with
the message `The input string 'not-a-number' was not in a correct format.`
That was sufficient to attribute the storm to repeated invalid numeric
parsing. No counters or snapshot query were needed, and there is no fixed
variant endpoint.

**UX notes.** The response suggested a `System.Net.Http` EventSource capture
and a `query_snapshot(view="byType")` drilldown. Neither was relevant or
necessary after the detail response had already identified a uniform type and
message.

### `leak` — 5 steps to root cause (6 calls executed)

1. `inspect_process(view="triage", processId=1)`.
2. `collect_events(kind="counters", processId=1, durationSeconds=10,
   intervalSeconds=1, depth="Detail")`.
3. `inspect_heap(source="live", processId=1, topTypes=15,
   includeRetentionPaths=true, retentionPathLimit=12,
   includeStaticFields=true)`.
4. `query_snapshot(view="object")` for the first retention intermediate.
5. `query_snapshot(view="object")` for the second retention intermediate.
6. `query_snapshot(view="gcroot")` for the retained list (redundant).

Five leak requests during triage and five more during counters grew the GC
heap from approximately 22.59 MB to 43.67 MB. Counters also reported a
33,653,264-byte LOH and a 138.80 MB working set. The live heap contained
44,667,896 bytes, of which `System.Byte[]` accounted for 42,105,103 bytes
(94.26%). Its retention path was initially rendered as
`Byte[] -> <retainer> -> <retainer> -> Stack`. The two object queries resolved
those anonymous intermediates to a length-16 `System.Byte[][]` and then a
`List<System.Byte[]>` whose `_items` field referenced that backing array.
That fifth call established the retaining resource; the subsequent `gcroot`
query added only `Stack -> List<System.Byte[]>`.

There is no `/leak-fixed` endpoint.

**UX notes.** Triage classified the process as `healthy`, and the counters
summary described it as quiet despite roughly 21 MB of observed heap growth
and a large LOH. The retention-path output required two extra calls because it
displayed both intermediate objects only as `<retainer>`. The live heap
inspection itself took approximately 16.5 seconds.

### `loh-alloc` — 3 steps

1. `collect_batch` with concurrent `collect_events` requests for `counters`
   and `gc`, `processId=1`, `durationSeconds=10`.
2. Repeat the same batch after the local consumer overflowed while retaining
   the first response.
3. `query_snapshot(handle="B6704799SEKKND8FW3XG", view="byProvider")`.

During the capture, 30 workload requests allocated 1.2 GB. The GC stream
reported 32 collections, all Gen2, with 1.311 seconds of total pause time and
an 81.1 ms maximum pause. Counters showed a 3,299,280-byte LOH, 64.5%
fragmentation, 15% time in GC, and a 40,019,888-byte allocation rate. Together
these establish heavy LOH churn as the source of the frequent Gen2 pauses.
There is no `/loh-alloc-fixed` endpoint.

**UX notes.** `collect_batch` omitted 26 of 41 counters inline, including the
LOH evidence, requiring the snapshot drilldown. Its counter summary also
reported a Gen2 value of `1`, which is easy to misread against the GC
collector's authoritative window count of 32. The second batch invocation was
not diagnostically necessary, but it counts because the first inline response
overflowed at the consumer boundary.

### `crash?mode=unhandled` — failed after 1 step

1. `collect_events(kind="crash-guard", processId=1, durationSeconds=30)`.

The workload returned HTTP 202 and then raised an unhandled
`System.InvalidOperationException`. The target exited, but the MCP call
returned `IncompleteRead(0 bytes read)` instead of
`unhandledExceptionObserved`, `finalException`, or a snapshot handle.
Consequently, `query_snapshot(view="stack")` was impossible and the MCP
workflow did not deliver a diagnosis.

**UX notes.** In the documented local Docker topology, the sidecar uses
`--pid=container:<target>`. When the target container exits, Docker also
terminates the sidecar joined to that PID namespace. That destroys the
reporting transport precisely when `crash-guard` needs to return its final
envelope. The runtime exception was visible only in the target container log;
it is not counted as MCP diagnostic evidence.

## Post-fix re-audit — 2026-07-29

This re-audit rebuilt both local images from the current repository HEAD
before measuring:

- `dotnet-diagnostics-mcp:dev` → `2026-07-29T14:08:06Z`
- `badcode-sample:dev` → `2026-07-29T14:08:47Z`

The first `badcode-sample:dev` build reused a stale 2026-07-24 image, so that
attempt was discarded and repeated with `--no-cache`. Every scenario below ran
in the **anchored PID-namespace** topology from
[`local-docker-sidecar.md`](../local-docker-sidecar.md): inert
`diag-pid-anchor`, target + sidecar joined to the anchor PID namespace, shared
`/tmp`, and sidecar `--user 0 --cap-add SYS_PTRACE`.

For future re-runs, prefer **pulling** the published MCP server image
(`ghcr.io/pedrosakuma/dotnet-diagnostics:sha-<shortsha>` or `:edge`) and only
building `badcode-sample:dev` locally; this run kept the already-completed
local MCP build because it matched HEAD `a25328e`.

This pass focused on the live scenarios tied directly to the closed UX
issues. The separate `compare_to_baseline` correction (#692 / PR #715) was not
re-measured here.

**Date of this update:** 2026-07-29 (final rerun after
[#741](https://github.com/pedrosakuma/dotnet-diagnostics/issues/741),
[#742](https://github.com/pedrosakuma/dotnet-diagnostics/issues/742), and
[#743](https://github.com/pedrosakuma/dotnet-diagnostics/issues/743), using
the published `ghcr.io/pedrosakuma/dotnet-diagnostics:edge` image for the MCP
containers and a locally built `badcode-sample:dev` target).

| Scenario | Steps to root cause | Notes / deltas vs 2026-07-24 | Status |
|---|---:|---|---|
| crash (unhandled) | 1 | `collect_events(kind="crash-guard")` now survived target exit and returned inline `System.InvalidOperationException` evidence plus managed frames through `Program.<<Main>$>b__49()` / `g__CrashFixtureMessage|0_36` — the anchored PID namespace fix landed (#691 / PR #717). | pass |
| leak | 3 | Triage no longer said “healthy”: it emitted `memory.intra-window-growth` / `memory.footprint-growth` with **gc-heap-size 18.37→43.77 MB** and **LOH 16.78→29.36 MB** during the window (#697 / PR #718). `inspect_heap(source="live", includeRetentionPaths=true)` plus one object drilldown still reached `System.Collections.Generic.List<System.Byte[]>._items -> System.Byte[][] -> System.Byte[]`. | pass |
| loh-alloc | 1 | `collect_batch` now surfaced the decisive evidence inline in one call: **34 Gen2 collections** and **3 LOH/GC-specific counters** (`loh-size`, `gen-2-gc-count`, fragmentation) instead of hiding LOH evidence behind a follow-up drill (#698 / PR #719, #703 / PR #721). | pass |
| culture-lookup | 2 | Triage now emitted `cpu.effective-core-consumption` (**0.97 cores**) and suggested CPU sampling instead of misrouting toward activities (#697 / PR #718). The follow-up CPU sample’s inline signal directly named `System.Globalization.CompareInfo.IcuGetHashCodeOfString(...)` at **49.1%** self-time, so the diagnosis no longer needed a call-tree drill just to discover the hot method (#703 / PR #721). | pass |
| cpu-burn | 3 | Triage now emitted `cpu.effective-core-consumption` (**1.01 cores**) and routed directly to CPU sampling (#697 / PR #718). The rooted follow-up tree still localized running self-time to `Program+<>c.<<Main>$>b__0_4(...)`; reruns showed crypto child frames only as 0–2 sample noise (`SHA256.HashData(...)`, `Interop+Crypto.HashAlgorithmToEvp(...)`, or none), so [`bad-code-scenarios.md`](../bad-code-scenarios.md) is now accurate to treat the endpoint lambda as the stable signal instead of promising a visible SHA256 leaf (#742 / PR #746). | pass |
| sync-over-async (spot-check) | 2 executed | Stronger load reproduced **87 likely blocked** threads, but this quick summary-mode recheck did not cleanly re-derive the old `GetResult` stack group; the original 3-step result remains the better historical measurement. | inconclusive spot-check |
| lock-storm (spot-check) | 1 | `collect_thread_snapshot(depth="detail")` still showed the sleeping owner inline: `Program+<>c__DisplayClass0_3.<<Main>$>b__47()` under `Thread.Sleep`, with **20 blocked waiters**. | pass |
| exceptions (spot-check) | 1 | Still attributed all **2,000** events to `System.FormatException` with the repeated `'not-a-number'` parse failure in one call. | pass |
| slow-http (spot-check) | 1 | Still correlated the full `System.Net.Http` request lifecycle in one call, with `Request/Start` → `ResponseHeaders/Start` spanning about **3.06 s** against `/slow-hang?seconds=3`. | pass |

### External-investigation passthrough (issue #704) — validation

This rerun first exercised the recommended operator flow from #737 / PR #739:
start a standalone `BadCodeSample` container, run
`dotnet-diagnostics-cli docker-bootstrap --target-container <name>`, restart
the central MCP with the emitted external-profile config, then
`list_orchestrator(kind="external-profiles")` → `attach_to_pod(...)`.

On this Docker Desktop host, `docker-bootstrap` still failed before creating a
sidecar, but now with the new precise result from #743 / PR #745 instead of the
old generic `TargetNotRunning` misclassification:

```json
{
  "summary": "Target container 'extbs-target' is still running, but this host cannot read its host /proc status file.",
  "error": {
    "kind": "HostProcNotAccessible",
    "message": "docker inspect still reports Running=true and Pid=59463, but /proc/59463/status is not readable from the host. This commonly happens on Docker Desktop, rootless Docker, Docker-in-Docker, or other VM-backed / namespaced Docker hosts where /proc/<pid>/root is not exposed to the outer host namespace."
  }
}
```

That result identified the outer-host `/proc` dependency that #748 later
removed. `docker-bootstrap` now probes UID/GID and the target's inner PID from
a short-lived container in the daemon host PID namespace and points the
sidecar's `TMPDIR` at `/proc/<target-namespace-pid>/root/tmp`, so neither native
Windows PowerShell nor WSL2 needs direct access to Docker Desktop's VM `/proc`.

The manual external-profile fallback still validated the actual passthrough path
end to end against `loh-alloc` in four MCP calls:

1. `list_orchestrator(kind="external-profiles")`
2. `attach_to_pod(profileName="sidecar")`
3. `collect_batch(investigationHandleId=..., requests=[counters,gc])`
4. `detach_from_pod(handleId=...)`

With only the central MCP published on the host, the routed
`collect_batch` call still returned the decisive LOH signal through the
central: **39 Gen2 collections**, **1.025 s** total GC pause, **56.0 ms** max
pause, **7.20 MB** LOH, and **69.1%** GC fragmentation. Compared with the
direct sidecar `loh-alloc` path, this still costs two extra MCP calls (attach +
detach) plus one-time operator bootstrap/restart work, but the environment
failure mode is now clearly diagnosed and correctly documented.

### Notes on the previously held findings

- **Legacy token / loopback scope behavior (#741).** Repeating the published-via-
  `docker -p` sidecar scenario with only `MCP_BEARER_TOKEN=dev-token` now
  matched [`authorization.md`](../authorization.md): live heap retention was
  rejected because the legacy `root` token did **not** satisfy the literal
  `sensitive-heap-read` modifier scope (`principal 'legacy-root' presented
  [root]`). Adding explicit `Auth__BearerTokens__0__Scopes__*` entries
  (`read-counters`, `eventpipe`, `heap-read`, `ptrace`,
  `sensitive-heap-read`, `dump-write`, `investigation-export`) immediately
  re-enabled `inspect_heap(source="live", includeRetentionPaths=true)`. Plain
  CPU `query_snapshot(view="call-tree")` remained allowed in this rerun because
  it stayed within primary scopes, so the current behavior and docs now line up
  without a product change.
- **`cpu-burn` doc drift (#742).** The endpoint lambda remained the decisive
  rooted hotspot on every rerun; occasional crypto children appeared only as
  0–2 sample noise and were not stable enough to treat as the investigative
  fingerprint. The doc fix was therefore the right one: rely on the hot lambda,
  not on a guaranteed visible `SHA256` leaf.

**UX notes.**

- The crash-guard fix is real: the sidecar stayed alive long enough to return
  the final envelope after the target exited, which was impossible in the old
  two-container `--pid=container:<target>` topology.
- The CPU/memory triage fixes are also real: `cpu-burn` and `culture-lookup`
  now surface one-core saturation via `effective-core-usage`, and `leak`
  surfaces bounded intra-window growth instead of a “healthy” verdict.
- `collect_batch` is materially better for LOH churn now that its bounded inline
  summary keeps the Gen2 / LOH evidence up front rather than forcing an
  immediate drilldown for the basic story.
