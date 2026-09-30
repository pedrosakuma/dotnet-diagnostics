# DC3: prospective counter-storage experiment

**Status:** Revision 3 accepted for internal protocol/DC4 purposes after
independent review and correction of its Q2 fixture-scoping condition.
No execution or production decision is authorized by this acceptance.

Tracking: #1008 under #999; RFC #1000; DC4 #1009 and DC5 #1003.
The [accepted DC1 contract](durable-capture-contract.md) governs identity,
publication, recovery and current authorization. DC2 #1002 independently proves
one contention-artifact lifecycle; it is not a counter performance baseline.

The coordinator replaced revisions 1-2 after review found false reset inference,
ambiguous accounting, unfixed budgets and incomparable durability settings.
This document supersedes them; no run or fixture from them is admissible.

The [JSON companion](durable-capture-comparison-protocol.json) is the normative
numeric configuration and case inventory. Prose defines semantics. A conflict
blocks execution rather than letting a runner choose its preferred reading.
Numbers are **prospective engineering constraints**, not measured performance,
production defaults or a claim that either candidate will pass.

## 1. Bounded scope and ownership

One local Linux host, installed .NET 10 runtime, SDK 10.0.201, published
`samples/CoreClrSample` (`net10.0`). Resolve and record the exact runtime patch,
kernel, CPU/cgroup capacity, filesystem and tool revisions before execution.
No runtime roll-forward across major versions. Windows, .NET 8/9, Kubernetes,
additional providers, arbitrary imports and power-loss claims are deferred.

DC4 implements the shared bounded pipeline, fixture generator and harness
interfaces. DC5 implements adapters and the declared runner. New experimental
adapters remain internal; the current collector does not retain a tick series.
Only existing .NET/repository tooling is used, with dependencies governed by
the repository's central package/private-feed rules. This protocol installs
nothing and changes no source. No system-wide tuning or other-process killing.

Candidate A writes normalized records directly into SQLite. Candidate B writes
framed batches and builds its derived SQLite query database **before sealing**.
Both return the same bounded typed views. B's index is not built/refreshed by
ordinary reopen; canonical files are immutable. There is no lazy cache here.

## 2. Logical evidence and exact fixture rules

`CounterValue.cs` and `EventPipeCounterCollector.ExtractCounterPayload` define
the existing provider/name/display-name/value/kind/unit/interval/display-scale
fields. `CounterKind.Sum` is an **interval increment**, not a cumulative total.
A falling increment is not a reset signal. Retain `resetState = unknown`;
do not infer a reset, restart or missing-event count.

The experimental record has:

| Field | Contract |
| --- | --- |
| `sequence` | Unique positive int64 assigned in source-callback order, before admission; rejected records leave gaps. Not a wire sequence. |
| `key` | Exact ordinal pair `(provider, name)`; eight synthetic keys, at most 128 live keys. No tags/Meter series. |
| `provider`, `name`, `displayName`, `unit` | UTF-8 limits respectively 128, 256, 512, 64 bytes; unit nullable. Over-limit values reject explicitly, never truncate. |
| `value`, `kind` | Finite double and `Mean`/`Sum`. Nonfinite values or unknown kinds reject with a counted reason. |
| `intervalSec`, `intervalState` | Nullable finite double, preserving zero/negative values; state is missing, valid, nonpositive or nonfinite. Nonfinite source metadata is represented as null plus nonfinite state, never silently valid. |
| `displayScaleTicks`, `displayScaleState` | Nullable int64 TimeSpan ticks, preserving nonpositive values with explicit state; no invented configured scale. |
| `sourceTime`, `clockDomain`, `clockOrigin` | Nullable int64 session-relative timestamp ticks (100 ns), or fixture-relative ticks. Capture TraceEvent relative time using a declared checked conversion; record its source API and rounding in the resolved manifest. Unknown source time stays null. Origin is descriptive capture metadata, not insertion time. |
| `coverageGap` | Unknown if either timestamp or current positive valid interval is unavailable, or time regresses; otherwise true only when the per-key timestamp delta is strictly greater than 3 times the current interval. This is an observed timing gap, not proof of lost ticks. |
| `resetState` | Always unknown in this experiment. |

Compute gap metadata in the shared producer before storage admission. Preserve
prior per-key state under the 128-key limit; report rejected new keys separately.
Do not recompute source gaps from a storage-filtered series and call them
transport loss. Storage rejection is a distinct quality limitation.

