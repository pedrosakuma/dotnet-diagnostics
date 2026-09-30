# DC5 candidate B (append-first) spike: what is actually implemented

Status: **implementation only, no engine endorsement, no DC5 approval, no execution
authorization.** This document describes exactly what the new code under
`tests/DotnetDiagnostics.TestSupport/DurableCounterSpike/AppendFirst/` and
`tests/DotnetDiagnostics.Core.Tests/DurableCounterSpike/AppendFirst/` does and its
concrete limits. It is not a benchmark report, not a comparison against candidate A,
and not part of the frozen 35-case DC5 campaign (`docs/design/durable-capture-comparison-protocol.md`,
revision 3). The DC5 execution gate remains closed
(`DurableStorageExperimentFoundation.ValidateManifest` still throws `ExecutionGateClosed`
whenever a resolved manifest sets `executionEnabled: true`); nothing here changes that.

Ownership boundary: this spike adds only new files under the two `AppendFirst/`
directories listed above, plus this document. It does not modify
`DurableStorageExperimentContract.cs`, `DurableCounterPipelineSpike.cs`, the accepted
protocol `.md`/`.json`, project files, the closed-gate command host, or any file owned
by candidate A's parallel worktree.

## What candidate B is

Candidate B commits each pipeline batch (up to 64 records / 262,144 owned bytes /
4,096 bytes per record, per the frozen `DurableStorageFrameContract` and
`DurableCounterPipelineLimits`) as one self-describing binary frame appended to a
single canonical file. Ingestion is **pure append-only**: `CommitAsync` never touches
SQLite. The derived SQLite query index is built exactly once, by
`FinalizePreSealAsync`, from a full, re-verified, byte-0 re-scan of the canonical log
— never incrementally alongside commits. This is a deliberate architectural choice
(not the original design of this spike): building the index during ingestion made it
possible for an index-write fault to leave the derived index inconsistent with an
already-durable canonical frame, or for that fault to escape past the F3 acknowledgement
barrier and cause the shared pipeline to report `Failed` for a batch that was, in fact,
already committed to durable storage. Deriving the index only from a clean, independently
re-verified re-read of the sealed-for-indexing canonical log removes that failure mode
by construction: there is no index state to have diverged from canonical truth, because
the index is built *from* that already-durable truth after the fact, not maintained in
parallel with it.

### Frame format (`DurableAppendFirstFrame`)

```
header (32 bytes): magic(4) | version(2) | reserved(2) | recordCount(4)
                   | firstSequence(8) | lastSequence(8) | entriesLength(4)
entries (entriesLength bytes): repeated { sequence(8) | length(4) | payload(length) }
trailer (40 bytes): sha256(entries)(32) | commitFooterMarker(8)
```

The per-record `payload` is the exact bytes the shared pipeline produced in
`DurableCounterSinkRecord.Encoded` (a JSON-encoded `DurableCounterRecord`, optionally
padded with trailing ASCII spaces to a fixture-requested size). `payload.Length` is
recorded verbatim in the frame and used to overwrite the record's stale, pre-padding
`encodedBytes` field on decode (`DurableAppendFirstRecordCodec.Decode`) — the payload
itself is never re-encoded or altered.

A frame is a *committed batch* only once its 8-byte commit-footer marker has been
written **and** the whole write has been flushed with `FileStream.Flush(flushToDisk:
true)`. Scanning (`DurableAppendFirstFrame.ReadNext`) distinguishes, and never
conflates:

- **Valid** — header, entries, checksum, and footer are all present and consistent
  (checksum match, sequence-membership match, no duplicate/decreasing sequences).
- **CleanEnd** — nothing left to read; a legitimate end of the committed history.
- **Truncated** — a header, entries section, or footer is only partially present.
  This is a structural observation, not a determination of its physical cause.
- **Corrupt** — a complete-length frame whose magic/version, checksum, footer marker,
  or sequence membership does not match what the header declared.

Explicit recovery may return a verified prefix at a truncated tail, always with
unknown volatile-tail quality. Corruption fails recovery without returning a prefix.
Ordinary finalization rejects both incomplete and corrupt canonical input.

### Commit path and fault barriers (`DurableAppendFirstStorageAdapter.CommitAsync`)

