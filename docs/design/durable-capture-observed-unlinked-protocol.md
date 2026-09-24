# DC5 prospective observed-unlinked accounting protocol (revision 6)

**Prospective only: readiness is unavailable.** This design and its
[machine-readable contract](durable-capture-observed-unlinked-protocol.json)
do not authorize execution. No real eight-entry prevalidation, full 35-case
campaign, 50,000-offer substitute, eight-probe substitute, or EventPipe
workload is part of implementing this change. No feasibility, performance
result, winner, product decision, or third backend is claimed.

The policy is `explicit-observed-unlinked-accounting/1`. It inherits these
exact frozen inputs, without editing their documents, JSON, maps or goldens:

| Input | SHA-256 |
| --- | --- |
| Revision 5 sampled loss | `739b485e257b0b436eb75a9bdefc32ce6f489156a6c847105fb7a7763671f26a` |
| Revision 4 monitored protocol | `e37c44917f738f8d798399258961aa28548b0f4df351111cec047b29c0a50e71` |
| Revision 3 comparison/workload | `faaced68f26a53b7841713d4b1fe1044ce0dd3b277bbac2a9c6f9b6a09f498dd` |
| Real-worker prevalidation addendum | `b825cb4a15b66d6b1ac4a245ffffb22e192f5bcb5625e65087b1cf0b51374bb5` |

All workload populations, order, provider configuration, durations,
one-attempt limits, the 95% source floor, requests, RSS limits,
drain/publication/reopen limits, exact inventories, cleanup and A/B decision
rule remain inherited. This changes a research runner's observation
accounting, not a production API or MCP tool.

## Positive observations, not disappearance assumptions

Revision 5's closed sampling-loss classifications are inherited exactly:
native pin ENOENT, the two mapped fdinfo not-found HResults, and proven
linked-regular coherence changes. Lost bytes and types remain unknown,
never zero. Neither an errno nor a descriptor number establishes that a
lost resource was linked, unlinked, harmless or empty.

The additional classification requires two successful, coherent,
zero-link regular-file snapshots under the exact still-live owning
PID/start-time/role identity. Stable native identity, target and flags
must be established. Charge the maximum of the observed lengths, once
per native identity in a sweep, not once per FD alias. Positive unlinked
classification is measured storage, not a newly excused sampling loss.
Both lengths must be valid and nonnegative, but **need not be equal**:
ordinary linked writing and indexed temporary growth are not restricted
to stable sizes. This is a non-atomic observed size, not an instantaneous
bound. A link-count transition remains a hard coherence error, even if
target strings happen to match. Inherited operation-5 loss admission
still requires two positively linked snapshots.

Known owned paths retain their package/recovery/history/workspace classification.
An otherwise unknown native path can receive conservative current-context
native-temporary accounting only through the writable diagnostic-owner
rule, with a valid absolute native-filesystem target and the full two-snapshot
proof. The role is `active-native-temporary`; neither a filename prefix
nor a temporary-directory allowlist is involved. This is attribution to
the active diagnostic context, not proof of creator or path ownership.
Unknown read-only descriptors, unknown paths owned by harness/target
processes, invalid/proc/anon/memfd targets and dead/reused owners remain
hard failures. The inherited geometry fixture and verified runtime
classifications remain separate and are not blanket exemptions.
This is **not attribution to SQLite**. The historical failing FD's
backend, provenance and bytes remain unknown; a later controlled
observation does not identify a historical descriptor.

Runtime-only exclusions remain conditional on the existing explicit
verified proof; observed-unlinked accounting cannot bypass that proof.
Hard links, unavailable metadata, unproven completed-context ownership,
process identity loss, permission/I/O failures, unclassified files and
overflow remain hard failures. No generic temporary directory, engine,
filename or FD-number exemption is introduced. Root enumeration never
receives a descriptor-loss exemption.

`complete` remains false with sampled loss or hard errors.
`sampledAdmissible` requires fully accounted candidates and no hard error
or alarm. It is not a feasibility assertion and cannot replace any source,
workload, cleanup or final inventory gate.

## Measurement and shared storage budgets

The existing `SampledLossBinding` is reused in `manifest.sampledLoss`, with
the new explicit `policy`, new protocol hash and new map hash. It must not
be interpreted merely as “sampledLoss is non-null.”

Every new-policy `SampledLossMeasurement` contains:

```json
{
  "counts": ["the same fifteen inherited integer counters"],
  "firstLoss": null,
  "observedUnlinked": {
    "identities": 0,
    "nativeTemporaryIdentities": 0,
    "nativeTemporaryBytes": 0
  }
}
```

