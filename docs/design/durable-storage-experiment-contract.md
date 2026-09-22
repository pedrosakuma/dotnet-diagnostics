# DC5 storage experiment foundation contract

**Status:** Partial DC5 internal foundation, accepted after independent review. The
execution gate is closed. No adapter, live capture, benchmark campaign, storage
engine, or production/public API is approved.

## Purpose and shared ownership

The accepted DC4 pipeline, fixture generator, oracle, typed query DTOs, and
bounded ownership logic now compile once from
`tests/DotnetDiagnostics.TestSupport/DurableCounterSpike/`. Core tests and the
`DiagnosedBenchmarks` executable consume that same internal assembly through
explicit `InternalsVisibleTo` friendships. There is no copied A/B pipeline.

The foundation adds the minimum shared contract needed for independent adapter
work:

- `IDurableCounterStorageAdapterFactory` owns adapter identity, version,
  configuration schema, commit-acknowledgement meaning, fresh read-only opens,
  and explicit recovery into new capture/artifact IDs;
- `IDurableCounterStorageAdapter` implements the existing bounded
  `IDurableCounterSink`, exposes the fixed read-only typed views, completes
  query/index files before sealing;
- `IDurableCounterReadonlyStore` exposes only bounded `summary`, exclusive-
  cursor `series`, and `quality` projections;
- package manifest, member, pre-seal, seal, recovery, and error records are
  versioned and candidate-neutral;
- `IDurableStorageFaultController` names the DC3 F1-F5/ack-gap barriers without
  implementing process termination or backend faults in this foundation.

No factory is registered yet. Adapter lookup is exact and an unknown ID fails;
there is no fallback or success-shaped no-op.

The common adapter preparation base references the already centrally pinned
`Microsoft.Data.Sqlite` package from TestSupport only. This does not add a
production dependency or register a backend.

### Independent reader and recovery lifetime

Fresh opens and recovery are factory operations, not operations that require
creating a new writer. `OpenReadonly` receives the package root, manifest and
shared pipeline/query limits
only after the host has validated the immutable seal, member hashes and current
authorization and acquired a bounded reader lease. The factory must validate
its own identity/schema expectations and open only declared member paths; it
must not initialize a database or create auxiliary files.

Readers implement `IAsyncDisposable` so connections and retained buffers have
an explicit bounded lifetime. The caller disposes readers returned by
`OpenReadonly`; the host owns the lease and must hold it until reader disposal
completes. An adapter owns and disposes its `Reader` in its own `DisposeAsync`;
callers borrow that reader and must not dispose it separately.
Adapter-owned `Reader` access remains unavailable
during acquisition; it becomes usable only after pre-seal finalization.
Unsealed packages are not ordinary opens: host validation must return a
recovery-required error before invoking the reader factory. These host duties
remain part of the runner gate, not a claim that this foundation enforces them.

Factory recovery does not create or mutate a source writer. It writes only to
the explicitly new recovery staging root, returning new IDs and provenance.
The host owns exclusive leases, source/seal validation, final publication and
physical quotas. No recovery implementation exists in this foundation.

### Quality is not reconstructed from retained rows

After drain completes and ownership is quiescent, the host passes the exact
DC4 `DurableCounterQualityReport` to `FinalizePreSealAsync` and persists it as
`FinalPipelineQuality` in the manifest covered by the seal. Adapter finalization
and host publication must share the protocol's finalization deadline, not each
receive a fresh time allowance. Sink `FinalizeAsync` is the preceding sink
completion step, not permission to publish a seal or query an active writer.

Storage quality wraps this unchanged report with `RetainedRecords` and
`VolatileTailUnknown`. Normal finalized captures preserve the host snapshot
including rejections, cancellation and commit uncertainty; adapters cannot
infer those values from committed rows. Fresh readers use the host-validated
manifest snapshot and validate it with `Validate(report, manifest)`, including
rejection entries by value rather than dictionary identity. Commit uncertainty
does not erase admission conservation: known commits are a lower bound on
retained rows, and known plus uncertain commits are the upper bound.
The inner DC4 report is compared to the original oracle;
no fixture or oracle hash changes for the storage envelope.

Explicitly recovered packages have `FinalPipelineQuality = null` and
`VolatileTailUnknown = true`, plus their independently counted retained rows.
They do not invent zero offered/rejected/abandoned counts or treat recovered
row count as the old admission total. The source package still preserves any
historical quality it contained, but it is not relabeled as terminal accounting
for the new derived capture.

## Fixed logical schema and sink ownership

