# Durable capture contract and internal spike gate

**Status:** Independently reviewed and accepted for the internal DC2 spike and
DC3 protocol work under the authorized orchestration. Not approved for
production implementation, release, storage-engine selection, or public APIs.

**Tracking:** [#1004](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1004),
under [#999](https://github.com/pedrosakuma/dotnet-diagnostics/issues/999).

**Review:** Claude Opus 5 reviewed the GPT-5.6 Sol-authored contract. Acceptance
was conditional on correcting the distinct-monitor semantics; that correction
and a zero-lock-ID fixture are included below. Public decisions remain at DC6.

**Related design:** [durable-capture RFC](durable-capture-rfc.md) and
[discussion record](durable-capture-discussion.md).

This document narrows the RFC to an implementable, test-only Core contract.
DC2 may exercise it without launching a target, opening a new EventPipe session,
adding a database dependency, or exposing a public command. Any JSON used by
that spike is an internal fixture format, not approval of a public continuity
format or a production backend. DC3 owns numeric comparison budgets. DC6 must
approve public API placement before an adapter ships.

## 1. Exactly one allowlisted artifact

The spike allowlist contains exactly one existing artifact:
`DotnetDiagnostics.Core.Contention.ContentionSnapshot`, handle kind
`contention-snapshot`.

This artifact is appropriate because it is self-contained after collection and
its retained event list is capped at
`EventPipeContentionCollector.MaxTrackedEvents` (200). The collector retains the
longest events, caps distinct monitor tracking
at 4,096, keeps aggregate totals, and records retention/approximation limitations
in `Notes`. Its handle is registered with `evictWhenProcessExits: false`, and its
three current query views read only the stored object.

These are cardinality bounds, not a proof of a total byte bound: method/module
strings and notes also consume space. DC2 must enforce finite serialized-input,
string/collection, manifest, and package limits before unbounded materialization.
Its limits are test-scoped inputs, not production defaults or DC3 budgets.

`CounterSnapshot` is deliberately **not** the first artifact. Meter series have a
caller-supplied cap, but the current EventCounters dictionaries do not impose a
general distinct-counter-key cap. It also stores only first/latest/max values,
not timestamped ticks. Selecting it would either weaken the bounded-artifact
gate or imply temporal continuity that does not exist. A single bounded counter
series remains the separate DC3 A/B experiment.

All other artifact kinds, arbitrary object serialization, untrusted package
import, native files, and live-origin dependencies are unsupported by the DC2
spike. An allowlist miss fails with `UnsupportedArtifactKind`; it never falls
back to reflection serialization.

### 1.1 Retained representation

The artifact representation copies only the existing DTO fields:

| Retained field | Meaning and unit |
| --- | --- |
| `ProcessId` | Historical local PID, provenance only; never sufficient target identity |
| `StartedAt` | UTC capture start represented by the collector |
| `Duration` | Requested collector window as a duration; not proof of complete coverage |
| `TotalEvents` | Matched contention completions observed by the collector |
| `DistinctMonitors` | Distinct non-zero lock IDs, exact for that tracked subset until the 4,096-ID cap. A lower bound on all monitors: zero IDs are excluded without a note; only cap overflow produces a limitation note. |
| `TotalContentionDuration` | Sum of observed matched wait durations, `TimeSpan` |
| `P50ContentionDuration`, `P95ContentionDuration` | Duration percentiles; reservoir approximations after the collector's exact-sample capacity |
| `MaxContentionDuration` | Maximum observed matched duration |
| `Events` | At most 200 retained longest events |
| `Notes` | Existing ordered limitations; preserved verbatim |

Each `ContentionEventSample` preserves `StartedAt`, `StoppedAt`, `Duration`,
`ContendingThreadId`, nullable `OwnerManagedThreadId`, `LockId`,
`AssociatedObjectId`, `CallSiteMethod`, and `CallSiteModule`. Durations remain
durations, IDs remain integers, and timestamps remain timestamps; the spike must
not flatten these into display strings.

`ContentionSnapshot` has no `EvidenceQuality` property. The spike therefore
stores its `Notes` unchanged and marks structured artifact quality as
`EvidenceQuality.LegacyUnknown`; it must not parse English notes to manufacture
exact drop counts. Representation-level quality may additionally report
publication, integrity, version, or recovery limitations, but cannot overwrite
collector semantics.

### 1.2 Supported typed views

After reopen, the reader must reproduce the current
`CollectionQueryDispatcher` behavior for:

- `summary`: `ContentionSummaryView`;
- `byCallSite`: `ContentionByCallSiteView`, ranked and bounded by `topN`;
- `byOwner`: `ContentionByOwnerView`, ranked and bounded by `topN`.

The payload, ordering, units, capture start, duration, and handle kind must match
dispatch over the original in-memory snapshot. No `events`, timeline, raw,
live-refresh, or cross-artifact view is available. Unsupported views return
`UnsupportedView` with the supported list; they do not reattach or expose the
stored object as arbitrary JSON.

Source map:

- DTO: `src/DotnetDiagnostics.Core/Contention/ContentionSnapshot.cs`;
- retention and limitations:
  `src/DotnetDiagnostics.Core/Contention/EventPipeContentionCollector.cs`;
- registered self-contained handle:
  `src/DotnetDiagnostics.Core/UseCases/EventCollectionUseCases.cs`;
- typed views:
  `src/DotnetDiagnostics.Core/Collection/CollectionQueryDispatcher.cs` and
  `CollectionQueryResult.cs`;
- current MCP authorization:
  `src/DotnetDiagnostics.Mcp/Tools/QuerySnapshotTool.Dispatch.cs`;
- boundedness summary: `docs/resource-boundedness.md`.

## 2. Identity and compatibility

Four values remain distinct:

- **`CaptureId`**: stable opaque identifier for one logical package. It is
  generated once and embedded in canonical metadata.
- **`ArtifactId`**: stable opaque identifier for one retained representation
  inside that capture. The spike permits exactly one.
- **locator**: host-local relative path or catalog key used to find bytes. It is
  mutable operational metadata and is not identity.
- **handle**: temporary authorized capability registered in
  `IDiagnosticHandleStore` after selection. It can expire or be evicted without
  changing either durable ID.

Callers select by `CaptureId` plus `ArtifactId`. A path, PID, display name, or
old handle cannot substitute for either ID. Reopen returns a fresh handle with
`HandleOrigin.Imported`; it never revives the original handle.

The package declares independent versions:

1. `contractVersion`: package/lifecycle semantics;
2. `representationKind` and `representationVersion`: for this spike,
   `contention-snapshot` and an exact adapter version;
3. `writerVersion`: implementation that emitted the canonical bytes;
4. `minimumReaderContractVersion`: oldest contract reader explicitly supported.

Every query result also reports the executing `readerVersion`; a recovered or
otherwise derived package records that reader version in its derivation
provenance rather than rewriting the source package.

Readers accept only explicitly supported contract major versions and
representation versions. Unknown required fields, unsupported versions, hash
mismatch, or a representation kind outside the allowlist fail explicitly.
Readers may ignore documented optional fields only within a compatible version.
There is no in-place migration of sealed evidence.

### 2.1 Internal DC2 fixture layout

To keep DC2 implementable without selecting a production backend, its internal
fixture may use exactly three files beneath one relative package directory:

- `manifest.json`: IDs, versions, target provenance, representation descriptor,
  supported views, retention deadline, quality reference, and byte-budget
  accounting;
- `representations/<artifactId>.json`: the allowlisted
  `ContentionSnapshot` fields only;
- `seal.json`: final publication record containing the `CaptureId` and
  cryptographic hashes/lengths of the other canonical files.

Property names above are internal test vocabulary. No generic serializer,
archive format, SQLite schema, or public “C format” is approved. A directory
without a valid `seal.json` is interrupted. The catalog locator lives outside
canonical identity and may change when the directory moves.

## 3. Target provenance

PID is recorded because the DTO contains it, but PID reuse makes it inadequate
as identity. Canonical metadata also records every value available without a
new attach at persistence time: capture start, runtime flavor/version,
operating system, process architecture, managed entrypoint assembly, command
line or a redacted/hash form chosen by host policy, and host/container binding
metadata when already known. Missing values are `unknown`, not inferred.

The process start instant or runtime diagnostic identity should be recorded when
already available; DC2 must not launch or reattach merely to obtain it. Reopen
never searches for a matching PID and never enriches evidence from a current
process. Provenance is descriptive, not an attachment capability.

## 4. Lifecycle, publication, and recovery

The logical states are:

`creating -> writing -> finalizing -> sealed`

with terminal or recoverable side states `interrupted`, `invalid`, `deleted`,
and explicit `recovering`. `deleted` is a catalog/tombstone state because
canonical sealed bytes cannot be updated after deletion. Evidence completeness
is separate from package state.
A sealed package may retain collector loss; an interrupted package is not a
complete capture.

For the DC2 artifact spike, `writing` copies an already completed in-memory
artifact. It does not launch a collector. `finalizing` writes canonical
metadata, the allowlisted representation, minimal indexes if the chosen
prototype needs them, byte lengths and hashes. The seal/publication record is
written last. Sealed canonical files are immutable.

Normal reopen:

1. resolves a catalog entry below the authorized local root;
2. acquires a reader lease;
3. requires a valid seal;
4. validates IDs, versions, lengths, hashes, and required files;
5. opens read-only and creates a temporary handle.

It must not repair, checkpoint, add indexes, append notes, migrate, or reseal.
An unsealed package is `RecoveryRequired`, not something a normal query scans.

Recovery is explicit and exclusive after writer and reader leases are absent.
It validates only committed/complete records the selected representation can
prove, preserves unknown tails as unknown, and publishes a **new** derived
capture with a new `CaptureId`, new `ArtifactId`, and
`derivedFromCaptureId`/`recoveryReason`. Original bytes remain unchanged.
Recovery cannot claim exact loss for volatile queue/batch work that was never
durably accounted.

Database transactions do not atomically publish an external manifest or sibling
files. Even though the first artifact needs no native companion, the contract
requires a publication order and a final integrity record covering all package
members. Rename is an availability mechanism, not by itself a power-loss
guarantee. The implementation declares separately:

- process-crash recovery behavior;
- system-crash/power-loss behavior for the chosen sync/filesystem policy;
- which manifest/data combinations are invalid or explicitly recoverable.

DC1 approves none of SQLite, WAL, rollback journal, JSON, directory rename, or
a sync mode as the production answer.

## 5. Budgets and observable accounting

Numeric values are intentionally absent; DC3 freezes comparison budgets before
measurement. Implementations must nevertheless meter and cap these categories:

- estimated record bytes reserved before an expensive ownership copy;
- actual owned-copy bytes, with reservation reconciliation;
- admission record count and bytes per stream and globally;
- queued bytes/count;
- active writer batch actual bytes/count, still charged after dequeue until
  commit or terminal disposal;
- backend canonical data, database journal/WAL or record segments;
- pre-seal indexes and manifest/integrity metadata;
- native companions, when a later allowlist permits them;
- finalization/recovery conversion and temporary space;
- per-package final bytes and artifact count;
- concurrent active captures and aggregate host working set/disk;
- historical package count and bytes;
- reader leases, reader buffers, and derived-cache bytes;
- deletion tombstones/catalog metadata where retained.

Stages report only knowable facts: offered/observed where available, admitted,
rejected, copied, queued, active-batch, committed, finalized, projected, and
deleted. One artifact or logical record need not equal one database row.
Accepted memory is not committed storage. Unknown process-crash tails remain
unknown. The DC2 copy-from-memory spike exercises package/finalization budgets;
it does not validate callback admission or claim DC3 ingestion performance.

## 6. Local roots, leases, retention, and authorization

Core accepts an operator-selected root and relative locators. Resolution must
apply the existing `SafeArtifactPath` principles: canonicalize below the root
and reject traversal, absolute escape, and symlink escape. Catalog enumeration
must not expose unrestricted absolute paths to remote callers.

One exclusive writer/recovery/deletion lease or multiple read leases may exist
for a package; never both. A timed-out synchronous writer retains its lease
until it actually terminates. Cleanup skips active leases and retries later.
Deletion removes the whole logical package, not one canonical member that would
leave a misleading seal.

Retention is separate from handle TTL. Policy must cap historical package
count and aggregate bytes as well as age. Expiry makes evidence unavailable; it
does not recollect. Cleanup order and user pinning, if any, remain policy
decisions, but every deletion reason is observable. The current generic 24-hour
file reaper is precedent, not an approved durable-package TTL.

Core owns IDs, manifest/reader contracts, leases, lifecycle, compatibility, and
typed adapters. Hosts own roots, user/tenant boundaries, authorization,
retention policy, audit, and presentation:

- MCP reevaluates the current principal. This contention artifact requires the
  current `eventpipe` read boundary; recorded capture scopes are provenance only.
  Delete retains the explicit `delete-artifact` boundary. Recovery needs a
  separately approved explicit mutation scope.
- CLI relies on the local operating-system identity and explicit root/path
  selection; it must still protect against path escape and accidental deletion.
- BenchmarkDotNet may use Core with a caller-selected result directory, but is
  outside the first public adapter.

## 7. Proposed public placement (DC6 approval required)

These names are proposals, not existing APIs and not approved for
implementation:

| Operation | Proposed placement |
| --- | --- |
| List captures | New read-only MCP Resource `diagnostic-captures://catalog`; CLI `captures list` |
| Inspect manifest/capabilities | Resource `diagnostic-captures://capture/{captureId}`; CLI `captures show --capture-id <id>` |
| Reopen/select/query | Extend existing `query_snapshot` with optional `captureId` and `artifactId`, mutually exclusive with `handle`; it authorizes, mints a temporary handle, and runs the existing typed view. CLI extends existing one-shot `query` with `--capture-id` and `--artifact-id` |
| Delete | Extend existing lifecycle tool `get_bytes` with proposed discriminator `kind="capture-delete"` and `captureId`; CLI `captures delete --capture-id <id>` |
| Explicit recovery | Extend `get_bytes` with proposed discriminator `kind="capture-recover"` and `captureId`, requiring an explicit recovery scope and returning the new IDs; CLI `captures recover --capture-id <id>` |

This keeps the 17-tool count unchanged and keeps ordinary evidence queries in
`query_snapshot`. Resources are catalog/metadata views, not raw evidence dumps.
`get_bytes` is proposed for destructive lifecycle placement because it already
owns artifact list/delete behavior; whether recovery belongs there is an open
DC6 review point. The first internal spike exposes none of these surfaces.

## 8. DC2 fixtures and acceptance matrix

All fixtures are constructed DTOs; no process, collector, database engine, or
network is required.

| Fixture/case | Required result |
| --- | --- |
| Representative artifact | Round-trip every retained DTO field, nullable owner, IDs, timestamps, `TimeSpan`, method/module strings, and notes exactly |
| Empty artifact | Preserve zero events and the original no-observation note; do not conclude healthy |
| Unknown lock IDs | Preserve events with `LockId == 0` and the original distinct count even without an overflow note; do not infer complete monitor coverage |
| Cap/approximation artifact | Preserve 200 retained events and notes describing longest-event retention, monitor lower bound, and approximate percentiles |
| Three query views | Original and reopened artifacts produce equivalent `summary`, `byCallSite`, and `byOwner` results for several `topN` values |
| Unsupported view/kind | Typed explicit failure; no reflection fallback or live access |
| Identity | Moving the package does not change IDs; changing manifest ID without matching integrity fails; reopening creates a different temporary handle |
| Version matrix | Supported exact version opens; unsupported contract/representation version fails without mutation |
| Integrity | Missing member, length/hash mismatch, or missing seal fails closed |
| Interrupted package | Normal reopen returns `RecoveryRequired`; no query or automatic mutation |
| Explicit recovery fixture | Exclusive recovery creates new IDs with derivation provenance and unknown-tail quality; source bytes/mtime remain unchanged |
| Immutability | Successful reopen/query leaves canonical bytes and modification times unchanged |
| Root security | Traversal, absolute escape, and symlink escape are rejected |
| Leases/cleanup | Cleanup skips writer/read leases; delete is whole-package and observable after release |
| Authorization adapter seam | Core requires a host authorization decision but contains no bearer-token dependency |
| Budget accounting | Package, manifest, representation, temporary, lease, and historical counters reconcile without assuming records equal rows |

DC2 succeeds only if the artifact can be reopened and queried with retained
semantics, no invented detail, no current-process dependency, and no privilege
reuse. Its status remains an internal contract proof, not a release promise.

## 9. Shared inputs required by DC3

DC3 must consume, rather than redefine:

- the identity/version/manifest and immutable-seal rules above;
- the same target provenance and typed availability vocabulary;
- a separately specified timestamped-counter logical record with source
  timestamp, clock domain, counter kind, unit, interval, display-rate scale,
  missing/reset boundary, and ordering semantics;
- one frozen input sequence supplied identically to A and B;
- frozen count/byte budgets covering copy, queue, active batch, backend,
  journal/segments, indexes, temporary conversion, final package, history, and
  concurrent captures;
- one declared durability profile per comparison cell, separating process crash
  from power loss;
- the same pre-seal minimal indexes, supported queries, redaction, failure
  injection points, and correctness oracle;
- measured target/host effect, stage loss, disk amplification,
  finalization/reopen/query latency, recovery, and total context cost.

DC3 must not compare this aggregate contention continuity artifact with a
timestamped counter stream as though they had equal fidelity. A remains the
first candidate and B a fair comparator; neither engine is approved by DC1.

## 10. Open risks and gate

- The first public delivery—continuity or counter series—remains a maintainer
  product decision.
- Exact schema/layout, journal/sync mode, package directory/archive form,
  recovery publication identity, retention defaults, numeric budgets, and
  shutdown escalation remain open.
- `ContentionSnapshot.Notes` are textual legacy quality. A production adapter
  may need a separately reviewed structured-quality evolution; DC2 must not
  infer it by parsing messages.
- Method/module names and object/lock identifiers may be sensitive operational
  data even though this artifact is less sensitive than dumps or parameters.
  Host policy and retention review remain required.
- Existing generic artifact lifecycle is file-oriented and not lease-aware.
  Reusing it for packages requires package-atomic deletion and lease semantics,
  not recursive file deletion by accident.

**DC1's internal contract gate is accepted following independent review and
resolution of its condition.** DC2/#1002 and DC3/#1008 may proceed under the
maintainer-authorized orchestration. Their own acceptance gates remain in force;
production implementation and the explicit DC6 go/no-go are separate decisions.