Enqueue/commit times and batch IDs are operational telemetry, **not logical
evidence fields** and not included in cross-candidate value equivalence.
UTC conversion does not create cross-stream alignment.

For Q1, P0, stress and fault inputs only, the fixture generator is deterministic,
with no PRNG: sequence `i` selects
key `(i-1) mod 8`, source time `(i-1)*10 ms`, alternating Mean/Sum keys, and
value `100 - (i mod 50)`. Intervals are 1 second and display scale 1 second.
Use fixed synthetic names only, padded to the case's serialized record length
by a dedicated fixture-only padding field discarded before typed projection.
Record the canonical UTF-8 encoding settings and hash exact input bytes.

Q1 contains 1,024 valid records. Q2 contains exactly 16 ordered records:
ordinary Mean; Sum=10; same-key Sum=5 (reset unknown); missing interval;
interval=0; interval=-1; missing scale; scale=0; same-key four-second gap;
regressing source time; equal time with higher sequence; null source time;
exactly 4,096 serialized bytes; 4,097 bytes; 257-byte name; nonfinite value.
Q2 is an **explicit table**, not an application of the generator formula.
The JSON `fixture.q2.records` fixes its key, kind, value, time and metadata;
its rows override every generated-field rule above. In particular, ordinal 9
uses the same key as ordinal 3 at 5,000 ms versus 1,000 ms: the four-second gap
must be true. Ordinals 10-12 explicitly regress, repeat and omit that key's time.
The final three reject for their declared content reason. Earlier invalid
metadata remains explicit evidence, not an admission failure. Fix canonical
encoding and expected outputs in DC4's hashed files before any adapter run;
DC4 may not reinterpret the explicit Q2 rows as generator defaults.

Live input permits only the existing default EventCounter providers and their
counter metadata. No application-added names, tags or arbitrary payloads.
Synthetic strings are fixed non-sensitive fixtures. Apply the same approved
redaction policy before either sink; if a live value cannot meet that policy,
reject it explicitly. Counter names are not universally assumed non-sensitive.

## 3. Views and semantic oracle

`summary` returns at most 128 keys with retained counts, first/last/min/max,
source-window and gap/unknown summaries; its population is retained records.
`series(key, afterSequence, pageSize)` uses exclusive sequence cursors and a
maximum of 100 rows. Source timestamps are preserved but not used to reorder
arrival history. `qualityReport` reports known stage totals and unknown states.
Every response is bounded to 1 MiB with an explicit continuation/limit result.
No free-form SQL, raw dump, new MCP tool or claim these names already ship.

For Q1/Q2, an **external feeder** submits batches of at most 64, waiting at
batch barriers for completion before submitting more. It waits outside the
callback; admission itself remains nonblocking. No capacity rejection is
expected in this class. Timeout or unexpected rejection fails the case, not
silently reduces the comparison population.

Each candidate must match the independently constructed expected valid
sequence/value/view outputs, not merely match the other candidate's mistakes.
Q2 must also match the explicit content-rejection reasons. Exclude operational
timestamps and transient queue occupancy from cross-candidate equality.
Check summaries and full paginated traversal, including empty and invalid
cursors/views. Unsupported requests must fail explicitly.

## 4. Accounting and finite resource constraints

The numeric limits are in JSON. All physical ceilings, not just logical ones,
apply identically to A and B. Different actual usage is measured, not excused.

Reserve one 4,096-byte owned buffer **before encoding/copying** into it; reject
if no reservation is available. Bounded encoding must stop at the limit. Charge
the actual buffer capacity (including pool bucket capacity, if pooled), not
just serialized length. Returned buffers remain charged if retained in a pool;
this profile instead disposes/releases them rather than growing an idle pool.
Other per-record metadata is count-bounded and included in process-memory
measurement; owned-buffer accounting is not called exact managed heap size.

The normal queue holds at most 256 records, active batch at most 64 records
and 262,144 owned bytes, one in-copy record; total owned count at most 321.
The 2 MiB owned-byte ceiling includes in-copy, queue and active batch. A batch
remains charged until terminal disposal, not just dequeue. The memory-limit
case M1 overrides that ceiling to 262,144 bytes for **both** candidates.
Serialize writer access; no per-record task fan-out.

For clean termination, distinguish cumulative totals from gauges:

- `offered = rejected + admitted`, **only** for this harness's known offers
  and terminal admission decisions. This is not an equation for upstream
  runtime events or provider/transport loss.
- `admitted = inFlight + committed + failedAfterAdmission + abandonedKnown`
  at quiescent accounting checkpoints; the disjoint inFlight set includes
  owned copying, queued and active-batch records.
- A committed record cannot also fail or be abandoned. If commit outcome is
  ambiguous after a crash, report unknown rather than forcing these equations.
- Sealing, querying and deletion are package/view operations, not further
  mutually exclusive record states. Projecting a record twice is not ingestion.

Measure all package files by their apparent file lengths, each path once:
data, WAL/journal, B's framed input and complete query database, temporary
index/conversion files, manifest and seal. Reject symlink/hardlink members.
The 256 MiB package peak cap includes staging and recovery output simultaneously;
256 MiB is also the final cap. Do not pretend SQLite index bytes can be
separated exactly from table bytes or double-charge overlapping categories.
Track high-water values at writes/growth requests and stage boundaries;
reserve bounded worst-case growth before committing it. Polling after an
overshoot is detection, not proof of a hard filesystem quota.

Additional ceilings: eight reader leases, 8 MiB retained reader buffers,
1 MiB per result, no derived cache, SQLite cache target 2 MiB with mmap disabled,
64 retained packages/2 GiB historical bytes, 3 GiB total evidence workspace,
512 MiB diagnostic-process peak working set, 256 MiB harness and 768 MiB target.
Process-memory sampling every 100 ms is an abort monitor, not an exact
instantaneous allocation bound. Unknown between-sample peaks remain a limitation.
No raw trace/dump retention is part of this experiment.

Preflight requires at least 8 GiB free artifact-root space and 2 GiB available
effective memory. Do not silently exceed quotas, evict failed evidence to make
room or increase limits to rescue a candidate. Exhaustion stops the campaign.

## 5. One comparable commit profile

Only profile P1 is evaluated: process-crash recovery, with one explicit durable
flush per completed bounded batch. No power-loss package guarantee is claimed.

| Candidate | Required configuration and acknowledgement |
| --- | --- |
| A | SQLite WAL, synchronous FULL, cache_size=-2048, mmap_size=0. Read back all PRAGMAs. Commit acknowledgement follows successful transaction commit. |
| B | Framed batch with sequence membership, length, checksum and commit footer. Complete write plus `FileStream.Flush(flushToDisk: true)` once per batch before acknowledgement. No per-record fsync. |

Use the same 64-record/262,144-byte batch maximum and 100 ms maximum batch age
in both. The all-buffers-owned reservation still applies to underfilled batches.
Both finalize their query indexes and required files before seal. Stop on
configuration drift. This chooses a **test configuration**, not production WAL
or FULL defaults. NORMAL/off/per-record flushing requires a different protocol.

Crash recovery cannot require equality with externally received acknowledgements:
the writer can commit and die before sending its acknowledgement. Let Acks be
the harness-confirmed committed sequence set, R the verified recovered set, and
O the known offered set. Require `Acks subset-of R subset-of O`, with no duplicate
records and only valid complete committed batches. R minus Acks is potentially
committed-but-unacknowledged, not corruption or invented exact loss.
At an explicitly held **pre-commit** barrier, that active batch must not appear
in R. A post-commit/pre-ack barrier intentionally exercises the acknowledgement
gap. The volatile admitted tail remains unknown after process death.

## 6. Fixed case inventory and execution order

Exactly 35 planned executions, **one attempt each**, with a 120-second outer
deadline per execution and 4,200 seconds for the entire campaign. There are no
automatic retries, extra tuning runs or optional matrix cells.

| Class | Cases and fixed inputs | Executions |
| --- | --- | --- |
| Candidate-blind readiness | P0: 1,024 Q1 records into bounded shared in-memory sink, barriers; P1: one ephemeral live episode below | 2 |
| Semantic oracle | Q1 and Q2, once per candidate; barrier-paced, 10-second inner deadline | 4 |
| Synthetic stress | N1: 100/s for 10 s; B1: 4,096 immediate offers while writer is held; M1: same with reduced memory cap; O1: 5,000/s for 10 s; C1: 1,000/s, stop after offer 1,000 | 10 |
| Isolated-writer fault correctness | F1 storage-full; F2 pre-commit kill; F3 post-commit/pre-ack kill; F4 pre-seal kill; F5 concurrent-package rejection; once per candidate | 10 |
| Live observer | Three blocks of baseline/A/B, with orders E-A-B, B-E-A, A-B-E | 9 |