The illustrative `counts` placeholder above is not executable JSON
evidence: actual `counts` is precisely fifteen bounded integers.
`firstLoss` retains its inherited null-or-four-integer shape and meanings.
`observedUnlinked` must be non-null even when all values are zero, and
must be absent under strict and revision-5 policies. A missing measurement
in a not-run or failed outcome is not zero-loss evidence.

Identity counts describe per-sweep classified identities; counts summed
across sweeps are repeated observation populations, not a lifetime census.
The identity ceiling is 4,096 per sweep and 8,388,608 across 2,048 records.
`nativeTemporaryIdentities` is a subset of `identities`.
`nativeTemporaryBytes` is a nonnegative Int64 scalar; across observations,
its retained byte value is a maximum, not the sum of repeated samples.
Source/recovery merging and authority aggregation must count each
authoritative observation only once.

Native temporary bytes partition `workspaceBytes`: they are already
included once in `observedSweepBytes`, context and suite accounting.
They are additionally charged to the **same shared 268,435,456-byte
package-plus-recovery gate**:

```text
packageBytes + recoveryBytes + nativeTemporaryBytes <= 268435456
```

There is no additional native-temporary allowance. Overflow-safe checks
apply to worker boundary releases, campaign/recovery monitoring,
prevalidation authority replies and successful raw-evidence inspection.
Exact quiescent package/source/recovery inventories remain independently
mandatory; boundary monitoring charges descriptor-only native bytes before
the worker receives permission to continue finalization. Do not add these
bytes a second time to workspace or the 3,221,225,472-byte suite total.

Results remain non-atomic observed sampled maxima. They are not an
instantaneous aggregate bound, a descriptor census or an upper bound on
unknown lost bytes.

## Versioned wire and unchanged bounds

The summary schema is `durable-observed-unlinked-sweep/1`, compact wire
`v=2`. It preserves all revision-5 groups (`n`, `g`, `t`, `f`, `l`, `x`,
`z`) and adds:

```text
a=[classifiedUnlinkedIdentities,nativeTemporaryIdentities,nativeTemporaryBytes]
```

`ObservedUnlinkedProtocol.FieldMap` is exactly the frozen
`SampledLossProtocol.FieldMap` with `v=1` replaced by `v=2`, followed by:

```text
;a=[classifiedUnlinkedIdentities,nativeTemporaryIdentities,nativeTemporaryBytes];unlinked=two-coherent-zero-link-regular-snapshots,exact-live-owner,max-observed-length;native-temporary=unknown-native-path,writable-diagnostic,conservative-context-attribution;native-temporary-bytes=workspace-partition-and-shared-package-recovery-budget
```

The SHA-256 is over this exact UTF-8 string, without a trailing newline.
Use `observed-unlinked-plan` to obtain the authoritative string/hash and
the encoded maximal scalar/context-width fixture. The fixture is a width
proof, not an admissible storage observation or real-run evidence.

The 1,024-byte LF-inclusive summary cap, 2,048 summary records,
2,048-byte authority frames, 256 authority requests, 64 worker controls,
8 MiB stdout/stderr caps, 100 ms polling and 1 s maximum gap are unchanged.
So are the 32 descriptor-only, 539 rooted, 571 context and 4,096 suite
identity ceilings and the 2,506-identity conservative layout derivation.
The strict 854-byte golden and revision-5 947-byte width fixture remain
unchanged. New widths must fit inherited caps; no cap may grow to fit an
implementation.

The valid all-group width fixture is **982 bytes including LF**. Replacing
each of the fifteen counters with its independent 16,384 upper bound
(deliberately ignoring joint population constraints) gives a conservative
**997-byte** bound. All other integer, context, token and floating-point
slots already have maximal scalar widths. The cumulative authority
fixture is **1,513 bytes including LF**. These are component encoding
facts, not workload evidence. Independently widening every authority
counter to its entry maximum and every summary counter to its sweep
maximum gives a conservative **1,542-byte** authority-frame bound, still
below 2,048 bytes. Neither width proof increases the admitted populations.

## Metadata routes and artifact creation guide

These benchmark harness commands are metadata/inspection routes, not
execution authorization. Prefix every route below with
`dotnet benchmarks/DiagnosedBenchmarks/bin/Release/net10.0/DiagnosedBenchmarks.dll durable-capture-spike`
from the worktree root:

| Command | Output or validation |
| --- | --- |
| `observed-unlinked-plan` | New and inherited hashes, all new schemas, exact map/hash, current runtime-environment hash, unchanged eight-entry/full-35 plans and bounds, encoded maximal width, `readinessAvailable=false`, `campaignAdmissionGranted=false` |
| `observed-unlinked-campaign-binding --manifest M` | Exact prospective campaign binding, normalizing only `sampledLoss.readiness` to null; rejects a foreign policy/schema or altered 35-entry plan |
| `observed-unlinked-readiness-validate --manifest M` | Validates independently reviewed, sealed all-eight evidence against M; does not authorize a campaign |
| `prevalidation-inspect --manifest P` | Inspects the actual report/control/raw-summary/seal tree; rejects a policy/measurement mismatch even in available failed/partial summaries |

The sampled-loss metadata/readiness routes remain revision-5-only where
they validate policy. `SampledLossProtocol.ValidateBinding` is unchanged;
dispatch to the new validator is explicit. Legacy evidence is never
upgraded by inspection.

### 1. Fresh prevalidation manifest after committed-source build

Resolve source identities only after the prospective implementation is
committed and its actual Release binaries have been built. Fresh receipts,
component artifacts, manifests and independent review must describe those
exact bytes. Do not copy historical successful flags, acceptances or
authorization. Preparing code or printing metadata supplies none of these
receipts.

The new policy requires the executing support/tool binaries to remain
under their clean Git worktree. `runnerCommit` and `monitorCommit` must
both equal its `HEAD`, and both assemblies' informational versions must
end in `+<HEAD>`. Rebuild after committing; a pre-commit build or a
dirty/untracked source tree cannot qualify. `protocolCommit`,
`pipelineCommit` and `adapterCommit` must resolve to ancestors of that
revision. Do not override the SDK's source-revision version stamping.
Read-only Git checks are bounded to five seconds and 4,096 characters
per stream per query. This new provenance gate does not change legacy
admission. Binary hashes and the complete executing inventory are still
checked independently; commit labels do not replace byte identities.

Use schema `durable-observed-unlinked-prevalidation-manifest/1`. Retain all
required fields:

`schema`, `scope`, `suiteId`, `privateRoot`, `addendumCommit`,
`addendumSha256`, `protocolSha256`, `probes`, `bounds`, `sourceCommits`,
`runtimeBinary`, `toolBinary`, `sampleBinary`, `managedBinaries`,
`nativeBinaries`, `fixtureManifest`, `host`, `clock`, `attribution`,
`encoding`, `componentEvidence`, `adoption`, `implementationAcceptance`,
`historicalReport`, `authorizationReceipt`, `runtimeEnvironmentSha256`,
`contextSummaryFieldMapSha256`, and `sampledLoss`.

Scope stays `monitor-feasibility-only`; the top-level `protocolSha256`
continues to name frozen revision 4, and `addendumCommit`/`addendumSha256`
continue to name the frozen prevalidation addendum. Do not substitute
revision 6 into those inherited fields. Set
`contextSummaryFieldMapSha256` to the new metadata map hash and supply:

```json
{
  "policy": "explicit-observed-unlinked-accounting/1",
  "protocolSha256": "<observed-unlinked-plan protocolSha256>",
  "contextMapSha256": "<observed-unlinked-plan contextSummaryFieldMapSha256>",
  "runtimeEnvironmentSha256": "<same as top-level runtimeEnvironmentSha256>",
  "managedBinaries": [{"path": "<absolute path>", "sha256": "<resolved SHA-256>"}],
  "readiness": null
}
```

All file identities use the exact `{path, sha256}` shape. The ordered
`managedBinaries` inventory must equal the manifest inventory and cover
the tool/sample DLL, deps and runtimeconfig files. Source identities use
`protocolCommit`, `pipelineCommit`, `adapterCommit`, `runnerCommit`,
`monitorCommit`. Runtime/tool/sample/native identities, runtime proof,
host/cgroup/filesystem, clock, environment and fixture checks remain
required. The environment hash retains the existing DOTNET/COMPlus/CORECLR,
loader, locale and temporary-location selection; no environment or source
configuration change is authorized.

The inherited encoding and component artifacts keep their base schemas
and observed facts. The new binding declares the effective compact wire;
never turn a component feasibility boolean into readiness.

### 2. Fresh adoption, acceptance and one-suite authorization

The new schemas deliberately cannot reuse revision-5 receipts:

- **Adoption:** `durable-observed-unlinked-prevalidation-adoption/1`.
  Exact fields: `schema`, `addendumSha256`, `maintainer`, `adoptedAt`,
  `scope`. For this schema only, `addendumSha256` names the **new revision-6
  protocol hash**, not the inherited addendum.
