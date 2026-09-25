# Core SQLite capture storage contract — current v2, previous v1

This is the local storage foundation for the approved durable-capture design,
not the completed collector/CLI/MCP feature. The implementation is in
`DotnetDiagnostics.Core.Captures`. It does not attach to processes, register
handles, modify existing collectors, enable persistence by default, or use the
TestSupport experiments. Creating a `SqliteCaptureStore` and listing a
nonexistent store create no files.

## Public API and ownership

```csharp
var store = new SqliteCaptureStore(artifactRootProvider, new CaptureStoreOptions());
var access = new CaptureAccess(ownerId); // Identity resolved by the trusted host.
await using var writer = await store.CreateAsync(new("capture name", groupId), access);
string artifactId = writer.AddArtifact("exceptions", "Exception occurrences");
bool admitted = writer.TryAppend(artifactId, new CaptureRecord(
    Timestamp: timestamp,
    ThreadId: threadId,
    Category: "exception",
    Name: exceptionType,
    Fields: [new("message", CaptureFieldKind.Text, StringValue: message)]));
writer.SetSourceRejected(sourceLossCount); // null means unavailable, NOT zero.
CaptureInfo capture = await writer.CompleteAsync(cancellationToken);

using var reader = await store.OpenAsync(capture.CaptureId, access);
CaptureRecordPage page = reader.Query(new(artifactId, PageSize: 100));
```

`CreateAsync`, `OpenAsync`, `ListAsync`, `DeleteAsync`, and `RecoverAsync` all
take the current `CaptureAccess`. `AllOwners=true` is an explicit privilege
supplied by trusted host code; no stored permission field grants privilege.
The MCP host supplies `BearerPrincipal.OwnershipKey`, not a display name, and
maps explicit root/`*` authority to the bypass flag. CLI callers use a stable
local owner key under local OS authority. Per-kind/view MCP scopes remain
independent host checks, not stored package permissions.
Normal callers see only their owner in catalog results and cannot open,
delete, or recover another owner's package. Recovery creates a package owned
by the **current** caller, including an explicitly privileged caller.

Capture and artifact IDs are separate, lowercase, 32-character GUID-derived
opaque strings. All public ID-based filesystem operations validate the ID
before resolving paths. Artifact handles, reopened ephemeral handles, bearer
scopes, and session context are outside this API.

`AddArtifact(kind, name)` remains unchanged. Before completion, a producer may
call `SetArtifactProvenance(artifactId, CaptureArtifactProvenance)` with facts
it **already has**: nullable `ProcessId`, `ProducingTool`,
`OriginalHandleOrigin`, capture `StartedAt`/`Duration`, `ProcessStartUtc`,
`RuntimeName`, and `RuntimeVersion`. The setter can replace an earlier value
while recording (for example, adding the final duration); sealed packages
cannot be updated. It is a bounded metadata/control operation, not a callback
admission API. It does no process lookup, attach, or enrichment. Missing facts
remain null rather than invented defaults.

Provenance is exposed on `CaptureArtifactInfo.Provenance` and stored in the
v2 manifest, not encoded into the occurrence records or duplicated as snapshot
JSON. Its strings have the existing 1 KiB UTF-8 metadata bound; the complete
manifest still has a 128 KiB bound. Supplied process IDs must be positive,
durations nonnegative, and timestamps canonicalize to UTC without changing
their instants. Producer tool/origin strings are descriptive context only:
even values such as `root` or `*` never grant ownership or MCP scopes. The
current caller and host policy remain authoritative.

The artifact root is trusted local storage, not an arbitrary package-import
boundary. SHA-256 seals detect accidental mutation; they are not signatures
or owner authentication against an attacker who can rewrite the private root.
Use consistent operator admission settings for every host sharing a root.
The root provider must supply a private directory; on Windows, restrictive
parent ACLs remain the operator's responsibility.

## Files, leases, and publication

Each logical capture has one package at:

```text
<IArtifactRootProvider.Root>/captures/<CaptureId>/
  .lease
  manifest.json
  capture.sqlite
  seal.json                 # only after successful completion
```

Unsealed packages can additionally contain SQLite `-wal`/`-shm` files and
unpublished `manifest.json.pending`/`seal.json.pending` files. Unknown members,
nested directories, symlinks, and reparse-point ancestors/members are rejected.
`SafeArtifactPath` capture-specific resolution and `CreateRestrictedFile` are
reused. Created capture directories are Unix `0700`; explicitly created files
are `0600`. SQLite inherits auxiliary-file permissions from its private main
file. The host must not allow untrusted local actors to race directory-entry
replacement; portable path checks are not an `openat`-based hostile-filesystem
defense.

The reserved `captures/` root also contains a `.capture-store` marker,
`.admission`, and up to two `.writer-N` slot files. These are control files,
**not a central database or growing session log**. There is no required
`.nettrace`, external native artifact, or hidden raw-file dependency. Native
dependencies are not supported by this initial foundation API.
The standard SQLite engine is an explicit transitive runtime dependency of
the centrally pinned `Microsoft.Data.Sqlite` package; it is not a collector
native dependency. Failure to load that engine fails storage initialization
instead of silently succeeding in memory.
The private marker contains exactly the UTF-8 bytes
`dotnet-diagnostics-captures/1`, without a newline. Creation writes and flushes
the marker before any package/admission/writer-slot files are created;
concurrent initializers must acquire its shared read lease and verify its
bounded contents before proceeding. Normal reads never create or repair the
marker. Reads reject a missing marker in an existing store; incompatible
contents reject both reads and creation. Generic artifact tools use the marker
to protect re-rooted paths.

Package readers hold shared `FileStream` leases on the existing `.lease`;
writers, recovery, and deletion require an exclusive lease. Admission is
serialized separately across processes. Operations fail with `Busy` rather
than waiting indefinitely for a lease. Multiple readers coexist; a reader
does not consume a writer slot. Filesystems must implement the platform's
sharing/locking semantics; network/object filesystems are not supported by
this contract.

Completion drains admitted offers, persists snapshots/empty artifacts, closes
the writer connection, checkpoints WAL, validates SQLite, publishes the final
manifest, and publishes a seal containing the SHA-256 hashes of the manifest
and main database. Fixed metadata DTOs use source-generated JSON, not arbitrary
reflection serialization. Metadata files are written privately, flushed, and renamed.
The seal is the publication boundary. The implementation does **not** fsync
directory entries or claim tested power-loss durability.

Normal open acquires a shared lease, checks owner, validates manifest versions
and required features, requires a sealed state and seal, rejects WAL/SHM or
pending metadata in a sealed package, and verifies hashes **before opening
SQLite** with `mode=readonly`, `immutable=1`, and pooling disabled. It then
validates database versions, required schema/indexes, and `quick_check`.
It performs no repair, index creation, checkpoint, migration, or WAL creation.
Reads can change filesystem access times, but not package bytes or member sets.

Deletion creates a durable root tombstone `.deleted-<CaptureId>` while holding
the exclusive package lease and store admission lease. Catalog and open stop
exposing the package before any member is removed; open/recovery recheck the
tombstone after acquiring their lease. Only complete physical removal returns
success. Failure leaves the tombstone and remaining evidence, reports an
error, and requires operator cleanup; it is not reported as successful
deletion. Remaining tombstoned directories still consume admission capacity.
Raw-file retention must exclude **the entire captures subtree**, not just
`capture.sqlite`. Capture retention is a separate authorized lifecycle.

## Independent format axes and v1/v2 compatibility

The SQLite `format` row and manifest independently record package, schema,
record, index, writer, and required-reader versions. Their descriptors must
agree; conflicting but individually supported descriptors are corrupt, not
silently interpreted using whichever version happens to work.

