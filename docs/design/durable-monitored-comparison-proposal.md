# DC5: prospective monitored-storage comparison proposal

**Status:** Draft for independent review and explicit maintainer adoption.
The maintainer authorized preparing this proposal, not executing a campaign.
Tracking: #1003 under #999; production decision #1006.

## Purpose and immutable baseline

Compare A (direct SQLite) with B (append-first plus a pre-seal SQLite index)
without first implementing a complete native filesystem reservation layer.
This deliberately weakens one experimental prerequisite: proof of aggregate
point-of-growth storage enforcement. It must not be described as equivalent
protection, a manifest-only clarification, or compliance with revision 3.

The accepted [revision 3 protocol](durable-capture-comparison-protocol.md) and
its JSON remain unchanged. The baseline JSON SHA-256 is
`faaced68f26a53b7841713d4b1fe1044ce0dd3b277bbac2a9c6f9b6a09f498dd`.
The [proposal companion](durable-monitored-comparison-proposal.json) is a
declarative review overlay, not a resolved run manifest or executable protocol.
An adopted successor must have its own revision, hash, campaign identity and
validator. Existing revision-3 validation must not silently accept this overlay.

No scored campaign has run. Component development and its fault-injection
tests are not comparative measurements and cannot be relabeled campaign data.

## Exact change in the experimental claim

| Area | Revision 3 | Proposed successor |
| --- | --- | --- |
| Aggregate package growth | Reserve bounded worst-case growth before committing it | Monitor observed apparent lengths and stop on detected excess; native transient peaks remain unknown |
| Package peak threshold | 256 MiB including source/staging/recovery overlap | Same numeric threshold as an observation/abort threshold, not a proven peak ceiling |
| Sealed final package | At most 256 MiB | Unchanged; complete quiescent final inventory must establish this bound |
| Historical/workspace storage | 64 packages / 2 GiB history; 3 GiB workspace | Unchanged numerical limits; admit against known retained files, monitor active growth, and disclose the same transient-growth limitation |
| Safe implementation claim | Complete storage-growth perimeter required | No physical-quota or complete growth-reservation claim |
| Recommendation | Restricted to the tested scope | Restricted further to these monitored implementations/workloads; never proof of safe production storage growth |

Existing managed reservations remain enabled, including their conservative
unknown-outcome rules. Sampling does not release reservations or replace batch
commit semantics. No data is evicted, compacted, truncated or deleted to hide an
overshoot or make another case fit.

Everything else is inherited unchanged: logical fields and oracles; all 35
executions and their order; single attempts and no automatic retries; P1's
WAL/FULL versus framed durable-flush acknowledgements; F1's existing injection
(not a monitor-triggered substitute); F2-F5; workload, queue, batch, memory,
reader, output and timing budgets; live baseline validity; comparative ratios;
and the predeclared A simplicity tie-break. No third index backend is added.

## Monitoring contract to establish before execution

Use the same instrumentation and rules for E, A and B. E's lack of a durable
package is reported explicitly, not fabricated as a measured storage result.
The harness owns the storage monitor in every arm; do not move it into one
candidate's diagnostic process. Charge it to the existing harness budget and
report its CPU/allocation and observation timing separately. Diagnostic-process
CPU remains the existing comparative metric. Monitor I/O and scheduling can
still affect workload timing, and E/A/B have inherently different amounts of
storage to observe. This asymmetry is a declared limitation: the comparison
selects among monitored implementations, not isolated storage-engine speeds.
Do not subtract estimated overhead or reweight scores after seeing results.
A demonstrated monitor defect is an infrastructure failure, not an engine
defect; unknown causality stays unknown. A correctly operating monitored arm
that fails an inherited screen fails only that monitored configuration's
screen, not a claim about the underlying engine without instrumentation.

The proposed poll target is 100 ms, matching the existing memory-poll cadence.
Record actual monotonic sweep start/end times and gaps. A gap over 1,000 ms
while a storage-mutating stage is active makes monitoring incomplete. This is
a new, prospective data-quality screen, not a guarantee of scheduling or a
bound on undetected growth. Incomplete monitoring cannot qualify a candidate.

Also observe at ownership boundaries: before admitting package writes, after
drain, before and after pre-seal finalization, after manifest/seal publication,
before and after recovery, and before and after ordinary reopen. These must
not reset or extend existing drain/finalization/execution deadlines.

F2/F3/F4 additionally require a completed observation while the declared fault
barrier holds the writer, followed by confirmed termination and a quiescent
inventory of the surviving owned roots before recovery admission. Release
monitor-owned file references before the kill/handoff so observation does not
keep otherwise-dead temporary files alive. Disappearance of the intentionally
terminated process's descriptor table is expected, not an enumeration failure;
do not query a recycled PID. Its lost live-descriptor visibility and intervening
transient peaks are disclosed as unknown. This exception does not excuse a
missing pre-kill observation, unconfirmed death, unexpected early exit,
surviving unknown owners, or a failed post-kill inventory: those remain
incomplete. Existing crash-recovery/acknowledgement invariants are unchanged.

Coverage includes canonical/query files, WAL/shm/journals, temporary
index/conversion files, manifest/seal and source/output overlap. Root-directory
scans alone are insufficient: a runner must account for observable owned
temporary files outside the root and open-unlinked files, or report incomplete
coverage. The accepted supplied-handle inventory is not that discovery layer.
No silent temp-store/configuration change or assumption that all SQLite files
are named, under the root, or visible after close is allowed.

