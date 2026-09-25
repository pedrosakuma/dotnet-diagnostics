# Portable capture contract — PK1, archive version 1

**Status: proposed implementation contract for #1048, under #1047 and #999.**
This document specifies downstream work, not implemented portability, measured
capacity, successful security validation, or release approval. Normative
“must” statements below are acceptance requirements. No production code changes
accompany this contract. Frozen experimental protocols, results, and previous
format fixtures remain unchanged.

## 1. Foundation and scope

The implementation baseline is integration commit `91b17b7`. Its
[`durable-sqlite-storage-contract.md`](durable-sqlite-storage-contract.md) and
[`durable-capture-host-contract.md`](durable-capture-host-contract.md) describe
implemented local storage and host behavior:

- `CapturePackage` writes package/writer/required-reader **2**, schema/record/index
  **1**, and reads the frozen package **1**. `CaptureReader.ExecutingReader`
  identifies executing reader 2 separately from the source descriptor.
- `CaptureInfo` contains ownership, quality, artifacts, `DerivedFrom` and
  `SourceHashes`. `CaptureArtifactInfo.SourceArtifactId` is a recovery alias;
  it is not a complete portable-origin or arbitrary multi-hop identity map.
- `CaptureAccess` comes from the trusted host. The MCP host uses
  `BearerPrincipal.OwnershipKey`; CLI uses local OS authority. Archived owner,
  producer, origin, PID, scopes, and paths cannot grant current privileges.
- The ordinary SQLite reader is for a private, trusted store. Its version,
  required-index, shape and `quick_check` checks are **not** untrusted-import
  admission. The stronger boundary in section 7 is new work.
- Seals hash the manifest and database. They are not signatures. Ordinary
  reads never recover, rewrite, checkpoint, or migrate sealed evidence.

Portability transports **whole independent sealed captures**, each with its own
database, artifacts, quality and seal. It does not introduce a shared mutable
multi-capture database, arbitrary SQL, a central history service, live collection
changes, native file transport, or a new MCP tool. CLI continues to reference
Core only. Package ownership and per-kind/view authorization remain independent.

Trusted export and untrusted import have different execution boundaries. Export
selects capture IDs from the host's existing trusted private store; it does not
accept arbitrary source paths, archives or externally supplied raw SQLite files.
A matching seal/hash does not establish that trust. Copying an external database
into a store directory is not an admission mechanism: it must pass isolated
import first. Import alone requires the isolated worker described in section 7.

An interrupted source must undergo explicit existing recovery first. A recovered
sealed capture can be exported even if its retained evidence has incomplete or
unknown quality. “Sealed” means immutable publication, not complete observation.

## 2. Identity and naming

All generated IDs use independent cryptographically random GUIDs, lowercase
32-character `N` format, collision-checked in their relevant namespace.

| Identity | Meaning and lifetime |
|---|---|
| Source capture/storage ID | Assigned at collection start, permanent within its source store; never derived from a name |
| Artifact ID | Assigned within that capture, not an ephemeral drilldown handle |
| Bundle ID | New for each export operation; retries of that operation keep it |
| Entry ID | Unique within one bundle, even for repeated source IDs or repeated identical captures |
| Operation ID | Destination-local durable import/export retry receipt; not an evidence ID |
| Transfer ID | Expiring host/session byte-transfer capability reference, separate from operation/bundle IDs |
| Destination capture/artifact IDs | Fresh on import into the selected destination store and owner |

Each source can have an optional friendly name supplied before sealing.
Foundation `CaptureCreateRequest.Name` is currently required; making the host
name optional must choose an ordinary display fallback, not change its random ID.
There is no post-seal rename/annotation service in this version.

An export filename and each entry's nullable `label` can be chosen at export.
They do not modify source bytes, name, ID or quality. Labels may duplicate, contain
slashes, or resemble IDs; they are always JSON display text, never paths,
selectors, SQL, authorization keys, or a reason to merge evidence. Limit labels
to 256 UTF-8 bytes of valid Unicode; reject control characters, including NUL,
and render with ordinary JSON/terminal escaping. Do not normalize or deduplicate
labels. Duplicate **entry IDs**, in contrast, are invalid.

A repeated source ID is interpreted only in the context of its entry. The same
capture may appear twice in a bundle, or independent stores may have colliding
IDs. Import maps each entry independently. Source equality is not deduplication;
no package is overwritten, reused or authorized by its archived ID or hash.
No persistent bundle catalog is required after import.

## 3. ZIP layout and integrity

Version 1 deliberately uses **ZIP method 0 (Stored), not Deflate**. This is a ZIP
transport, not a compression requirement. Stored-only simplifies streaming,
work accounting and interoperability; compression is a later version decision.

```text
bundle.json
bundle.seal.json
entries/<entryId>/manifest.json
entries/<entryId>/capture.sqlite
entries/<entryId>/seal.json
... exactly three members per indexed entry
```

There are no directory entries or extra members. In particular `.lease`, the
store marker, admission/writer slots, tombstones, WAL/SHM, pending metadata,
symlinks, native binaries, `.nettrace`, dumps and nested ZIPs are not allowed.
Snapshot strings naming native files remain inert provenance; they neither
authorize reading that path nor make native-dependent offline views available.
Export must reject, rather than silently omit, any required unsupported evidence
member. Successful export includes all artifacts and rows, not a filtered slice.

### 3.1 Index representation

UTF-8 JSON, no BOM, comments, trailing bytes, duplicate properties, or unknown
properties in version 1. Integers are JSON integers, never floats or strings.
IDs and hashes are lowercase hexadecimal; SHA-256 digests have 64 characters.
Property order and insignificant whitespace are not semantic. Bounds apply
before parsing/materialization; readers reject oversized input.

The following schematic defines required fields (angle brackets are placeholders):

```json
{
  "archiveVersion": 1,
  "requiredArchiveReaderVersion": 1,
  "requiredFeatures": ["independent-captures-v1", "stored-zip-v1", "index-sha256-v1"],
  "bundleId": "<bundle-id>",
  "createdUtc": "<UTC RFC3339 timestamp>",
  "entries": [
    {
      "entryId": "<entry-id>",
      "label": null,
      "sourceCaptureId": "<id in this entry's manifest>",
      "format": {
        "packageVersion": 2, "schemaVersion": 1, "recordVersion": 1,
        "indexVersion": 1, "writerVersion": 2, "requiredReaderVersion": 2
      },
      "members": [
        {"name": "manifest.json", "bytes": 123, "sha256": "<digest>"},
        {"name": "capture.sqlite", "bytes": 456, "sha256": "<digest>"},
        {"name": "seal.json", "bytes": 123, "sha256": "<digest>"}
      ]
    }
  ]
}
```

`entries` order is the publication/result order. Member order is exactly the
three shown above. Member `name` is an enum, not a supplied path. The importer
constructs full member names from its validated entry IDs. There are 1–16 entries.
Index source identity and all six format axes must match the respective manifest;
the manifest and database must agree under the source package contract.

