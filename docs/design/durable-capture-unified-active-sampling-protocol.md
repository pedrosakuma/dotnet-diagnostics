# DC5 prospective unified active sampling (revision 7)

**Implementation is not admission or readiness.** The maintainer adopted
active observation sampling with explicit losses and exact final inventories
after disclosure of expanded unknown coverage and the absence of an
instantaneous storage bound. The [JSON contract](durable-capture-unified-active-sampling-protocol.json)
introduces policy `explicit-unified-active-sampling/1`, not a backend,
production API, MCP tool, retry mechanism, or permission to run a campaign.

The exact inherited revision-6 hash is
`72e057c9f7f7125c6d176d5ae2ea2c62767f76b6d459917ddb3538f6d96f349f`.
The JSON also binds unchanged revision 5, revision 4, revision 3 and the
prevalidation addendum. Their files, field maps, goldens and semantics are
not amended. Use `unified-active-plan` below for the new exact JSON and map
hashes. New schemas, both hashes and an explicit policy are required;
`sampledLoss != null` alone never enables root losses.

## Historical evidence stays historical

[#1032's report](../evidence/dc5/prevalidation-observed-6a0916e.md) remains
partial and unsealed. Actual A O1 had 50,000 offers, 49,939 commits, 61
rejections, one descriptor loss and one positively observed unlinked native
temporary with zero measured bytes. That measured zero is not the unknown
size of its separate loss. Actual B failed before pre-seal finalization on
`PathObservationFileNotFoundException`; its missing pathname is unknown.
The controlled DELETE-journal proof does not identify that historical file.
Neither result is upgraded by this policy or inspection.

## One observation model, explicit namespace scope

The same v7 traversal is used for periodic and boundary sweeps. It reports
all charged roots, including strict regions; it does not hide mutation or
omit stages. Within an explicitly active current context:

* A known mutable file candidate that cannot be pinned because of native
  `ENOENT` is a lost file observation. Native error 2 is positively mapped
  to `FileNotFoundException`, HResult `-2147024894`. The exact managed
  `FileNotFoundException` with that HResult and `DirectoryNotFoundException`
  with HResult `-2147024893` are the only managed file-loss classes.
* A previously enumerated mutable directory branch whose enumeration
  reports exact `DirectoryNotFoundException`/`-2147024893` is a lost
  directory branch. Its remaining descendant population is **unknown**.
  Files never enumerated are not invented as candidates.
* Permission, generic I/O, other native errors, invalid metadata, symbolic
  and hard links, foreign roots, root-anchor loss, process identity loss and
  overflow remain hard errors. Root and descriptor errors are collected
  independently; a sampled loss cannot suppress either or a budget alarm.

The mutable directory scope is the current execution's owned workspace
(package-staging and recovery-staging), not the campaign's whole workspace.
Prevalidation uses its isolated current context workspace. At recovery
handoff, source `package-staging` is strict even while the recovery staging
tree is active. Declared root anchors remain strict.

The fixed mutable file scope additionally includes current worker and
recovery-worker `*-monitor.jsonl`, `*-stdout.jsonl` and `*-stderr.log`.
The coordinator also declares only the current context's
`coordinator-monitor.jsonl`, `harness-stdout.log`, `harness-stderr.log` and
`ownership.jsonl`. These are existing owned stream contracts, not exemptions
for arbitrary filenames. No extra files are created by observation.
Control JSON, source and published packages, all retained history,
synthetic inventory fixtures, geometry fixtures and earlier contexts are
strict. A read-only candidate is strict even inside a mutable workspace.
A directory containing controls does not become a mutable branch merely
because a declared stream is inside it.

| Consumer / stage | Root observation contract |
| --- | --- |
| Coordinator admission and control copying | Exact controls; no mutable directory scope |
| Fixture preparation and `fixtures-complete` | Current workspace/declared streams sampled while active; synthetic retained fixtures always strict and separately counted exactly |
| Worker preparation, admission, writing, drain, pre-seal finalization, manifest/seal publication | Active sampling in the same current mutable scope; no backend mutation coordination or timing change |
| Live warmup with a durable adapter | Explicitly active in v7 because the adapter/pipeline already exists; baseline without durable storage remains inactive |
| `pre-kill-*` | Active sample, not quiescent inventory |
| Recovery preparation / `before-explicit-recovery` / `after-explicit-recovery` | Recovery mutable namespace sampled; killed source strict; before/after `HashTree` remains exact |
| `post-kill-quiescent-root-inventory`, worker/harness exit, owned cleanup, suite final enumeration | Exact roots, no root-loss admission |
| Ordinary reopen / first query, geometry's 539-rooted observation | Exact roots even if a caller incorrectly labels the boundary active |
| `ExactQuiescentBytes`, `HashTree`, immutable-package validation, retained/suite inventory, seal validation, freeze and owned cleanup | Original strict helpers and caps; no sampling overload or not-found exemption |
| Component proof inventories and binary inventories | Original strict consumers; not v7 workload observations |

False `activeStorageStage` always disables root-loss admission. Known exact
boundary names also enforce exact roots independently of that boolean.
Descriptor sampling remains the inherited fifteen-counter contract; root
quiescence is not a claim of an instantaneous descriptor census. Evidence
streams can still append while a storage namespace is quiescent; no
missing file or branch is admitted at an exact boundary.

Root file pins use `O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW`, followed
by native metadata validation. This retains read-permission failures, avoids
blocking on a replacement FIFO, and rejects leaf symlinks. Ancestor links
are checked as well. Atomic replacement may yield a different valid native
identity: enumeration of a pathname never promised the previous inode.
Positive observations use the existing dev/inode ledger and maximum native
observed length across root/FD aliases. The inherited two-snapshot
observed-unlinked rules remain separate from missing-path losses.

## Populations, completeness and boundedness

Every v7 `SampledLossMeasurement` includes non-null `observedUnlinked` and:

```json
"rootSampling": {
  "knownFileCandidates": 0,
  "observedFiles": 0,
  "lostFilePaths": 0,
  "lostDirectoryBranches": 0
}
```

These are repeated per-sweep observation populations, not unique file or
inode counts. `knownFileCandidates` counts enumerated file entries;
`observedFiles` counts successful attributed native observations before
identity deduplication. Hard-failed residual candidates are not successes.
Lost branches contribute no fabricated number to the file denominator.

Reports expose these populations under
`observationPopulation.rootSampling`, with `namespaceCoverage="partial"`
when root loss or residual candidates exist,
`unknownBranchDescendants="unknown-not-zero"` when a branch was lost, and
`lostBytesAndTypes="unknown-not-zero"`. A loss-free sample is labeled
`observed-without-loss-not-a-census`, not a filesystem census.
The existing outer candidate/loss/rate fields remain **descriptor-only**;
there is intentionally no combined percentage pretending unknown branch
descendants are known.

`Complete` / `monitoringComplete` are false for any root or descriptor
loss or hard failure. `sampledAdmissible` additionally requires fully
accounted known file and descriptor candidates, no hard failure and no
alarm. An entirely lost active pass can be sampled-admissible without a
quality threshold, but cannot be complete or establish readiness alone.
Positive measured byte subtotals may be zero; that never means missing
content was empty, freed or measured. All storage maxima remain non-atomic
observed maxima with unknown lost bytes/types, **not an instantaneous
aggregate bound**.

The iterative traversal bounds **at insertion** both known file candidates
and directory work to 4,096 per sweep, across all charged roots. Overlapping
root declarations are traversed once, while every root anchor is checked.
UTF-8 paths are capped at 4,096 bytes, so neither the pending stack nor
depth/path growth is unbounded. A cap is a hard error, not a truncated
successful sample. Partial positive observations and classified losses
survive independently of subsequent hard errors. No pathname, metadata,
inode sample or exception-message history is added.

Each root scalar is at most 8,388,608 per 2,048-record entry; the campaign
aggregate is at most 293,601,280 across 35 entries. Source/recovery merge
uses the shared entry allowance, once per raw record. Prevalidation's
single coordinator authority owns the entry totals, not an additional
harness aggregation. `FirstLoss` remains descriptor-only;
`FirstDescriptorFailure` and `FirstErrorCode` retain their hard-error
meanings. No root counter claims to know a missing filename.

The shared package + recovery + native-temporary cap is still 268,435,456
bytes; suite workspace is 3,221,225,472 bytes. The 539 rooted / 571 context /
32 descriptor-only / 4,096 suite identity limits retain observed semantics.
Retained history, exact final inventories, the conservative 2,506-identity
layout, source/rate/workload gates and cleanup are unchanged.

## Wire and width proof

Schema `durable-unified-active-sweep/1` is compact `v=3`. All v6 groups are
retained and `q` is exactly:

```text
q=[knownFileCandidates,observedFiles,lostFilePaths,lostDirectoryBranches]
```

`UnifiedActiveProtocol.FieldMap` replaces only the v6 map's leading
`v=2;` with `v=3;`, then appends the explicit root map/semantics. The map
hash is over that exact UTF-8 string without LF. Old maps are unchanged.
V1/V2 cannot carry `q`; V3 cannot omit/null it or its inherited `a`.
Wrong shapes, negative/oversize counters, invalid denominators, scalar
overflow, policy downgrade, false completeness/admission and root loss
at an exact boundary fail with structured errors.

The valid maximal summary fixture is **1,007 bytes including LF**.
Independent maximum counters plus maximum allowable scalar/context,
token, boolean and floating-point widths give a conservative **1,023-byte**
upper bound. The valid cumulative authority fixture is **1,665 bytes
including LF**. Independent widening of all cumulative root and descriptor
counters gives **1,695 bytes**, below the 2,048-byte authority cap. Width-only
independent maxima may violate joint population constraints; they are not
admissible observations or workload evidence.

Unchanged limits: 1,024 UTF-8 bytes including LF per summary, 2,048 summaries,
2,048 UTF-8 bytes including LF per authority frame, 256 authority requests,
64 worker controls, 8 MiB stdout/stderr, 100 ms polling, 1 s maximum gap.
No retry-until-clean or post-result threshold is introduced.

## Fresh artifact guide

All routes below are benchmark metadata/inspection routes. Prefix them with:

```sh
dotnet benchmarks/DiagnosedBenchmarks/bin/Release/net10.0/DiagnosedBenchmarks.dll durable-capture-spike
```

| Route | Effect |
| --- | --- |
| `unified-active-plan` | New/inherited hashes, exact field map/hash, schema values, environment hash, unchanged eight-entry and 35-case plans, bounds and maximal summary fixture; readiness/admission false |
| `unified-active-campaign-binding --manifest M` | Validate the explicit v7 policy/schema and unchanged plan, then hash M normalizing only `sampledLoss.readiness` to null |
| `unified-active-readiness-validate --manifest M` | Reopen and validate fresh reviewed all-eight sealed evidence, including exact build/host/environment/fixture binding; not execution authorization |
| `prevalidation-validate --repository-root R --manifest P` | Existing admission machinery with v7-specific receipts |
| `prevalidation-inspect --manifest P` | Inspect raw summaries, coverage, controls and exact sealed inventory under P's actual policy, never upgrade old results |

### 1. Committed-source binary provenance first

An uncommitted implementation cannot qualify. After a separately authorized
commit, build the exact committed source with the pinned SDK. Inherited
v6 provenance requires a clean underlying Git tree, `runnerCommit` and
`monitorCommit` equal to HEAD, both tool/support informational versions
ending in `+HEAD`, and protocol/pipeline/adapter ancestors. Exact runtime,
managed and native bytes, sample, environment, clock and host/cgroup/
filesystem identities remain independently required. No source-version
override or copied historical success flag is valid.

### 2. Fresh prevalidation manifest and receipts

Use `schema="durable-unified-active-prevalidation-manifest/1"` and
`scope="monitor-feasibility-only"`. Keep the original required fields:
`suiteId`, `privateRoot`, `addendumCommit`, `addendumSha256`,
`protocolSha256`, `probes`, `bounds`, `sourceCommits`, `runtimeBinary`,
`toolBinary`, `sampleBinary`, `managedBinaries`, `nativeBinaries`,
`fixtureManifest`, `host`, `clock`, `attribution`, `encoding`,
`componentEvidence`, `adoption`, `implementationAcceptance`,
`historicalReport`, `authorizationReceipt`, `runtimeEnvironmentSha256`,
`contextSummaryFieldMapSha256`, `sampledLoss`.

Top-level `protocolSha256` remains revision 4; addendum fields remain the
frozen addendum. Set `contextSummaryFieldMapSha256` from the new plan.
The new binding is:

```json
{
  "policy": "explicit-unified-active-sampling/1",
  "protocolSha256": "<unified-active-plan protocolSha256>",
  "contextMapSha256": "<unified-active-plan contextSummaryFieldMapSha256>",
  "runtimeEnvironmentSha256": "<same as manifest and current plan>",
  "managedBinaries": [{"path": "<absolute path>", "sha256": "<exact bytes>"}],
  "readiness": null
}
```

Use exact ordered manifest inventory, not only the illustrative single
entry. Artifact identities are always `{path, sha256}`. The source object
has `protocolCommit`, `pipelineCommit`, `adapterCommit`, `runnerCommit`,
`monitorCommit`. Inherited attribution, encoding and component artifacts
keep their original schemas/facts; do not rewrite feasibility booleans.

| Receipt | `schema` | Hash semantics |
| --- | --- | --- |
| Adoption | `durable-unified-active-prevalidation-adoption/1` | Existing `addendumSha256` slot binds **new v7 protocol hash**; scope, maintainer, adoptedAt required |
| Implementation acceptance | `durable-unified-active-prevalidation-implementation-acceptance/1` | `sampledProtocolSha256` binds v7; `addendumSha256` still inherited; exact source/binary inventory, component/attribution/encoding hashes, layout bound, reviewer and all four reviewed booleans required |
| One-suite authorization | `durable-unified-active-prevalidation-authorization/1` | Exact fresh manifest and acceptance hashes, suite ID, inherited addendum/history hashes, authorizer/time, entries=8, attempts=1, singleSuiteOnly=true |

No receipt is created by printing metadata. Keep all inputs immutable and
outside owned observation roots as required by existing admission.

### 3. Only actual sealed all-eight evidence can enable the 35-case gate

Use campaign `schema="durable-unified-active-campaign-manifest/1"` with
the same binding and unchanged revision-4 plan. Complete its actual paths,
source/binary/environment/host/clock/fixture fields before requesting
`unified-active-campaign-binding`. After actual successful prevalidation,
independent review supplies:

```json
{
  "schema": "durable-unified-active-readiness/1",
  "prevalidationManifest": {"path": "<immutable P>", "sha256": "<P hash>"},
  "seal": {"path": "<P privateRoot>/seal.json", "sha256": "<exact seal hash>"},
  "campaignBindingSha256": "<binding route output>",
  "reviewer": "<independent reviewer>",
  "reviewedAt": "<non-default ISO timestamp>"
}
```

Put this artifact's identity into campaign `sampledLoss.readiness`.
The validator independently reopens the exact seal/inventory, all eight
ordered raw coverages, cleanup, geometry, controls and receipts; a partial
report, DTO-only fixture or changed seal cannot qualify. Its current source,
binary and environment gate remains active at decision time.

The final campaign authorization schema is
`durable-unified-active-campaign-authorization/1`, with all original
authorization fields binding the final manifest hash. Its
`protocolJsonSha256` still names the inherited plan; the final manifest
additionally binds v7 and reviewed readiness. A decision revalidates
readiness and labels its scope
`unified-active-policy/1-conditional-no-product-decision`.

No real eight-probe suite, 50,000-offer substitute, scored campaign or
EventPipe workload is needed or authorized to implement this revision.
Tiny deterministic root/descriptor/transaction fixtures and scripted
component recovery are implementation evidence only.
