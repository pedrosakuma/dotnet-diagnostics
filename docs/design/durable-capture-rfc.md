# RFC: local durable diagnostic captures

**Status:** Discussion draft; not an approved implementation or shipping API.

**Date:** 2026-09-21

**Source baseline:** `3e5ea03e2cb981f1358ee191559b6a4a20dd8568`

**Tracking:** [RFC #999](https://github.com/pedrosakuma/dotnet-diagnostics/issues/999).

**Related work:** #551, #919, #997.

This RFC separates durable investigation evidence from the temporary handles
used to query it. It consolidates the maintainer discussion and a multi-model
design review. No collector, retention default, tool, package dependency,
diagnostic threshold or frozen evaluation changes as a result of this document.

The storage-independent DC1 spike contract, exact first-artifact allowlist,
lifecycle fixtures and proposed (not approved) public placement are specified
in [Durable capture contract and internal spike gate](durable-capture-contract.md).

## 1. Problem and goals

Current compact responses can omit useful detail, while in-memory artifacts
expire or disappear when their host restarts. An investigation may need to ask
several questions about the same capture after the target exits, or reopen the
evidence in a later session without collecting a different workload episode.

Persistence and greater measurement fidelity are different features. Saving a
counter snapshot containing first/latest/max values does not create a tick
timeline. Saving a merged call tree does not recover individual sample order.
The proposal must distinguish continuity from new temporal retention.

The intended investigation remains:

1. Collect a bounded capture, with persistence explicitly requested.
2. Return compact observations, measurement semantics and limitations.
3. Query selected retained evidence using typed, bounded operations.
4. Reopen the same durable capture later, obtaining a new temporary handle.
5. Collect again only when the required evidence was not retained or observed.

Resource use must be interpreted alongside workload and outcome. Increased CPU
with increased completed work is not by itself inefficiency; request mix,
coverage, waits, latency and errors matter. The API should expose evidence for
such analysis, not automatically declare a cause. Existing explicit triage
hypotheses remain useful but are not mandatory narratives on every collection.

### Non-goals

- A central database, network-mounted SQLite database or telemetry ingestion
  service.
- Always-on monitoring, persistent conversation memory or unbounded history.
- Analytical reads of a package while it is being acquired.
- Arbitrary SQL supplied by a model, or new speculative MCP tools.
- Default retention of all raw events, dumps, parameter values or application
  payloads.
- Reconstructing unobserved events, silently reattaching to a later live process,
  or treating temporal proximity as proof of causality.
- Replacing existing provider sessions with one merged EventPipe session.
- Changing the frozen human/LLM evaluation corpus or interpretation thresholds.

## 2. Existing behavior and reusable components

The production API has 17 tools, 13 normally visible. Core is shared by the MCP
server, standalone CLI and BenchmarkDotNet diagnoser. The shared handle store
defaults to 32 live artifacts, has a configurable ceiling of 1,024, and
distinguishes expiration, capacity eviction and unknown handles. Many producer
TTLs are ten minutes. These limits are entry counts, not aggregate byte budgets.

Artifacts are usually bounded snapshots or aggregates, not complete raw
streams. Some `query_snapshot` views use stored data, some reopen a dump, and
others reattach to a live process. File exports already exist for dumps and
selected raw traces; their default reaper TTL is 24 hours. Container/volume
lifetime is an independent constraint.

Production counters already carry producer interval and display-rate metadata.
Their omission in the earlier blinded test gateway is not a production
collector defect. Neither that gateway's prospective v2 nor the current
counter artifact is a complete, aligned time series.

`collect_batch` groups up to four independent collectors. `sweep` groups
counters, GC, exceptions, ThreadPool and process resources. One networking
session observes several providers. Therefore these identities are distinct:

- logical capture;
- collector execution and physical session;
- source stream;
- persisted artifact;
- temporary query handle.

Reuse `EvidenceQuality` rather than creating a competing completeness score.
It already distinguishes transport loss, processing failure, collector
eviction, output projection, inference, capture-window and unavailable/unknown
evidence. Reuse typed query dispatch, comparable-snapshot metric definitions,
artifact roots, redaction and authorization mechanisms where applicable.

The repository's statelessness boundary remains important: this proposal is
for portable, explicitly requested evidence files, like existing dump/trace
artifacts, not server-owned investigation history. Approval is still required
for the new artifact format and lifecycle. The in-memory catalog must remain
bounded and rebuildable; files do not become persistent agent memory.

## 3. Requirements independent of storage engine

| Requirement | Meaning |
| --- | --- |
| Local acquisition | The collecting host or sidecar writes locally. Remote callers use the API; they do not open its active database over a network filesystem. |
| Explicit persistence | Existing ephemeral behavior and payload defaults remain unchanged unless persistence/projection is requested. |
| No concurrent analysis | Capture first, finalize or recover, then query. Progress/status reporting does not read an active analytical dataset. |
| Stable identity | A capture and its artifacts have durable identities independent of process IDs, sessions, handles and file paths. |
| Bounded ownership | Record, queue, batch, package, temporary-space and concurrent-capture budgets are explicit. |
| Honest recovery | Accepted, committed, finalized and evidence-complete are different concepts. Unknown crash-tail losses remain unknown. |
| Selective access | Query by supported time/key/grouping with bounded output; do not replay every record into model context. |
| Preserved boundaries | File retention does not bypass authorization, redaction, capability gates or sensitive-value opt-ins. |
| Versioned interpretation | Readers know which schemas and measurement representations they understand; unsupported versions fail explicitly. |
| Measured choice | No engine or I/O technique is assumed cheaper without equivalent-fidelity, equivalent-durability measurements. |

## 4. Candidate storage designs

### A. Direct SQLite ingestion

Small owned records enter bounded admission queues. One logical writer per
package uses prepared statements and bounded transactions. Secondary analytical
indexes are built after acquisition where feasible. Large native traces/dumps
remain separate optional files.

Benefits include transactional recovery and an existing query engine. Costs
include row encoding, page/index maintenance, commit I/O and provider dependency.
SQLite does not provide SQL Server's `SqlBulkCopy`; batching amortizes
transaction work but does not eliminate row processing.

`Microsoft.Data.Sqlite` ADO.NET async methods execute synchronously. Isolate
writer execution from producer callbacks; `await` alone is not asynchronous
disk I/O. Do not create a task per event.

WAL is an experimental choice, not a requirement or a forbidden option.
It can change write/fsync behavior without concurrent readers, but adds
checkpoint and side-file lifecycle concerns. Compare journaling modes under
the same durability target. Never improve a benchmark by silently weakening
durability.

### B. Record files first, derived query index afterward

Write bounded framed records or supported native artifacts during acquisition.
Build a SQLite or other index after collection, or when the capture is reopened.

This may reduce acquisition-time work, but introduces parsing, recovery of
interrupted records, extra I/O, conversion space and time-to-first-query. A custom
file format is not automatically simpler or faster than SQLite. Deferred work
can still compete with an application that remains running.

An index is disposable only if canonical retained inputs and a compatible
reader can rebuild its contents. A normalized record stream is not necessarily
raw evidence. Native trace formats may require backend-specific readers and
have different interrupted-file recovery capabilities.

Under A, normalized tables are canonical for the fields actually preserved;
SQLite indexes are derived. Under B, the declared record files are canonical
and the query database is derived. Do not promise that A's database can be
rebuilt merely because an unrelated native trace exists beside it.

### C. Persist existing artifacts only

Serialize a supported subset of the bounded artifacts already held in memory,
with provenance, quality, capabilities and a manifest. Reopening restores
artifact-based queries but not live reaccess or previously omitted evidence.

This is a smaller continuity feature and a possible first phase, not a solution
to missing temporal information. Do not claim every current artifact is
automatically portable or serializable.

### D. Memory-mapped access

Memory mapping is an I/O technique, not a storage schema, query engine or
durability protocol. Variable-length events can be encoded; structs containing
managed references cannot simply be copied as portable objects.

Mappings can incur page faults, memory pressure and I/O failures. They do not
remove the need for record boundaries, ownership, capacity, recovery or flush
semantics. Treat mmap as a later measured optimization, especially for immutable
read access, not a prerequisite for durable capture.

## 5. Package and interpretation contract

The logical package contains a versioned manifest, one or more retained
representations, and optional separately authorized native files. Physical
layout and the final storage engine remain subject to the decision process.

The manifest must describe:

- capture/artifact identities and target identity beyond PID;
- collector/backend and producer/reader schema versions;
- origin timestamps, clock domain and actual observation windows;
- retained representation, its units, aggregation and population semantics;
- available query views and whether they project retained data or require an
  external artifact;
- per-stream quality, retention policy and observed loss/unknown categories;
- package lifecycle, integrity references, expiration and file dependencies.

Do not label configured/requested duration as actual coverage. Do not assign
write/commit timestamps to source events. Preserve per-source ordering and
clock/alignment uncertainty; queue arrival order is not a global event order.

Every query must distinguish:

| State | Interpretation |
| --- | --- |
| Omitted inline | Retained elsewhere and accessible through an advertised valid route. |
| Aggregated | Some questions remain answerable; original detail/order may not. |
| Not retained | A larger `topN` cannot restore it. |
| Unavailable | Mechanism, permission, version, file or capture state prevents the operation. |

Compute statistics over the explicitly declared population. A percentile of
the longest retained contention records is not the percentile of all observed
contentions. Cheap bounded aggregates may precede detail-retention loss, but
their availability and cost cannot be assumed for every collector.

### Counter evidence and cross-signal follow-up

A persisted last/max counter artifact only preserves that artifact. A future
counter-series view additionally needs source timestamps, actual interval
metadata, counter kind/unit and explicit missing/reset boundaries at each
retained observation. Neither disk persistence nor SQL can recover samples
that the collector never retained.

Cross-signal answers must expose which streams cover the requested window,
their clock/interval compatibility and their loss or aggregation limits.
Correlated CPU and demand increases are observations, not proof of inefficiency.
Keep optional diagnostic hypotheses separate from stored observations and
reproducible typed query results.

This contract does not require every statistic in the initial response.
Follow-up queries should request only the relevant window and population,
returning bounded results with clear continuation and truncation semantics.
Such views remain proposed until the collector retains the necessary inputs.

## 6. Acquisition, failure and resource rules

Producer callbacks must not wait for disk or for queue space. Admission budgets
cover bytes as well as item count and apply before allocating/copying a large
record. Parser-owned event objects or buffers may be reused; queued data must
have explicit bounded ownership.

`Channel<T>` is a possible implementation, not the persistence contract.
`Wait` mode with `TryWrite` can reject immediately on capacity pressure.
Automatic drop modes require explicit drop accounting. A false write result
must be interpreted with capture state; closed/faulted is not ordinary overload.

A noisy stream must not silently displace unrelated evidence. Define per-stream
and global budgets and an observable overload policy before choosing scheduling
machinery. One logical database writer does not require a separate writer per
collector. Concurrent packages still need a host-wide resource limit.

Record stage accounting where observable: observed, filtered, admitted, rejected,
committed and projected. Events need not correspond one-to-one to SQL rows.
Loss after process crash may be unknowable. Persistence loss must not be
misreported as EventPipe transport loss.

Acquisition cancellation and writer shutdown have different deadlines:

1. Stop acquisition and establish that producers have stopped.
2. Close admissions.
3. Drain accepted work under a separate finite deadline.
4. Confirm successful transactions/writes and record known limitations.
5. Finalize a complete or explicitly partial evidence package.

Writer failure must reach the controller. No unbounded retries, silent fallback
to another backend or continuing as if durable capture were healthy.

A drain deadline does not authorize starting an unbounded final commit.
Synchronous native I/O may not stop promptly when a cancellation token expires;
the prototype must establish its stop/escalation behavior. A timed-out writer
must not be reported as stopped or have its files reaped or ownership released
while it can still write.

Package state and evidence quality are separate. A finalized package can contain
partial evidence. An unfinalized package is never silently treated as complete.
Disk exhaustion can prevent recording the final failure: the absence of a valid
publication record must retain incomplete/unknown status.

SQLite transaction atomicity does not cover independent raw files plus an
external manifest. The implementation must specify publication order,
integrity checks, recovery and its process-crash versus power-loss guarantees.
Atomic rename alone is not a cross-platform power-loss durability proof.

Quotas must include journals/WAL, raw files, indexes and conversion space, not
just final database length. Do not accumulate the entire capture before one
large final transaction. Cleanup must coordinate with active readers and writers
and preserve the documented retention contract.

Historical packages also need aggregate byte/count limits and explicit expiry
or deletion policy, separate from query-handle TTL and active-capture budgets.
Persistence is not a promise of indefinite retention. Expired/deleted evidence
must be reported as unavailable, not silently recollected. Integrate with the
artifact reaper without deleting files held by active readers/writers.

The active transaction batch remains charged after dequeue, until commit or
terminal disposal. Admission may reserve bytes before copying, but it must
finish ownership transfer before the producer callback returns. An asynchronous
writer must never dereference an already-reused parser event.

### Publication and read-only boundaries

For the first prototype, build the declared minimal indexes before sealing.
A sealed canonical file is immutable: do not build indexes, append recovery
notes or change manifest rows inside it during ordinary reopen. A future lazy
index must be a separate bounded derived cache keyed by capture integrity,
representation and reader version, not an in-place mutation.

Recovery is an explicit, exclusive phase after acquisition ownership has ended,
not an automatic side effect of a read-only query. It preserves recoverable
source evidence and provenance and publishes an explicitly recovered/partial
result before analysis begins. Corruption, unsupported versions and missing
required dependencies remain errors or explicit unavailable views; do not
silently repair the package into an apparently complete capture.

The implementation must distinguish these claims:

| Claim | Required interpretation |
| --- | --- |
| Admitted | Owned in volatile memory; not a persistence acknowledgment. |
| Committed | Backend acknowledged a transaction/write under the declared durability policy. |
| Sealed | Package publication and dependency integrity checks completed; this does not prove loss-free acquisition. |
| Process-crash recoverable | Recovery behavior is established for the tested process-failure boundary. |
| Power-loss durable | A stronger, separately declared and evaluated synchronization/filesystem guarantee; not inferred from `COMMIT` alone. |

## 7. Reopen, authorization and host integration

The typed API is the primary compatibility boundary. Physical table layout is
not automatically a public SQL API. Versioned package/reader compatibility must
nevertheless be documented; an SQLite viewer opening a file is not proof that
an older diagnostic reader can interpret it correctly.

Reopening a durable capture resolves an authorized artifact into a fresh
temporary handle. It must not silently find a current process with the same
PID or reattach to reproduce missing values. Capture identity, integrity and
original observation time remain stable; derived results identify their own
projection/reader version.

The exact reopen/list/delete API placement is an open design gate. Prefer
existing typed tools/resources and explicit artifact selection; do not advertise
new parameter names as shipped or add a speculative eighteenth tool. A capture
containing several artifacts needs selection at artifact level, not an ambiguous
single handle pretending every query has the same source.

Core owns storage abstractions, lifecycle and supported artifact/query adapters.
MCP adds authorization/protocol presentation; CLI stays Core-only and can reopen
packages across one-shot invocations; BenchmarkDotNet may persist explicitly
selected packages beside its reports. These are proposed integrations, not
claims that current hosts already implement them.

Local artifacts can outlive the caller's connection. Define ownership and
authorization on reopen/export/delete independently of old handle validity.
Keep paths under authorized artifact roots; a caller-supplied identifier must
not become unrestricted filesystem access.

Scopes recorded in a manifest are provenance, not authorization grants. Use the
current caller and current policy. Preserve the existing read boundary of each
view without assuming that historical privileges remain valid. File hashes
detect mismatches; they do not establish ownership or authenticity by themselves.

Normalized application values are redacted before persistence. Native dumps
and raw traces may contain sensitive content that is not safely redactable;
retain their explicit capability/policy boundaries and never present them as
automatically sanitized. Arbitrary imported packages and cross-user transfer
require a separate threat/compatibility design, not automatic MVP support.

## 8. Evaluation and implementation gates

No performance thresholds or engine winner are established by this RFC.
Before executing a comparison, freeze numeric budgets appropriate to the
supported workload/host classes; record failures instead of tuning until green.

Compare A and B using the same retained record representation, quotas,
redaction, durability target and query set. Include C as a continuity baseline
where applicable, not as a fidelity-equivalent temporal alternative.

Measure:

- target latency/throughput disturbance relative to ephemeral collection;
- collector/writer CPU, allocations, queue/batch working set and disk use;
- per-stream losses, temporal coverage and burst/long-duration behavior;
- cancellation/drain, disk-full, process-crash and interrupted-finalization
  outcomes, with power-loss claims tested separately if in scope;
- finalization, reopen and representative selective-query latency;
- maximum temporary space and concurrent-capture interference;
- total investigation context cost, including catalog, repeated queries and
  projections, rather than first-response size alone.

Cases must include transient load, sustained overload, mixed high/low-volume
streams, missing measurements, dissimilar request mixes and nonaligned windows.
Observations of absent/normal signals require suitable coverage; one LLM's
agreement is not diagnostic ground truth.

### Acceptance invariants

- Every advertised query/recovery route works for its retained representation.
- Compact/detail/reopened results preserve timing, quality and cancellation.
- Unsupported live-only operations fail explicitly after reopening.
- Crashes do not turn unknown tails into zero loss or healthy conclusions.
- Quotas hold across transient buffers, journals and finalization work.
- Evidence is not retroactively enriched without distinct derivation provenance.
- Storage choice can change without forcing a new diagnostic tool vocabulary.

## 9. Multi-model discussion and decision record

The [discussion record](durable-capture-discussion.md) records independent
proposals, peer challenges, corrections and dissent from `gpt-5.6-sol`,
`claude-sonnet-5` and `gemini-3.8-flash`. This was an architecture critique,
not a storage experiment or an extension of the frozen advisory evaluation.
Model agreement is advisory, not maintainer approval or empirical validation.

### Recommended direction

Use **A as the first prototype candidate**, keeping **B as a meaningful
comparator**, not a straw man. SQLite is attractive because the problem needs
versioned structured records, transactions and selective queries. That is a
reason to investigate it, not evidence that its observer effect is acceptable.
Do not hide an A failure by raising frozen quotas or weakening durability.

C is a narrower continuity capability, not a fidelity-equivalent competitor.
It can exercise common package/reopen contracts without adding a time series.
Mmap remains deferred until a measured I/O bottleneck justifies it.

The editor recommends this bounded sequence, subject to maintainer review:

| Step | Scope | Exit gate |
| --- | --- | --- |
| Package contract spike | One existing bounded, self-contained artifact; stable capture/artifact IDs, supported-view metadata, explicit retention, current authorization and sealed read-only reopen. No general object-graph serializer. | Reopen reproduces the retained semantics without live reattachment, invented detail or privilege reuse. |
| One-stream storage experiment | Timestamped counter observations, preserving actual intervals and missingness; A first and B under equal contracts. No automatic time alignment or broad four-stream capture. | Predeclared resource, fidelity, recovery and query budgets pass; limits and failures remain visible. |
| First supported delivery | Choose continuity-only or the measured counter-series capability based on review and user value. Do not require two independent production formats. | API placement, compatibility, durability and retention guarantees approved for the supported host/workload scope. |
| Additional streams | Add individually justified collector adapters, then exercise mixed-stream contention. Native files remain separately authorized dependencies. | Collector-specific quality/security contracts and multi-stream fairness hold; a counters-only result is not a universal high-volume benchmark. |

This is a sequence for reducing uncertainty, not authorization to implement all
steps. A lifecycle spike need not become a separately shipped C product.

### Decisions still open

- First publicly shipped capability: continuity-only versus counter series.
- Exact reopen/list/delete/recovery placement within existing typed tools,
  resources and host commands; temporary-handle lifetime and disposal.
- Canonical schema/layout, version migration and recovery publication identity.
- A versus B for each supported workload; journal/sync mode and native artifact
  publication protocol.
- Per-record, stream, package, historical-store, temporary-space and host-wide
  quotas, scheduling policy and finite shutdown behavior.
- Numeric observer-effect/loss/latency acceptance budgets, approved before
  running experiments rather than chosen to fit a preferred result.
- Optional external derived indexes after MVP; minimal package-owned indexes
  are built before sealing in the proposed first prototype.

There are no new command/parameter names or production defaults to adopt from
this RFC. Implementation issues should be scoped separately after review.

## 10. References

- [Resource boundedness](../resource-boundedness.md)
- [Evidence-quality pilot](evidence-quality-threadpool-pilot.md)
- [Ephemeral-process capture design](ephemeral-process-capture-design.md)
- [MCP catalog budget](../research/mcp-tool-catalog-budget.md)
- [Production safety](../production-safety.md)
- [SQLite application/transfer format use cases](https://www.sqlite.org/whentouse.html)
- [SQLite WAL](https://www.sqlite.org/wal.html)
- [SQLite atomic commit](https://www.sqlite.org/atomiccommit.html)
- [SQLite consistent backup](https://www.sqlite.org/backup.html)
- [Microsoft.Data.Sqlite async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)
- [.NET channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)
- [SQLite memory-mapped I/O](https://www.sqlite.org/mmap.html)
