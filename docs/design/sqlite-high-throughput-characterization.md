# SQLite structured-record capacity characterization — prospective prototype v1

**Status: plan + experimental code + component tests only. No capacity measurements
authorized or executed by this change.** Review this protocol, numerical bounds,
schema, units, source patch and component evidence before any `cell` invocation.
Issue #1041 requested characterization first, not an invented production SLO.
SQLite was selected for simplicity, **not proven throughput**. The whole DC5
counter A/B remains immutable and inconclusive.

This is one research step toward **one public feature release**, not a staged
public rollout. Nothing here implements a shipping CPU/EventPipe/GC/activity
adapter, `collect_batch` persistence, CLI command, MCP tool or arbitrary SQL API.
The only route is `experimental-sqlite-capacity` in `DiagnosedBenchmarks`;
implementation lives in non-shipping `DotnetDiagnostics.TestSupport/SqliteCapacity`.

## Question and finite matrix

Under a declared host/filesystem/runtime, where do representative structured
occurrences, their dictionary fan-out, indexes and queries hit **this prototype's**
limits? Neither a highest observed rate nor completion means “SQLite is viable”.
These are load probes, **not SLOs or actual runtime-event demand**.

All identifiers are generated integers or `synthetic-frame-<integer>`; seed zero,
no application data. Source sequence starts at zero, never resets within a case.

| Profile | One offered logical observation | Additional SQL detail |
|---|---|---|
| `Numeric` | One timestamped numeric event occurrence; 64 keys, 8 synthetic threads | None |
| `RepeatedStacks` | One stack-sample occurrence; stack ID = sequence modulo 128 | First occurrence of each stack creates four frame rows and four ordered stack/frame edges; subsequent occurrences retain their own time/thread/value row |
| `NovelStacks` | Same occurrence shape; new stack ID = sequence | Four new frames + four edges per admitted novel stack, until the fixed dictionary cap; this deliberately exposes a cardinality ceiling |
| `Activity` | One synthetic completed-operation-like occurrence; trace group = floor(sequence / 8) | Four numeric attribute rows, keys 0..3, value = sequence × 4 + key; correlation/group IDs, not reconstructed causal lifecycle or W3C semantics |

Every occurrence stores `(seq, nominal, offered, key_id, thread_id, stack_id,
trace_id, value, profile)` as nine signed 64-bit integers. Key = sequence modulo
64; thread = sequence modulo 8; value = sequence modulo 97. Non-applicable
stack/trace ID = -1. Four-frame stack depth is fixed. Frame ID = stack × 4 +
ordinal. These bounded dictionaries share identities across repeated samples;
no application method identity is inferred. Attribute keys and synthetic frame
names have bounded encodings.

The complete planned matrix is **4 profiles × 4 offered rates × 2 modes × 3
repetitions = 96 cases**, at rates **1,000 / 10,000 / 50,000 / 100,000 logical
observations/s**, with **10-second acquisition windows**. Each case has at most
1,000,000 nominal slots. Modes:

* `sqlite`: generator → bounded queue → normalization/dictionary/oracle + SQLite.
* `producer-only`: exactly the same schedule, queue, record normalization,
  dictionary admission and streaming oracle, **no SQL connection or database**.
  It consumes records but does not claim SQL commits. It establishes the combined
  generator/normalization/oracle ceiling, **not a pure empty-callback baseline**.

Execute repetitions in order 1,2,3; within each, rates ascending; within each
rate, profiles in table order; then producer-only followed by SQLite. No adaptive
rates, batch tuning, retries-until-green, baseline subtraction or selective
exclusion. Separately retain all failed/incomplete attempts.

**Workspace reservation may stop the matrix early.** Retain every attempted
package and report; do not prune earlier evidence to force completion. A fresh
3-GiB workspace cannot promise to retain 48 maximal 256-MiB databases. Stop before
starting a case when one full package plus four report caps no longer fits.
Report the unattempted remainder as `workspace-budget-not-run`, not missing data
or a successful curve. A later storage/retention revision requires prospective
review, not opportunistic archive/delete during this campaign.

