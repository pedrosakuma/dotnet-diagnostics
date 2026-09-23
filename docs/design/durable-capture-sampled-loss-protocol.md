# DC5 prospective sampled-loss protocol (revision 5)

This document and the [machine-readable contract](durable-capture-sampled-loss-protocol.json)
are prospective. They do not amend revision 3, revision 4, the real-worker
prevalidation addendum, either historical attempt, or component evidence.
The maintainer selected nonintrusive sampling with explicit losses while
preserving exact final checks. No real eight-entry prevalidation, 35-case
comparison, 50,000-offer substitute, or EventPipe workload was executed to
implement this protocol. No feasibility, performance result, or winner is claimed.

The contract inherits the frozen workload matrix, order, one attempt per case,
provider configuration, all durations, offered populations, 95% source floor,
request populations, RSS limits, package/history/suite limits, and A/B
recommendation rule by the three exact input hashes in its `inherits` object.
Only the descriptor-observation contract changes. It does **not** introduce
a storage-engine choice or an automatic product decision.

## Admission rule fixed before observation

Only these positively classified failures are sampled losses:

| Kind | Exact observation |
| --- | --- |
| Native pin disappearance | `DescriptorObservationUnavailable`, operation 1 or 3, native errno 2 (`ENOENT`) |
| Flags disappearance | Operation 2 or 4, `DescriptorFlagsFileNotFoundException` with HResult `-2147024894`, or `DescriptorFlagsDirectoryNotFoundException` with HResult `-2147024893` |
| Positively observed coherence change | Operation 5, structured proof of two valid linked regular-file snapshots with changed identity, flags or target, under the exact same owning process identity |

The descriptor number must be a nonnegative Int32 and the role must be
harness (0), diagnostic (1), or target (2). The exact PID/start-time identity
must still match when admitting a loss and after the descriptor pass.
Process death is a hard failure unless the existing owned termination
handshake explicitly authorized it. This is not a retry mechanism.

Positively observed deleted/unlinked targets are **not** eligible for this
loss classification. Their existing explicit geometry-fixture and verified
runtime-memory classifications remain separate. A first-pin loss has unknown
type, lifetime and bytes: it does not establish that a resource was harmless,
linked, empty, or freed. A linked snapshot followed by disappearance similarly
does not establish what happened after that snapshot.

Coherence loss is the third closed kind selected during prospective design,
before any freeze or actual run, under the maintainer's explicit sampled-loss
policy. It is not a post-observation threshold adjustment. The existing
[descriptor component evidence](../evidence/dc5/descriptor-observer-component.md)
establishes a failure mechanism only; it is not changed or upgraded to readiness.

The observer supplies transient `DescriptorCoherenceProof(Owner, First, Second)`
only in sampled mode, after both snapshot reads succeed. Each snapshot must
have a nonempty absolute target, nonnegative flags, matching native identities,
nonnegative regular-file length and exactly one link. Both paths must satisfy
the existing owned-root or read-only dependency rules; completed-context rules
still apply. A deleted suffix, zero links, unknown/unavailable metadata
(including absent regular-file metadata), hard links, an unknown path, or a
runtime-proof target prevents coherence-loss admission. Neither a code string
nor operation 5 alone supplies proof. Exact PID/start-time/role must match the
proof owner and the live owner at admission, and the process is checked again
after the pass. No runtime-proof, permission, or I/O failure is suppressed.

Generic IOException, EACCES, EIO, other native
errors, invalid contexts, root-enumeration loss, unknown files, runtime-proof
mismatch, ownership loss, process identity loss, and overflow remain hard
failures. An admitted descriptor loss never suppresses another hard error.

There is no loss-rate quality threshold. A threshold would not turn this
non-atomic sample into a census or bound unknown lost bytes. All losses,
including an all-lost candidate population, are reported; no threshold may
be selected after observing results to obtain admission. Workload/source and
exact final inventory gates still apply unchanged.

## Bounded evidence and semantics

Each sweep records fifteen fixed counters: for each role, in role order,
`[enumeratedCandidates, successfullyClassified, nativePinLoss, flagsLoss, coherenceLoss]`.
Counts are observation populations, **not unique descriptor or inode counts**.
Residual candidates are failed/unclassified observations, not successes.
The denominator includes the full bounded enumerated list, including entries
that a hard error prevents visiting. A list exceeding 4,096 entries is a hard
overflow and is not reported as a valid truncated sample.

