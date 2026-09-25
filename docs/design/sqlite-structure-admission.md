# Isolated SQLite structure/scalar admission

This internal #1050 milestone extends the [worker substrate](isolated-import-worker.md).
This operation alone is **not full capture validation or import**. The
[configured Core importer](portable-capture-import.md) composes it with archive,
semantic, rebuild and publication checks. Default public import still reports
`UnsupportedFormat/ImportWorkerUnavailable` without safe worker configuration.

## Entry point and trust boundary

`IsolatedCaptureWorker.AdmitSqliteAsync(SqliteAdmissionRequest, Stream,
SqliteAdmissionLimits?, CancellationToken)` selects a parent-owned private
staging file and streams **provisional** evidence to the caller-owned output.
The parent never opens that source with SQLite. It checks bounded absolute
paths, symlinks, staging permissions, sidecars and the fixed 100-byte header;
it retains a read lease while the native child runs. Asset paths and the
immutable URI are built from trusted configuration, never archived URI options.
This method is separate from the capability probe and needs no helper PID.

The parent must already have authorized/reserved staging and checked the
archive, seals and bounded manifest metadata. This slice does not perform
those upstream operations. Its minimal descriptor contains the six format
axes, current entry artifact IDs/kinds/names, and expected persisted count.
It contains no owner claims, SQL, native paths or import authority.

Inside the existing Landlock/seccomp confinement, `--admit` receives a
length-prefixed request (at most 128 KiB), rechecks file geometry and rejects
WAL/SHM/journal sidecars. A checkpointed WAL-mode header is supported if the
main file is independently complete. SQLite hard heap is configured before
open; connection limits, defensive/trusted-schema/extension settings and
authorizer/progress hooks precede every application query.

The shared supervisor retains CPU/wall/RSS/gap enforcement and bounded I/O.
A zero-RSS observation does not establish whether the child is exiting or
telemetry is unavailable. Exit confirmation may use only the remaining time
before the **last valid RSS sample plus 10 ms**, also bounded by the original
wall deadline and cancellation. Whole-millisecond waits are rounded down;
sub-millisecond remainders permit only nonblocking exit probes. Zero RSS never
becomes a valid zero-cost sample or advances either deadline. Only confirmed
exit observed within that window can conclude this reconciliation. Still
unconfirmed at the exact deadline is unavailable; observation beyond it is a
gap failure, even if exit is then confirmed. Exit-observation errors also fail
closed. No host settings, priorities, validation retries or new time allowance
are used. A process-metric read failure can also reconcile a positively confirmed
exit within that same original window; the read failure alone is not evidence.
Confirmed exit ends only RSS observation, not wall/cancellation enforcement or
the requirement to drain pending output. Cleanup joins cancelled I/O before
receipt finalization; unconfirmed I/O retains staging and reservations.

## Actual validation

The native validator in `Isolation/native/sqlite_admission.h`:

- Streams bounded `sqlite_schema` metadata and requires exactly the six
  foundation tables, six named indexes and four corresponding autoindexes.
  It compares full allowlisted DDL tokens, including columns, types, nullability,
  keys, references and checks. Only whitespace and known identifier quoting
  variations are accepted. String literals, quoted numbers/keywords, comments
  and additional clauses are not normalized into an accepted definition.
- Verifies index membership, uniqueness, origin, partial-index flags, full key
  order, binary collation and ascending direction using fixed index PRAGMAs.
  It rejects views, triggers, virtual/generated objects, statistics and extra
  indexes before data scans. Archived DDL is never executed.
- Runs bounded `quick_check(1)` and foreign-key validation, then streams every
  data table, not merely rows reachable from the first public query.
- Checks storage classes, strict UTF-8, positive/ordered occurrence and string
  IDs, lowercase artifact IDs, descriptor equality, contiguous field ordinals,
  scalar slots, finite doubles, booleans, UTC tick bounds and nonnegative
  durations. Unused string dimensions are rejected. SQL joins/correlated reads
  validate references without parent/native population-sized hash maps.
