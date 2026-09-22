# Durable contention capture lifecycle spike results

**Status:** Accepted by independent review for the internal DC2 Linux spike. This is not
approval of a production representation, storage engine, public API, numeric
budget, or release plan.

Claude Opus 5 independently reviewed the GPT-5.6 Sol implementation and the
subsequent canonical-lease-key, age-retention, fresh-store and reopened-view
corrections. The Windows case-alias guard remains unobserved on Linux.

## Scope

The spike exercises the DC1 lifecycle contract with exactly one existing
allowlisted Core artifact: `ContentionSnapshot`. All inputs are constructed
DTOs. The tests launch no process, create no EventPipe session, attach to no
PID, open no database, and use no network.

The implementation exists only under
`tests/DotnetDiagnostics.Core.Tests/DurableCaptureSpike/`. It uses an internal
three-file JSON package:

- `manifest.json` contains stable capture/artifact identity, target provenance,
  version declarations, supported views, retention, quality state, accounting,
  and optional recovery derivation;
- `representations/<ArtifactId>.json` contains the retained
  `ContentionSnapshot`;
- `seal.json` contains member lengths and SHA-256 hashes and is the final
  publication marker.

This layout is a test fixture, not a proposed public continuity format. The
spike has no public MCP or CLI surface and does not select JSON or directory
rename for production.

## Source mapping

The persisted DTO is the production type in
`src/DotnetDiagnostics.Core/Contention/ContentionSnapshot.cs`. Its current
typed query behavior is exercised through
`src/DotnetDiagnostics.Core/Collection/CollectionQueryDispatcher.cs` for
`summary`, `byCallSite`, and `byOwner`.

The fixture preserves every retained DTO field, including nullable owner
thread IDs, timestamps, durations, lock/object IDs, method/module names, and
notes. It does not infer structured quality from note text. In particular,
events whose `LockId` is zero remain present while the recorded
`DistinctMonitors` value remains unchanged. The 200-event fixture and its
longest-retention, monitor-lower-bound, and approximate-percentile notes follow
the bounded collector behavior in
`src/DotnetDiagnostics.Core/Contention/EventPipeContentionCollector.cs`.

Reopened artifacts are registered through the existing
`IDiagnosticHandleStore` as fresh `HandleOrigin.Imported` handles. Stable
`CaptureId` and `ArtifactId` values survive package movement and host-handle
replacement; the relative locator and temporary handle do not become durable
identity. Target provenance includes PID plus available start/runtime/host
facts, but reopen performs no PID lookup or enrichment.

## Lifecycle behavior demonstrated

- Write validates collection counts and string lengths, then charges an
  estimated copy size before serialization. Bounded streams cap representation,
  manifest, and seal writes. Package file count and total bytes are checked
  before publication.
- Normal reopen requires the seal, bounds package/member inputs before JSON
  materialization, validates lengths and hashes, checks contract,
  representation, artifact kind, IDs, and supported views, then calls a
  host-supplied authorization policy before reading the representation.
- Normal reopen is read-only. Tests compare canonical lengths, hashes, and
  modification times before and after reopen.
- A missing seal returns `RecoveryRequired`; reopen does not recover, mutate,
  or reseal automatically.
- Explicit recovery takes an exclusive source lease, creates a new package
  with new stable IDs, records `derivedFromCaptureId`, the recovery reason,
  reader version, and an unknown-tail marker, and verifies that source bytes
  and modification times remain unchanged.
- The configured root, existing package paths, and new move destinations are
  canonicalized through `SafeArtifactPath`. Lease keys use the resulting
  canonical paths and the platform path comparer. Read leases block delete.
  Exclusive writer/recovery/delete leases block readers and other exclusive
  operations. A timed-out writer retains ownership until its underlying task
  actually completes. Cleanup canonicalizes enumerated paths and skips active
  leases even when the configured root is a symlink alias.
- Cleanup enforces age/count/aggregate-byte policy and a finite historical scan
  limit. `MaxAge = null` explicitly disables age-based expiration; a positive
  duration expires old packages while retaining fresh packages. Delete removes
  the whole package after authorization.
- Traversal, absolute locators, and a Linux symlink escape are rejected by the
  authorized-root path checks, including a move destination beneath an escaping
  symlink.

The manifest records estimated-copy, actual representation, and active-batch
bytes. Tests reconcile representation bytes with disk, enforce whole-package
and historical-byte limits, and verify cleanup's remaining-byte accounting.
The finite values are test inputs only; they are not DC3 measurements or
production defaults.

## Acceptance evidence

The focused Linux run contains 22 passing cases and one expected Windows-only
skip, covering:

- representative, empty, zero-lock-ID, and 200-event capped artifacts;
- all three typed query views at multiple `topN` values;
- unsupported view and artifact kind;
- stable IDs, movable locators, a successful reopen through a fresh store
  instance, and fresh imported handles;
- contract/representation versions, identity mismatch, missing members,
  length/hash mismatch, missing seal, and read-only reopen;
- explicit recovery identity, provenance, unknown tail, and source
  immutability;
- read/recovery/delete authorization denial;
- finite event, note, string, estimated-copy, representation, manifest,
  package-byte, file-count, and historical-scan limits;
- accounting, canonical reader/writer lease keys, timeout ownership,
  whole-package deletion, positive-age/count/byte cleanup policy, traversal,
  absolute paths, root aliases, Windows case variants, and symlink escape.

Validation used SDK 10.0.201 through its exact MSBuild entry point:

```text
flock --close <session>/files/ci-validation.lock \
  /home/pedrotravi/.dotnet/dotnet exec \
  /home/pedrotravi/.dotnet/sdk/10.0.201/MSBuild.dll \
  tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj \
  -restore:false -t:Build -p:Configuration=Release -clp:ErrorsOnly

flock --close <session>/files/ci-validation.lock \
  /home/pedrotravi/.dotnet/dotnet exec \
  /home/pedrotravi/.dotnet/sdk/10.0.201/MSBuild.dll \
  tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj \
  -restore:false -t:VSTest -p:Configuration=Release \
  -p:VSTestNoBuild=true \
  -p:VSTestTestCaseFilter=FullyQualifiedName~DurableContentionCaptureSpikeTests \
  -v:minimal
```

Result on Linux: **22 passed, 0 failed, 1 skipped**; the skipped regression is
the Windows-only case-variant lease-key check. The initial no-restore build
found no assets file, so dependencies were restored once from the authorized
`https://packagefeedproxy.microsoft.io/nuget/v3/index.json` source.

## Limits and open decisions

This spike does not prove callback admission, queue pressure, stream/global
quotas, throughput, latency, process-crash durability, system-crash or
power-loss durability, general filesystem behavior beyond the targeted path
regressions, multi-process locking, hostile
untrusted import safety, native-companion publication, database/file
atomicity, or cleanup safety outside its host-controlled fixture root. Its
lease registry is process-local and store-instance-local.

It also does not approve an engine, sync mode, file format, filename scheme,
numeric threshold, retention default, recovery API, MCP/CLI placement, or
which artifact should become the first public MVP. DC3 must define and measure
fair comparison inputs. DC6 and maintainers retain the production and public
API decisions.