For every synthetic stress case use 512-byte encoded records cycling the same
eight keys. B1/M1 hold the writer before dequeue until all 4,096 admission
attempts finish, then release; a 5-second feeder deadline prevents deadlock.
The held writer must not consume before release. These two cases establish
reachable queue and memory rejection without relying on real tick rates.
O1 is throughput stress: a fast writer need not lose records. Record achieved
offer timing; if the generator cannot deliver at least 95% of the scheduled
offers within the interval, classify that stress result inconclusive-source.
Never block admission to disguise overload. C1 stops new offers, then exercises
independent drain (5 seconds) and finalization (10 seconds).

F1: A uses SQLite `max_page_count` equal to its current allocated page count
after the initial committed batch; B's instrumented write stream throws a
declared storage-full IOException before completing the next batch. Feed up
to 4,096 records to trigger the fault. Require the expected storage-full error;
if not triggered, the case is invalid-injection, not a pass. This is controlled
backend-failure injection, **not** evidence of real filesystem ENOSPC safety.
Restore neither backend nor configuration mid-case to make it pass.

F2/F3: commit one batch, stage the next 64 records, then hold at the named
barrier and terminate that exact writer PID. F4 holds after manifest/index
completion but before seal publication. Normal reopen must require recovery;
explicit exclusive recovery creates new IDs/provenance without modifying source.
F5 holds one package's writer lease, attempts a second against the one-capture
limit and requires immediate explicit rejection, not an unbounded wait queue.

After the two readiness executions, run Q1,Q2,N1,B1,M1,O1,C1,F1-F5 in that
order, A then B for odd case indices and B then A for even indices. Then run
the three live blocks. Stop immediately on a safety/containment violation;
ordinary terminal case failures remain recorded and do not earn a replacement.
Resolve cases against the JSON IDs, not whether a name resembles a profile.

## 7. Live workload and measurement

Fresh target and diagnostic process per episode, loopback only. Launch the
published sample DLL; readiness deadline 10 seconds. A bounded .NET HttpClient
harness schedules 20 requests/second to `/cpu-burn?ms=10`, with at most two
in-flight requests, one-second timeout and no redirects. Skipped scheduled
requests count as unmet demand, not disappeared latency samples.

Warm load for 10 seconds. Start the counter session at t=10, interval=1,
duration=34 seconds, using exactly the three current default providers.
Measure request outcomes during t=[12,42); stop load and collection at t=44.
Session startup and target process launch times are recorded. Unexpected
redirect/readiness failure or missing capture coverage invalidates the episode.
Do not modify sample code or use pathological leak/regex endpoints.

This produces about 600 scheduled measurement requests, not a promised
counter-event rate. Request demand does not control EventCounter frequency.
A/B retain new ticks; E uses the unmodified first/latest/max counter collector.
Thus observer deltas represent the **whole temporal-persistence feature**, not
pure disk cost. Synthetic N1/O1 separately describe writer pipeline cost.

Use monotonic Stopwatch for request/duration metrics, Process.TotalProcessorTime
for CPU time, process working-set sampling/high-water APIs for memory and
GC.GetTotalAllocatedBytes/CollectionCount inside the diagnostic process.
No claim that MemoryDiagnoser provides thread CPU or peak RSS. Extra profiling
sessions are excluded from scored runs. Unavailable target GC-pause/off-CPU
detail remains unavailable; do not infer it from counter totals.

Persist per-request samples only up to 1,000 per episode, then stop as a
harness-budget failure. Report scheduled/completed/success/error counts, p50,
p95 and the explicit completed-request population. Report host CPU/allocation,
source coverage, stage loss, package peak/final bytes, drain/finalize/reopen and
bounded-query latency. Fault cases also report acknowledgement-gap uncertainty.

Context cost is UTF-8 bytes, not tokens: current unchanged MCP catalog bytes
using the existing ToolCatalogBudgetTests measurement approach, plus one
experimental summary, a 100-row series page, quality report and next series
page. Report catalog and response components separately. Experimental views
are not a shipping MCP schema; catalog cost is a stated unchanged-catalog
assumption, not a measured future catalog. No model call or tokenizer is needed.