- Applies exact foundation string/logical-record accounting, field/record/
  snapshot/logical-byte caps, table/aggregate row caps, and persisted occurrence
  population equality. The parent additionally bounds snapshot JSON depth
  (64), tokens and aggregate tokens before any typed DTO materialization.

All statements are compiled-in metadata/data statements or parameterized
record lookups. The authorizer denies writes, attach, extensions, functions and
unexpected PRAGMAs. The cumulative 200,000,000 VM budget includes metadata,
integrity and row queries. Progress callbacks occur every 1,000 instructions;
`sqlite3_stmt_status(SQLITE_STMTSTATUS_VM_STEP)` also charges completed steps
from short statements that never reach a callback. In-progress callbacks check
the running statement against the remaining cumulative budget. Native parsing
outside VM execution is still covered by heap/time/process monitoring.

## Internal evidence wire v1

After the nonce/version handshake, the parent sends exactly `GO\n`, then a
little-endian request length and bounded binary descriptors/limits. Each output
frame has a little-endian 32-bit payload length (1..65,536), followed by a tag:

| Tag | Payload after tag |
|---|---|
| 1 | Table number (format/strings/artifacts/occurrences/fields/snapshots = 0..5), column count |
| 2 | SQLite storage class (1 integer, 2 double, 3 UTF-8, 4 blob, 5 null), uint32 byte length |
| 3 | Cell bytes, chunked to at most 65,535 bytes; integers/doubles use exact little-endian 64-bit representation |
| 4 | Row end |
| 5 | Six int64 row counts, int64 logical bytes, int64 cumulative VM work |
| 127 | Bounded fixed ASCII failure reason; no SQL or source-controlled error text |

The parent checks frame lengths **before allocation**, table/column order,
storage tags, cell lengths, row counts, snapshot limits and final populations.
Wire consumption is capped at 512 MiB including framing; overhead may therefore
reject otherwise compact evidence. There are no population-sized queues:
backpressure is a caller-owned stream, one reusable 64 KiB frame buffer, and at
most one reserved 8 MiB snapshot buffer for syntax/depth/token scanning, well
below the 32 MiB retained-buffer ceiling. Subsequent typed graph allocation uses
the Core importer's separate conservative workspace checks, not this reservation.

The internal request can select protocol 2 to append an int64 CPU-microsecond
measurement to tag 5. It uses the child's own `getrusage`; actual wire accounting
includes those eight bytes. The parent persists a normalized protocol-1 summary
and returns CPU/wall/VM usage separately so the fresh rebuild consumes only the
remaining per-entry budgets. This does not change the archive format.

Frames may precede a later error. Even tag 5 is provisional until EOF, successful
child exit, final watchdog/cancellation checks and output flush. Callers must
discard the entire prefix on any failure and must not publish/import from a
partial stream. Output/cancellation faults terminate the child. Precise
`Schema.*`, `Data.*`, `Header.*`, `Wire.*`, `Format.*` and `Limit.*` reasons
distinguish malformed data, unsupported formats, resource rejection and I/O.

## Evidence and remaining gates

Tests preserve the existing sealed v1 archive/database hashes and add an
independently generated, frozen v2 database with generator/provenance alongside
`Fixtures/SqliteAdmission/`. The v1 synthetic codec and v2 synthetic JSON are
**not asserted to be supported typed snapshots**. Invalid fixtures are created
from trusted test SQL into new private files; all validation reads use the
contained child. Tests cover schema/index/constraint changes, malformed scalars,
UTF-8/FKs/ordinals, native corruption, resource boundaries, framing, cancellation
and output failure, plus an actual exported database opened offline.

Contract section 7 steps 6-8 are composed by the separate Core importer:
typed codecs and reference validation, quality/provenance checks, fresh-schema
rebuild, remapping and publication. This low-level operation still matches only
its documented structure/scalar populations. A `SqliteAdmissionResult` alone
does not establish whole-capture validity. Host packaging and release/performance
acceptance remain separate gates.