`bundle.seal.json` has exactly
`{"archiveVersion":1,"indexBytes":<length>,"indexSha256":"<digest>"}`.
Hash the **exact uncompressed bytes** of `bundle.json`, including labels, ordered
membership, IDs, versions, sizes and member hashes. Hash the exact bytes of each
member; verify the inner seal's manifest/database hashes as well. ZIP CRC-32 must
also match. The index seal does not hash itself. There is no circular dependency.

Changing labels, swapping/adding/removing entries, or modifying a member without
updating the corresponding seals must fail. An attacker can recompute every hash;
therefore successful verification means internal integrity only, **not trusted
authorship, trusted SQL, or permission**. An independently supplied whole-archive
SHA-256 is checked during transfer and identifies exact transport bytes, not
authority. ZIP timestamps/attributes are not evidence and never restored locally.

### 3.2 Constrained ZIP grammar and parser order

Writers emit `bundle.json`, `bundle.seal.json`, then entries in index order and
member order, followed by one central directory and one EOCD. No ZIP64, encryption,
multi-disk archives, data descriptors, extra fields, archive/member comments,
prefix/self-extractor, suffix, overlapping ranges, gaps, aliases or duplicate
names. Names are ASCII with `/` separators and the exact case shown above.
Set UTF-8 flag bit 11; all other general-purpose flags must be zero. Members are
ordinary files; reject Unix non-regular modes, DOS directory flags and reparse
attributes. Do not trust platform extraction helpers or restore permissions.

Before constructing `ZipArchive` or any API that eagerly materializes the central
directory, spool through the raw-byte quota to a private seekable file. Read only
the fixed 22-byte EOCD at its expected EOF location; validate non-ZIP64 counts,
single-disk fields, zero comment, directory length/offset, checked arithmetic,
and all bounds from section 6. Stream the central directory through a fixed
buffer, enforcing each header/name length and the actual number of headers
**before allocating each entry**. Cross-check every local header, offset, size,
method, flags, name and CRC against it. Reject extra local records, trailing
directories and any bytes outside the exact grammar. Declared limits alone
are insufficient: count actual bytes and records while consuming them.

Only after this bounded preflight may a bounded ZIP API be used. Method 0 requires
compressed length = uncompressed length = consumed member length. Method 8 and
every other compression method are rejected before decompression. This excludes
compression bombs rather than trusting a claimed expansion ratio.

## 4. Versions and portable provenance

Archive version is independent of capture package, SQLite schema, record, index,
writer, required reader, typed snapshot and composition wrapper versions.
Archive reader 1 accepts exactly archive version 1, required reader 1, and the
three required features above (once each, order irrelevant). Unknown/missing
features and future versions reject the entire operation before publication.
There is no best-effort opaque passthrough or automatic migration.

### 4.1 Explicit supported capture window

| Source package | Six-axis tuple: package/schema/record/index/writer/reader | Status |
|---|---|---|
| 1 | `1/1/1/1/1/1` | Implemented frozen previous format; remains readable/exportable/importable |
| 2 | `2/1/1/1/2/2` | Implemented current foundation; remains readable/exportable/importable |
| 3 | `3/1/1/1/3/3` | **Proposed** import-derived representation below |

The portability reader generation is **3**, explicitly supporting these three
tuples, not a vague “current/previous” rule. Old readers 1/2 must reject package 3
as unsupported, never reinterpret it as 2. Export preserves a source's tuple and
bytes; import creates package 3, never migrates a source in place. Package 3
requires `normalized-scalars-v1`, `artifact-provenance-v1`, and
`portable-source-v1`; it also declares `recovery-artifact-identity-v1` if retaining
recovery aliases. Schema, record and indexes do not change. This is an explicit
extension to the foundation, not a claim that current reader 2 supports imports.
Freeze real v2 fixture bytes before implementing v3, alongside unchanged v1
fixtures. New ordinary collections may continue writing v2.

Typed snapshot representation 1 remains allowlisted by `CaptureArtifactCodec`.
Composition uses `CaptureSnapshot.Version=2` with `compositionVersion` 1 or 2.
The record-stream wrapper uses `CaptureSnapshot.Version=3` with
`metadataVersion=1` and nested representation 0 (metadata only), 1 or 2.
These existing numbers are **not package 3**. Unknown kinds/versions, including
an undecodable snapshot in an otherwise supported package, fail v1 import/export.
Export must emit supported archive/package/record/snapshot representations and
validate their portable compatibility; this is not a guarantee of admission at
every destination. Import independently checks all untrusted input and may reject
otherwise compatible evidence for destination capacity, current authorization,
configured lower limits or import-worker work/resource limits.
Artifacts without snapshots remain valid when their declared record semantics
are supported. Absence of a snapshot is not absence of retained records.

### 4.2 New bounded package-3 provenance

Add an optional `PortableSource` field to the canonical manifest's `CaptureInfo`,
required for imported package 3. Its exact logical data contract is:

- `Origin`: first portable source's `CaptureId`, `OwnerId`, `Name`, `GroupId`,
  `CreatedUtc`, `DerivedFrom`, `SourceHashes`, six-axis `Format`, `Quality`, and
  artifact descriptors (IDs, kinds, names, producer provenance and recovery
  aliases). These are immutable **claimed source facts**, not local authority.
- `OriginMemberHashes`: hashes of that source's manifest, database and seal.
- `ImmediateSource`: source capture ID, six-axis format and three member hashes
  for this import, allowing differentiation from the first portable source.
- `ArtifactMap`: one row per artifact:
  `{originArtifactId, entryArtifactId, localArtifactId}`. Each column is
  bijective within this capture; never resolve a mapping across bundle entries.
  `entryArtifactId` is the artifact's current `ArtifactId` in this archive entry's
  manifest, not its recovery alias or an ID looked up in another store.
- `ImportedUtc`: current destination import time. Local `CaptureInfo.CreatedUtc`
  remains source capture creation time, not upload time.

For the first import, origin equals the archived source. For re-import, carry
forward its already validated `Origin` and `OriginMemberHashes` unchanged;
replace `ImmediateSource`, `ImportedUtc`, and mapping entry/destination IDs.
Resolve each preserved origin artifact ID through the source's validated
`ArtifactMap` row whose `localArtifactId` equals the current entry artifact ID.
Do not recursively embed previous manifests or append unbounded history.
`Origin` is a bounded DTO, not arbitrary JSON or a nested `PortableSource`.
Its aliases describe existing pre-portable recovery; they do not grow on import.
Hashes establish linkage, not authentication of these claims.

`CaptureArtifactInfo.SourceArtifactId` remains the existing intra-store recovery
mechanism: a fixed original reference key used to resolve unchanged recovered
snapshots within one capture. It is not the immediately preceding artifact ID.
The proposed `PortableArtifactMapping.EntryArtifactId`, in contrast, identifies
the immediate archived artifact across an import boundary, scoped by bundle
entry. Never copy `EntryArtifactId` into `SourceArtifactId`: import preserves any
existing recovery alias unchanged, and leaves it null when absent. Neither
mechanism confers ownership, cross-store lookup permission or query authority.