## 8. Prospective gates and recommendation rule

The JSON carries thresholds with units. They are coarse engineering rejection
screens for this host/workload, not statistical proof or application SLOs.

Hard gates: independent semantic oracle; correct explicit recovery/immutability;
honest known/unknown outcomes; no unauthorized access; count/owned-buffer/disk/
history/result limits; drain <=5 s, finalization <=10 s, reopen+first bounded
query <=2 s for non-injected successful cases; diagnostic peak sampled memory
<=512 MiB. Injected failures must satisfy their expected outcomes, not produce
an ordinary success-shaped capture. N1 and live A/B episodes must commit all
valid offered records without capacity loss; source-invalid rejections stay
visible and cannot count as successfully retained observations.

For each candidate, pair each live episode with E from the same block. Require
median successful-completion ratio >=0.95, median error-rate increase <=0.005,
and median p95 increase <=max(5 ms, 0.15 times median baseline p95).
These prospective tolerances allow modest local timing noise while rejecting
large disturbance from a low-volume feature. No lowering after seeing results.
If baseline p95 max/min across the three blocks exceeds 1.5, or baseline
successful completions are below 95% of schedule, live evidence is inconclusive.
If any required episode is missing/invalid, do not compute a winner from the
remaining convenient subset.

No candidate can be recommended if it fails a hard gate. If exactly one is
fully eligible, recommend it **for the tested scope only**. If both are eligible,
recommend A only when its O1 committed count is >=0.95 times B's, its median
N1/O1 diagnostic CPU seconds per committed record and final bytes per committed
logical byte are each <=1.10 times B's, and its median reopen+first-query
latency <=1.10 times B's; otherwise recommend B only if it meets the reciprocal
four inequalities. This prevents rewarding an engine for discarding more work.
If both meet the inequalities, A is the predeclared
simplicity tie-break; if neither does, report a trade-off/inconclusive result.
Zero/undefined denominators invalidate that comparison; no arbitrary epsilon.
Report all other metrics descriptively without inventing retrospective weights.
DC6 still owns actual MVP/backend approval; a recommendation is not a release.

## 9. Freeze, attempts and evidence

Independent review must accept this revision's schema, fixed cases and numeric
constraints before DC4 starts. DC2 acceptance is also required by #1009.
DC4 then authors the deterministic inputs, expected-output oracle and pipeline.
Before any execution, a resolved run manifest pins protocol/JSON SHA-256,
pipeline/adapter commits, fixture/oracle hashes, runtime/tool binaries,
clock conversion, root/host facts, case order and all limits with **no unresolved
values**. Implementation-dependent hashes and host identifiers are not tunable
budgets. A readiness probe is not permission to invent missing protocol fields.

The two candidate-blind readiness executions verify harness functionality only.
They do not change budgets. Failure stops execution pending an explicit new
protocol revision/review; after any candidate output exists, budget changes
belong to a new campaign and cannot repair the old outcome.

Outcome vocabulary: pass; failed-candidate (positive evidence of candidate
violation); inconclusive-infra (positive harness/host evidence);
inconclusive-source; invalid-injection; inconclusive-unknown; not-run.
Attempt exhaustion never changes attribution. An unexplained timeout is unknown,
not automatically infrastructure or a proven candidate defect.

Use a configured private evidence root, not automatic repository commits:
`<root>/<campaignId>/<ordinal>-<candidate>-<case>/`. Keep immutable manifests,
bounded input/ack/outcome logs, stdout/stderr (8 MiB each), file inventories,
hashes and case outcomes, including failed/incomplete attempts. Seal campaign
metadata only after final enumeration. Publish reviewed aggregate reports in
`docs/evidence/dc5/`; never commit secrets, dumps or nettraces. Preserve raw
artifacts within the 3 GiB campaign quota; reaching it stops, not silently prunes.

## 10. Acceptance status

Claude Opus 5 accepted this coordinator-authored revision for the internal
protocol, conditional on scoping the generated formula separately from Q2.
The explicit Q2 table and generator scope above resolve that condition.
Numeric constraints are accepted only as prospective experiment inputs.
No calibration, process, capture, adapter or scored comparison has run.
DC4 still requires the independently accepted DC2 result.
DC5 additionally requires implementation and a complete frozen run manifest.
JSON readiness booleans are an acceptance-time snapshot; current issue-gate
evidence is recorded separately, not inferred from a stale boolean.