At most four processes are registered; at most 4,096 candidates per process
are materialized (`Take(4097)` detects overflow before retaining a larger
list). Thus a sweep has at most 16,384 candidates and 2,048 records have at
most 33,554,432 candidates. A source/recovery pair shares the existing record
budget; counters are merged once, not double-counted. The prevalidation
authority owns the whole entry's counters. Three fixed role groups and one
first-loss context replace any per-FD or per-path history. The first hard
descriptor failure remains separate from the first sampled loss.

`Complete` remains false if there is any descriptor loss or hard error.
`sampledAdmissible` is independently true only with the new policy,
fully accounted candidates, no hard error/unclassified resource and no
alarm. Sticky `IsIncomplete`/`TerminalIssue` represent hard incompleteness
on the opted-in monitor; losses remain visible in every raw summary and
the entry totals. Case/coverage `monitoringComplete` is false when that
entry has losses, while `sampledAdmissible` explicitly permits the new gate.
Neither is an instantaneous descriptor-completeness assertion.

The new compact wire schema is `durable-sampled-sweep/1`. Its exact positional
field map and SHA-256 are defined by `SampledLossProtocol.FieldMap` and printed
by `sampled-loss-plan`. Groups are `v` (version), `n` (30 integer scalars),
`g` (gap), `t` (four strings), `f` (active, complete, non-atomic,
sampled-admissible), `l` (15 counters), `x` (first loss) and `z` (first hard
descriptor error). Null context scalars use -1. Strings retain their bounded
ASCII domains; no PID, path or exception message is added.

For operation 5 only, the context's `error` slot is a closed change mask:
1 = identity, 2 = flags, 4 = target; combinations 1 through 7 are valid.
It is **not errno or HResult**. This applies to `x` and `z` in sampled
encoding only. `x` additionally certifies the proof and attribution checks
above; `z` records the observed change but no loss admission. Legacy/native
operations retain their exact error meanings and legacy encoding rejects
operation 5. Only the first fixed context is retained, never snapshot/path
history or a per-FD collection. The unpublished `/1` sampled schemas remain
prospective; the new protocol/map hashes and 15-counter shape reject the
earlier two-kind draft and require fresh receipts/readiness.

The maximum-width fixture is **947 bytes including LF**; the maximal
authority reply with cumulative counters is **1,360 bytes including LF**.
The strict scored golden remains **854 bytes**; strict prevalidation `/3`
retains its 1,017-byte worst case. New opt-in encoding is not represented
as unchanged strict encoding. Legacy inspection never upgrades old records.

All limits remain: 2,048 summaries x 1,024 bytes, 8 MiB stdout and stderr,
2,048-byte authority frames (256 requests), 64 worker-control records, 100 ms
poll delay, maximum 1 s observation gap, 32 descriptor-only / 539 rooted /
571 context identities, and 4,096 suite identities. There are no new files
inside the owned layout: its conservative derivation stays 2,506 identities.
Readiness/review artifacts must be outside the observed campaign roots.
The full-window record derivation remains 1,201 periodic + 64 boundaries =
1,265, and reservations fit the same combined output cap.

Storage results are **non-atomic observed sampled maxima with unknown lost
bytes/types**. They are incomplete/lower-bound coverage of what was charged,
not an instantaneous native aggregate bound (nor necessarily a lower bound
on an instantaneous total). `observationPopulation` on case/coverage/seal
reports exposes candidates, classified observations, losses, and `lossRate`
(`null` for zero candidates); `lostBytesAndTypes` is `unknown-not-zero`.
The campaign seal carries the merged raw counters as well as this population.
On an incomplete campaign this aggregate covers only outcomes carrying a
measurement; missing outcome measurements remain null, not zero-loss evidence.
Only a complete, admitted 35-outcome campaign has the full comparison population.
No sampled maximum is substituted for an exact package inventory.

Normal boundary descriptor samples may have admitted losses. Exact quiescent
root inventories, owned cleanup confirmation, final drain, pre-seal
finalization, manifest/seal publication, ordinary reopen and first query
remain mandatory and unchanged. Root enumeration receives **no** exemption.
Observed package/history/workspace caps still alarm; exact final package,
retained-root, and suite inventory/seal caps still reject excess.

## Artifact and command interface

All commands below are benchmark routes, prefixed with
`dotnet DiagnosedBenchmarks.dll durable-capture-spike`. The two new manifests
use the existing `prevalidation-validate/run/inspect` and
`monitored-validate/run/worker` mechanics; there is no environment-variable
or command-line switch that can silently relax a strict manifest.

