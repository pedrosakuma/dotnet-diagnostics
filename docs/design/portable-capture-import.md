# Isolated portable capture import - Core candidate

This implements the Core pipeline for #1050 against the
[approved contract](portable-capture-contract.md), using the
[source SQLite validator](sqlite-structure-admission.md) and
[Linux isolation substrate](isolated-import-worker.md). It is not CLI/MCP
integration, worker packaging, broader platform support, or release acceptance.
The strict observation policy can reject valid captures on ordinary schedulers;
the implementation must not retry or reinterpret that rejection as admission.

## Explicit configuration and ownership

```csharp
var portable = new PortableCaptureUseCases(
    store,
    authorizeExport: AuthorizeWholeCaptureAsync,
    importWorker: new PortableCaptureImportWorker(workerExecutable, sqliteLibrary));

PortableImportResult result = await portable.ImportAsync(
    new CaptureImportRequest(operationKey, expectedBytes, expectedSha256),
    sourceStream, currentAccess, AuthorizeImportAsync, cancellationToken);
```

Executable/library paths are trusted deployment configuration, never bundle
fields. Default construction still supports trusted export without a worker.
Import without safe Linux x86-64 assets/facilities is explicitly unavailable
before source consumption. There is no in-process foreign-SQLite fallback.
The caller owns its streams; length and digest declarations do not replace
consumption-time checks.

Core independently owns destination captures under `currentAccess.OwnerId`.
The import callback receives all validated source descriptors once at Prepare
and again immediately before each entry's Publish rename. Producer owner names,
native paths, provenance and labels are inert data, not authority. Whole-capture
policy must cover every represented artifact/record; Core does not infer it
from summary visibility.

## Trust promotion, in order

1. Reserve private bytes, receive the bounded stream and verify actual length
   and SHA-256. The manual Stored-only ZIP scanner bounds EOCD/central-directory
   inventory before allocation, checks local/central agreement, CRC and hashes,
   and rejects unsupported features, duplicates, aliases, traversal, trailing
   bytes and unknown members. Exact index bytes bind labels and membership.
2. Extract only fixed manifest/database/seal members into owned private staging.
   Bound metadata arrays/text before deserialization; reject unknown/missing
   fields, invalid UTF-8 and duplicate JSON keys. Check all format axes, required
   features, seals, quality arithmetic, descriptors and source hashes.
3. Open foreign SQLite only in the read-only confined native process. It
   validates schema, geometry, integrity, every scalar row and manifest
   populations, returning bounded provisional frames rather than a database.
4. Recheck those frames in Core. Disk-backed dimension/record indexes avoid
   population-sized dictionaries. Allowlisted codecs check typed/nullability
   shape, finite values and count fields. Validate composition cycles, wrappers,
   record populations and artifact-local CPU definitions/references; remap only
   registered capture-local references. Bound snapshot bytes/tokens/workspace
   before typed materialization. Spool validated, normalized scalar frames.
5. Finish validation of **every** entry before Prepare or any publication.
   For each destination, launch a separate confined writer. It receives only
   parent-validated scalar frames, creates the compiled trusted schema and uses
   fixed parameterized inserts. It never receives source pages, source DDL or
   executable SQL. Source admission and rebuild share the entry's CPU/wall/VM
   budget. The parent may open this freshly rebuilt trusted database, but never
   the foreign source.
6. Seal the fresh destination, persist its planned ID and seal hash, check
   current quota/ownership/policy, and rename its directory on the same filesystem
   without overwrite. Publication is atomic per entry, not across directories.
   Earlier published entries survive a later failure and are explicitly reported.

## Identity and quality

Executing reader version 3 reads package versions 1, 2 and 3; schema, record and
index versions remain 1. Ordinary collection still writes package 2. Imported
captures use package/writer/required-reader 3 and `portable-source-v1`.

Each import creates fresh capture/artifact IDs. `PortableSource` contains a
bounded, nonrecursive permanent origin, original member hashes, immediate source
identity/hashes and an artifact bijection. Re-import preserves the origin and
replaces immediate/local mappings. Entry artifact IDs remain distinct from
recovery `SourceArtifactId` aliases; current-ID resolution precedes the accepted
alias mapping. Occurrence IDs and artifact-local CPU stack IDs are unchanged.

Original quality is retained, including loss/rejections, pending population,
unknown source counts and unknown/interrupted tails. A clean destination writer
does not manufacture clean source quality. Native-file-dependent views remain
unavailable without their separately authorized dependencies.

## Resource and interruption behavior

Contract ceilings remain unchanged: 16 entries/50 members, 512 MiB archive/wire,
256 MiB source databases, 8 MiB snapshots, 4 MiB metadata and 32 MiB retained
portable workspace. Conservative workspace reservations can reject evidence
below another individual byte ceiling. Lower configured store limits also apply.
One validator per store, two portable operations, one per owner and the ordinary
writer slots are shared, not bypassed. Destination reservations include main and
side-file space; receipt replacement and all spools remain accounted.

Unknown-length spools reserve the first 1 MiB before use and subsequent 1 MiB
increments before writes. The 2 GiB private/4 GiB store ceilings are not evidence
that every structurally valid archive fits. Buffers are bounded; scalar data is
spooled rather than retaining all rows or snapshots.

Before input, the journal records both native-worker and parent-I/O identities.
Native process death alone cannot free storage while a parent evidence task can
still write. The supervisor cancels and joins I/O before durable finalization.
Unconfirmed completion retains files and reservations, including across status
reconciliation. A live/reused PID or inaccessible process identity is not treated
as death; persisted PIDs are never signalled.

The mandatory RSS window ends only on positively confirmed child exit within
the original last-valid-sample deadline. Parent-only flush then remains subject
to wall/cancellation limits. Metric-read failure, zero RSS or a late exit cannot
reset that deadline. RSS remains a sampled 256 MiB stop with possible overshoot,
not a kernel hard limit. Gaps over 10 ms reject the result without retry.

Receipts bind current owner, key and input digest. Terminal retry returns stable
outcomes/mappings without re-reading input; changed input conflicts.
`GetImportResultAsync` reconciles only planned destination IDs with matching
seal hashes and owner/state. Rename-before-receipt recovery does not discover or
adopt unrelated captures. Unpublished interrupted entries remain explicit;
there is no automatic continuation or hidden history. Cleanup is bounded and
never releases storage whose ownership/termination cannot be established.

## Evidence and remaining gates

The independent known-kind v1/v2 bundle is frozen at SHA-256
`bbdc6584acb14da9b21fc2d5170252aab2cf307fbcda1f554a05b6f1938c9ea1`.
Its Python generator uses literal schema/ZIP definitions, not the production
exporter; identities, per-member hashes and provenance are beside the fixture.
Older frozen fixtures remain unchanged. Additional tests cover typed producer
captures, repeated package-3 import, aliases, CPU references, malicious ZIP/
SQLite/JSON, partial authorization/cancellation, quotas and reconciliation.

This candidate's validation is **not an all-passed acceptance gate**. The retained
post-I/O-cleanup run passed 292/293 selected cases; a real rebuild observation gap
preempted one partial-publication authorization case. It was not retried to green
or accepted as that scenario's success. The stack identifies the monitoring
stage, not the physical scheduling cause. Deterministic lifecycle tests separately
cover deadline/exit decisions and I/O reservation retention.

Independent review and remaining whole-feature acceptance are required.
Host packaging/CLI (#1053), authenticated MCP transfer (#1052), comparison
integration (#1051), performance evidence (#1041) and integration/release (#1054)
remain outside this Core change.