Eight separate 32-record `component` warmups (one per profile/mode, rate 1,000)
are proposed before the matrix, excluded from capacity summaries and retained
as functional evidence. Every cell starts a **fresh writer process**: these
warmups cannot remove that process's JIT cost. Writer setup is outside the
acquisition window but inside whole-case wall time and worker CPU/allocation.
JIT during acquisition is included. OS/filesystem caches are unspecified/warm;
no root, global tuning, cache flush or “cold-cache” claim.

## Relationship to actual collectors

At source base `c48695da0749d04ee2ab1e481123430beb6e40f0`:

* `src/DotnetDiagnostics.Core/CpuSampling/EventPipeCpuSampler.cs` and
  `CpuSampling/EventPipeAllocationSampler.cs` motivate per-occurrence
  samples and repeated/novel dictionaries. Their temporary traces and top-N
  aggregates are **not** this input. AllocationTick is sampling, not one event per
  allocation. `benchmarks/DiagnosedBenchmarks/WorkloadBenchmarks.cs` supplies
  future actual CPU/allocation workloads, not a calibrated SQLite rate.
* `Gc/EventPipeGcCollector.cs`, `Contention/EventPipeContentionCollector.cs` and
  `Exceptions/EventPipeExceptionCollector.cs` motivate timestamp/key queries.
  Numeric is only a compact representative shape; it does not implement their
  different correlation, retention or provider-loss semantics.
* `Activities/EventPipeActivityCollector.cs` and
  `Networking/EventPipeNetworkingCollector.cs` motivate operation/trace groups
  and bounded attributes. This fixture has no networking workload, real trace
  context, missing stop events, parent inference or distributed-clock semantics.

See [resource-boundedness](../resource-boundedness.md) and
[collector hotpaths](../hotpaths/README.md). Retaining occurrences and attributes
beyond today's first-N/top-N/aggregate DTOs is **explicit experimental new
detail**, not transparent persistence of existing outputs.

Future separately approved work must calibrate actual provider-delivered source
rates, bursts, payload/cardinality and fan-out on owned sample processes, then
test collector integration and target overhead. Bursts, mixed streams, variable
stack depth, real strings, concurrent packages, real activity lifecycle joins
and end-to-end target demand are **not covered** by this small fixed matrix.

## Schema, indexes and oracle

`CapacityProtocol.Schema`, `Indexes`, and `Queries` are the exact normative SQL.
Tables:

* `records`: sequence primary key plus the eight other integer fields above;
* `frames`: integer primary key, bounded synthetic name;
* `stack_frames`: primary key `(stack_id, ordinal)`, frame foreign key;
* `attributes`: primary key `(seq, key_id)`, record foreign key, integer value.

**Indexes are created after acquisition and drain, before sealing**, once per
package. No during-capture secondary indexing variant is silently mixed in.
The five indexes are `(nominal,seq)`, `(key_id,nominal,seq)`,
`(thread_id,nominal,seq)`, `(stack_id,nominal,seq)`, `(trace_id,nominal,seq)`.
Irrelevant dimensions remain -1; all five indexes and query shapes are retained
for comparable fixed schema overhead (not optimized separately per profile).

Five parameterized count queries use the half-open middle time interval
`[window/4, 3*window/4)`, respectively:

1. all occurrences in time range;
2. key 7 in time range;
3. thread 3 in time range;
4. a selected stack in time range: ID 3 for repeated stacks, or the nominal
   midpoint sequence (`5 × rate`) for novel stacks;
5. the nominal midpoint trace group (`floor(5 × rate / 8)`) in time range.

The tiny component mode instead queries `[0, 100 ms)` and selects stack/trace
ID 3. Trace/stack counts may legitimately be zero after dictionary loss or for
non-applicable profiles; selected values are recorded, not inferred from SQL.
Each query is run five times in a **new verifier process after writer
exit**. First query includes open-to-first-answer timing; separately report
open duration and bounded per-query and combined latency histograms (no fake
percentiles from 25 samples).
Actual `EXPLAIN QUERY PLAN` must have one `SEARCH records USING COVERING INDEX`
row naming the intended index, not a table scan or a forced `INDEXED BY` fiction.
The command-line cannot supply arbitrary SQL.

The oracle uses only **successful commit membership**, not nominal offered
count and not SQL queried against itself. Queue and dictionary loss produce
sequence gaps. Expected counts are incremented from admitted committed records
in managed code. Ordered SHA-256 streams independently cover:

* record columns ordered by source sequence;
* `(seq, attribute key, attribute value)` ordered by sequence/key;
* `(stack, ordinal, frame ID, UTF-8 byte length, UTF-8 frame name)` ordered by
  stack/ordinal. Expected dictionary membership is bounded by 16,384 stacks.

Integers are little-endian Int64; strings are UTF-8 length-prefixed with Int64.
The reader traverses all retained rows streaming, not `ToList()` of all records.
It checks record count, attribute/dictionary hashes and total SQL-row count,
including no extra/unreferenced frame rows. Independent literal component
expectations exercise fan-out, lost-sequence gaps and modified detail.

Seal means successful indexes, successful `wal_checkpoint(TRUNCATE)` returning
`(0,0,0)`, close, and worker exit—not crash recovery. Read-only reopen uses
`mode=readonly`, private unpooled connection and `immutable=1` on that sealed
file. It needs no native trace. Before/after SHA-256 and directory entry checks
detect DB mutation or created sidecars. This is a functional immutability check,
not a full v7 descriptor census and not a claim about transient peak space.

## Frozen limits and units

| Boundary | Fixed bound / behavior |
|---|---|
| Offered source | 10 s, at most 1,000,000 nominal slots; no schedule feedback from writer |
| Owned record reservation | 512 bytes max/record, a conservative logical reservation, **not measured managed RSS** |
| Queue | 4,096 records × 512 = 2 MiB reservation; reject at insertion, no producer SQL |
| Writer batch | 256 records / 128 KiB reservation / oldest offered age 100 ms; commit on first reached condition or completion; native stalls may exceed requested age and are observable |
| Dictionaries | 16,384 stacks, 65,536 frames, 8 MiB exact logical encodings; reject new identity at insertion, retain repeated known identities |
| Attributes | Exactly four bounded integer attributes per Activity record |
| Committed logical payload | 96 MiB record + dictionary encodings; reject before transaction insertion |
| SQLite | page size 4,096; max page count 32,768 = 128 MiB main DB logical-page ceiling; no universal physical quota claim |
| Package | sampled DB + WAL + SHM stop at 256 MiB; poll cannot prevent transient overshoot |
| Workspace | 3 GiB; one active case; pre-case full-package reservation; 10,000 file inventory cap; dedicated workspace with no concurrent writers |
| Diagnostic RSS | sampled **owned child + supervisor** sum ≤512 MiB; stop above, 20-ms requested poll; not whole-host RSS |
| Whole case | monotonic 60-s deadline including setup, acquisition, drain, indexes, close, subprocess reopen and queries; then at most 1 s waiting for owned-PID stop acknowledgment |
| Reports | ≤64 KiB per JSON report (worker, verifier, supervisor); child stdout+stderr ≤16,384 UTF-16 chars/child, conservatively ≤64 KiB encoded; retain only a 2,048-char combined diagnostic prefix/child, drain the rest and stop on cap |
| Histograms | eight buckets: ≤10/100/1,000/10,000/100,000/1,000,000/10,000,000 microseconds, then overflow; maximum retained |
| Timelines | 11 offered one-second buckets, 61 committed one-second buckets, first/last actual offered ticks, fixed histogram of lateness; 61 queue-depth snapshots (-1 = not sampled) and queue depth at source end |

Logical record encodings: nine Int64 = **72 bytes**; Activity also has four
key/value Int64 pairs = **136 bytes**. Dictionary encoding includes frame ID
and UTF-8 name plus three Int64 per stack/frame edge. These are logical payload
counts, **not SQL pages, file sizes, allocation size or on-disk write bandwidth**.
SQL row count includes records, attributes, new frames and stack/frame edges.
Reported index bytes are the difference in allocated database pages before/after
index creation, not a claimed isolated physical-index allocation.

The supervisor passes the same absolute monotonic deadline to both children.
Worker and verifier checks consume that remaining budget, never a fresh
60-second allowance. Worker and supervisor reports retain the shared timestamp.

WAL and `synchronous=FULL` are required and actual PRAGMAs checked. Additional
fixed settings: private connection, no pooling, one logical writer; foreign keys
ON, cache -2,048 KiB, temp_store MEMORY, wal_autocheckpoint 256 pages,
journal_size_limit 8 MiB, busy timeout 1 s. The latter limits do **not** bound
active WAL or native transient memory. No NORMAL/OFF fallback. Prepared INSERT
commands are reused. RSS supervision and a subprocess deadline cover native
SQLite blocking in a way a managed cancellation token cannot.