The local manifest's `CaptureId`, artifact IDs and `OwnerId` are new/current;
name, group, timestamps, producer provenance and quality are preserved.
Preserve pre-existing `DerivedFrom`, `SourceHashes` and recovery aliases both in
their existing descriptive fields and in `Origin`; do not misuse `DerivedFrom`
to mean a destination-local capture with that ID. Labels, export filenames,
bearer scopes, transfer/session IDs and local filesystem paths do not enter
immutable capture provenance. Labels live in the bundle and bounded operation
receipt only. Local seals cover the new canonical manifest and new database.
The complete manifest, including origin and mappings, still must fit 128 KiB;
excess fails admission, never truncates provenance or silently drops artifacts.

### 4.3 Reference remapping

Import builds and validates a complete artifact bijection **before** copying
rows. Fresh database rows use destination artifact IDs; occurrence IDs, scalar
values, field order, timestamps, units, quality and stream metadata are preserved.
Preserve occurrence IDs explicitly, not by assuming a replay loop assigns the
same IDs. Record/stack IDs scoped within an artifact remain stable.

Use allowlisted codecs to rewrite semantic artifact references in composition
children, parent `Sweep`/`GcActivities` metadata and any other registered
reference-bearing representation. Resolve each typed artifact reference in this
deterministic order:

1. Validate uniqueness across all current IDs and recovery aliases in this
   entry's manifest first; any collision rejects, even if a direct ID matches.
2. Resolve an exact current `ArtifactId`; only if absent, resolve an exact
   `SourceArtifactId` recovery alias to that artifact's current ID. Missing or
   ambiguous references reject; origin IDs and other entries are never fallbacks.
3. Use that current ID as `EntryArtifactId` in this entry's bijection to obtain
   the new local ID. Rewrite the typed reference to that local ID.

Do not regex-replace ID-looking strings in user fields or stack names.
Every child/parent/stack reference must resolve in the same capture under its
declared representation. Unknown, missing, duplicate or ambiguous references
reject the entire prepared bundle, not a partly usable artifact.

Known references in indexed records must have a kind/category/field-specific
remapper; absent registration is `UnsupportedFormat`, not an arbitrary string
rewrite. Re-encoded snapshots must retain all non-reference data and semantics,
including null/unknown states and error/quality metadata, within unchanged bounds.
Destination snapshots must reference current local IDs directly, so repeated
imports need no growing alias chain. Original recovery aliases remain provenance,
not a way to override direct destination references. Freeze reference-remapping
fixtures in #1050; no source package is consulted during later local drilldown.

Acceptance example (symbols stand for valid opaque IDs; `H1`/`H2` identify
different origin member-hash sets): a recovered source has capture/artifact
`C/A`, recovery alias `R`, and a parent's child reference to `R`. Another independent
source has the same `C/A/R` IDs but different evidence hashes. Both must import
without cross-entry resolution:

| Import and entry | Immediate archived capture/artifact | Preserved origin capture/artifact/hashes | EntryArtifactId | New local capture/artifact |
|---|---|---|---|---|
| First, E1 | C/A | C/A/H1 | A | D1/B1 |
| First, E2 | C/A | C/A/H2 | A | D2/B2 |
| Repeat, F1 | D1/B1 | C/A/H1 | B1 | N1/K1 |
| Repeat, F2 | D1/B1 | C/A/H1 | B1 | N2/K2 |

For the repeat, export D1 twice as distinct entries F1/F2 and import again.
Both retain origin `C/A/H1`, record D1 and its actual sealed member hashes as
`ImmediateSource`, and get independent destination IDs. The first child reference
resolves `R -> A -> B1`; the repeated references resolve directly `B1 -> K1`
and `B1 -> K2` within their respective entries. Every retained recovery alias
stays `R`, never becomes `A` or `B1`; the origin artifact is `A`, not `R`.
All destinations use the current importing owner. Colliding source IDs, duplicate
labels and identical source bytes never merge entries or grant access.

## 5. Core API and lifecycle

The following are **proposed** Core API shapes, not calls available at the
baseline. Names here fix the Core interoperation contract; host CLI option names
and comparison presentation remain downstream decisions. Use existing
`CaptureAccess`, `CaptureInfo`, `CaptureErrorCode` and `CaptureStoreException`.

```csharp
public sealed record PortableOperationKey(string Id, DateTimeOffset RequestedUtc);
public sealed record CaptureExportSelection(string CaptureId, string? Label);
public sealed record CaptureExportRequest(
    PortableOperationKey Operation,
    IReadOnlyList<CaptureExportSelection> Entries);
public sealed record CaptureImportRequest(
    PortableOperationKey Operation, long ArchiveBytes, string ArchiveSha256);
public sealed record PortableArtifactMapping(
    string EntryArtifactId, string OriginArtifactId, string LocalArtifactId);
public sealed record PortableEntryMapping(
    string EntryId, string? Label, string SourceCaptureId,
    string LocalCaptureId, IReadOnlyList<PortableArtifactMapping> Artifacts);
public enum PortableEntryState { Pending, Published, Failed, Cancelled, NotAttempted }
public sealed record PortableFailure(
    CaptureErrorCode Code, string Reason, string? EntryId,
    string? Limit, long? Observed, long? Maximum);
public sealed record PortableEntryResult(
    string EntryId, PortableEntryState State, PortableEntryMapping? Mapping,
    PortableFailure? Failure);
public sealed record PortableImportResult(
    string OperationId, string? BundleId, string ArchiveSha256,
    bool Complete, bool Cancelled, IReadOnlyList<PortableEntryResult> Entries,
    PortableFailure? Failure);
public sealed record PortableExportResult(
    string OperationId, string BundleId, long ArchiveBytes, string ArchiveSha256);
public sealed record PortableImportEntry(
    string EntryId, string? Label, CaptureInfo Source,
    CaptureFormatVersions Format);
public enum PortableAuthorizationPhase { Prepare, Publish }
public delegate ValueTask AuthorizePortableImport(
    IReadOnlyList<PortableImportEntry> entries,
    PortableAuthorizationPhase phase, string? publishingEntryId,
    CancellationToken cancellationToken);
```

`PortableCaptureUseCases` is constructed with the trusted store, finite portable
limits and clock through Core abstractions. The bounded untrusted-validation
worker is an import-only dependency: export-only construction and `ExportAsync`
must work without it. `ImportAsync` without a configured worker capable of
enforcing section 7 fails explicitly with `CaptureErrorCode.UnsupportedFormat`
and reason `ImportWorkerUnavailable` before processing source data or publishing
anything; no in-process fallback or successful stub is permitted. Reading an
existing operation result does not require a worker. Core must not depend on
MCP/HTTP or caller-supplied source directory paths. The public method and DTO
shapes are unchanged by this dependency boundary.

```csharp
Task<PortableExportResult> ExportAsync(
    CaptureExportRequest request, Stream destination, CaptureAccess access,
    CancellationToken cancellationToken = default);
Task<PortableImportResult> ImportAsync(
    CaptureImportRequest request, Stream source, CaptureAccess access,
    AuthorizePortableImport authorize,
    CancellationToken cancellationToken = default);
Task<PortableImportResult> GetImportResultAsync(
    PortableOperationKey operation, CaptureAccess access,
    CancellationToken cancellationToken = default);
```

