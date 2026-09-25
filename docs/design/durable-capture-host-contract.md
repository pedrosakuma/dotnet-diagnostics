# Durable capture host contract

`DurableCaptureUseCases` wraps an existing asynchronous operation with
`CaptureAsync(name, kind, access, collect, cancellationToken)`. It does not replace
collectors. The default remains ephemeral: construction and DI registration do
not create capture files. Hosts supply `CaptureStoreOptions` and the artifact
root; no capture-specific environment selection or automatic retention runs in Core.

The result preserves the original data, hints, signals, process metadata, handle,
error and cancellation fields, adding `DiagnosticResult<T>.Capture`. Thrown
collector exceptions become structured failures with an interrupted capture.
Cancellation retains an interrupted capture. Persistence failures never return a
successful result. Admission failures before package creation throw a
`CaptureStoreException` and cannot provide a package reference.

Heterogeneous dispatchers can use `CaptureOperationAsync` with a required
`Func<T, DiagnosticResult<object?>>` outcome projection. `DurableCaptureEnvelope.Box`
and `Apply` let a host's existing generic boxing helper preserve concrete result
types without reflection. The returned operation contains the original `Result`,
`HasResult`, and authoritative `Outcome`/`Capture`; never discard outcome failures.

One invocation creates an observation artifact before collection. Scalar fields
retain integer precision, explicit nulls and exact strings; writer bounds reject
oversized records without truncation. Callback paths never serialize snapshots
or perform synchronous filesystem operations. Registration retains at most
`MaxArtifacts` references, deduplicated by handle ID; allowlisted snapshot codecs
run only after the callback completes. Collectors must finish their callbacks
before returning. Snapshot rows are not counted as raw observations. Source loss
reports sum across sessions, including repeated names; any unknown report remains
unknown, and absence is never treated as zero.

Compatibility snapshots carry a separately versioned, bounded metadata wrapper.
`OpenAsync` exposes `RecordStreamAvailable` and `RecordStream` (per-artifact
admission counts and source-name loss). A snapshot alone does not declare a
record stream: a producer observation or explicit source report does. The
`records` view is advertised only when independently retained records exist or
the producer declared its stream, including legitimate zero-event streams.
Use `QueryRecordsAsync`, not a direct reader query, so snapshot-only/undeclared
artifacts reject records instead of returning misleading empty success. Legacy
snapshots without metadata can expose retained records, but not invent an empty
stream. Metadata-only interrupted artifacts can retain stream availability
through recovery without pretending to contain a typed compatibility snapshot.

Hosts use `ListAsync`, `DescribeAsync`, `QueryRecordsAsync`, `DeleteAsync`, and
explicit `RecoverAsync`. Ordinary describe/query/open never recover or mutate an
interrupted package. Recovery produces a separate derived package.

## Reopen and authorization

1. Use `DescribeAsync(captureId, access)` to obtain artifact metadata **without
   registering a handle**. Authorize the original producer and kind with current
   host policy. Missing producer metadata is not a safe authorization default.
   Registered artifacts preserve producing tool, original handle origin and PID;
   returned-only DTOs without that metadata must not invent a producer.
   `DescribeArtifactViewsAsync(captureId, artifactId, access)` validates the
   authoritative offline allowlist without registering a handle. Shape-dependent
   views still require bounded snapshot decoding; known current handles expose
   their already-computed allowlist through `LookupBinding(handle).SupportedViews`.
2. `OpenAsync(captureId, artifactId, access)` decodes only a bounded known
   kind/version. It returns `Handle`, `SupportedViews`, `Capture`, and `Artifact`.
   The fresh handle has `Imported` origin and does not expire when a PID exits.
3. On **every** existing handle query, check `LookupBinding(handle)`, call
   `AuthorizeViewAsync(handle, view, access)`, and reapply current host scopes for
   the original producer/kind/view before invoking the existing dispatcher.
   `AuthorizeHandleAsync` checks ownership/deletion without selecting a view.
   The returned binding's `Artifact` contains the original producer metadata.
   No scope or bearer token from a manifest is authority.

Original producer handles, including child and over-budget registrations, are
also bound. Pending/unsupported bindings fail closed rather than falling back to
ephemeral permissions. This does not invalidate or replace the original handles.
Bindings use weak snapshot keys, so handle eviction does not retain snapshots
indefinitely. Aliases sharing a snapshot have a fixed 1,024-entry ceiling;
unknown aliases after overflow fail closed, while known bindings are retained.
Keep the use-case service singleton with its handle store. Decoding
fully materializes the snapshot while holding the reader lease; it then releases
the lease. Deleting a package does not erase already materialized memory, but
subsequent authorization fails. Authorization and dispatch are separate host
operations, not a transactional lease across arbitrary external dispatch.

Stored dump/native paths and PIDs are provenance only. The allowlist rejects
live-dependent heap/object/root views before dispatch, even for originally live
heap snapshots. Hosts must not bypass this gate or grant file/ptrace access
because a snapshot contains a path.

## Child composition

Wrap each batch/sweep child with
`RunChildAsync(kind, name, collect, cancellationToken)` before its callbacks start.
The outer `CaptureAsync` owns persistence. This helper passes results through
unchanged outside a recording invocation and reports child errors/cancellation
inside one. Successful groups store a separately versioned reference snapshot,
not a reflection-serialized aggregate. `OpenAsync(...).Composition` exposes the
child references and bounded per-child admission/source/error metadata; select a
child artifact for existing typed drilldown. A group handle has no dispatcher
views. Source reports from repeated session names sum within each child; any
unknown child contribution makes total source loss unknown.

Core composed collectors can use
`CaptureRecordingContext.CreateChild(kind, name)` and enter the returned sink
with `CaptureRecordingContext.Enter(child)`. Retain that exact sink to re-enter
around delayed handle registration (for example, GC/activity `BuildSide`).
Never infer child routing from kind or PID: concurrent same-kind/same-PID
collections remain distinct. `ReportCompletion(error, cancelled, data)` can
retain returned-only typed data or record a partial failure.

Child routes and all retained registrations share `MaxArtifacts` bounds.
Per-child source names have a 64-entry/1,024-UTF-8-byte-name bound; rejected source
metadata is explicitly counted and keeps loss unknown. A child failure leaves an
interrupted capture with explicit group completion metadata. Unscoped multiple
or mismatched registrations still fail rather than misattribute observations.
Unknown returned-only DTOs also fail; there is no reflection serializer fallback.

**Recovery limitation:** the current store regenerates artifact IDs while
copying compatibility snapshots. Recovered individual child snapshots work,
but old group references deliberately fail validation rather than guessing by
name/PID or reading the source package. An explicit old-to-new artifact mapping
is required in the storage recovery contract to reopen recovered group wrappers.