The supervisor stops only its exact owned child PID (never by name or process
tree), records whether stop was acknowledged, and does not wait indefinitely.
An uninterruptible OS/native operation can outlive the stop request: mark
`OwnedStopConfirmed=false`, cease the campaign, and leave containment explicitly
unresolved rather than claim the 60 s deadline proved termination.

## Accounting and timing semantics

The monotonic source origin is after SQLite setup. Nominal slot time is
`floor(sequence * Stopwatch.Frequency / rate)`. If late, attempt elapsed slots
without resetting the origin or sleeping for the writer. At the window end,
count all remaining slots as `NotOffered`; do not silently discard them or
present intended rate as observed ingress. A fast catch-up burst is visible in
actual times and offer-lateness histogram.

* `Planned = Offered + NotOffered`.
* `Offered = Admitted + QueueRejected`. These are **synthetic logical
  observations**, not EventPipe/native/runtime events.
* `Admitted = Committed + ControlConsumed + DictionaryRejected +
  LogicalCapRejected + Unknown`. Only one of Committed/ControlConsumed can be
  nonzero. Unknown includes the failed/in-flight tail, conservatively even when
  rollback likely succeeded. A failed or killed cell is never capacity success.
* `SqlRows` counts successful transaction row fan-out, **not observations**.
* Expected oracle state advances after commit returns; an ambiguous commit
  error is not promoted to known membership. No recovery result is invented.

`ObservedOfferedPerSecond` divides actual offered records by source-loop elapsed
time, not configured rate. Also report offered / declared-window time and first
to last coverage; a shortened source or prefix burst cannot establish sustained
capacity. Actual offered ticks are retained in every committed occurrence;
discarded offers retain bounded aggregates, **not an unbounded per-offer log**.
Committed throughput divides committed records by acquisition-through-drain
elapsed time; keep acquisition buckets and drain time alongside it. Report SQL
rows/s and logical bytes/s with that same explicitly labelled denominator.
DB/WAL measured sizes are byte snapshots, not inferred write-I/O throughput.

Record commit-call latency and offered-to-commit histogram, drain duration,
index build, checkpoint and total finalization durations. `WorkerCpuTicks` uses
TimeSpan 100-ns ticks; all other duration ticks use reported Stopwatch frequency.
Worker allocations use process-wide GC allocated bytes. Worker final RSS and
supervisor sampled writer-worker/verifier/combined RSS are separate measurements. Worker CPU,
allocations and RSS include producer, writer and oracle; **writer-thread-only
CPU is unavailable**, never attributed from unrelated host processes.

Cap hits, queue overload and source undercoverage receive explicit non-success
outcomes and stage counters even if a partial package passes semantic queries.
Histogram limits, unknown transient native/OS allocations and sampling blind
spots remain visible. A post-exit inventory is not evidence of active peak or a
hard instantaneous quota.

If a file disappears between active case enumeration and size observation,
the supervisor counts `TransientPathMissObservations`; its missing size is
unknown, not zero. Pre-case workspace inventory is quiescent and does not
admit missing paths. Other inventory errors remain failures.

## Admission and execution boundary

### Revision 2 follow-up: avoid aged-backlog singleton transactions

The initial 96-cell execution exposed a batching cliff: once the oldest
queued observation exceeded 100 ms, checking age after dequeuing one record
could repeatedly commit one record per FULL transaction. Retained measurements
include mean committed batch sizes of 1.25 (NovelStacks, 50k/s), 1.51 (Activity,
50k/s), and 2.00 (Numeric, 100k/s, repetition 2), rather than approximately 256.
This is a prototype batching limitation, not an intrinsic SQLite capacity.

Revision 2 fills the available batch, up to the unchanged 256-record bound,
before checking the unchanged oldest-offer timestamp. It neither waits for
new arrivals nor renews the age budget. Queue/byte/dictionary limits, FULL
durability, schemas, indexes, oracles and measurement windows are unchanged.
The 100-ms threshold is a flush trigger, not a guarantee of commit latency.
The protocol version and structural hash change explicitly; original results
and source remain retained separately.