| Axis | Frozen previous package | Current package |
|---|---:|---:|
| Package / canonical artifact metadata | 1 | 2 |
| SQLite schema | 1 | 1 |
| Occurrence record representation | 1 | 1 |
| Required indexes | 1 | 1 |
| Producing writer generation | 1 | 2 |
| Minimum required reader generation | 1 | 2 |

v1 requires exactly `normalized-scalars-v1`. v2 requires that feature plus
`artifact-provenance-v1`; derived packages carrying recovery aliases additionally
require `recovery-artifact-identity-v1`. The provenance feature is optional as data, but
understanding its v2 metadata representation is required of the reader.
Unknown/missing features, unsupported future versions, undeclared v2
provenance in a v1 manifest, and other version combinations fail closed.
The normalized SQL tables/indexes did not change; provenance lives in the
bounded canonical manifest, so schema, record, and index axes correctly stay
at 1. The root marker's `/1` protocol and the snapshot codec's independent
representation version 1 are also unchanged.

`CaptureReader.Format` returns the source descriptor as
`CaptureFormatVersions`, including `RequiredReaderVersion`.
`CaptureReader.ExecutingReader` separately reports
`CaptureReaderIdentity("DotnetDiagnostics.Core.Captures.CaptureReader", 2)`.
Thus opening a v1 package reports source required-reader 1 and actual executing
reader 2; stored metadata is never relabeled as the executing implementation.
There is no in-place sealed migration, reindex, checkpoint, or evidence rewrite
when opening either supported version.

The genuine previous-format fixtures were generated by committed v1
implementation `59e34e40681720334db3812f5746e463592449d0` and frozen in distinct
commit `e9cfca694cac0587016fb75454205cf3a15242e5` **before v2 source changes**.
`tests/DotnetDiagnostics.Core.Tests/Fixtures/DurableCaptureV1/` retains small
sealed/interrupted package archives, archive/member SHA-256 values, generator
source, SDK identity, and producing Core assembly hash. The v1 producer
actually opened and queried its sealed fixture during generation. Tests use
those unchanged bytes with the executing v2 reader; they do not regenerate
v1 under v2 or pretend a current reader is an independently frozen previous
reader. v1 artifacts expose `Provenance=null`.

Only these current/previous frozen formats are supported. Pre-freeze
experimental databases are not imports. Further evolution requires an
explicit compatibility decision and independently frozen evidence.

## Frozen normalized schema

Tables:

| Table | Contents |
|---|---|
| `artifacts` | Artifact ID, kind, name; multiple artifacts per package |
| `occurrences` | Monotonic record ID, artifact ID, nullable UTC timestamp ticks, thread ID, category/name string IDs, numeric value, duration nanoseconds, unit string ID |
| `fields` | Record ID and field ordinal; name ID, scalar-kind tag, nullable text/integer/floating/boolean slots, unit ID |
| `strings` | Exact durable text and unique numeric ID |
| `snapshots` | Artifact ID, producer-supplied positive snapshot version, bounded UTF-8 JSON BLOB |

Occurrence indexes cover `(artifact,id)`, `(artifact,time,id)`,
`(artifact,thread,id)`, `(artifact,category,id)`, and `(artifact,name,id)`.
Fields also have a name/record index. Inserts and string lookup use prepared
parameterized statements; the public API exposes no arbitrary SQL or import.
The bounded FIFO string cache evicts **only cached lookup entries**; durable
strings are never deleted during cache eviction.

Scalar tags are `Null`, `Text`, `SignedInteger`, `FloatingPoint`, and `Boolean`.
Exactly the designated value slot is populated. Signed 64-bit fields preserve
full integer precision; the convenience `NumericValue` dimension is explicitly
a double, not a lossless integer slot. Missing dimensions remain SQL NULL;
explicit null fields remain distinct from an absent field. Units, exact text
(including embedded NUL and Unicode), field order, and signed values are
preserved. Timestamps normalize to UTC ticks; original timezone offsets are
not retained. NaN/infinity, invalid Unicode, negative durations, mixed scalar
slots, and oversized records are rejected, not truncated or coerced to zero.
Producer-side redaction must happen before admission.