The bounded discovery domain is the explicitly declared private package,
recovery, retained-history and campaign-evidence roots, plus open descriptors
of verified owned live diagnostic workers and the harness. Never scan the
host filesystem or shared `/tmp` indiscriminately. Record actor identity and
liveness, not just a PID. The successor must establish that known persistent
charged files cannot escape that domain; an unknown outside-domain resource or
unsupported charged lifetime makes monitoring incomplete. Files that live and
disappear entirely between observations remain temporally unobserved, not a
claim of discovery completeness. Any known charged file class needing another
discovery mechanism must be supported before execution, not excluded silently.

Before execution, freeze an attribution map for package, retained history,
workspace, stdout/stderr and runtime-only resources. Every exclusion needs
an explicit rationale. Do not double-count duplicate observations of a file,
exempt package bytes from workspace totals, or use a reused descriptor number
as file identity. Package symlink/hardlink rejection remains required.

A sweep is not necessarily atomic. Its sum must be named `observedSweepBytes`,
not actual instantaneous usage or proven peak. Report
`maximumObservedSweepBytes` with its observation intervals. It is not
automatically even a lower bound on the true peak if file lifetimes overlap
ambiguously during a sweep. Between-observation transient peaks remain
`unknown`, including on otherwise successful cases.

Failure to enumerate/read/classify a potentially charged resource, identity
ambiguity, missing required boundary observations, monitor failure, or exceeded
monitor metadata/output limits produces incomplete monitoring, not zero bytes
or a coverage-complete claim. Native files that live entirely between successful
observations remain an explicit limitation rather than claimed coverage.

Keep the monitor bounded: at most 4,096 tracked identities per sweep and
4,096 UTF-8 bytes per observed path, rejecting excess without truncation.
These are new instrumentation limits, not per-file storage partitions.
Stream evidence within the existing per-execution stdout budget; no extra
log allowance or capture-wide unbounded in-memory list is added. A successor's
resolved manifest must fix implementation, process attribution and evidence
encoding before any execution; unresolved values still block a run.

Aggregate sweep/boundary summaries are capped at 1,024 UTF-8 bytes each and
2,048 records per execution (at most 2 MiB, within the existing 8 MiB stdout
budget). No full path list is repeated every poll. Fix bounded inventory/error
encoding and prove the combined summary plus other stdout bound before
adoption. Also establish the maximum simultaneous identities for the actual
runner, including O1, live cases and full retained history. Sealed history may
be inventoried at admission and accounted from a known ledger while exclusive
retention ownership prevents changes; active package/native files may not be
replaced by cached lengths. No assertion is made that these instrumentation
caps already fit an unimplemented runner. If the feasibility evidence fails,
revise the proposal before adoption/data, not after an inconvenient run.

## Abort and attribution

Any observed aggregate threshold excess or incomplete monitoring stops
admission and initiates bounded cancellation. Stop the campaign, retain
failed evidence and mark remaining
cases not-run; do not retry or increase limits. Use existing deadlines, with
escalation confined to verified owned processes. This cannot undo an overshoot
or guarantee enough free storage during detection/cancellation.

The abort signal is not by itself a candidate defect: a non-atomic sweep,
attribution failure or inaccessible resource may be a monitoring failure.
Use the existing outcome vocabulary and require positive evidence to assign
`failed-candidate` or `inconclusive-infra`; otherwise use
`inconclusive-unknown`. A complete quiescent inventory showing excess is
stronger evidence than an ambiguous in-flight sum. In particular, an ambiguous
in-flight excess is inconclusive, not a candidate storage-gate violation:
that classification requires confirmed excess from a complete quiescent
inventory and attribution to the candidate. This precedence also governs
eligibility; an inconclusive case cannot qualify a candidate or be used to
recommend the other candidate from a convenient subset.

Missing, invalid or monitoring-incomplete required evidence prevents choosing
a winner from the remaining convenient subset. Detecting excess is not a
successful F1 injection. Ordinary shutdown must preserve unknown commit and
volatile-tail states instead of manufacturing successful terminal accounting.

## Recommendation and production boundary

Retain all original non-storage hard gates and comparative equations. Replace
only storage-peak eligibility with complete required monitoring evidence and
no unresolved threshold alarm, while retaining exact quiescent final limits.
Apply the attribution precedence above: unresolved/incomplete is inconclusive,
not interchangeable with a positively established candidate failure.
Label an eligible result `monitored-scope-only`, never revision-3 compliant.
If both candidates satisfy the existing reciprocal inequalities, A remains the
simplicity tie-break. Missing evidence, conflicting trade-offs or invalid
baselines remain inconclusive; no new retrospective weights are introduced.

This may recommend which backend to develop further. It does not establish
the original hard aggregate guarantee, full boundedness, cross-platform
behavior, power-loss durability or production readiness. DC6 still requires
an explicit maintainer decision, the separate schema-compatibility evidence
proposed in #1017, and resolution of the outstanding production storage-
boundedness requirement. Merely accepting monitored comparison cannot unblock
production follow-ups.

## Adoption checklist

1. Independently review this explicit semantic change and instrumentation
   rules; then obtain maintainer adoption. Preparing or publishing a draft
   is not adoption.
2. Produce and review a complete successor protocol/JSON and validator, proving
   unchanged inherited cases, fixtures, P1/F1 settings and numerical budgets.
   This overlay alone cannot authorize execution.
3. Implement the minimal end-to-end runner, monitored discovery/attribution,
   lifecycle and bounded abort behavior; establish their component evidence,
   including active temporary/unlinked files, intentional-kill handoff,
   incomplete-monitor failures and the identity/output feasibility bounds.
4. Resolve all run identities and preflight prerequisites; obtain execution
   authorization, then run the single declared campaign. No scored trial runs,
   budget calibration, hidden retries or retroactive rescue.

If these prerequisites cannot be established, report the blocker or defer the
comparison. Do not promote the existing prototypes or observations into a
winner merely because native reservation work was deferred.