Streams are caller-owned and left open; import requires readable input and export
writable output, neither must be seekable. Caller streams have bounded I/O and
cancellation; seekable private spooling belongs to Core. CLI can supply the
OS-authorized no-op import policy, explicitly, while MCP supplies current
whole-entry producer/kind/view policy. The callback receives fully validated
bounded descriptions before publication and throws to deny. It is called with
`Prepare` and null entry ID once after validation, then `Publish` and the current
entry ID immediately before each rename, without changing the entry list.
Core enforces destination ownership regardless of that callback. Host export authorization
must check every selected capture/artifact, including sensitive record access,
before invoking export; Core independently checks `CaptureAccess`.

Export may use the existing in-process store reader for these trusted sources,
plus bounded portable metadata, record and snapshot compatibility validation and
byte/work checks. This does not turn that reader into an untrusted SQLite parser.
Export acquires shared source leases in sorted unique capture-ID order, validates
all sources, and retains leases while copying their exact bytes. Duplicate
selections reuse one lease, not a mutable second read. It reserves private space,
writes and validates a complete staged archive, then streams it to destination.
Success means the exact archive was fully written; a failed destination stream
can contain a prefix and is not a published file. CLI writes a private sibling
file and renames to its user-selected destination only after success, refusing
overwrite by default. Do not promise an atomic move across filesystems.
Cancellation never unseals or changes a source.

### 5.1 Import publication: per-entry atomic, bundle may be partial

There is deliberately **no all-or-nothing cross-directory transaction**:

1. Under a private managed `captures/.portable/` namespace, create an exclusive
   owner-bound operation receipt and spool the bounded archive. This new namespace
   must be excluded from ordinary catalog enumeration and generic artifact
   access/reaping, but included in total store accounting.
2. Verify the complete archive, all entries, versions, SQLite content, provenance,
   remapping and destination-manifest bounds before publishing any entry.
   Invoke `authorize` with `Prepare` for the entire prepared entry list. Structural/semantic
   rejection or initial authorization failure publishes zero captures.
3. In index order, reserve destination capacity and one existing writer slot.
   Construct one new database/manifest/seal plus a locally created `.lease` in
   a private staging directory on the **same filesystem** as `captures/`.
   Use trusted schema and parameterized data inserts, not copied SQL.
4. Flush/close the sealed package, validate it through the new local reader,
   and durably record its planned ID, mapping and seal hashes in the operation
   receipt before publication. Under store admission control, recheck quotas,
   identity collision, owner and cancellation, and call `authorize` with
   `Publish` for this entry. A host's callback must not acquire the same store
   admission lock recursively; it checks current policy against the supplied
   validated descriptors.
5. Rename that complete staged **directory** to `captures/<newId>` once. This
   same-filesystem directory rename is the entry publication boundary. It avoids
   exposing a partially populated/interrupted destination through existing list
   operations. Never create a final directory early with public `CreateAsync`.
6. Mark the entry published in the receipt. Continue to the next entry. Stop on
   the first error or cancellation; retain published entries, mark the current
   unpublished entry `Failed` or `Cancelled` and remaining entries `NotAttempted`. No automatic
   rollback/deletion of published evidence.

Filesystems without supported local rename and lease semantics are rejected.
This extends admission/staging internals; it is not behavior the current public
store can provide merely by looping `CreateAsync`. As in the foundation, flushed
files and rename do not claim tested directory-fsync/power-loss durability.
Unexpected power-loss inconsistency fails closed and retains identifiable
evidence for operator inspection.

Before any publication, failures use `CaptureStoreException` or cancellation.
After the first publication, return an authoritative `PortableImportResult`
with published mappings, `Complete=false` and structured failure/cancellation,
even if the caller cancelled. Use a bounded non-cancellable receipt-finalization
step (at most 5 seconds); if it cannot complete, surface `StorageFailure` with
the operation ID and require `GetImportResultAsync`, never claim zero imports.
A disconnected client learns the same outcome by status/retry.

### 5.2 Retry, crash and cleanup

The idempotency scope is `(destination store, current owner, operation key)`.
Keep the request fingerprint (ordered export selections/labels or import archive
length/hash), generated IDs and terminal result in a bounded private receipt.
Same key/different fingerprint is `InvalidInput/OperationConflict`. Same key and
same input never allocate duplicate published IDs. Distinct keys intentionally
create independent imports, even for byte-identical bundles.

Keys include `RequestedUtc`: first use must be within 5 minutes of host UTC;
retained retries/status are supported until `RequestedUtc + 24 hours`. Receipts
are retained through that deadline, including across restart. An older key is
`InvalidInput/OperationExpired`, not silently treated as a new operation after
receipt deletion. A new key is explicit permission to import again.

On restart or retry, exclusively reconcile planned IDs against destination seal
hashes before classifying an entry. A matching published directory resolves a
crash between rename and receipt update. Never adopt an unrelated pre-existing ID.
Unpublished staging can be discarded. Once classified terminal, retry returns
the same partial result; it does not silently finish remaining entries. To import
remaining entries, export/select them explicitly under a new operation key.
Pending requests whose workers died become interrupted terminal operations after
reconciliation; users do not need to guess whether bytes were published.
Before an index was validated, the receipt result has null `BundleId`, no entries
and an operation-level failure; it must not invent a bundle or entry identity.

Export retains its exact staged archive for retry while its transfer is active;
destination-stream retries use that same archive and bundle ID. After staged
bytes expire, export retry returns `InvalidInput/TransferExpired`; create a new
operation for another export. Import receipts outlive transfer bytes. Disposal,
abort, idle timeout and startup cleanup remove only unpublished private data,
after releasing leases. Cleanup errors retain reservation/accounting and report
`StorageFailure/CleanupFailed`; do not free quota on paper while files remain.
Published capture deletion uses the ordinary authorized capture lifecycle.

## 6. Mandatory finite operating limits

These are conservative **v1 admission ceilings**, not throughput recommendations
or evidence that #1041 has passed. Hosts may lower them; raising them requires a
contract/version review. Integer arithmetic is checked. Limits apply to actual
consumption and reservations across cooperating processes, not only declarations.
Archive, storage, portable-buffer, metadata, row/token and operation limits apply
to both paths where relevant. The explicitly identified import-worker limits
below govern isolated untrusted admission, not the trusted local store reader.