Snapshots are optional compatibility material, never the sole representation
of recorded occurrences. `SetSnapshot` accepts explicit UTF-8 JSON, not an
arbitrary object or reflection serializer, and stores at most one snapshot
per artifact. Snapshot versions belong to the producer/view contract; the
store does not reinterpret their JSON.
`CaptureSnapshot.Version` is the encoding representation version, independent
of the package/schema versions. `CaptureSnapshot.Kind` is returned from the
owning immutable `artifacts.kind` row, linked by `snapshots.artifact_id`; it is
never guessed from CLR reflection or JSON contents. Store readers always
populate it. A compatibility codec can therefore decode with
`Decode(snapshot.Kind, snapshot.Version, snapshot.Utf8Json, maxBytes)`.
The optional constructor default for `Kind` only preserves source compatibility
for callers constructing their own snapshot DTOs; it does not omit durable
kind metadata. Kind-specific encoding, decoding, supported views, and the
known-type allowlist belong to the separate codec implementation.

`Query` uses artifact-scoped filters, inclusive timestamp bounds, and keyset
pagination (`AfterRecordId`, `NextAfterRecordId`) ordered by record ID.
Each call has finite page and field/string bounds. `ReadSnapshot` checks
length before materializing its BLOB. No API returns an unbounded full-capture
record list.
Record pages also enforce `MaxQueryPageBytes` (default 1 MiB, configurable from
1 KiB through 16 MiB), independently of the row cap. The reader fully
materializes and validates one candidate at a time instead of retaining all
1,000 possible rows before applying the byte limit. It measures each candidate
with the fixed source-generated compact UTF-8 representation, including JSON
escaping, plus a comma and a conservative 128-byte page-envelope reservation.
`CaptureRecordPage.AccountedBytes` exposes this accounting. Hosts adding their
own envelopes, indentation, converters, or alternative representations must
budget that additional wire encoding separately.

When the byte budget stops a page, `NextAfterRecordId` identifies the last
returned record; the omitted candidate is therefore returned by a subsequent
page rather than lost. A single record too large for the configured page
budget fails with `CapacityExceeded`, not an empty page/unchanged continuation
loop. Memory also includes one bounded lookahead record and its serialized
measurement, but not an unbounded retained result list.

## Producer admission, writer, and quality

`TryAppend` performs bounded validation, counts **all** record/field strings,
copies the bounded field vector, and attempts queue admission. It does no
SQL, filesystem work, or asynchronous waiting. A contended admission gate or
full queue returns `false` and increments queue rejection. Queue references
contain only immutable scalar records/strings and the copied field vector,
never TraceEvent objects or arbitrary object graphs.

The queue reservation includes the in-flight transaction batch until its
commit/failure, not merely channel occupancy. The dedicated writer task fills
immediately available work up to the batch limit **before** checking oldest
offer age. Its fill delay is at most the configured age, but thread scheduling,
SQL contention, and storage latency do not have a promised wall-clock bound.
There is one writer task and at most one cached shutdown task per capture.

There are **two independent logical byte budgets**. `QueueBytes` is the live
queued/in-flight reservation and is released when that work commits or fails.
`MaxLogicalBytes` bounds the **cumulative capture-lifetime admitted record and
snapshot payload**, including already committed retained evidence. A successful
commit therefore never decrements `LogicalBytes`: it changes where evidence
resides, not how much evidence the capture has admitted. Neither counter is a
measurement of live managed RAM. A failed capture may conservatively retain
admission charges for rolled-back offers; that never grants new capacity or
turns failure into success. An offer that never enters the queue is not charged.

SQLite runs in WAL mode with `synchronous=FULL`, foreign keys enabled,
`trusted_schema=OFF`, and bounded auto-checkpointing. The writer sets and reads
back `max_page_count` on its own connection; the limit is not assumed to
persist across connections. Main-file capacity and transaction errors fail
completion: there is no fallback-to-memory success.