| Route | Non-workload effect |
| --- | --- |
| `sampled-loss-plan` | Print policy/protocol/context-map hashes, schemas, environment hash, unchanged eight-entry and 35-case plans and bounds |
| `sampled-loss-campaign-binding --manifest M` | Print the exact prospective campaign binding hash, with only `sampledLoss.readiness` normalized to null |
| `sampled-loss-readiness-validate --manifest M` | Validate the reviewed, sealed eight-entry evidence against M; return the underlying inspection result, not campaign execution authorization |

The closed DTO definitions are in
`tests/DotnetDiagnostics.TestSupport/DurableCounterSpike/Monitored/`:
`PrevalidationProtocol.cs` contains `PrevalidationManifest`,
`PrevalidationAcceptance`, `PrevalidationAdoption`, `PrevalidationAuthorization`;
`MonitoredProtocol.cs` contains `MonitoredRunManifest` and
`MonitoredAuthorizationReceipt`; `SampledLoss.cs` contains
`SampledLossBinding` and `SampledLossReadiness`. JSON uses camelCase.

Create a **fresh** prevalidation manifest with schema
`durable-sampled-prevalidation-manifest/1`. Keep all original required
manifest fields, frozen input hashes, scope `monitor-feasibility-only`,
matrix and bounds. Set `contextSummaryFieldMapSha256` to the new printed map
hash. Add this exact `sampledLoss` object (substitute resolved values):

```json
{
  "policy": "explicit-descriptor-sampling-loss/1",
  "protocolSha256": "<sampled-loss-plan protocolSha256>",
  "contextMapSha256": "<sampled-loss-plan contextSummaryFieldMapSha256>",
  "runtimeEnvironmentSha256": "<same as manifest runtimeEnvironmentSha256>",
  "managedBinaries": [{"path": "<absolute path>", "sha256": "<resolved sha256>"}],
  "readiness": null
}
```

`managedBinaries` must exactly equal the prevalidation manifest's ordered
inventory, including all required DLL/deps/runtimeconfig files in the tool
and sample directories. Source, runtime, tool, sample, native libraries,
runtime proof, host/cgroup/filesystem, clock, environment and fixture are
still checked. The inherited `encoding` and component evidence retain their
strict base schemas and facts: the explicit sampled binding identifies the
effective new wire encoding; it must never rewrite a component feasibility
boolean to true.

New receipts are mandatory:

- Adoption: `durable-sampled-prevalidation-adoption/1`; its existing
  `addendumSha256` field names the **new sampled protocol hash** for this
  schema, not the old addendum.
- Implementation acceptance:
  `durable-sampled-prevalidation-implementation-acceptance/1`; add
  `sampledProtocolSha256` with the new hash. All existing source/binary,
  component/attribution/encoding, layout and review fields remain required;
  its `addendumSha256` still binds the inherited frozen addendum.
- Authorization: `durable-sampled-prevalidation-authorization/1`;
  bind the exact new manifest hash, acceptance hash, suite ID and all
  existing single-suite/eight-entry/single-attempt fields. Its addendum
  hash also remains the inherited one.

Only after a genuinely successful new suite, inspect its exact sealed tree.
Prepare the prospective campaign manifest using schema
`durable-sampled-campaign-manifest/1` and the same `sampledLoss` binding.
The embedded execution plan is still the unchanged revision-4 35-entry
plan: revision 5 changes observation admission, not the workload plan.
Set the complete prospective campaign paths and fields before computing its
binding hash. Source commits and ordered native/managed binary identities,
runtime/tool/sample, fixture, clock, environment and stable host facts must
match the accepted suite. Foreign builds, hosts, manifests, partial suites,
missing probes, failed coverage and unsealed evidence are rejected.

Create a new immutable independent-review artifact using `SampledLossReadiness`:

```json
{
  "schema": "durable-sampled-readiness/1",
  "prevalidationManifest": {"path": "<new immutable manifest>", "sha256": "<hash>"},
  "seal": {"path": "<that suite root>/seal.json", "sha256": "<hash>"},
  "campaignBindingSha256": "<sampled-loss-campaign-binding output>",
  "reviewer": "<independent reviewer identity>",
  "reviewedAt": "<non-default ISO-8601 timestamp>"
}
```

