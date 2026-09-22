# DC5 candidate A: direct SQLite storage spike

**Status:** Internal component prototype accepted after independent review. This is not
DC5 campaign readiness, a production storage choice, or approval to execute the
35-run comparison protocol.

## Implemented candidate

`DurableSqliteStorageAdapterFactory` identifies candidate `A`, version
`dc5-sqlite-p1/1`, with configuration schema
`durable-sqlite-p1-config/1`. The only accepted configuration is:

```json
{ "profile": "P1" }
```

Unknown properties and alternative values fail explicitly. The adapter uses one
logical writer and one prepared insert command per bounded transaction. It
copies the 17 logical record fields into SQLite parameters and does not retain
the pipeline-owned encoded memory after `CommitAsync` returns.

The write connection applies and reads back the frozen profile:

- `journal_mode=WAL`;
- `synchronous=FULL`;
- `cache_size=-2048`;
- `mmap_size=0`.

The canonical member is `canonical/counters.db`. The adapter creates the
`(provider, name, sequence)` query index and checkpoints every WAL frame before
returning pre-seal metadata. Manifest and seal publication remain host-owned.

## Typed reads and quality

Fresh opens use SQLite read-only/query-only mode, validate adapter/database
identity, require the pre-seal index, run `quick_check(1)`, and expose only the
shared typed `summary`, exclusive-cursor `series`, and `quality` projections.
There is no arbitrary SQL surface.

Summary holds at most 128 aggregate rows. Series reads at most 101 rows to
produce a 100-row page and continuation cursor. Typed results enforce the
1 MiB result limit; retained series data is additionally bounded by the 8 MiB
reader-buffer limit. Ordering that leaves SQLite is normalized with .NET
ordinal comparison where the DC4 contract requires it.

Pre-seal finalization receives the exact host terminal quality snapshot.
Ordinary fresh readers validate the storage envelope against the
host-validated manifest with the two-argument quality rule. Recovered packages
instead report a known retained row count, null terminal pipeline quality, and
an unknown volatile tail.

The component tests separately prove snapshot binding rather than treating a
tampered manifest as a second source of truth. An alternate quality envelope
with internally conserved accounting passes the one-argument validation, but
opening the original authoritative manifest with that alternate envelope fails
with `QualitySnapshotMismatch`. Manifest authentication and member-hash
validation remain host gates.

## Fault and recovery seams

Candidate A exposes component-level versions of:

- F1: set `max_page_count` to the current allocated page count before the
  second batch and require a real `SQLITE_FULL`;
- F2: named pre-commit barrier, whose failure rolls back and is known failed;
- F3: named post-commit/pre-acknowledgement barrier, whose failure or
  cancellation is reported as unknown rather than known failed.

Transaction handling separates preparation, commit attempt, transaction
cleanup, and acknowledgement. A failure is known failed only when it occurs
before the commit attempt and rollback/cleanup completes. A commit-attempt
exception, rollback or cleanup failure, and a cleanup failure after successful
commit all return `Unknown`. The exercised F1 `SQLITE_FULL` case remains known
failed after successful explicit rollback and cleanup. An automatic-rollback
fallback additionally permits known failure after successful cleanup when a
bounded sequence check proves that none of the batch rows are present; that
fallback is not exercised by the current component tests.

Component-local operation hooks force commit-attempt, commit-then-throw,
rollback, and cleanup failures. These hooks test classification and recovery;
they are not evidence about native I/O faults or process termination.

F4 publication timing and F5 host-global capture admission remain runner
responsibilities and are not simulated at a different adapter stage.

Explicit recovery copies only the bounded source database and WAL into an
exclusive new staging root. The transient shared-memory index is counted in the
source package peak but rebuilt for the copied WAL rather than copied. Recovery
opens and checkpoints the copy, creates any missing query index there, rewrites
only the copied capture/artifact identity, and returns new provenance. Tests
hash every source file before and after recovery. The original package is never
opened writable or repaired.

## Resource boundary and remaining runner precondition

The adapter sets SQLite `max_page_count` from the 256 MiB package ceiling,
checks observed package bytes before each bounded write/finalization/recovery
stage, and rejects a next-operation reservation that cannot fit. Recovery
copies use a fixed 1 MiB buffer and reject source members beyond the package
limit.

These checks are not a filesystem hard quota. SQLite may allocate database,
WAL, shared-memory, or filesystem metadata between application checks, and an
observed-size reservation cannot prove that no transient overshoot occurred.
The DC5 runner must therefore provide an exclusive artifact root with a
reviewed physical quota/reservation mechanism that includes staging and
recovery output before the comparison campaign can claim the protocol's
256 MiB peak bound. This component makes no real-ENOSPC, power-loss, process-
kill, performance, or campaign result claim.

## Integration handoff

The coordinator can compose `DurableSqliteStorageAdapterFactory` into a new
registry bootstrap without editing the common contract. The future runner must
supply host-validated manifests, reader/writer leases, finalization deadlines,
physical package quotas, publication/seal operations, and exact-process fault
orchestration. Adapter availability alone must not open the experiment
execution gate.