Quality populations obey, after quiescence:

```text
Offered = Persisted + RecordRejected + QueueRejected + StorageRejected + Pending
```

`Accepted` is cumulative successful queue admission, not another disjoint
population. Storage rejection includes logical-cap admission refusals and
admitted records lost to failed/rolled-back transactions. Committed records
remain persisted even if a later package monitor or snapshot write fails.
`SnapshotRejected` is separate and does not enter the occurrence equation.
Source loss is a separate nullable count supplied by the producer; unavailable
source loss is never invented as zero. In-flight metric reads are observational
and need not be an atomic multi-counter snapshot.
Invalid/unpaired UTF-16 in any occurrence string and NaN/positive or negative
infinity in either the numeric dimension or a floating-point field return
`false` from `TryAppend` and increment `RecordRejected`. They are rejected
before SQLite conversion/insertion, not replaced with U+FFFD, SQL NULL, zero,
or a truncated value. Consequently they make the sealed quality incomplete;
genuinely missing/null values remain independently representable.

`IsComplete` requires known zero source loss, zero losses/pending, and no
interruption/unknown tail. Sealing does **not** imply completeness: a sealed
capture can be incomplete, or source completeness can remain unknown.
Disposal without completion drains already admitted evidence but marks the
package interrupted. Observed cancellation and write failures never report
successful completion. Disposing after a failed completion can rethrow that
same cached failure; hosts must handle it without replacing their primary
diagnostic error. Catalog listing conservatively labels unsealed packages
interrupted/unknown, rather than inferring live-process or writer liveness.

`CaptureWriterMetrics` includes quality, logical reservation, current queue
reservation/count, transactions, largest batch, observed package bytes, and
the actual writer journal/synchronous/page-limit pragmas.

## Provisional release configuration

These are configurable, finite implementation defaults, **not measured
universal safety limits or performance recommendations**:

| Bound | Default |
|---|---:|
| Queue records, including in-flight batch | 8,192 |
| Owned logical queue reservation | 16 MiB |
| Accounted bytes per record | 64 KiB |
| Scalar fields per record | 64 |
| Records per transaction | 256 |
| Batch fill age | 50 ms |
| Capture record/snapshot logical reservation | 128 MiB |
| SQLite main file (`4096 * max_page_count`) | 256 MiB |
| Monitored package bytes | 512 MiB |
| Store admission budget | 4 GiB |
| Captures / active writers per root | 256 / 2 |
| Snapshot bytes, **shared across the whole capture** | 8 MiB |
| Artifacts per capture | 64 |
| String-cache entries / accounted bytes | 1,024 / 1 MiB |
| Record page / catalog page | 1,000 / 100 maximum |
| Record-page compact UTF-8 byte accounting | 1 MiB |
| Serialized manifest/seal file | 128 KiB each |
| Capture/owner/artifact metadata text | 1 KiB UTF-8 per value |

Record reservation is deliberately conservative: 128 fixed bytes, 64 per
field, and for every supplied string 24 bytes plus UTF-16 character bytes plus
UTF-8 bytes. It is an admission accounting model, **not total managed heap or
physical disk usage**. Artifact/control metadata has separate finite count and
byte bounds. Snapshot JSON parsing allows depth at most 64.

Store admission sums actual sealed bytes and reserves the **persisted package
reservation** for unsealed captures (or a conservative maximum if metadata
is missing). Recovery scratch is also counted. Small captures can reach the
count bound; many interrupted captures can reach the reserved byte bound much
earlier. Tiny custom limits may be too small even for the schema and fail with
an actionable capacity error.

`max_page_count` bounds only the main database. WAL, SHM, metadata, recovery
copies, filesystem metadata, concurrent timing, and open-file accounting are
not made instantaneously bounded by that pragma. Package bytes are sampled
after committed batches/final snapshot writes and a threshold violation fails
the capture; overshoot can already have occurred. Admission is not a hard
filesystem quota, nor does it control external actors writing the root.