`CommitAsync` copies every record's `Encoded` bytes synchronously into owned arrays
before any `await` (the shared contract's owned buffers are only valid until the call
returns), then:

0. Checks a cross-frame sequence watermark (`_lastCommittedSequence`) **before any
   write for this batch**. A batch whose first sequence does not strictly exceed the
   watermark is rejected with a typed `SequenceRegression` error and never reaches the
   instrumented write stream at all — a duplicate/regressed sequence must never reach
   SQLite's primary key and fail there opaquely (there is no SQLite touch during
   ingestion in the first place, but the same watermark also protects the eventual
   `FinalizePreSealAsync` re-scan from ever encountering one).
1. Reaches `StorageFullNextBatch` (F1) as the literal first action of the instrumented
   write stream (`DurableAppendFirstInstrumentedCanonicalWriter.WriteNextBatchFirstSegmentAsync`),
   immediately before that stream's own `FileStream.WriteAsync` call — this is a genuine
   "instrumented write stream" seam, not a generic pre-write controller callback: any
   non-cancellation, non-`IOException` fault raised at this seam is wrapped as a declared
   `IOException` (with the original as `InnerException`) before propagating, matching
   the protocol's literal wording that the write stream itself throws a declared
   storage-full `IOException`. Nothing for this batch has touched disk when this fires;
   the pipeline sees a pre-commit failure. Cancellation remains
   `OperationCanceledException`, not a storage-full error.
2. Reserves the frame's exact byte length against the combined package cap
   (`DurableAppendFirstCapacityGuard`), then writes the header+entries+checksum through
   that same instrumented stream.
3. Reaches `BeforeCommit` (F2) strictly **before** the commit-footer bytes exist on
   disk. If the controller throws here, the adapter rolls the canonical file back to
   the last known-good frame boundary (`SetLength` + flush) before propagating — the
   batch is never observable as committed, and the pipeline correctly sees `Failed`.
   If truncation or its confirming flush fails, the outcome is instead `Unknown`;
   further writes and ordinary finalization are blocked until explicit recovery.
4. Writes the footer and calls `Flush(flushToDisk: true)` — this is the durability
   boundary. One flush per completed batch, never a per-record fsync. Only after this
   flush succeeds are `_lastCommittedSequence` and `_totalCommittedRecords` advanced.
5. Reaches `AfterCommitBeforeAcknowledgement` (F3) **after** that flush. If the
   controller throws here, `CommitAsync` **does not propagate the exception** — it
   returns `DurableCounterCommitResult(Unknown, ...)`. The batch is durably on disk;
   only the acknowledgement path was interrupted. This is the one correctness point
   this spike is most deliberate about: a post-commit interruption must never be
   reported `Failed`, because the shared pipeline treats any exception from
   `CommitAsync` as `Failed`, which would misclassify already-durable data as lost.
   Nothing after this barrier touches SQLite — there is nothing left in `CommitAsync`
   for an index-write fault to corrupt, because the index is not built here at all.

`AfterIndexesBeforeSeal` (F4) and `ConcurrentCaptureGate` (F5) are **not** fired by
this adapter. Per the shared handoff, F4 belongs to the host (manifest/seal creation
is host-owned, not adapter-owned) and F5 belongs to the shared
`DurableCounterGlobalBudget.AcquireCapture()` active-capture gate that the pipeline
already enforces. Firing them from inside the adapter would be firing the wrong stage.

Commits are serialized by a single `SemaphoreSlim` — one writer, no concurrent
`CommitAsync` overlap, matching the frozen "one writer, no concurrent analysis" design
point from the accepted RFC discussion.

### Index construction and Reader lifecycle (`FinalizePreSealAsync`)

The adapter's own `Reader` property throws `InvalidOperationException` at any point
before a successful `FinalizePreSealAsync` call — it is genuinely unavailable during
acquisition, not merely discouraged, matching the shared contract's explicit lifecycle
text. `FinalizePreSealAsync`:

1. Closes the write-mode canonical stream (no further ingestion is possible after this
   point).