| Resource | Hard admission/stop rule |
|---|---|
| Archive raw (“compressed”) bytes | 512 MiB, counted while receiving/writing, including headers/directory |
| Uncompressed member bytes | 512 MiB aggregate; method 0 only, actual bytes must equal declared |
| Entries / members | 16 / 50 (`2 + 3*entries`), checked before directory allocation |
| Central directory / all ZIP headers | 32 KiB / 64 KiB respectively, streamed preflight |
| Member name / ZIP extra/comment | 128 ASCII bytes / zero bytes |
| Index / bundle seal | 128 KiB / 1 KiB; JSON depth 16 |
| Inner manifest / inner seal | 128 KiB each; JSON depth 32 |
| Source database | 256 MiB each; exact SQLite page geometry consistent with file length |
| Artifacts / source snapshots | 64 per capture / 8 MiB each; JSON depth 64 |
| Source logical payload / record / fields | 128 MiB per capture / 64 KiB / 64, using foundation accounting |
| Labels / ordinary provenance text | 256 bytes / existing 1 KiB UTF-8 bounds |
| SQLite schema | 32 entries maximum; 32 KiB total SQL text; import additionally enforces the exact allowlist in section 7 |
| Table work | At most 2,000,000 rows per table and 4,000,000 total rows per capture, including fields/strings |
| Semantic JSON work | 2,000,000 tokens per snapshot; 8,000,000 per capture; count before DTO materialization |
| Import-worker SQL execution work | 200,000,000 VM instructions per entry across all admission statements; callback every 1,000 instructions |
| Import-worker time / whole operation | 60 seconds CPU and 120 seconds wall per import entry; 600 seconds wall per export or import operation |
| Copy buffers / metadata | 64 KiB per stream, at most four live buffers / 4 MiB retained metadata outside snapshots |
| Import-worker native heap / SQLite cache | 32 MiB hard SQLite heap limit / 8 MiB cache, mmap disabled |
| Import-worker resident-memory stop | 256 MiB, watchdog samples at most 10 ms apart and terminates on exceedance |
| Host retained portable buffers | 32 MiB per active operation, reserved before allocation; never retain all snapshots |
| Portable operations / validators | 2 active operations per store, 1 per owner; 1 import validation worker per store |
| Store writer slots | Existing limit 2 total, shared with ordinary writers; imports do not add slots |
| Per-operation private disk | 2 GiB including archive, extracted sources, destination staging and SQLite side files |
| Aggregate managed store | 4 GiB and 256 captures by default, or lower configured limits; include private staging, receipts, tombstones and reservations |
| Receipts | 64 active/unexpired per store, at most 256 KiB each; reserve on begin, do not evict unexpired idempotency |
| Scan/receipt-finalization deadline | 5 seconds per control/reconciliation step; fail busy/storage rather than wait indefinitely |

Use the smaller applicable Core `CaptureStoreOptions` limit. Some locally valid
large captures will not be portable under these limits; return the exact limit,
observed value and maximum, not truncated “success”. Metadata/row/token/VM budgets
can reject a compact yet expensive input even below the byte cap on their
applicable path. Trusted export still enforces finite row/token work and the
600-second operation deadline: check cancellation/deadline before and after
trusted reader calls and between bounded pages, snapshots and copy chunks.
Cancellation/deadline failure never returns export success. These cooperative
checks do not promise preemption or an adversarial native-parser watchdog inside
the trusted in-process reader; those guarantees belong to isolated import.

Reserve archive and extraction capacity before receiving their next bytes; reserve
one full destination `MaxPackageBytes` before building that entry, including
worst-case SQLite side files separately within the 2 GiB private cap. Move
reservation into actual published-store accounting at publication, without
double-counting or an unaccounted interval. Recalculate/check under the existing
cross-process admission lock. Inactive readers need no portable writer slot.
`Busy` means a concurrent slot/lease conflict; `CapacityExceeded` means a byte,
count, work or deadline cap. Never silently wait in an unbounded queue.

Admission arrays, strings, decoded base64, JSON tokens and ZIP descriptors must
be checked/reserved **before** allocation; use a bounded lookahead byte/record
to detect overflow. In untrusted import, a VM progress handler does not bound all
native parser work. The worker deadline and process watchdog cover first SQLite
open, schema parsing and integrity checking as well. A watchdog RSS threshold is a
termination threshold, not a proof of zero sampling overshoot or a portable
kernel-enforced RSS cap. #1041 must measure peak/process-tree memory including
that overshoot; unsupported hard-limit/interrupt facilities must fail import
admission, not silently use the ordinary in-process reader. No “safe because hash
matched” shortcut is permitted.

## 7. Untrusted SQLite and payload admission

This section is mandatory for import, not a prerequisite worker for trusted
export. No untrusted database is opened by the long-lived CLI/MCP host or installed
as a local package. Run the admission/rebuild work in a disposable, quota-controlled
worker through a Core abstraction, without network, target-process access,
host bearer tokens, or access to unrelated stores. Hosts must package an actual
worker implementation; a test stub is not release acceptance. Use platform
isolation where available and fail closed if mandatory limits cannot be applied.
Limit worker output to validated bounded DTO/data frames, not executable SQL.
Parent rechecks frame lengths, counts, IDs and result metadata before publication.

Order is mandatory:

1. Verify archive/index/member integrity and bounded manifest descriptors first.
   Check source state is sealed; reject pending or native dependencies. Read the
   fixed SQLite header before native open: magic, encoding, supported page size,
   page count/file length and absence of external journaling dependencies.
   A checkpointed database can retain a WAL-mode header: that flag alone is
   not rejection, nor permission to seek an external WAL. All sealed data must
   be readable from this main file under immutable mode and pass validation. The
   standard SQLite engine is necessary; collector native files are not.
2. Configure process isolation/watchdog and SQLite native hard-heap/length/SQL/
   column/expression/variable limits **before processing input**. Use 9 MiB
   maximum SQLite value/encoded-row length (room for an 8 MiB snapshot plus row
   overhead), 32 KiB SQL length, 64 columns, expression depth
   32 and 64 variables. Open read-only/immutable, private cache, no pooling,
   `trusted_schema=OFF`, defensive mode, extension loading disabled, mmap zero.
   Install authorizer/progress/interrupt hooks before any application query.
   Do not use URI parameters from the archive.
3. Query bounded `sqlite_schema` metadata only. Require exact tables `format`,
   `strings`, `artifacts`, `occurrences`, `fields`, `snapshots`, their frozen
   columns/types/nullability/primary keys/foreign keys/checks, the six
   `ix_occurrence_*`/`ix_fields_name` indexes in `CapturePackage.Schema`, and only
   SQLite's corresponding required autoindexes. There are **six tables**
   including `format`. Reject any other object, trigger, view, virtual table,
   generated column, extra index, `sqlite_stat*`, extension reference or schema
   expression. Compare against allowlisted schema definitions/structural metadata,
   allowing only the known producer whitespace/quoting spellings; do not execute
   archived DDL to discover what it does. Before any data query, verify full
   index column order/uniqueness, not just names.
4. The authorizer permits only the fixed metadata/integrity statements and
   fixed parameterized reads of those tables; deny attach/detach, writes,
   application-supplied SQL/functions, schema modifications and other PRAGMAs.
   Execute bounded `quick_check` and foreign-key validation under cumulative
   work/time budgets. A correct schema or `quick_check=ok` alone is insufficient.
5. Stream every data table through bounded statements/pages. Validate SQLite
   storage classes, UTF-8, ID grammar/uniqueness, foreign keys, one format row,
   manifest/database artifact equality, positive occurrence/string IDs,
   contiguous field ordinals, scalar slots, finite doubles, timestamps, durations
   and existing per-record limits. Reject dangling or unused malicious dimensions
   rather than only checking rows reached by the first public query.
