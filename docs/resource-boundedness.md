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
>
> See also: [`hotpaths/README.md`](./hotpaths/README.md) (CPU/allocation hotpath
> profile per collector — where cycles go *inside* each collector's own code) ·
> [`production-safety.md`](./production-safety.md) (operational profiles and
> data-exposure boundary).

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

### Offline request-latency comparison

The Core-owned request-latency comparison (#1317) is not a collector, but its
caller-supplied evidence is bounded before calculation: each side contains
**1-32 windows**, each window retains at most **256** finite non-negative
millisecond values, and one-shot CLI files are at most **256 KiB** each. JSONL
accepts one bounded **576 KiB** control frame, enough for two maximum-size
window sets plus envelope overhead. MCP likewise rejects either inline
window-set body above **256 KiB**. Window and pair identifiers must be unique
within each side, counts are non-negative, and failed/aborted windows cannot
carry completed latency values.

Exact complete windows must retain every source-completed request. Any known
loss, error, censored request, missing completion, approximation, unpaired
window, failed/aborted window, pair-order mismatch, or insufficient request
count remains explicit and can withhold paired inference. The inferential path
requires at least eight completed requests in each window and eight valid
independent complete window pairs. Its fixed **2,048** bootstrap resamples
operate only over those bounded pair-level deltas, so work cannot scale with an
unbounded raw request stream. Descriptive p50/p95/p99 values use retained
completed requests; failure and incompleteness counts remain separate.

### JSONL structured capture views

The VS Code **Capture Diagnostics** path reuses the existing bounded Core collectors and
one-shot CLI dispatch; it adds no TypeScript event collection. Output projection keeps at
most `topN` (1-100) rows per table, `MaxTextLength=2048` characters per text value, and
`MaxNotes=50` notes. It reports retained-row omissions and text/note truncation counts.
These are presentation limits, not collector limits: retained totals do not establish
exhaustive capture coverage. Results identify themselves as unsaved live-stream evidence.

### Joint request/Activity capture (Part 1)

The Core-only `request-activities` JSONL capability (#1241) owns one union
DiagnosticSource EventPipe session, a 1-300 second requested window, a 30-second
attach budget and the existing five-second stop plus five-second drain budgets.
Cancellation, EOF and replacement stop/dispose the owner. An early source end
ends collection rather than waiting out the requested window. There are no raw
trace files, durable captures, HTTP calls, profiler, ptrace or dump operations.

Insertion retains at most **100** first-accepted pending requests and **200**
first-observed completed Activities, the existing standalone collectors' default
row budgets. Matched tracked request completions remove their pending row and
release that slot. Duplicate starts do not replace a retained row. This is
deliberately **not** the standalone request collector's oldest-pending guarantee;
that collector remains unchanged. Activities never evict, so a noisy prefix can
consume the budget. Missing/malformed request identities or timestamps are
rejected explicitly through parse quality. Activities without valid identifiers
cannot verify a relationship. No Activity tags are requested, parsed or returned
by the joint collector; no query strings, SQL, headers or destinations are added.

The new joint owner's **MaxIdentities=4096** guard is a deliberate additional
bound, not an inherited standalone Activity cap. It tracks seen request-start
and completed-Activity `(traceId,spanId)` identities, including dropped rows, for
the entire one-window lifetime. Keys are strict lowercase nonzero 32/16-hex W3C
IDs and are never display-truncated. Counts saturate at two per identity;
duplicate observations are separately counted. No identity is evicted. Saturation
withholds all verified joins because duplicate detection is then incomplete.
The secondary Activity lookup contains at most 200 entries; every query walks
at most 200 retained rows with chains/depth bounded by that inventory. Temporary
visited sets contain at most 200 keys. A missing, duplicated or cyclic chain
does not become a verified descendant.

Source filters are validated before subscription: 1-20 exact names, at most
200 ASCII characters each, no wildcard or transform syntax. Retained
method/source/operation strings are at most **256** characters; paths are at most
**1024**. Overlength values become an explicit omission marker rather than an
unbounded string or a truncated identity. Controls are normalized, query/fragment
suffixes excluded and existing sensitive-pattern redaction applied. Redaction
expansion is checked against the same caps. `truncatedFields` reports overlength
omissions; application names/paths/IDs may still contain sensitive information.

Cardinality and string caps imply less than **4 MiB conservative accounted
retention**, even when all three inventories are full. `accountedRetainedBytes`
includes 128 KiB fixed overhead, 256 + 4 bytes per identity-key character for
each identity slot, 512 + 4 bytes per retained row-string character, and another
256 bytes per Activity for the secondary lookup/close-time copies. This accounts
for bounded object/index lifetime and string storage, not just output row count.
It is **not** a measured CLR heap/RSS guarantee, and excludes the separate
64 MiB EventPipe circular buffer, decoder transients, runtime overhead and
serialization. Worst-case full-inventory/text tests assert that accounted limit.

Capture replies include at most 100 request rows, not the full Activity array.
Queries return at most **100** rows and explicitly report retained requests,
retained Activities, matching total and projection omissions (e.g. 200 retained,
100 returned, 100 omitted). JSONL joint frames have a hard **1 MiB UTF-8** check;
oversized output fails explicitly. The serializer's escaped-text amplification
is covered by worst-case Unicode tests. Serialization allocates one bounded JSON
string outside retained-artifact accounting; writes have a five-second budget.
Only one joint artifact is retained per CLI connection for ten minutes, with a
timer that reaps it at expiry. Replacement and disconnect cancel/drain first,
then invalidate it; there is no multi-capture history or automatic persistence.

Quality exposes observed starts/tracked completions, request and Activity drops,
parse failures, invalid/duplicate IDs, index saturation, retained identity count,
text omissions, source loss (unknown is null), actual header/window, completion
and process-lifetime provenance. A quality limitation may withhold verification
while preserving the observed structural relationship with an explicit reason.
No matching Stop records do not establish absence of open/uninstrumented work.
External DiagnosticSource-session coexistence and the full runtime matrix are
not established by the controlled single-owner tests.

### EventPipe EventSource live events (#1201)

The CLI `event_source` live kind is a continuous Core-owned EventPipe session over explicitly
selected providers; it is separate from counters/GC sessions. Provider count is 1–8, provider names
are at most 256 characters, the event level is 0–5, and keyword bitmasks travel as signed-64-bit
decimal strings. The EventPipe circular buffer is 64 MiB.

Projection is performed inside the EventPipe callback before enqueueing, but only after the
queue's pending-count check. Offers rejected because the queue is full still consume a sequence
number and increment the drop count without decoding or serializing their payload. The retained-byte
check follows projection because the observation's size is not known beforehand. This avoids
spending the drain budget projecting events that cannot be retained; it does not guarantee that
every high-volume stream can stop within the unchanged shutdown deadline. One event retains at most
32 payload fields, 128 characters per field name, 1,024 characters per value, 2,048 aggregate
payload name/value characters, and 256 characters for each metadata string. Values are formatted
only for supported primitive-like types; unsupported types get an explicit marker, and arbitrary
`ToString()` or recursive formatting is never invoked. The serialized observation frame is capped
at 16 KiB UTF-8. `metadataTruncated`, `payloadTruncated`, `omittedPayloadFields`,
`omittedPayloadCharacters`, and `unsupportedPayloadValues` make projection omissions explicit.

The Core event queue defaults to 32 pending observations, with an explicit
capacity range of 1-256 and a separate 4 MiB conservative retained-byte cap, enforced
at insertion. Core and composed queues retain drop-newest overflow behavior.
The smaller Events default trades more explicitly counted rejections for less
FIFO residence under slow consumption; it is not a latency guarantee.
Generated workload counts are not retained-observation counts: a normal stop drains
accepted observations but cannot replay rejected offers. Low-volume target-side
EventPipe buffers may also remain unflushed until stop; transport loss of zero does
not establish live delivery before that boundary. The managed-exception schema
test therefore retains all 167 uniquely identified workload exceptions across
small exception-only sessions, each with its own observed exception witness,
followed by stop/drain and zero local/source loss assertions. A separate blocked-handler
test verifies one in-flight witness plus 64 pending exceptions and exactly 103
drop-newest rejections from a 167-exception burst, without demanding exhaustive
delivery through that deliberate loss.
Accounted size estimates use four bytes per retained string character plus per-event
overhead and 128 bytes per payload field; this is an implementation budget, not a measurement of
managed heap size or RSS. When `event_source` is composed with other live kinds, the shared
dispatch queue has its own 4 MiB byte cap and uses the maximum requested kind
capacity, capped at 256. Events-only default requests use 32; other live kinds
can raise the shared capacity, without changing their existing defaults or
the Events child capacity. Their cumulative
`coreQueueDropped` and `cliDispatchQueueDropped` counters are separate. VS Code
explicitly requests 32 Events observations. The extension host's operational
window is 64 events, including its single in-flight batch, within unchanged hard
limits of 256 events / 1 MiB (accounted per event as twice serialized UTF-8 bytes plus 1 KiB object
overhead), sends one acknowledged webview batch at a time (at most 64 events / 64 KiB), and limits
bridge updates to 10 per second. The Events connection continuously drains and validates CLI
stdout even when the host window is full or the webview withholds acknowledgement.
New event offers exceeding either host cap are rejected at insertion and counted in
`hostDeliveryDropped`; admitted FIFO events, including the in-flight batch, remain retained.
This explicitly replaces host-capacity reader backpressure with host drop-newest: it can increase
host drops while avoiding a capacity-induced backlog of decoded/serialized frames outside the
host window. It is not a latency or RSS guarantee. The bounded JSONL reader is not paused by host
capacity or ACK state, and its deferred chunk does not grow while waiting for an ACK.
Quality frames continue to be consumed and coalesced into one latest snapshot; control and
terminal frames remain readable independently of delivery ACKs. A matching ACK frees admitted
events and permits the next bounded batch; it does not resume or gate protocol reading.
Counters/GC connections and Core/CLI overflow policies are unchanged.

The webview retains at most 2,000 events / 8 MiB, mounts at most 100 table rows per page, searches
at most 256 characters, and keeps at most one selected event. UI accounted event bytes use twice
serialized UTF-8 bytes plus 1 KiB object overhead and bound the selected event together with the
retained window; selection does not keep an evicted duplicate. Oldest entries are evicted first.
Pause-following stops page jumps, not collection or bounded retention; Clear removes retained UI
events but does not stop the source. The CLI reports EventPipe source loss as unavailable (`null`)
while unknown and supplies the final count if TraceEvent reports one. Source loss, Core queue drops,
CLI dispatch drops, and extension-host delivery drops are distinct layers, not additive estimates.
Payload omissions are separate per-event projection facts.
When event-data bytes are present but TraceEvent exposes no decoded payload fields,
`payloadFieldsUnavailable` reports that observed condition without inventing fields,
an unsupported-value count, or an explanation for the decoder's behavior.

These are retention/protocol limits, not evidence that a specific machine can sustain a particular
event rate, RSS, or interactive latency. TraceEvent allocation, the EventPipe circular buffer,
runtime/provider CPU work, and VS Code rendering still have workload-dependent costs.
TraceEvent decodes provider payload values before the callback can inspect them. Projection reads
only the bounded field set but cannot cap that upstream per-event decoding allocation.

### Managed object and array drilldown

ClrMD object reads retain at most 256 expanded fields with nested value-type depth 3.
Array reads inspect one requested zero-based linear range of at most 8 elements, never the whole
array. Struct-element fields share a single 256-row budget across that range, rather than
allocating 256 rows for each element. Object references are returned as addresses and are not
recursively traversed; nested inline structs are flattened to dotted field paths. String previews
remain capped at 256 characters. Array-rank handling is limited to the CLR maximum of 32.
Warnings report field/depth budget hits, individual read failures and the count of array slots
outside the inspected interval. Changing ranges is a separate read; no aggregate array cache is
built. The extension's bounded history retains metadata-only pages, not sensitive previews.

### Heap type-instance enumeration

The Core ClrMD query returns at most **100** exact-type object rows per request (the extension asks
for 50) and visits at most **250,000** heap objects after the supplied continuation point. Both
limits are enforced during enumeration: matching addresses are appended only while the page has
capacity, and the walk stops when either bound is reached. The query never materializes the full
matching inventory and never calculates retained size. Results report `scannedObjects`, whether
the heap end was reached, an opaque next cursor, structured quality, and a warning naming the cap
that stopped the page.

The cursor is bound to the owning ephemeral handle, snapshot provenance and exact type identity.
Dump pages enumerate segment/address order over the same immutable file and are stable. Live pages
re-attach independently; their cursor prevents accidental cross-handle/type reuse but cannot make
object addresses stable across GC. The response therefore labels live consistency as best-effort
and warns that logical instances can be skipped or repeated between pages. Gcdump has no object
addresses and fails explicitly. The extension retains only the pages the user requested for the
currently selected type; a new capture or type selection replaces that UI state, and the existing
CLI handle lifetime/connection ownership remains the authoritative availability boundary.

### Allocation profiling

The EventPipe allocation sampler uses `GCAllocationTick`, not per-object instrumentation.
It retains separate merged tick-count and estimated-byte trees; existing MCP/text queries keep
tick semantics, while the VS Code/JSONL explorer explicitly confirms estimated-byte weighting.
Navigation reuses the artifact and projects at most 64 nodes/depth 8.

Collection writes at most `MaxTraceBytes=67108864` (64 MiB) before conversion to ETLX.
The copy task is watched during collection, so a writer failure starts stop/drain immediately,
not after the requested duration. Copy failures during rundown/drain also propagate.
Aggregation checks `MaxAllocationEvents=100000`, `MaxAllocationTypes=4096`,
`MaxAllocationFrames=16384` (distinct frame keys and, independently, nodes in each tree),
and `MaxAllocationStackDepth=128` before the next insertion. Site/identity dictionaries are
bounded by retained frame keys. At most one current stack is materialized.
Limit hits name the cap, fail explicitly (`ResourceLimitExceeded` through the sampling use case),
and return no partial profile or handle. Lost-event traces also fail explicitly.
Temporary raw traces and ETLX files are cleaned up through the normal capture lifecycle.

These limits bound retained application aggregation and raw input size, **not peak process RSS,
ETLX output size, conversion time or target overhead**. TraceEvent conversion still constructs
indexes before aggregation; runtime rundown, the 256 MiB EventPipe circular buffer and capture
duration (maximum 5 minutes) have separate costs. Sampling can remain expensive under heavy
allocation or large runtime inventories. Prefer short, deliberately scoped captures.

### Managed capture package lifecycle boundary

The artifact-root `captures/` subtree is reserved for durable capture packages.
Generic artifact listing, file access, deletion and the raw-artifact TTL reaper
must not expose or mutate package members. Capture-specific operations own their
authorization, reader/writer leases, retention and deletion instead. A private
`.capture-store` marker preserves this boundary when a CLI command re-roots to
a package directory or reaches it through a symlink alias.

This reservation is not a filesystem quota or protection from an operator who
can directly modify the storage directory. It prevents ordinary diagnostic file
operations from bypassing the managed package lifecycle. Capture implementation
and release acceptance are tracked separately in #1001, #1007 and #1005.

### VS Code sharing and JSONL CPU export

CPU JSONL captures register their existing trace artifact in the shared bounded
handle store (20-minute TTL, default 32 entries); the protocol adds no independent
artifact dictionary. The extension retains the latest result and CPU export
reference, plus at most 20 host-held address-navigation results per live/dump panel
for Back/Forward sharing. A new capture clears that cache; missing/evicted results
cannot fall back to exporting another result. The extension serializes CPU exports.
CPU export requests time out after 60 seconds;
an unsettled request closes its CLI connection. Raw traces retain the sampler's
existing artifact-root storage/reaper policy.

Measurement-only exports retain at most 2,000 numeric/null measurements, examine
at most 200 entries per array, and stop descending after depth 10 or 10,000 visited
nodes. An explicit omission count includes redactions and presentation limits.
Names, dynamic keys, object values, paths, symbols and unsafe integer values are
not exported. Saved comparisons use the existing 8-Mi-character/60-second CLI
runner, validate at most 2,000 metrics and display the first 200 with an omission
notice; arithmetic and compatibility remain Core-owned.

### EventPipe CPU code-version attribution (`code-versions`, issue #1075)

`CodeVersionCensus` keeps at most `MaxPublishedVersions` (262144) compact published-range records
(de-duplicated by method id + start address, so rundown `DCStart`/`DCStop` count once) and at most
`MaxDistinctLeafAddresses` (16384) distinct leaf instruction pointers with counts. Both caps are
enforced at insertion inside the trace-conversion pass; hits are reported in `notes[]` with the drop
count. Only leaf frames are attributed, so per-sample cost is one dictionary update.

### EventPipe JIT inlining census (`inlining`, issue #1076)

Opt-in via `captureInlining` (JitTracing keyword, Verbose level). `InliningCensus` keeps at most
`MaxInliningRecords` (65536) aggregated rows keyed by (code version, inliner, inlinee, outcome,
reason), at most `MaxPendingThreads` (1024) threads, at most `MaxPendingDecisions` (65536) buffered decisions across all of them with an in-flight compilation and at most
`MaxDecisionsPerCompilation` (4096) buffered decisions per compilation; names are truncated to
`MaxNameLength` (512) characters. All caps are enforced at insertion inside the trace-conversion
pass. Dropped, replaced or never-joined decisions are counted as unattributed and each cap hit is
reported in `notes[]` with the constant name and the drop count. The 256 MB circular EventPipe buffer
(`EventPipeCpuSampler.CircularBufferMB`) is unchanged; the extra JitTracing volume is the reason the capture is opt-in.
When that buffer overflows during a `captureInlining` capture, the trace's lost-event count is reported
in `notes[]` (the CPU samples and decisions are then under-counted); captures without loss add no note.
The measured volume and overhead on one machine are in
[`research/inlining-capture-overhead.md`](./research/inlining-capture-overhead.md).

### Pure efficiency refactors — no data loss at any cap

| Collector | What changed | Result vs. before |
|---|---|---|
| `ClrMdDumpInspector.WalkGcHandles` (`Dump/`) | GC handle aggregation is now streamed into `GcHandleAggregation` incrementally instead of materializing every handle into a `List<GcHandleSample>` first | Identical — same buckets/counts, lower peak memory during heap inspection |
| `ClrMdDumpInspector.WalkStaticFields` (`Dump/`) | Maintains a bounded top-N structure while walking instead of collecting every static reference then `Take(topN)` | Identical — same top-N rows for the same `topN` value |
| `GcActivityCorrelator.Correlate` (`Collection/`) | Sorted-window scan, shared UTC interval union and bounded top-N heap; at most 100 displayed pause details per span | v2 correlates only validated fully-suspended intervals, never collection elapsed. Span-time union remains exact after output-detail caps. Activity candidate loss, pause-detail loss, unknown provenance and window gaps are distinct; rankings are not lower bounds |

`GcDumpTypeAggregator` folds the observed EventPipe node stream into one row per
type id. Its requested `SnapshotTopTypes` is an output projection applied after
aggregation, not an insertion-time retention cap; structured evidence quality
reports the number of lower-ranked type rows omitted from the snapshot. It must
not be described as collector eviction. The collector retains no object-edge/root
graph.

### Real retention trade-offs — bounded with explicit notes

| Collector | Cap | What's kept | What's dropped/merged | Signal |
|---|---|---|---|---|
| `EventPipeInFlightRequestCollector` (`Requests/`) | `maxRequests` (caller-supplied) | The **oldest** still-pending requests (most likely to explain a stall) | Newer request starts once the cap is hit | `notes`: `"Dropped {n} newer in-flight request start event(s) after reaching maxRequests=..."` — `Requests`/`InFlightCount` become lower bounds |
| `EventPipeGcCollector` (`Gc/`) | `maxEvents` 1..100,000 (default 200) independently caps collection rows, heap-stat samples and suspension intervals; pending collections 256, suspension states 128, runtime identities 16, ignored numeric reasons 16 | First N detail rows; aggregates over valid pairs continue after row caps. Pending collection key is CLR + unsigned count; suspension key is CLR + emitting thread. Small copied facts, no TraceEvent clones or TTL | New pending keys are rejected at insertion when full; existing keys can complete. No invented pause endpoint. Limitation categories have fixed names and saturating 64-bit counters; legacy 32-bit collection counters saturate explicitly | `DroppedEvents`/`DroppedHeapStats` concern collection detail only; `suspension.droppedIntervals` concerns pause detail. Fixed `limitations` entries name exact cap/loss/censoring categories. Transport loss or ambiguous pairing makes authoritative timing unavailable, not a lower bound. Missing optional restart-stop retains the primary phase but reports envelope loss; multiple CLR instances degrade process-wide attribution |
| `EventPipeContentionCollector` (`Contention/`) | `MaxTrackedEvents = 200` | The **longest-duration** contention events | Shorter events past the cap | `notes`: `"Retained the N longest contention event(s) after reaching the in-memory cap of 200; M shorter event(s) were dropped."` |
| — same collector, distinct monitor ids | `MaxTrackedMonitorIds = 4096` | First 4096 distinct monitor identities | `DistinctMonitors` becomes a lower bound past the cap | `notes`: distinct-monitor overflow note |
| `EventPipeJitCollector` (`Jit/`) | `MaxTrackedMethods = 2048`, plus separate caps for pending starts (4096 ids × 8 starts/id), IL-map correlation (4096), R2R-miss correlation (4096) | Top-line JIT counters always stay exact; per-method detail for the **first** 2048 distinct methods observed (first-come, not ranked) | Per-method rows for methods observed after the cap (still counted in aggregate totals) | `notes`: one message per exhausted cap, each naming the exact limit and drop count |
| `DbEventAggregationState` (`Db/`) | `MaxTrackedCommandAggregates = 256`, `MaxTrackedNPlusOneIncidents = 256`, `MaxTrackedPendingCommands = 2048` (+ a TTL for pending commands) | The **first** 256 distinct command shapes / N+1 keys observed (first-come, not ranked); up to 2048 in-flight (unmatched `BeginExecute`) commands, each expiring after a fixed TTL if never completed | Additional distinct command shapes fold into a single overflow bucket; pending commands beyond the cap evict oldest-first; pending commands stuck past the TTL are dropped (assumed abandoned) | `notes`: `"Aggregated N additional distinct DB command shape(s) into the overflow bucket..."`, `"Evicted N oldest pending SqlClient command(s)..."`, `"Expired N pending SqlClient command(s) that exceeded the M-minute completion TTL."` |
| `EventPipeNetworkingCollector` (`Networking/`) | 256 host/path buckets; 4096 pending starts per HTTP/DNS/TLS kind with 2-minute TTL; 65536 whole-session remembered IDs per kind | First 256 operation groups; bounded pending values plus exact identity history, including expired/evicted/completed IDs | Overflow bucket for groups; empty/duplicate/reused IDs excluded. History saturation permanently stops pairing for that kind instead of forgetting tombstones and accepting late stops | `Correlation` accounting on artifact/all queries plus exclusion notes and qualified summaries; see [networking correlation](networking-correlation.md) for the start-outcome partition and recovery limits |
| `EventPipeKestrelCollector` (`Kestrel/`) | Same shape as Networking: `MaxTrackedOperationGroups = 256`, `MaxPendingActivities = 4096` (connections/requests/TLS), `MaxQueuePoints = 4096` | The **first** 256 distinct method/path/version groups observed (first-come, not ranked); bounded pending correlation state; bounded queue-depth timeline | Extra request groups fold into `(other)`/`(overflow)`; queue-length samples beyond the cap are dropped (timeline becomes sparser, not wrong) | `notes`: overflow/evict/drop messages mirroring Networking |
| `EventPipeThreadPoolCollector` (`ThreadPool/`) | `MaxTimelineSamples = 4096` per series (worker, IOCP, hill-climbing) | The most recent 4096 samples per series (ring buffer — oldest evicted first) | Older timeline samples once a series exceeds 4096 points | `notes`: `"Dropped N worker-thread timeline sample(s) after reaching the in-memory cap of 4096."` (one per series), plus bounded `Quality` entries in the `CollectorEviction` category with the affected count and series scope |
| `EventPipeStartupCollector` (`Startup/`) | `MaxRetainedAssemblyLoads`/`MaxRetainedModuleLoads`/`MaxRetainedDiEvents = 1,000` each, `MaxRetainedTimelineEvents = 2,000`; capture duration hard-capped at `MaxStartupDurationSeconds = 30` | The **first** 1,000 assembly/module/DI events observed (first-come, not ranked); first 2,000 timeline rows; at most 30s of capture | Additional loader/DI events past the per-category cap; capture duration requests above 30s are rejected outright | Truncation surfaced via the snapshot's own notes; duration cap enforced at the use-case validation layer |
| `EventPipeActivityCollector` (`Activities/`) | Unfiltered: `maxActivities`; targeted: independent `maxMatchedActivities` (both default 200, >= 1) | First N completed stop events after source filtering, or first N matching a normalized non-zero W3C `traceId`; the callback uses bounded `ActivityRetentionState` before insertion | Unmatched events are counted but never retained; matching events beyond the effective cap are dropped. No raw spill to disk | `retention` carries applied filter, effective cap, observed/matching/retained matching/dropped matching/non-matching counts and nullable `retentionLimited`; summaries, trace drilldown and distributed coverage preserve it. Missing legacy metadata is unknown. Observed minus retained is **not** necessarily retention loss. Retained counts may be lower bounds; residuals/rankings are not, because missing children can inflate parent residuals |
| — opt-in HTTP destination correlation | `MaxIdentities=16384` exact whole-window W3C tuples; independent `MaxAuthorities=4096`, host <=253 chars | Identity history includes completed/unmatched/ambiguous entries and is never evicted. Source/trace filtering precedes insertion; retained stop cap does not stop identity accounting. No state/subscription when off or HTTP source-excluded | Identity saturation withholds **all** destination attribution, including prior pairs, rather than forgetting duplicate/reuse history. Authority saturation withholds subsequent values but keeps identity history. Duplicates/reuse/conflicts and loss/non-normal completion are conservative unavailability, not guessed joins | `httpDestinationCorrelation` records caps, rejected-event counts, starts/stops, invalid/duplicate/conflict/nonmatching events and unmatched identities; summaries name cap counts. `destination.availability` explains per-span absence. Null legacy metadata is unknown. [Full semantics](http-activity-tags.md#correlation-limits-and-accounting) |
| `MethodParameterCaptureCollector` (`MethodParameters/`) | `MaxEvents`/`CaptureLimit` (caller-supplied), now enforced **locally** in the observer, not just sent to the profiler | Events up to the caller's requested cap | Additional invocations once the local cap is reached, even if the profiler over-delivers or duplicates | `StopReason = "max_events_reached"` + `DroppedCount` in the summary (note: `"value-cap"` is a *different* signal, used only when an individual captured value is truncated for exceeding the per-value byte limit, not for the event-count cap) |
| `ClrMdTaskTimerAnalyzer` (`Dump/`) | `MaxTrackedTimerAddresses` (`DefaultMaxTrackedTimerAddresses = 200,000`) | Exact dedup for the first 200k distinct timer addresses seen during a heap walk | Beyond that, dedup becomes approximate (a timer leak large enough to hit this is itself the finding) — totals may slightly over-count duplicate timer containers | `TimerAddressTrackingTruncated` flag → note: *"Timer address de-duplication hit its safety cap ({cap}); totals may slightly over-count duplicate timer containers beyond that point."* (a separate, unrelated note — *"Timer callback rows are truncated to the top groups retained in the snapshot."* — covers ordinary top-N row truncation, not the address-dedup cap) |
| `ComWrapperAggregation` (`Dump/`, issue #1118) | `DefaultTopTypesPerKind = 20` ranked CCW/RCW type rows; `MaxCleanupBacklogSample = 50` inline RCW cleanup-queue entries | Every CCW/RCW observed during the heap walk contributes to the exact `CcwCount`/`RcwCount`/ref-count totals and disconnected/WinRT counters unconditionally — only the **ranked top-types rows** and the **inline cleanup-backlog sample** are capped | Type rows beyond the top 20 per kind fold into the exact totals without a dedicated row; `ClrRuntime.EnumerateRcwCleanupData()` entries beyond the first 50 are still counted in `RcwCleanupBacklogCount` but not listed inline. `EnumerateSyncBlockCleanupData()` is count-only (no per-entry retention at all) | `notes`: *"RCW cleanup-backlog sample is truncated to the first 50 of {count} total pending entries; the count above is exact."* plus a disconnected-RCW leak-smell note when `RcwDisconnectedCount > 0` |
| `HeapIntegrityAggregation` (`Dump/`, issue #1119) | `MaxCapturedCorruptions = 500`, enforced at insertion time inside `ClrMdDumpInspector.WalkHeapIntegrity` while enumerating `ClrHeap.VerifyHeap()` | The first 500 `ObjectCorruption` entries observed from ClrMD's own corruption walk (first-come, not ranked); `TotalCorruptions` keeps counting past the cap so the true count is never understated | `HeapCorruptionStat` rows beyond the cap (still counted in `TotalCorruptions`, just not returned in `Corruptions`) | `notes`: `"ClrHeap.VerifyHeap() found N corrupted object(s); retained the first 500 after reaching HeapIntegrityAggregation.MaxCapturedCorruptions=500. M additional corruption entries were omitted."`; dump-only, opt-in (`verifyHeap=true`), never folded into the default heap walk |
| `ObservedFreeBlockAggregation` (`Dump/`, issue #1270) | Per segment, `TopRangeLimit = 8` directly observed `obj.IsFree` records at least `MaterialBlockMinimumBytes = 4096`; fixed-array min-heap enforces the bound at insertion in O(log K) | Exact largest material record and exact observed count, plus the eight largest ranges ordered by size then address | Material ranges below the retained top eight; sub-4-KiB free records remain represented only by the existing aggregate `FreeBytes`/`FreeObjectCount` compatibility fields | Every segment reports observed/retained/omitted counts, threshold/cap, `complete`/`partial`/`indeterminate`, and limitations. ClrMD walks are `partial` after unresolved objects and otherwise `indeterminate`; warning-free is never promoted to complete. The projection makes no allocation-success, liveness, retained-size, pinning, or mosaic claim |
| `ClrMdHeapWalker.AggregateException` / `BuildRetainedExceptionStats` (`Dump/`, opt-in `includeRetainedExceptions`, hooked into the same single-pass object loop as the other opt-in heap aggregations above — no second `EnumerateObjects()` pass) | Distinct exception-type aggregation bounded at `Math.Max(SnapshotRetainedExceptionTopN * 32, 4096)` entries (`SnapshotRetainedExceptionTopN` defaults to 100); per type, at most `MaxRetainedExceptionSamplesPerType = 5` individual instance samples; each sample's `Message` truncated to `RetainedExceptionMessagePreviewLength = 256` chars | Exact per-type instance count and total retained bytes up to the moment the type-count cap is reached; up to 5 bounded-preview instance samples (`Message`, `HResult`) per retained type, ranked to the requested top-N on render | Same first-come, all-or-nothing cap behavior as `IncludeDelegateTargets`/`IncludeDuplicateStrings` in the same walker: once the cap is hit, aggregation stops entirely — new exception types are no longer admitted **and** already-tracked types stop accumulating further instances/bytes, so counts become a lower bound from that point on, not merely "no new types" | `warnings`: *"Retained-exception aggregation hit cap of {cap} unique types — results are truncated to the types seen so far."* The `retained-exceptions` view itself reports `ViewNotCaptured` when `includeRetainedExceptions` was not requested, mirroring `gchandles`/`finalizer-queue` — no redaction gate is applied to `Message`, matching those views' unredacted pattern rather than `duplicate-strings`/`object`'s sensitive-value gate |
| `RequestsNowCollector` (`ProcessDiscovery/`) | Snapshot-capture queue bounded at `SnapshotQueueCapacity = 256`, non-blocking `TryWrite` | Up to 256 concurrently-queued thread-snapshot captures per window | When the queue is full, the newly-started request is **removed from the result entirely** (not just its stack) — the final snapshot only includes requests with a captured call stack (`TopFrames.Length > 0`) | `notes`: `"Dropped N request thread snapshot capture(s) after reaching SnapshotQueueCapacity=256; request rows and counts are incomplete lower bounds..."`; the existing debug log is also preserved |
| `MvidReader` singleton cache (`CpuSampling/`) | `DefaultCapacity = 128` (FIFO eviction) | The 128 most-recently-resolved module identities | Older module→MVID mappings are evicted and re-resolved on next use (cache miss, not correctness loss) | N/A — pure cache, re-resolution is transparent |
| `CpuReplayStackObservationWriter` (`CpuSampling/`, opt-in durable EventPipe CPU replay only) | `MaximumStackDefinitions = 4096`; `MaximumStackCacheBytes = 8 MiB` for retained UTF-16 stack keys/reference names plus a conservative per-entry allowance | First admitted complete stack definitions; **every** source sample retains its own thread, relative timestamp and weight, with an artifact-local reference | No source sample is replaced by top-N. On dictionary saturation, rejected definition admission, or existing 128-frame/64-KiB stack-encoding truncation, samples use explicitly marked inline-stack fallback under the unchanged record/capture budgets | Bounded summary/artifact `Notes` name the cap and fallback counts; each fallback has `stackReferenceFallback`. Definition/source admission failures remain counted in capture quality |
| `MemoryDiagnosticHandleStore` (`Drilldown/`) | `Diagnostics:HandleStore:MaxEntries` (default 32, validated `1..1024`) live artifacts + exactly `4 × MaxEntries` lightweight tombstones | Live artifacts whose TTL deadline is latest; ties preserve newer registrations. Tombstones retain only id/reason/time/pid/kind | After TTL cleanup, capacity evicts the earliest-expiring artifact. Old tombstones age out FIFO, after which that handle is reported as unknown. Artifacts are disposed and never retained in tombstones | Warning log for capacity eviction; information logs for registration/TTL; disposal-failure warning; `DotnetDiagnostics.Core.DiagnosticHandles` counters; `query_snapshot` / CLI `query` return `HandleCapacityEvicted`, `HandleExpired`, or `HandleNotFound` |
| `collect_batch` correlated counter projection (`Mcp/Tools/CollectBatchSalientEvidence`) | `MaxInlineCounters = 18`; paired counters+GC requests subscribe only to `System.Runtime\dotnet.gc.collections` with `Gen2MeterMaxTimeSeries = 8`; at most two fixed explanatory notes and one fixed-size `gen2Evidence` record | The normal headline counter set; when paired GC reports Gen2 activity, also `gen-2-size`, `loh-size`, and `gc-fragmentation` when emitted; only the generation-tag variants of the one GC-collections Meter | Every other counter stays only in the existing full counter artifact behind the handle; no second full table is materialized; unrelated runtime Meter instruments are never enabled by the batch | Inline note names the counter cap and full captured count; Meter time-series overflow uses the counter collector's existing explicit note; `gen2Evidence` labels interval-delta, rate, process-cumulative, and collector-window scopes separately |
| `collect_batch` native-contention correlation (`Mcp/Tools/CollectBatchSalientEvidence.ApplyNativeContentionEvidence`, `Core/NativeLockContention/NativeLockContentionUx.CorrelateBatchEvidence`, issue #855) | No new session/state is opened — the correlation reads only the two already-bounded artifacts each entry's own collector produced (`NativeLockContentionArtifact`, capped by that collector's own `topN`/`samplePeriod`; `OffCpuSnapshotArtifact`, capped by the existing off-CPU/syscall-companion budgets documented above) and returns a single fixed-shape `nativeContentionEvidence` record — no per-span or per-callsite list is duplicated into the projection | One merged `NativeContentionEvidence` record (level, sampled lock-call count, span/micros counters, evidence sources/rationale) | Neither entry's own full artifact/handle is affected; each collector's own cap/degradation notes stay exactly as that collector already reports them | The merged record's `Level` is always the off_cpu entry's own level — native-lock activity never elevates it (evidence-taxonomy invariant); a trailing `Summary` clause names whichever of the two entries did not run or failed, so partial success (one collector unavailable, denied, or errored) is explicit without special-casing the per-entry `Error` that already reports the failure |
| Perf/ETW off-CPU & CPU samplers (`OffCpu/`, `CpuSampling/`) | `perf record --max-size` (already capped pre-fix; Linux off-CPU now uses independent caps: 128 MiB for sched_switch DWARF and 64 MiB for the optional target-scoped raw-syscall companion) + the parser/aggregator streams `perf script`/ETW output instead of buffering the full text/span list before aggregating. CoreCLR perf symbol tracking retains at most `JitMapEmitter.MaxTrackedMethods` (200,000) method-load/rundown events and `MaxTrackedModules` (4,096) module mappings. | Same final aggregate (call tree / stack rollups) as before for CPU and sched off-CPU. Off-CPU syscall labels and managed JIT symbols are best-effort enrichment. | Base stack rows are not dropped by syscall failure/truncation or symbol failure; unresolved frames remain raw. Overlapping/reused JIT ranges are omitted conservatively because an address alone cannot establish code lifetime. Methods whose module mapping was dropped retain address/name evidence but omit module metadata. | CPU notes report unresolved JIT candidates, ambiguous ranges, and exact method/module cap/drop counts. Off-CPU notes explicitly name sched/raw perf.data cap hits; syscall companion failures say stacks were returned without syscall labels. |
| Windows ETW native leaf provenance (`CpuSampling/NativeLeafCoverageCollector`) | First 4,096 distinct native leaf PCs and first 512 ETW-loaded module identities | Exact captured image/PDB identity and resolution reason for retained modules/PCs; aggregate native leaf and verified-range sample weights continue across the full capture | New distinct PC detail and later module rows are dropped at insertion after their independent caps. Repeated samples for a retained PC remain exact. Sample weight outside the retained PC/module inventories is counted explicitly; distinct-PC coverage is incomplete after a PC cap hit. | `NativeLeafCoverage` on the retained CPU trace artifact (not the `collect_sample` summary, to keep the tool catalog budget) reports both caps, retained module/PC counts, verified-range counts, unretained sample weights and per-module identities. Notes name `NativeLeafCoverageCollector.DefaultRetainedPcLimit` / `DefaultRetainedModuleLimit` when either cap is hit. |
| Perf CoreCLR JIT frame symbolization (`CpuSampling/PerfScriptFrameParser.cs`, `OffCpu/JitMapEmitter.cs`) | JIT method ranges are kept only for the current parse window; repeated IP lookups use a bounded 16,384-address cache | Frames whose raw instruction pointer falls inside the captured JIT map are stamped with existing `MethodIdentity`; `/memfd:doublemapper` / bracketed unknown displays are replaced only after address match | No target modification or persistent instrumentation. If the map is unavailable or an address does not match, the raw perf frame is kept and supported summaries add a note | Notes on native-alloc/native-lock/off-CPU summaries when unresolved JIT-like frames were observed |
| Off-CPU syscall attribution `SyscallIntervalIndex` (`OffCpu/SyscallIntervalIndex.cs`, issues #829/#839/#840) | Two layered caps, both enforced **at insertion**, never buffered-then-truncated: (1) `PerfSyscallScriptParser.MaxParsedEvents = 1,000,000` raw enter/exit lines while parsing the separate target-scoped `raw_syscalls:sys_enter`/`sys_exit` companion capture (the parser keeps draining `perf script`'s stdout past the cap so the child process is never blocked on a full pipe, it just stops appending); (2) `SyscallIntervalIndex.MaxIntervals = 500,000` paired intervals built from those events. The companion recording itself has a separate 64 MiB `perf record --max-size` cap and carries no callgraphs. | Parsed events / correlation intervals up to each cap; every off-CPU span is still emitted (uncorrelated spans just get no syscall label rather than being dropped). Closed futex/native-sync spans may be classified as `confirmed-blocking` native contention evidence only when the capture is not degraded. | Additional raw lines / enter-exit pairs once a cap is hit; those later spans simply don't get syscall attribution (never crashes/degrades the base off-CPU capture). Any cap/truncation/censoring/degradation note downgrades native-contention evidence to `probable-blocking` rather than confirmed. | `notes`: `"Syscall correlation stopped parsing raw_syscalls events after reaching the 1,000,000-event budget; N event(s) beyond that point were ignored, ..."`, `"Syscall correlation hit the 500000-interval cap; N syscall interval(s) were dropped, so some off-CPU spans may be missing a syscall label."`, or an explicit syscall companion failure/no-events note. The shaped `nativeContentionEvidence` payload also reports closed/censored span counts, durations, and rationale. |
| Perf native-allocation sampler (`NativeAlloc/PerfNativeAllocSampler.cs`) | Same streaming parser as CPU/off-CPU, **plus** a hard `PerfScriptSampleBudget = 250,000` samples | The first 250,000 parsed perf-script samples | Parsing stops once the budget is hit; hotspots reflect only the processed prefix, not the full allocator-hot run | `aggregate.Truncated` → note: *"Stopped parsing perf script after 250,000 samples to keep allocator-hot captures bounded; hotspots reflect the processed prefix only."* — **this is a real trade-off, not a pure refactor**, since a single very allocation-heavy run can exceed the budget and silently-in-spirit (though not silently-in-notes) miss later hotspots |

### Durable CPU stack definitions and occurrences

The CPU sampler replays its temporary EventPipe transport **after** live collection
has stopped and drained. A replay-capable recording sink uses normalized,
versioned records instead of repeating the same stack and interpretation metadata
in every observation:

- `category="definition.cpu-stack.v1"` is a **non-occurrence** record
  (`sourceOccurrence=false`). Its indexed `name` is a unique
  `cpu-stack-v1:<replay-id>:<sequence>` definition key. The row retains the
  leaf-first structured stack, module/method strings and available identities,
  clock domain, weight unit, truncation flag, and backend evidence classification.
- `category="sample.cpu.eventpipe.stack-ref.v1"` is one **source occurrence**
  (`sourceOccurrence=true`). Its indexed `name` references the definition key in
  the **same artifact**. OS thread ID, `sourceSeconds`, and sample `weight` remain
  per occurrence; the common UTC timestamp remains null.
- To resolve a sample's stack after reopening, query the **same artifact ID** with
  `category="definition.cpu-stack.v1"` and `name=<sample.name>`. Those existing
  category/name indexes retrieve the definition without needing a raw trace.
  Enumerating definitions also exposes their method/module/identity fields for
  method-oriented follow-up; query samples by the chosen definition key.
- `sample.cpu.eventpipe` remains the self-contained inline form for legacy sinks
  and explicit fallback. A normalized reader must not treat definition records
  as samples or sum their fields as sample weights.

Each definition must be admitted before any reference is offered. Rejected
definitions are never referenced: their source observation is instead offered
in the inline form, not retried as the rejected record. Cancellation can leave
an unused definition, but cannot emit a reference before its definition.
The writer's ordered admission/commit and interrupted-capture quality semantics
still apply; admission is not a persistence guarantee.

The dictionary is invocation-scoped, insertion-bounded, and never evicts/reuses
definition IDs. Different replays use distinct namespaces, even when an artifact
contains multiple collections. No live callback waits, no additional producer
queue, no raw trace retention, and no store quota or lifetime accounting change
are introduced. The compatibility snapshot still contains the full retained
offline call tree, not a top-N substitute. Stack normalization removes repeated
evidence, not logical-budget accounting: genuinely diverse stacks and longer
captures can still reach the original record/capture limits.

**Measured regression, not a universal capacity promise:** the eight-second
CoreClrSample `/cpu-burn?ms=400` × 6 fixture on 2026-09-25 offered 69,420 inline
samples accounting for 340,231,458 logical bytes (147,603,897 from repeated stack
strings). Only 27,197 were admitted before the unchanged 128-MiB lifetime budget
was exhausted; the compatibility snapshot was just 72,842 bytes. With normalized
definitions, the same fixture retained all 68,902 samples plus 63 definitions in
54,960,707 logical record bytes, with an 88,391-byte compatibility snapshot and
zero record/queue/storage rejection. Sample and unique-stack counts naturally
vary between live runs. Both stack duplication and repeated invariant metadata
were removed; SQLite string interning alone does not reduce logical admission
accounting.

## VS Code address navigation (issue #1168)

The extension's address navigation keeps at most 20 history entries; each caches only its own
CLI result (already bounded by the projections above), a new heap capture clears the history, and
only the current entry is rendered, so neither memory nor DOM grows with the number of clicks.
The CLI `query` `address` filter of `static-fields` projects the captured list (bounded by the
capture's static-field cap) and `object`/`gcroot` use the existing bounded inspection caps; nothing
retains additional heap data.

## Bounded LLM response projections

These caps bound the **wire projection**, not collector retention. The full artifact remains
behind its existing handle, so narrowing a follow-up query does not re-collect or discard evidence.

| Projection | Inline cap | Selection |
|---|---:|---|
| `collect_thread_snapshot(depth="summary")` | 6 threads × 6 frames; no locks | Deadlocks, contended-lock owners, exceptions, and running frames before generic waits |
| `collect_thread_snapshot(depth="detail"|"raw")` | 8 threads × 7 frames + 12 locks × 8 waiter IDs | Same thread ranking; most-contended locks first; each lock reports total/omitted waiter-ID counts |
| `query_snapshot` thread summaries / lock graph | 8 threads × 8 frames / 12 locks × 8 waiter IDs per page | Versioned opaque cursors encode the final deterministic sort key and scan the retained capture with page-sized workspace; no capture-sized ranking cache is built or retained. Thread pages expose whole-snapshot `totalThreads` separately from the paged `candidateThreads`; `stack(threadId=...)` selects an exact full captured stack, and `lock-graph(address=...)` cursor-pages every retained waiter ID for one stable lock address |
| CPU `call-tree` | 64 nodes, depth 8 | Every direct child participates in decision-first ranking; selected direct-child slots are reserved before descendants consume the remaining budget. OS-backed captures prioritize branches with measured on-CPU self samples; EventPipe stack-frequency captures fall back to inclusive/exclusive ranking because unmatched leaves have unknown scheduler state. Use `rootMethodFilter` to narrow |
| Heap `retention-paths` | 10 paths × 12 frames | Target and terminal root (`RootKind`) are always preserved, with typed/addressed intermediates filling the remaining budget; projection truncation sets `Truncated=true` |
| Heap `growth` rows | 1 path × 12 frames per grower | Carries total/omitted path counts; the current heap handle retains every path |
| Activities `trace` | `topN` span rows (default 50) plus fixed 11-key safe-tag allowlist × 128 characters per value | Deterministic parent-before-child prefix for one required W3C trace ID; hierarchy/residual/critical-path calculations use every retained matching completed span. High-cardinality/sensitive tag categories are omitted; allowed values pass through `SensitiveDataRedactor`. `Truncated`, `CriticalPathTruncated`, `CanClaimComplete=false`, and explicit completed-only/window/retention/link warnings expose every evidence boundary |

Thread and lock continuation scans the complete retained capture at most once per selected page
slot, so work is O(capture size × page size) independent of page depth and workspace/derived retained
state remain O(page size). Versioned cursors carry stable total-order keys, including original capture
position as the final tie-breaker, without materializing full candidate arrays, waiter sets,
dictionaries, or sorts. Cursors validate their handle/view/key (and exact lock address for waiter
pages); malformed, stale, and cross-handle values fail safely.
The default collection response may emit up to three bounded thread-wait signal groups, each with
at most five buckets; its aggregation workspace is fixed-size and retains no capture-sized derived
index. Deadlock/wait-chain graph construction remains deferred, with exact lock and inferred-cycle
analysis available through explicit handle queries.
Direct offsets above `MaxDirectOffset = 256`, including `int.MaxValue`, are rejected before capture
selection with guidance to restart at zero and follow the returned cursor. Small offsets remain for
compatibility.

The Activities trace projector builds derived hierarchy state proportional only to the already
bounded retained `ActivityCapture`; it stores no index beyond the query lifetime. The response is
bounded by `topN`, while tag output has a fixed key allowlist and per-value length cap. Residual
wall time merges clipped direct-child intervals before subtraction, so parallel children do not
inflate accounted time.

The CLI-only `gc-activities` coordinator owns exactly two concurrent collectors, not an
unbounded fan-out. Duration is 1–300 seconds; GC details, exploratory activities and targeted
matching activities each have independent 1–10000 budgets (default 200), and inline overlay
top-N is 1–100 (default 20). It retains the same two bounded objects in the ordinary handle
store and reuses the existing GC correlator. No raw trace spills to disk. A shared
duration-plus-65-second cancellation deadline covers queued startup; collector-owned
30-second startup and bounded stop/drain behavior remain in force, and both tasks are
observed before return. Per-side failures and registration failures remain explicit.
Activity captures now carry actual observed duration plus optional `observation` metadata
(requested duration, transport events lost, completion); legacy missing metadata is unknown,
and abnormal streams cannot be used for GC attribution.

Hints also avoid suggesting evidence already present in the current payload: an untruncated call
tree has no redundant call-tree hint, and heap-growth output with inline retention paths does not
recommend fetching those same paths again.

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