2. Creates a brand-new writable SQLite index and re-scans the **entire** canonical log
   from byte 0 via `DurableAppendFirstFrame.ReadNext`, re-verifying every frame's
   checksum and sequence membership exactly as recovery does, inserting each valid
   frame's decoded records in bounded per-frame batches.
3. Treats any non-`Valid`/non-`CleanEnd` boundary encountered during this re-scan (i.e.
   a `Truncated` or `Corrupt` tail) as a hard finalize failure
   (`IndexBuildSourceNotClean`) — an ingestion-sealed log that has already passed
   through this adapter's own successful `CommitAsync` calls is expected to be entirely
   clean. A non-clean tail fails explicitly without inferring its physical cause;
   finalization does not silently tolerate a recovered prefix.
4. Reconciles the rebuilt row count and last sequence against the ingestion-side
   counters (`_totalCommittedRecords`, `_lastCommittedSequence`) that were advanced
   purely from footer-flush confirmations, with **no** SQLite involvement — any
   mismatch is `IndexCanonicalReconciliationMismatch`, a hard failure. A bounded
   sample (the first record of each key, at most 128) is also compared field-for-field
   with the indexed row; this is not a claim that every indexed row was re-read.
5. Only on success: validates the terminal quality report for self-consistency
   (`DurableStorageQualityRules.Validate(terminalQuality)`), seals the index against
   further writes (`SealForLiveReading`), hashes the members and validates all pre-seal
   metadata. Only after all these steps succeed is ownership transferred to `Reader`.
6. On any failure anywhere in this sequence, the half-built index file is disposed and
   deletion is attempted, and a second finalize attempt on the same
   adapter instance is rejected with `InvalidOperationException` — finalize is not
   retried automatically, and a failed finalize can never publish a Reader.
   Cleanup failures surface rather than being silently ignored.

This directly replaces the earlier (superseded) design, in which the index was built
incrementally inside `CommitAsync` and `FinalizePreSealAsync` merely closed it; that
design allowed a post-F3, pre-return index-insert fault to escape uncaught and be
reported as a pipeline `Failed` despite an already-durable canonical frame, and gave no
reconciliation step to catch an index/canonical divergence at seal time. The
re-scan-and-reconcile approach removes both failure modes by construction, at the cost
of a full canonical re-read at finalize time, using bounded per-frame allocations.
The aggregate package-growth enforcement gate remains unresolved.

### Derived SQLite query index (`DurableAppendFirstQueryIndex`)

One table (`records`) with all 17 logical fields from `DurableStorageLogicalSchema`,
indexed by `(provider, name, sequence)`. Built with `journal_mode=DELETE` (not WAL) so
that once the connection is closed there is exactly one file on disk — no `-wal`/`-shm`
sidecars to reconcile on later strictly-read-only reopen. `cache_size=-2048` sets
a cache target and `mmap_size=0` disables database mmap; these are not a hard
bound on all SQLite memory.

Fresh opens validate adapter/package/record identities, SQLite application and
schema versions, all 17 table columns and the required index. Unsupported shapes
fail with typed errors. Read-only URI access uses `immutable=1`, and connection
pooling is disabled so disposal releases the native handles.

- `SummaryAsync` runs one grouped SQL query (`GROUP BY provider, name`, correlated
  subqueries for first/last value/kind/unit/sourceTimeTicks) capped at
  `DistinctKeys` (128) result rows, with an extra row used to detect and reject
  overflow. Output uses .NET ordinal ordering. No per-record row is materialized into
  managed memory for this view; only the aggregate projection crosses into .NET.
- `SeriesAsync` is a single parameterized, cursor-based (`sequence > @after`), ordered,
  `LIMIT pageSize + 1` query — the same "ask for one more row to detect `hasMore`"
  technique the shared in-memory `DurableCounterQuery` already uses, so paging
  semantics match exactly. Page size is capped at 100 (`PageRows`). All three
  typed views serialize their results and enforce the supplied `ResultBytes` limit.