A separately bound follow-up measures 48 cells: the eight pairs below,
producer-only followed by SQLite, three repetitions, in the original
rate/profile order. The other original matrix pairs are outside this
prospective follow-up, not silently successful.

| Profile | Rates (observations/s) | Purpose |
|---|---|---|
| Numeric | 50,000; 100,000 | Stable control and variable overload |
| RepeatedStacks | 100,000 | Overload without novel dictionary entries |
| NovelStacks | 50,000; 100,000 | Backlog and independent dictionary bound |
| Activity | 10,000; 50,000; 100,000 | Stable control and attribute-row fan-out |

Use a fresh 3-GiB workspace, the same eight component warmups, fresh source
and binary identities and unchanged stop rules. Compare all matched original
repetitions, including the Numeric outlier; do not pool revisions. Sequential,
non-randomized runs on a shared host cannot attribute every timing difference
to the correction.

Before any measurements, a different-model review must approve this new plan
and source patch. Record: source base + exact patch SHA-256, build/SDK identity
(10.0.201), protocol hash, .NET runtime/provider/native SQLite versions,
OS/kernel/architecture, CPU model/count, memory/cgroup limits, storage device,
mount/filesystem/options/free space, workspace and contention conditions.
Runtime/provider/SQLite/actual PRAGMAs are emitted by the worker; the remaining
host/filesystem provenance must be supplied prospectively, not fabricated.

Protocol hash format is **lowercase 64-hex SHA-256**, prefixed `sha256:` when
displayed. Exact input is UTF-8:

```text
Configuration + LF + Schema + LF + Indexes + LF + join(Queries, LF) + LF
```

These are the C# constant values (raw-string indentation stripped by the
compiler), not the source-file bytes or reserialized JSON. `describe` prints
the canonical JSON and digest without running a case. The source patch digest
separately binds implementation/host logic not present in this structural hash.
No historical v7 protocol hash changes.

The experimental route has `describe`, `component` (32 fixed records, 1,000
schedule, 100-ms maximum source window), and **future-use** `cell` operations.
The latter two require a fresh case directory, profile, rate, mode and exact
reviewed protocol digest. A matching hash is drift detection, **not human
authorization or permission to run**. Internal `worker`/`verify` entrypoints are
subprocess implementation details and must never be invoked as a substitute
for the supervisor. No full-matrix automation is shipped.

Preparation may compile the existing benchmark host and execute only targeted
component tests/tiny component subprocesses. Measurements require a separate
prospective execution manifest binding the reviewed source, protocol, binaries,
host provenance, fresh workspace and exact matrix order. The outer operator must
retain every outcome, mark the remaining cases not run after a workspace stop,
and stop entirely if owned-process termination is not confirmed. This synthetic
admission does not authorize demand calibration, EventPipe or the old
monitored/prevalidation commands. Existing frozen execution worktrees, runtime
controls and adapter A remain untouched.

Reused surfaces are the existing benchmark host, TestSupport reference,
Microsoft.Data.Sqlite central package, prepared-command pattern and existing
Core.Tests IVT. The frozen counter-only 64-record factory/limits are not reused
or relaxed; its counter pipeline and production method-identity helpers are
not appropriate for these synthetic non-method IDs. No package/version or
shipping project changes are required.

## Prospective result, not a pass/fail target

Publish every attempted cell and explicit unattempted remainder, not just a
peak number. Include offered/accepted/committed/control/rejected/unknown counts,
coverage and loss reasons, SQL-row/logical-byte fan-out, observed rates and
their denominators, queue peak, memory/CPU scope, measured files and unknown
transients, finalization/query costs, oracle equality and actual query plans.
Use all three repetitions where attempted; do not call an incomplete cell a
zero-throughput measurement or silently drop it from summaries.

Report only an empirical operating envelope under declared constraints. A
dictionary/page/RSS/package/workspace cap identifies a **prototype bound**, not
the SQLite engine's maximum. A generator-limited baseline identifies a source
ceiling, not storage headroom. Remaining gates: real collector demand/bursts,
end-to-end target impact and detail-retention costs, mixed/concurrent captures,
agreed production workload/overhead/resource targets, and the complete
single-release coverage matrix. No production viability decision follows from
this prototype alone.