- **Implementation acceptance:**
  `durable-observed-unlinked-prevalidation-implementation-acceptance/1`.
  Fields: `schema`, `scope`, `addendumSha256`, `reviewer`, `sourceCommits`,
  `binaryInventorySha256`, `componentEvidenceSha256`, `attributionSha256`,
  `encodingSha256`, `derivedSuiteIdentities`, `callPathsReviewed`,
  `twoLevelAccountingReviewed`, `cleanupReviewed`,
  `negativeAdmissionsReviewed`, `sampledProtocolSha256`.
  Here `addendumSha256` still binds the old addendum; the existing
  `sampledProtocolSha256` field binds the **new protocol hash**. The binary
  hash is `PrevalidationProtocol.BinaryInventoryHash`, not an independently
  reformatted inventory. The derived identity count stays 2,506.
- **Prevalidation authorization:**
  `durable-observed-unlinked-prevalidation-authorization/1`.
  Fields: `schema`, `scope`, `suiteId`, `manifestSha256`, `addendumSha256`,
  `acceptanceSha256`, `historicalReportSha256`, `authorizer`, `authorizedAt`,
  `entries`, `attempts`, `singleSuiteOnly`. Bind the actual new manifest
  and acceptance, inherited addendum/history hashes, eight entries, one
  attempt, and one suite only.

These are instructions for a separately authorized future owner, not
receipts or permission produced by this implementation.

### 3. Actual sealed all-eight coverage and exact full-35 readiness

Only after separately authorized, genuinely successful prevalidation may
an independent reviewer create readiness. A coverage-shaped DTO, a
component fixture, a partial report, an unsealed tree or a boolean is
insufficient. Inspection must reopen the exact seal inventory, match all
eight ordered raw coverage populations/boundaries, fixture geometry,
cleanup, immutable controls and authorization. Admission/final/context
summaries must carry the new measurement, including zero populations.

Prepare the full campaign manifest using
`durable-observed-unlinked-campaign-manifest/1`. All original campaign
fields remain: `campaignId`, unchanged revision-4 `plan`, fixture and
Q1/Q2 input/oracle hashes, source and binary identities, `host`, `clock`,
attribution/encoding/component paths and hashes, evidence/history/workspace/
output roots, `authorizationReceipt`, and the new `sampledLoss` binding.
No subset or altered order of the 35-entry plan is accepted.

Source commits, ordered managed/native inventories, runtime/tool/sample
bytes, runtime environment, stable host facts, clock and fixture hash must
match the accepted suite. Current binaries and executing support/tool/
runtime/sample inventory are rechecked, as are the current environment
and host. Set every campaign field/path before computing its binding:

```json
{
  "schema": "durable-observed-unlinked-readiness/1",
  "prevalidationManifest": {"path": "<immutable new P>", "sha256": "<P hash>"},
  "seal": {"path": "<P.privateRoot>/seal.json", "sha256": "<seal hash>"},
  "campaignBindingSha256": "<observed-unlinked-campaign-binding output>",
  "reviewer": "<independent reviewer identity>",
  "reviewedAt": "<non-default ISO-8601 timestamp>"
}
```

This reuses `SampledLossReadiness`'s fields, with the new closed schema.
Store the immutable artifact outside owned observation roots and put its
path/hash in campaign `sampledLoss.readiness`. Changing any other campaign
field invalidates the binding. The validator returns the underlying
inspection result with `campaignAdmissionGranted=false`; review is still
not execution authorization.

### 4. Separate final campaign authorization

Use `durable-observed-unlinked-campaign-authorization/1`. Exact fields:
`schema`, `campaignId`, `manifestSha256`, `protocolJsonSha256`,
`runnerCommit`, `monitorCommit`, `independentReviewer`, `authorizedAt`,
`executionConditionallyAuthorized`, `runnerReadinessApproved`,
`singleCampaignOnly`.

Bind the final manifest including readiness. `protocolJsonSha256` still
names the inherited plan JSON; the manifest separately binds revision 6.
The existing approval flags cannot bypass real readiness validation.
The decision engine revalidates readiness and measurement-policy matching;
its scope is
`observed-unlinked-policy/1-conditional-no-product-decision`. Original
performance comparisons and decision populations remain unchanged.

All artifacts must be fresh, immutable, single-link files with resolved
hashes and real reviewer/owner identities. Nothing in this document claims
they exist. Build/component/admission validation, when separately allowed,
must be reported as such rather than called eight-entry readiness.