- `QualityAsync` on the live (post-seal) adapter's own `Reader` returns the exact
  terminal quality report passed into `FinalizePreSealAsync` (fixed once at seal time
  by `SealForLiveReading`); the `Reader` does not exist, and this method cannot be
  called, before that point. `QualityAsync` on a reopened (`OpenReadonly`) store
  instead returns the exact host-validated manifest snapshot
  (`manifest.FinalPipelineQuality`, `manifest.VolatileTailUnknown`), with
  `RetainedRecords` independently recomputed via a strictly read-only `SELECT
  COUNT(*)` probe against the already SHA-256-verified query file — never derived from
  re-scanning retained rows, and never claiming more than the manifest snapshot itself
  claims.

### Byte accounting (`DurableAppendFirstCapacityGuard`)

Canonical growth is reserved **exactly**, before each write, because the adapter fully
controls that byte count (`ReserveCanonicalGrowth`/`ReleaseCanonicalGrowth`). The
derived SQLite index's growth is only observable after SQLite performs its own page
allocation, so the database file size is checked at the end of index construction
or recovery (`ObserveQueryBytes`), not after each transaction or before growth.
Transient journals and temporary allocations are not thereby proven bounded.
**This asymmetry is an explicit,
stated limitation** — it is not a claim of byte-exact pre-reservation on the SQLite
side, only on the canonical append-only side. Both are compared against the same
frozen 268,435,456-byte (256 MiB) combined cap.

### Recovery (`DurableAppendFirstStorageAdapterFactory.RecoverAsync`)

Reads the source canonical file strictly read-only (`FileShare.Read`, no write handle
ever opened against it) and scans frame-by-frame, tracking a running cross-frame
sequence watermark exactly like ingestion does. Every stream and connection opened
during recovery (the new canonical output file, the new SQLite index connection) is
wrapped so disposal is attempted for both on every exit path. Failed recovery
attempts remove only files created by that attempt; cleanup errors surface.
Source/nested-source and nonempty destination roots are rejected before writes.
Observed source file lengths are charged alongside the new output, but this does
not establish the native transient-growth bound.

The scan distinguishes three outcomes at a frame boundary, and never conflates them:

- **Truncated** — structurally incomplete bytes, without an inferred cause.
  The scan stops cleanly and returns a normal `DurableStorageRecoveryResult` over the
  verified prefix, with `VolatileTailUnknown: true`.
- **Corrupt** — a complete-length frame whose checksum, footer marker, or sequence
  membership does not match what its header declared. This is a hard, explicit
  failure: `RecoverAsync` throws `DurableStorageExperimentException("RecoveryCorruptedSource", ...)`
  and **no** `DurableStorageRecoveryResult` is ever returned. Silently treating
  corruption the same as a clean truncation would launder a genuine data-integrity
  problem into a "verified prefix" claim. **Internal scope limitation:** the shared
  `DurableStorageRecoveryResult` record has a fixed field set with no slot to carry
  *which* boundary type stopped a (non-throwing) recovery or its byte offset; this
  spike does not invent a private extension for it. Clean EOF and truncated-tail
  success both retain unknown volatile-tail quality as required by the existing
  contract. Corruption is already distinguishable by its typed failure.
- **Duplicate/regressed sequence across a frame boundary** — rejected before any
  insertion with a typed `DurableStorageExperimentException("RecoveredSequenceRegression", ...)`,
  the same watermark check `CommitAsync` performs during ingestion, so that a
  duplicate never reaches the derived index's primary key and fails there opaquely.

Only frames validated as `Valid` up to a stopping boundary are copied — byte-for-byte,
re-read from the exact validated range — into a brand-new canonical file, alongside a
freshly built derived index, both emitted as **ordinary** `CanonicalData`/`QueryIndex`
manifest members at the same relative paths (`canonical/records.bin`,
`query/index.db`) a normal sealed package uses — not a `RecoveryOutput`-tagged member
under a `recovery/` directory. This means a host can build a manifest directly from
`RecoveredMembers` and hand it to the same public `OpenReadonly` entry point a normal
package uses, with no adapter-specific recovery-aware code needed on the host side;
this is verified directly by a round-trip test. The result always reports
`VolatileTailUnknown: true` and a new capture/artifact identity pair distinct from the
source (enforced by `DurableStorageRecoveryRules.Validate`, called defensively before
returning). The source package is never opened for writing, never reindexed, and never
mutated.

### Factory lifecycle (`DurableAppendFirstStorageAdapterFactory`)

