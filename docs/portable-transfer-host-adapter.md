# Portable transfer host adapter

`PortableCaptureUseCases.PrepareExportAsync` stages a trusted ID-selected export
once, retaining its existing portable-operation lease and source leases.
The returned `PortableCaptureTransfer` exposes bounded offset reads and an
immutable length/hash/bundle descriptor, never a filesystem path. Each read
rechecks source availability, ownership and the supplied current-policy callback.

`BeginUpload` reserves the declared archive size within the same private-store
accounting and two/store, one/owner portable-operation admission. It does not
start a validator or the import deadline. `AppendAsync` accepts sequential
bounded buffers and flushes the accepted offset into the operation receipt.
`CommitAsync` verifies the whole archive and hands the same receipt, archive
and reservation into the existing isolated import workflow. No second archive,
ZIP parser, foreign SQLite reader, or additional operation slot is created.

The host owns authenticated transfer identities, chunk hashes and exact replay,
session binding, rate limits, idle/absolute expiry, and cancellation scheduling.
The lease serializes I/O without queuing competing reads/appends. Disposal waits
for I/O to quiesce before releasing the reservation lease and cleaning staging.
An imported capture is never deleted by transfer disposal. Cleanup failure
retains on-disk accounting; it does not free bytes on paper.

Host transfers are not restart capabilities. Startup cleanup expires their
staged export bytes and reconciles interrupted uploads/imports into durable
owner-bound receipts. Existing stream `ExportAsync`/`ImportAsync` callers keep
their prior copy, lifetime, cancellation and retry behavior.

Worker configuration validation is available through
`PortableCaptureImportWorker.Validate`; it validates trusted assets and the
supported platform without launching the worker. Valid configuration does not
claim that kernel isolation or the operating-envelope release gates have passed.

`AdmitTransferCall` shares the fixed 100-calls-per-UTC-second budget across
cooperating hosts of an existing store. It uses a fixed 16-byte counter under
the same cross-process control-file locking primitive, not a new database or
operation slot. Its `.portable-calls` record reserves and accounts 16 bytes
under store admission, but subsequent polling does not contend with publication's
`.admission` lock. An absent store remains absent; malformed control bytes fail
portable admission closed. The host also limits calls before
the first store is created. Rate rejection is `Busy`, never a waiting queue.