Put its path/hash in the campaign's `sampledLoss.readiness`. This is a review
binding, **not** a `feasibility=true` assertion: admission reopens the exact
sealed inventory, verifies all eight ordered raw coverage populations and
boundaries, cleanup, fixture geometry, immutable controls and authorization.
It also verifies the executing support/tool/runtime/sample and complete
managed inventory. Sealed summaries must use the new wire policy, including
admission and final observations. Only those checks derive accepted coverage.

Finally create the immutable campaign authorization with schema
`durable-sampled-campaign-authorization/1` and all original authorization
fields, binding the final manifest hash. Its `protocolJsonSha256` continues
to identify the inherited plan JSON; the manifest hash additionally binds
the new policy and reviewed readiness. Campaign admission validates both.
The decision engine revalidates sampled readiness rather than accepting a
caller-supplied feasibility boolean. Its result scope is
`sampled-loss-policy/1-conditional-no-product-decision`; the original
performance comparisons and decision populations are otherwise unchanged.

The owner of actual execution must retain fresh immutable artifacts and
authorization. Historical component measurements are not fabricated into
new real-run evidence, and old failures remain incomplete under their
original contract.

## Controlled validation (not readiness)

The preceding two-kind draft's SDK 10.0.201 Release builds of
DiagnosedBenchmarks and Core.Tests succeeded under the shared validation lock.
That draft's sampled-loss selection passed
42/42; all DurableCounterSpike tests then ran once: **322 Passed, 0 Failed,
0 Skipped** (the 280-test baseline plus 42 new cases).

Coverage includes native close at all four phases, strict default invariance,
mixed missing-root plus known-loss precedence, positive unlinked rejection,
process death, closed error classification, denominator/context/overflow
rejection, that draft's 933/1,322-byte widths, all 2,048 persisted records,
scripted source/recovery lifecycle, versioned coverage, legacy inspection,
foreign manifest/build and unsealed readiness rejection, and refusal to enter
the decision engine without the new readiness gate. Synthetic DTO/width
fixtures are not real observation populations or review receipts.

The session folder `sampled-loss-validation-20260923` retains that draft's initial
missing-assets build failure, private-feed restore/build logs, both targeted
TRX/logs (34 initial and 42 final tests), the one-pass full selection, and
strict/new plan-only output. These files, including the 322-case evidence,
remain unchanged.

The coherence follow-up built Core.Tests and DiagnosedBenchmarks in Release
with SDK 10.0.201 under the same shared lock. The final targeted selection
passed **72/72**; all DurableCounterSpike tests then ran **once**:
**352 Passed, 0 Failed, 0 Skipped**. This is the 280-case strict baseline plus
72 sampled-policy cases. The strict plan-only output is byte-for-byte equal
to the preceding output. No real probe suite or campaign was executed.

Controlled tests cover actual `dup2` replacement and `fcntl(F_SETFL)` flag-only
change, child-process sweeps, both known-unlinked snapshots, unknown paths,
runtime-proof targets, missing-root precedence, invalid snapshot proof,
stale PID/start-time identity, injected EACCES/EIO, every role's operation-5
codec and overflow, rejection of the two-kind draft's counters/hashes, strict
default failure, coverage population and readiness/decision-gate rejection.
The new worst-case summary and cumulative authority reply measured **947**
and **1,360 bytes including LF**, respectively; the strict golden is still
**854**. The 2,048-record test also persists the new full-width records.

The first coherence targeted run retained **65 Passed / 1 Failed**: its test
expected change mask 5 for `dup2`, but actual native behavior also clears
`FD_CLOEXEC`, giving mask 7. The assertion now checks both the full mask and
the exact flag delta. No admission rule or threshold changed in response.
Fresh `coherence-*` log/TRX/plan filenames and `coherence-evidence.sha256` in
the same validation folder preserve both this failure and the final results.

| Follow-up identity | SHA-256 |
| --- | --- |
| Protocol JSON | `739b485e257b0b436eb75a9bdefc32ce6f489156a6c847105fb7a7763671f26a` |
| Sampled context field map | `0891ba583daeb66a7347bb7c2ebd33df091892661b1bd9941ec8bd9f71b0ca5c` |
| Final targeted TRX | `0289a662580ccfed54be255148c3d85181e267f2ba15bfceee60114592c6a193` |
| One-pass all-DurableCounterSpike TRX | `5a9224f6a528fef89af89c8dc5f44fbd9f5fe2eff1c2ce45779902b7c8e7b96e` |

These are local controlled implementation results, not published/frozen
contracts, actual-run evidence, independent review, authorization or readiness.