- `Create` validates the P1-only configuration schema (`{"profile":"P1"}`, no other
  keys, no other profile value — anything else throws
  `UnsupportedConfigurationProfile`) and constructs a fresh writer; it never opens or
  mutates an existing package.
- `OpenReadonly` verifies the sealed manifest's canonical and query member
  length+SHA-256 against the files on disk before opening the query index strictly
  read-only (`Mode=ReadOnly`, `PRAGMA query_only=1`). It creates no new files.
- `RecoverAsync` never creates a writer against the source; it only ever writes new
  staging output, as described above.

## What this spike does not do

- It does not claim any performance number, throughput rate, or "faster than A"
  conclusion. No benchmark, load generation, or live EventPipe capture was run.
- It does not run, launch, or approve the 35-case DC5 execution grid; the execution
  gate remains closed at the shared-contract level regardless of this code.
- It does not implement F4 (`AfterIndexesBeforeSeal`) or F5 (`ConcurrentCaptureGate`)
  fault-barrier firing; those remain host-owned per the shared handoff.
- It does not attempt byte-exact pre-reservation of SQLite's own on-disk growth — only
  a post-write observed check against the same combined cap.
- It does not extend the shared `DurableStorageRecoveryResult` schema to distinguish
  clean EOF from truncated-tail success; both retain unknown volatile-tail quality.
  Corruption produces a typed failure, not a successful recovery result.
- It does not register this adapter with any host/runner. The shared
  `DurableStorageAdapterRegistry` is not wired to this factory in this change.

## Validation performed

Component tests (`DurableAppendFirstStorageAdapterTests`; not the DC5
campaign, not comparative A-vs-B evidence) cover: an end-to-end Q1 run through the real
shared `DurableCounterPipeline`, checked against the independently authored
`DurableCounterOracle.Q1()` after a full finalize+reopen cycle, including an explicit
assertion that `Reader` throws before seal and a full 17-logical-field parity check on
the first 100 rows plus a sample from each key (not just the small oracle-field subset);
the Q2 fixture's explicit
reset/gap semantics (a `Sum` decrease is never reset evidence); frame round-trip,
truncation, and corruption detection in isolation; the F1/F2/F3 fault-barrier behaviors
(instrumented-stream storage-full injection and its non-`IOException`-wrapping
behavior, pre-commit leaves no trace, post-commit-ack-gap is `Unknown` never `Failed`,
verified end-to-end through a real pipeline run whose retained row count is checked
against the conservative `[Committed, Committed + UnknownCommitOutcome]` bound); a
cross-frame sequence regression rejected before any write on the ingestion side and
before any insertion on the recovery side; an index-build failure on a post-commit
corrupted canonical log that must fail `FinalizePreSealAsync`, leave no half-built
index file behind, and permanently block the `Reader`; recovery's truncated-vs-corrupt
distinction (a truncated tail yields a verified-prefix result, a corrupted frame throws
explicitly and publishes nothing); recovery cancellation after an actual frame and
index transaction have been written, followed by resource and owned-output cleanup;
recovery's public-factory round-trip (a manifest
built directly from `RecoveredMembers` opens through the same `OpenReadonly` path a
normal package uses, with new IDs, null final pipeline quality, and an unknown volatile
tail); strict reopen immutability (no new files, no changed hashes across a full
summary/series/quality read cycle); the capacity guard's cap rejection;
configuration-profile rejection; foreign identity/version and malformed query-schema
rejection (including persisted WAL mode); late finalization failure without Reader
publication; per-key projection mismatch detection; occupied/source recovery-root
protection; and actual serialized result-byte limits for every typed view.

Operation hooks additionally exercise an actual flushed frame followed by both a
reported flush failure and a failed rollback. The real pipeline reports an unknown
commit outcome, further writes/finalization are blocked, and explicit recovery retains
the frame. This establishes classification under instrumentation, not native I/O-fault,
process-kill, power-loss or filesystem durability guarantees.

Targeted validation uses the existing Core project and the
`FullyQualifiedName~DurableCounterSpike` selector with SDK 10.0.201, serialized
through the shared `ci-validation.lock`. No comparative campaign is represented by
these component cases.