6. Validate all snapshots using known representation/kind codecs and semantic
   reference rules, including record-stream metadata, source loss, composition
   metadata and stack chains. Bound depth and token counts before materializing
   one snapshot at a time. Reject cycles where the representation requires a
   tree/DAG, unresolved references, duplicate keys and type-name activation.
7. Validate quality as data: nonnegative counters, `Accepted <= Offered`,
   `Persisted <= Accepted`, and
   `Offered = Persisted + RecordRejected + QueueRejected + StorageRejected + Pending`
   with checked arithmetic. Match persisted occurrence counts and available
   per-artifact stream counts, without counting typed snapshots as occurrences.
   Null source loss remains null. A sealed recovered capture may legitimately
   have interrupted/unknown-tail quality; do not “repair” it to complete.
8. Rebuild the destination from trusted schema using parameterized inserts and
   validated data, remapping IDs as section 4 specifies. Do not use `ATTACH`,
   execute SQL from the source, copy source pages into the trusted store, or
   run the recovery path as an import shortcut. Preserve quality explicitly:
   a new writer's successful insert counters must not replace source loss.
   Validate and seal the derived package before publication.

Rebuilding prevents active source-schema objects from becoming trusted local
schema; it does not authenticate source facts or remove sensitive content.
Existing redaction is defense in depth and does not prove absence of secrets,
PII or confidential data. Do not silently redact/change transported evidence.
Treat exported packages and downloaded files as sensitive diagnostic artifacts.

## 8. Actual MCP and CLI transfer

### 8.1 One protocol on HTTP and stdio

Extend existing `get_bytes(kind="captures", captureAction=...)`; do not add a tool.
Retain existing list/describe/delete/recover actions. The following new action
tokens are the v1 transfer contract, using ordinary MCP `tools/call`:

| Action | Required input | Result |
|---|---|---|
| `export-start` | operation key; 1–16 capture-ID/label selections | Transfer descriptor; prepares the export asynchronously |
| `download-chunk` | transfer ID; byte offset; count | Offset, actual count, base64 bytes, chunk SHA-256, EOF |
| `import-start` | operation key; exact total archive bytes and SHA-256 | Owner-bound upload descriptor, next offset 0 |
| `upload-chunk` | transfer ID; offset; base64 bytes; chunk SHA-256 | Accepted count and next offset |
| `import-commit` | transfer ID | Begin validation/publication; descriptor/status, not an unbounded blocking request |
| `transfer-status` | transfer ID or operation key, not both | Bounded state, offset/progress, terminal error or result summary |
| `import-result` | operation key; after-entry ordinal; page size | Mapping/result page, up to 1 entry and 64 artifact mappings |
| `transfer-cancel` | transfer ID | Stop unpublished work and return status; never delete published captures |

The Core methods remain stream/use-case APIs. #1052 implements the host byte
adapter and bounded asynchronous transfer state; neither HTTP types nor MCP
arguments move into Core.

Descriptor fields are `transferId`, `operationId`, `direction`, `state`,
`expiresUtc`, `idleExpiresUtc`, `maximumChunkBytes`, `nextOffset`, `archiveBytes`,
and `archiveSha256` (length/hash null until staged for export, declared for upload).
States are `Preparing`, `Receiving`, `Ready`, `Validating`, `Publishing`, `Completed`,
`Failed`, `Cancelled`, `Expired`. Bundle ID appears only after validated/staged.
No server filesystem path is advertised as a download. A display-only suggested
filename is optional; the client independently chooses its local file.

HTTP clients authenticate **every** `tools/call` through the actual existing
Streamable HTTP MCP endpoint/middleware. Stdio clients send the identical calls
as JSON-RPC over the existing subprocess pipes under the synthetic local
principal. Returned chunk bytes must be written by the client to its chosen
file and whole-archive hash checked. A download is allowed only after export
status becomes `Ready`; its advertised length/hash then cannot change.
Upload reads the client's file and sends
bounded chunks. A resource link or server-local ZIP alone is not completion.
Version 1 needs no new HTTP endpoint, custom MCP method, resource-read bypass,
out-of-band shared filesystem, or SDK Tasks extension.

### 8.2 Wire, retry and lifetime rules

- Maximum raw chunk is **24 KiB**, maximum base64 value **32 KiB**. Require
  canonical base64 with no whitespace; validate length before decoding.
  Maximum encoded transfer request is **64 KiB**, response **128 KiB**, including
  JSON-RPC/MCP wrappers, duplicate structured/text content, escaping and hints.
  These are stricter than `DurableCaptureTools.MaximumResponseBytes = 1 MiB`.
- Enforce request framing limits **before** eager JSON deserialization:
  bounded HTTP body consumption even for chunked transfer encoding, and bounded
  stdio message framing. If the MCP SDK cannot selectively preflight a tool,
  cap the containing transport frame at 1 MiB before SDK parsing, then apply
  the 64 KiB action limit before decoding arguments. No unbounded pipe `ReadLine`.
  Verify existing non-transfer request compatibility in #1052.
- Upload is sequential: offset must equal `nextOffset`; no sparse/out-of-order
  writes. An exact replay of the immediately previous accepted chunk is
  acknowledged without appending if offset/length/hash/bytes match. Other retries
  are `InvalidInput/OffsetMismatch` with the authorized next offset. Hash mismatch
  never advances offset. Persist offset after successful bounded write.
- Download permits arbitrary 24 KiB-aligned offsets for retries; request count
  is 24 KiB, with only the final response shortened to remaining bytes. It never
  reads beyond total length and always returns actual count and
  chunk hash. Whole-file length/hash are authoritative. EOF at exact total length
  is a zero-byte result; offsets beyond it reject.
- Per transfer: at most **65,536** total chunk calls including retries and one
  in-flight chunk. Idle timeout is **5 minutes** since
  last successful progress; lifetime is **60 minutes** absolute. Idle downloads
  count newly reached offsets as progress, not repeated bytes. In-progress
  commit work uses the 600-second operation deadline, not renewed chunk leases.
- At most **2 transfers per store**, **1 per owner**, sharing Core's two active
  operation reservations, not adding another allowance. Rate-limit to **100
  calls/second/store**, reject excess `Busy` with retry delay. Response/status
  polling counts. No unbounded waiter queue.
- Restart expires active transfer IDs; clients use the durable operation key to
  reconcile import outcomes. Completed/cancelled/failed transfers release staged
  bytes after no more than **5 minutes**; expiry cleanup runs at startup and
  at most every **60 seconds**. Retain bounded import receipts for 24 hours.
- `import-commit` is idempotent, accepts only exact declared length/hash, and
  initiates at most one worker. Premature commit is `Incomplete/UploadIncomplete`.
  Status/result can be retrieved after network cancellation. Expiry/cancel during
  publication has section 5 partial-result semantics.

### 8.3 Authorization and CLI boundary

