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

One invocation creates an observation artifact before collection. Scalar fields
retain integer precision, explicit nulls and exact strings; writer bounds reject
oversized records without truncation. Callback paths never serialize snapshots
or perform synchronous filesystem operations. Registration retains at most
`MaxArtifacts` references, deduplicated by handle ID; allowlisted snapshot codecs
run only after the callback completes. Collectors must finish their callbacks
before returning. Snapshot rows are not counted as raw observations. Source loss
reports sum across sessions, including repeated names; any unknown report remains
unknown, and absence is never treated as zero.

Hosts use `ListAsync`, `DescribeAsync`, `QueryRecordsAsync`, `DeleteAsync`, and
explicit `RecoverAsync`. Ordinary describe/query/open never recover or mutate an
interrupted package. Recovery produces a separate derived package.

## Reopen and authorization

1. Use `DescribeAsync(captureId, access)` to obtain artifact metadata **without
   registering a handle**. Authorize the original producer and kind with current
   host policy. Missing producer metadata is not a safe authorization default.
   Registered artifacts preserve producing tool, original handle origin and PID;
   returned-only DTOs without that metadata must not invent a producer.
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

## Explicit composition limit

Batch/sweep wrappers require child invocation scopes plus a versioned reference
list; a global invocation scope cannot attribute concurrent observations to the
correct child. Until that contract exists, multiple or mismatched child
registrations produce an **interrupted, unsupported** capture, retaining bounded
children for explicit recovery rather than falsely encoding an aggregate as one
child or claiming completeness. Capture each child independently instead.
Unknown returned-only DTOs likewise fail explicitly; there is no reflection
serializer fallback.
