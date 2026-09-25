# Trusted portable capture export — Core implementation

The #1049 implementation provides bounded, immutable export from an existing
private `SqliteCaptureStore`. The wire format and trust boundary are specified
by the [portable capture contract](portable-capture-contract.md). This is not
the isolated importer, a CLI command, an MCP transfer implementation, or release
acceptance for the complete portability feature.

## Core entry point

```csharp
var portable = new PortableCaptureUseCases(
    store,
    authorizeExport: AuthorizeWholeCaptureAsync,
    options: new PortableCaptureOptions(),
    timeProvider: TimeProvider.System);

var request = new CaptureExportRequest(
    new PortableOperationKey(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow),
    [new CaptureExportSelection(captureId, "before deployment")]);

PortableExportResult exported =
    await portable.ExportAsync(request, destinationStream, currentAccess, cancellationToken);
```

`AuthorizeWholeCaptureAsync(CaptureInfo, CancellationToken)` is a required trusted
host callback. It must check current policy for every artifact, descendant and
sensitive record represented by the complete capture, not just a summary view.
Core separately applies the existing `CaptureAccess` ownership check. Local
OS-authorized callers can deliberately supply a no-op policy; there is no implicit
authorization fallback in the constructor. Policy runs against bounded source
metadata under its retained lease, before the store reader validates database
bytes, and again before destination bytes are written. No handle is registered.

Selections are IDs in the configured private store, not arbitrary file paths or
SQLite streams. Labels are display-only, can repeat, and never select files.
Repeated source IDs receive separate random entry IDs while sharing one retained
source lease. All distinct sources are leased in sorted ID order. The complete
source manifest, database and seal are copied byte-for-byte; `.lease`, control
files, WAL/SHM, pending metadata and native files are never bundle members.
Unsupported source members cause rejection, not omission.

The current store reader supports source package versions 1 and 2. Their six-axis
descriptors remain unchanged in the index and inner packages. Package 3 remains
unsupported until the import-derived reader work lands. Known typed snapshots,
composition versions and record-stream wrappers are validated through existing
Core codecs. Unknown kinds/versions are rejected, including the synthetic codec
in the independently frozen v1 fixture: package-version compatibility is not an
opaque-payload exemption. Tests also exercise a constructed schema-1 source with
a known codec; that case is not presented as historical old-writer evidence.

Sealed recovered captures retain their recovery aliases and incomplete/unknown
quality. An interrupted source is rejected and is never automatically recovered.
Native paths inside compatible snapshots remain inert provenance.

## Bounded staging and output

The exporter first validates source metadata, record populations, snapshot
compatibility, source seals and SQLite page geometry. It then measures member
hashes/CRC, writes a complete private Stored-only ZIP, and independently scans
its headers and consumed content before writing caller output.

The ZIP helper validates the bounded EOCD before allocating its at-most-50-entry
inventory. It does not instantiate an eager `ZipArchive` reader. Local/central
headers, exact member grammar/order, lengths, CRC, SHA-256, index membership and
the source member hashes must agree. No compression, ZIP64 or data descriptors
are emitted. The bundle seal covers exact index bytes, including labels.

`PortableCaptureOptions` permits only reductions of the contract's entry,
archive/uncompressed-byte, index, row/token and operation-time ceilings. The
existing store's smaller applicable record/snapshot/package limits also apply.
Byte checks run during consumption and writes; declared stream lengths are not
sufficient. Copies use 64 KiB chunks and never materialize the archive in memory.
Record validation uses bounded keyset pages, retaining one snapshot at a time.

The 4 MiB metadata and 32 MiB host buffer/graph budgets use conservative
reservations: 1 MiB of metadata workspace plus eight times retained source
manifest bytes; snapshot decode additionally reserves eight times encoded
snapshot bytes plus 256 bytes per JSON token. These are logical workspace
reservations, not measured RSS. A snapshot below its 8 MiB byte ceiling can still
be rejected by the independent host-memory budget. No payload is truncated to fit.
CPU stack definition/reference checks remain artifact-scoped.

Trusted-reader cancellation/deadlines are cooperative, checked around reader
calls, bounded pages, snapshots and copy chunks. They are not an adversarial
native parser watchdog. Isolated import retains that separate requirement.

Caller streams remain open and need not be seekable. Success is returned only
after the complete archive has been written, verified during copying, flushed,
and its retention receipt finalized. A destination exception or cancellation can
leave a prefix; it never produces a successful result. File-publishing hosts
must use a private sibling output and publish it only after success.
Source leases remain held through output, so ordinary deletion reports `Busy`
rather than racing the copy. Source bytes are not rewritten on failure.

## Receipts, concurrency and cleanup

Private staging lives in `captures/.portable/`, already protected by the managed
capture namespace's generic artifact exclusion. Operation directories are keyed
by a hash of the current owner and operation ID. Receipt fingerprints bind the
ordered selections and labels; the same key with changed input is a conflict.
A completed staged archive can be retried byte-for-byte, with unchanged bundle
and entry IDs, through a fresh use-case instance.

Admission uses the store's existing cross-process control lease. There are at
most two active portable operations per store and one per owner. Contended
control leases fail explicitly with `Busy`; concurrency is not a guarantee that
every simultaneous mutation succeeds. Cleanup failure reports `StorageFailure`
and retains its accounting rather than claiming disk space was released.

The store's quota values and writer queues are unchanged. Its admission scan now
also counts portable reservations so a concurrent ordinary writer cannot ignore
staged bytes. Each operation reserves its exact archive size plus 512 KiB for
bounded receipt replacement (two 256 KiB metadata files). The per-operation
private cap remains 2 GiB; portable admission observes the smaller of the store's
configured byte ceiling and 4 GiB. Export adds no capture and uses no writer slot.

Successful output and output failures retain validated retry bytes for up to
five minutes, within the original one-hour absolute lifetime. Cancellation and
incomplete staging are cleaned up without deleting source captures. Owner-bound
receipts remain until the operation key's 24-hour deadline; at most 64 unexpired
receipts are admitted. Retry after byte expiry reports `TransferExpired`, not a
new export under the same key. Inactive incomplete staging is discarded during
cleanup, including after a process restart.

`CleanupExpiredExportsAsync` is the explicit Core lifecycle hook, also run on
new export admission. Construction does not start a daemon, timer or filesystem
scan. Future transfer hosts must invoke cleanup on startup and periodically
within the contract's 60-second interval; this Core implementation does not
claim that an inactive application performs background cleanup.

## Shared seam and remaining work

`PortableCaptureContracts.cs` carries the approved export/import request/result,
mapping and failure types. `PortableCaptureJson.cs`, `PortableZip.cs`,
`PortableCaptureStorage.cs` and the store's portable admission bridge provide
bounded format/control primitives for the later importer. The ZIP helper is
not a complete untrusted importer; source compatibility validation is explicitly
for trusted store-owned evidence.

`ImportAsync` fails with `UnsupportedFormat/ImportWorkerUnavailable` without
reading input or registering anything. `GetImportResultAsync` currently reports
`NotFound` because this exporter-only store cannot create import receipts; lookup
does not invoke a worker. Isolated admission, package-3 provenance, remapping,
import publication and host transfer remain separate work. No whole-feature
performance or release gate is claimed by the focused exporter tests.

The [isolated worker capability substrate](isolated-import-worker.md) is a
separate internal prerequisite; it does not enable import or change export.