Transfer IDs are unguessable but **not bearer authority**. Bind them to
`OwnershipKey`, host instance, direction and initiating authenticated session;
check current principal and expiry on every action. Another same-name principal
is not the same owner. Stdio transfer state belongs to that subprocess, never
a global anonymous owner. Logs contain IDs/outcomes, not bearer values, chunk
contents or source absolute paths.

Follow the foundation's actual lifecycle gate:
`HasExplicitScope("module-bytes-read") && HasScope("investigation-export")`.
Literal membership is required only for `module-bytes-read`; `root`/`*`
satisfies the ordinary `investigation-export` check, but not the explicit
`module-bytes-read` check. The proposed transfer gate retains this distinction.
The implemented stdio principal currently contains only `root`, so it does
**not** already pass this gate. #1052 must add
a trusted local startup configuration for explicit capture-byte access:
the verified stdio host constructs its local principal with literal
`module-bytes-read` only when opted in, and explicitly configured sensitivity
modifiers when needed. Default non-opted-in stdio remains denied. The exact
startup option name is a host documentation choice, not a new tool or remotely
accepted scope argument. A remote principal merely named `stdio-root` receives
no such privilege. Tests must start the real stdio host with this explicit
configuration and exercise ordinary durable lifecycle/read gates as well as
transfer. Export additionally authorizes **every**
artifact and descendant using existing producer/kind/records sensitivity policy,
not merely a summary view: whole evidence can include sensitive heap records,
generic EventSource and method parameters. Import requires the same lifecycle
gate and validates all artifact policy before publication; unknown policy fails
closed. Archived producer values may add requirements, never remove the
conservative kind-based requirements. Missing metadata never becomes authority.

Reapply current policy on export start, each download, import commit and before
each entry publication, as well as later describe/open/query/comparison.
Revoked authorization stops future bytes/publication even if a transfer exists.
The host must resolve current policy on each `authorize` callback, including
the `Publish` phase; a cached decision from `Prepare` is insufficient.
A denied publication callback stops further publication with partial-result
semantics. The host cancels active work when it observes revocation.
A tiny authorization/dispatch race is not a transactional policy lease; document
the same limit as current durable handle dispatch. Already downloaded bytes or
published owner-held evidence cannot be recalled.

CLI #1053 provides file-oriented export/import using these Core methods and its
explicit selected root; no bearer, HTTP, daemon or MCP project reference.
It displays labels as data and mappings as the way to obtain new selectors.
CLI nonzero exit on partial import prints all published IDs and the operation
key, never only “failed”. File overwrite, output-to-stdout and exact option
spellings belong to CLI review; the contract requires bounded binary copying,
cancellation, actionable errors and no accidental partial final output file.

## 9. Historical comparison contract

#1051 selects **two independent references**, each logically
`{host/store context, captureId, artifactId, view, bounded filters}`. In local
Core, host/store context is the service instance, never a caller-supplied
filesystem path. Remote context must resolve through a trusted configured host.
Bundle IDs/entry IDs/labels are not selectors: use the import mappings first.
Comparisons may use different captures from different bundles or no bundle.
No requirement for a central bundle catalog or common source owner is introduced.

Reuse existing offline drilldown/`compare_to_baseline` concepts. This document
does not invent final new tool names, CLI syntax or a generic comparison DSL.
The logical result schema, however, must include:

```text
left, right: resolved local references and claimed source identities
compatibility:
  status = comparable | qualified | incompatible
  reasons[] = { code, side = left | right | both, field, message }
quality:
  left, right: original CaptureQuality plus per-artifact stream quality/notes
metrics[]:
  key, unit, semanticsVersion, population, aggregation, normalization,
  leftValue?, rightValue?, absoluteDelta?, relativeDelta?, unavailableReason?
error?: { code, reason, side?, retryable }
```