## Explicit derived recovery

`RecoverAsync` accepts only an unsealed package and requires its owner's
authority plus an exclusive source lease. It validates and hashes the source
member set, checks finite copy/admission budgets, and copies only the main
database and WAL to a private `.recovery-<id>` directory. SQLite recovery and
reads run on that copy, never the source. Source SHM is hashed but not reused.

Only committed evidence visible to SQLite on the copy is replayed through
bounded scalar pages into a **new** capture and **new** artifact IDs. Recovery
waits for destination queue capacity instead of counting retries as losses.
Snapshots are copied with the same explicit bounds. The derived manifest
records `DerivedFrom`, the source member hashes, and `UnknownTail=true`.
Recovery always produces the current v2 descriptor and preserves available v2
artifact provenance. Recovery of a genuine interrupted v1 fixture produces
new capture/artifact IDs with null provenance; it never enriches or rewrites
the source to fill missing facts.

`CaptureArtifactInfo` appends optional `SourceArtifactId=null`. Only recovery
sets this value, using `source.SourceArtifactId ?? source.ArtifactId`; ordinary
`AddArtifact` leaves it null and there is no public writer setter. It preserves
the **original exact reference key**, not necessarily the immediately preceding
package's artifact ID. Repeated recovery retains this one fixed-size key per
artifact instead of accumulating an unbounded chain of aliases.

This supports byte-identical compatibility group snapshots that explicitly
reference child artifact IDs. Group resolution must match an exact current
`ArtifactId` or `SourceArtifactId` in the recovered package, and must refuse
missing/ambiguous references rather than guessing names, kinds, or process IDs
or reading the source package. Every alias is a validated opaque GUID and is
unique across **both** current IDs and aliases in the whole manifest (including
self-collisions). The existing artifact-count and manifest-byte caps still
apply. Provenance remains independent and is copied unchanged.

Derived packages with aliases declare required feature
`recovery-artifact-identity-v1`. An older v2 reader that does not understand it
rejects that package rather than claiming valid group composition. Updated
readers still accept v2 packages without the key/feature and expose null; they
do not fabricate recovery mapping. An alias without its feature, feature with
no aliases, or feature without derived-capture metadata is rejected. The
unreleased current-v2/genuine-previous-v1 window and all version axes remain
unchanged; snapshot-body representation versions remain producer-controlled.
The two-round test prepares an unsealed intermediate derived source by removing
its seal, then checks source member hashes around each recovery and original
child keys across unchanged group-body bytes. This is a deterministic missing
publication fixture, not a claimed power-loss simulation.
It never claims that a volatile producer queue or uncommitted WAL tail was
recovered, even if the source had a clean disposal. Recovery does not reattach
to a process. Cancellation/failure can leave an interrupted derived capture;
the source remains unchanged. Ordinary cleanup removes scratch; abrupt process
death can leave reserved scratch for operator cleanup.

## Validation scope and integration obligations

Deterministic `DurableCaptureStoreTests*` cover scalar/string precision,
multiple artifacts, indexed filters and keyset pages, cache eviction, fresh
store reopen, byte-identical readonly reads, pragmas, owner isolation, invalid
paths/links, unsupported/corrupt/missing evidence, queue/record/snapshot/store
bounds, real SQLite-full rollback accounting, cancellation, batch fill,
concurrent shutdown, leases, and derived WAL recovery with unchanged source
bytes. The cross-process lease test exercises Linux `flock` and has a Windows
FileStream/PowerShell path; platform results must be reported separately.
The retained WAL fixture tests committed-evidence recovery, **not power failure
or crash-consistency guarantees**.

Remaining integration work: explicit enablement/configuration, faithful
collector adapters and source-loss metadata, recording context, query/handle
routing, host identity/privilege mapping, CLI/MCP workflows, capture-specific
retention policy, and end-to-end release acceptance. This foundation alone
does not establish their completeness, throughput, or operational safety.