The shared logical columns are fixed in `DurableStorageLogicalSchema`: sequence,
provider/name/display name/unit, finite value and `CounterKind`, interval and
display-scale values/states, source time and clock metadata, coverage gap,
unknown reset state, and encoded length. Candidate implementations may choose
physical types and indexes but may not remove, reinterpret, or silently derive
these logical fields.

`IDurableCounterSink.CommitAsync` receives at most the frozen 64 records and
262,144 owned bytes. Each item contains the typed record and the same bounded
encoded memory owned by the pipeline. That memory is valid only until the
commit call returns; an adapter must synchronously consume or explicitly copy
it before acknowledgement. `Committed`, `Failed`, and `Unknown` remain distinct
outcomes. The post-commit/pre-ack barrier therefore cannot be reported as a
known failure.

## Package and seal boundary

The versioned package vocabulary reserves:

```text
manifest.json
canonical/<adapter canonical members>
query/<pre-seal query/index members>
seal.json
recovery/<temporary or derived recovery output>
```

Paths are relative, unique, traversal-free, normalized with forward slashes,
and restricted to the corresponding `canonical/` or `query/` directory. They
are accompanied by length, SHA-256, and role.
`FinalizePreSealAsync` must return all canonical and query
members only after required indexes are complete. The host creates the final
manifest and seal; ordinary reopen must not add indexes or mutate canonical
members.

The seal covers the manifest hash and the canonical-order member inventory.
The shared validation boundary checks member shape, frozen 4,096-byte record
and 64/262,144 batch limits, and the 256 MiB final-package ceiling. Actual file
hash calculation, symlink/hardlink rejection, staging, atomic publication,
process-crash recovery, and package quotas belong to the runner/adapters and
remain unimplemented.

Candidate B's framing contract fixes the validation vocabulary before its
implementation: magic/version, batch length, record count, first/last sequence,
payload SHA-256, and commit footer. Candidate A may not pretend a SQLite row is
the logical record/accounting unit; both adapters acknowledge only their
declared completed batch commit.

## Fault barriers

The shared barrier enum provides extension points for:

- storage-full on the next batch (F1);
- before commit (F2);
- after commit but before acknowledgement (F3);
- after manifest/index completion but before seal (F4);
- concurrent-capture gate (F5);
- a pre-write barrier used by deterministic adapter tests.

This foundation does not kill processes or claim that injected exceptions
model real filesystem ENOSPC. A/B implementations own their separate fault
controller files and tests.

## Closed executable host

`DiagnosedBenchmarks` now routes:

```text
durable-capture-spike help
durable-capture-spike describe
durable-capture-spike validate-manifest --manifest <path> --repository-root <path>
```

The route occurs before BenchmarkDotNet fallback. `describe` reports an empty
adapter catalog and a closed execution gate. `validate-manifest` is read-only:
it bounds and parses the manifest, requires protocol/pipeline/runtime/host/clock
identities, hashes the referenced accepted protocol and frozen fixture
manifest, checks protocol revision 3, and rejects enabled execution or unknown
adapters. No `run` command exists.

The current resolved-manifest type is foundation validation, not the final DC5
run manifest. It intentionally cannot authorize the 35-execution campaign.

## Parallel extension ownership

To avoid shared-file conflicts in the next phase:

| Owner | Files/scope |
| --- | --- |
| Shared foundation | `DurableStorageExperimentContract.cs`, executable command routing, resolved-manifest validation |
| Candidate A | New `DurableCounterSpike/Sqlite/` files implementing factory, adapter, reader, schema, PRAGMA verification, and A-specific faults |
| Candidate B | New `DurableCounterSpike/AppendFirst/` files implementing factory, framing, derived pre-seal query index, reader, and B-specific faults |
| Runner/fault campaign | New `benchmarks/DiagnosedBenchmarks/DurableCapture/` files for live capture integration, process isolation/termination, package publication/recovery, measurements, and resolved run manifest |

Adapter branches should add factories through composition in a new registry
bootstrap file rather than editing the shared contract. If both candidates need
a contract change, stop parallel implementation and review that change first.

## Remaining gates and limits

This foundation proves compilation, manifest/hash validation, closed execution,
unknown-adapter failure, package metadata validation, and continued DC4
behavior. It does not implement either backend, SQL schema, framed writes,
package I/O, live counter mapping, seal publication, explicit recovery,
filesystem containment, crash barriers, benchmarks, or campaign evidence.

DC5 remains blocked on independently reviewed A and B adapters, runner/live
integration, complete package lifecycle tests, a fully resolved immutable run
manifest, and explicit authorization to execute the accepted protocol.