Authorize ownership and original producer/kind/**selected view** on both sides,
including composition descendants, before returning either side's content.
Recheck durable handle bindings on every reuse; do not permit one authorized side
to disclose the other's existence, labels, quality or errors. A denial returns
no partial comparison payload. A deleted/expired handle is not silently replaced
by a source ID. Materialized snapshots do not bypass later authorization.

Compatibility is metric-specific, not “both are CPU”:

- Supported representation, view and semantic metric definition/version;
  units, aggregation, grouping keys and required dimensions must agree.
- Distinguish event counts, sampled counts/weights, allocation bytes, native
  allocation **sample counts**, retained point-in-time rows and derived
  projections (`sourceOccurrence=false`) from complete occurrence streams.
- Normalized rates require explicit compatible denominators and observed
  duration/windows; requested duration is not silently substituted.
  Process lifetime, runtime/architecture, collector configuration, sampling
  interval/weighting and overlapping/window semantics are checked when relevant.
- Missing values are null, not zero. Unknown loss is unknown, not complete.
  Truncation/rejections/interruption/unknown-tail, snapshot-only availability
  and recovery limitations propagate on **both** sides.
- A supported retained-data comparison with known limitations is `qualified`,
  not an unqualified full-population claim. Incompatible semantics suppress
  deltas for affected metrics and give reasons; no speculative conversions.
  A zero left denominator yields null relative delta with an explicit reason.
  Signed absolute deltas must avoid integer overflow/precision loss.

Use bounded existing typed queries/keyset pages; no arbitrary SQL or cross-store
SQL joins. At most 1,000 rows and 1 MiB per side/page, 2,000 result metrics and
1 MiB combined Core result, 10 seconds wall and 10,000,000 examined rows per
comparison; host envelopes may require smaller pages. Missing required metadata
is `incompatible` unless the metric's reviewed policy explicitly permits a
`qualified` retained-only comparison. No “force compatible” switch in version 1.

## 10. Error contract

Reuse `CaptureStoreException.Code` for Core admission/storage failures and
the existing MCP `DiagnosticError("CaptureStoreError", ..., code)` family;
host scope failures remain `InsufficientScope`. Add a bounded `PortableFailure`
payload for machine-readable reason, entry and limit details, not a new parallel
unbounded exception dump. Transport actions return tool error results, not a
successful empty blob. Messages are English, at most 1 KiB, with no local path,
SQL text, raw payload or credentials. Unauthorized responses omit sensitive
entry/source/mapping details. Missing and unauthorized transfers share the same
external denial; do not expose other owners' receipt state.

| Existing code | Portable reason examples / behavior |
|---|---|
| `InvalidInput` | `OperationConflict`, `OperationExpired`, `TransferExpired`, `OffsetMismatch`, invalid selector |
| `Forbidden` | Current owner/policy denial; host uses its existing scope envelope |
| `NotFound` / `Deleted` | Authorized source/local capture absent or deleted |
| `Busy` | Admission/lease/worker/transfer concurrency; bounded retry delay |
| `CapacityExceeded` | Named byte/header/row/work/memory/store/time limit; no truncation |
| `Incomplete` | `SourceNotSealed`, `UploadIncomplete`; explicit recovery needed only for source evidence |
| `UnsupportedFormat` | Archive/package/required feature/codec/schema capability unsupported; `ImportWorkerUnavailable` when safe import cannot be configured |
| `CorruptPackage` | Hash/CRC mismatch, inconsistent descriptor, invalid SQLite data or references |
| `UnsafePath` | Invalid archive member grammar, symlink/reparse/alias attempt |
| `StorageFailure` | I/O, worker crash, cleanup or receipt failure; preserve operation reference |

Comparison's logical `error` uses the same ownership/storage categories;
`IncompatibleEvidence` is a comparison-domain reason, not an invented existing
`CaptureErrorCode` enum member. Quality-qualified success is distinguishable from
incompatible/error. Cancellation before publication is ordinary cancellation;
after publication it is a partial result, not `Complete=true`.

## 11. Acceptance fixtures and downstream ownership

The following are future required cases, **not test results from this draft**.
Use real cross-root bytes and client transfers, not direct helper-only mocks.

| Case | Required observation | Owner |
|---|---|---|
| Trusted ID-selected export without an import worker | Export remains bounded, authorized, leased and immutable; arbitrary paths/raw external SQLite are not accepted as sources | #1049 |
| Import without a configured safe worker | Explicit `UnsupportedFormat/ImportWorkerUnavailable` before input processing/publication; no in-process fallback or stub success | #1050 |
| Compatible export into a restricted destination | Import may reject current policy, capacity or worker budgets without mislabeling the source format as incompatible or weakening admission | #1050/#1054 |
| Two sealed captures with duplicate labels/source IDs | Distinct entry/local IDs; whole-bundle index verified; no merge or overwrite | #1049/#1050 |
| Export labels/filename changed | Source package bytes/member sets/quality unchanged; index and bundle hashes change | #1049 |
| Interrupted source and explicit recovery | Direct export fails; recovered sealed evidence roundtrips with unknown-tail/rejection facts | #1049/#1050 |
| Scalars, rows, snapshots, composition, CPU stacks | Exact precision/null/order/units/record IDs; parent/child/stack references resolve after import and re-import | #1050 |
| First origin, immediate source, local owner | Origin stays immutable across roots/repeated imports; current owner and local mappings change; no source lookup | #1050 |
| Frozen v1, frozen real v2, new v3 | Mixed bundle supported by reader3; source/actual reader distinguished; readers1/2 reject v3; old fixtures byte-identical | #1049/#1050 |
| Future axes/features/codec or missing feature | Entire preparation rejects before publication, no downgraded/opaque entry | #1049/#1050 |
| Index label/member/order tamper | Hash failure; recomputed hashes still invoke full untrusted admission | #1050 |
| ZIP bombs/ZIP64/duplicate names/path traversal/symlink/overlap | Rejected before decompression/extraction; method8 rejected; no out-of-root writes | #1050 |
| Huge/spoofed central directory/count/name, overflowing offsets | Rejected before eager ZIP directory allocation; bounded memory demonstrated | #1050/#1041 |
| Tiny archive with expensive schema/rows/JSON/native parser | Schema allowlist, VM/row/token/time/memory guards stop work; host survives worker termination | #1050/#1041 |
| Valid hashes plus views/triggers/virtual tables/bad indexes | No application data query before schema rejection; no extensions/network/native attach | #1050 |
| Bad scalar types, dangling aliases, cycles, inconsistent quality | No partial prepared publication and no fabricated complete/empty stream | #1050 |
| Cancellation during read/validation/build | No published partial package; cleanup and reservations measurable | #1049/#1050 |
| Crash before rename and immediately after rename | Reconcile receipt deterministically; no duplicate IDs on retry; published evidence retained | #1050 |
| Second entry disk/quota/policy failure after first publication | Explicit partial result; first queryable; later not attempted; retry returns same result | #1050/#1052 |
| Same key/different input; replay after deadline | Conflict/expired errors, not duplicate import or adoption of existing ID | #1050 |
| Concurrent collectors/importers; cleanup failure | Shared slot/count/disk admission holds; failed cleanup remains charged | #1050/#1041 |
| Actual remote HTTP export/download/upload/import | Independent client writes bytes and checks hash, imports into another root; no server path dependence | #1052/#1054 |
| Actual stdio-only export/download/upload/import | Same chunk protocol through subprocess pipes, no HTTP dependency; bounded frames | #1052/#1054 |
| Lost chunk response, replay, bad hash/base64, oversized frame | Exact bounded retry behavior, no duplicate write, pre-deserialization limits | #1052 |
| Cross-owner/name collision, expiry, restart, scope revocation | No unauthorized bytes/results; transfer expires; operation reconciliation remains possible | #1052 |
| Native-dependent view after import | Explicit unavailable/unsupported result, no native-path access or invented heap facts | #1050/#1051 |
| CLI root A to root B, cancellation, partial import | Binary file roundtrip, current ownership and printed mappings; Core-only dependency test retained | #1053 |
| Comparisons across bundles with one side unauthorized | No partial disclosure; both current policies applied | #1051 |
| Known/unknown loss, snapshots vs occurrences, different metric units/windows | Qualified/incompatible results and both-side quality; no misleading deltas | #1051 |
| Worst allowed count/size/work plus one beyond every limit | Deterministic boundary errors, measured memory/disk/CPU including parser and worker | #1041/#1054 |

Implementation ownership:

- **#1049:** bounded trusted-store immutable export, archive fixtures, and Core
  export API; no dependency on implementing the isolated import worker first.
- **#1050:** untrusted admission worker, package-3 provenance/reader fixtures,
  typed reference remapping, quotas, staging/publication and retry receipts.
- **#1053:** Core-only local CLI file workflows and user reference documentation.
- **#1052:** real authorized HTTP/stdio byte adapters, pre-parser wire limits,
  transfer state/expiry, current-policy publication callback and client examples.
- **#1051:** reviewed metric compatibility registry, two-side selectors,
  authorization and quality-bearing comparisons.
- **#1041:** measured operating envelope, all process-tree resource costs and
  watchdog overshoot; do not extrapolate frozen experiments into these gates.
- **#1054:** integrated portability/comparison compatibility and adversarial CI,
  independent review, documentation/tool parity and the single feature release.

## 12. Deliberate restrictions and remaining risks

The decisions above settle v1 behavior, not implementation evidence. Stored-only
ZIP, whole captures, exact codecs, one-at-a-time publication, no opaque passthrough,
and finite work limits intentionally reject some otherwise valid large/local
evidence. A future compressed format, authenticated signatures, cross-machine
comparison execution, all-bundle atomic publication and native dependency transport
are not implied or silently enabled.

Downstream implementation must demonstrate the SQLite hook/worker isolation and
bounded transport framing available on supported operating systems. If the
chosen native binding/SDK cannot implement them, that platform is unsupported
for portable import until corrected; do not weaken admission to ship. The exact
worker packaging/isolation mechanism and final comparison/CLI presentation names
remain implementation choices, not unsettled ownership/integrity semantics.
Package 3 and the three-generation read window require new independent fixtures
and review; this document does not upgrade the existing reader.

Timing, usable capacity and peak RSS (including watchdog overshoot) remain
unmeasured for this path. Directory fsync/power-loss guarantees are not promised.
Partial publication is deliberate and must be visible to humans and agents.
These risks remain release gates under #1041/#1054, not reasons to claim PK2–PK6
or production acceptance complete.
