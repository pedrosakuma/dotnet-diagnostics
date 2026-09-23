# DC5 real-worker monitoring prevalidation proposal

## Status and purpose

**Proposal for independent review; not execution authorization.** The maintainer
authorized preparation of this addendum on 2026-09-23. No additional process,
capture, or scored workload is authorized by publishing it. Adoption, reviewed
implementation, a resolved prevalidation manifest, and a separate authorization
receipt are required before the first probe.

This proposal addresses the specific readiness block recorded in
[the DC5 report](../evidence/dc5/monitored-readiness-dbeb8cd.md): representative
component probes cannot establish the monitor's identity/descriptor feasibility
for the actual O1/live workers. A configured limit is not evidence of fit.

The reference implementation is `dbeb8cdb0cce663143d3ce7b831f69dc8cf7b75e`,
published with the report at `0dcff7a65d2b8e919e2bc87e893d47129af8fa0e`
(draft #1024). The [revision-4 protocol](durable-capture-monitored-protocol.md)
remains byte-for-byte unchanged:
`e37c44917f738f8d798399258961aa28548b0f4df351111cec047b29c0a50e71`
is its JSON SHA-256.

## Explicit change to execution scope

Revision 4 permits exactly 35 scored-plan executions and prohibits extra
tuning runs. This addendum proposes **eight additional, separately identified
readiness probes**, not a reinterpretation that the existing authorization
already allowed real-workload rehearsals. They deliberately exercise workloads
also used by the later comparison, so they must be disclosed as prior exposure.

The 35-entry scored plan, its order, single-attempt rule, all thresholds, and
the A/B recommendation rule remain unchanged. The eight probes do not replace,
count as, or contribute measurements to any of those entries. They cannot
authorize a winner. The existing all-`not-run` report remains historical evidence
and must not be rewritten into a successful run.

There are two distinct admissions:

1. **Prevalidation admission** requires existing component evidence of safe
   bounded observation/abort, an independently reviewed real-worker route, and
   authorization for exactly this additional matrix. It does not require the
   as-yet unavailable real-worker feasibility result.
2. **Scored-campaign admission** continues to require independently accepted
   real-worker feasibility evidence, all other revision-4 gates, and its own
   manifest-bound authorization. A completed prevalidation suite does not
   automatically open it.

This distinction removes the circular prerequisite without changing a false
feasibility field to true or giving the current `monitored-run` route a bypass.
The existing validator remains fail-closed until reviewed code can validate the
new evidence and its provenance.

## Prospective matrix: eight probes, one attempt each

This order is fixed before execution. There is no optional arm or fastest-arm
selection. Each actual-workload probe uses a fresh standalone benchmark harness,
diagnostic worker, and, for live probes, published CoreClrSample process.
The VSTest-host omission in scripted component tests is not permitted here.

| Ordinal | ID | Arm | Fixed work | Readiness observation |
| --- | --- | --- | --- | --- |
| 1 | PV-O1-A | A | Revision-4 O1: 50,000 scheduled 512-byte offers, eight keys, 5,000/s for 10 s | Real SQLite writer, native files, drain, finalization, immutable reopen |
| 2 | PV-O1-B | B | The identical O1 schedule | Real append writer, derived index, publication and reopen |
| 3 | PV-LIVE-E | E | One revision-4 live episode with the unmodified shipping collector | Actual baseline harness/worker/target descriptor population |
| 4 | PV-LIVE-A | A | One identical live episode with durable A | EventPipe plus A acquisition/finalization/reopen ownership |
| 5 | PV-LIVE-B | B | One identical live episode with durable B | EventPipe plus B acquisition/index/reopen ownership |
| 6 | PV-F3-A | A | Revision-4 F3: two 64-record batches, held post-commit/pre-ack kill and recovery | Actual native writer death, descriptor release, surviving source and recovery overlap |
| 7 | PV-F3-B | B | The identical F3 barrier/recovery | Actual append writer death and derived-index recovery ownership |
| 8 | PV-GEOMETRY | shared | Fixed full-history/file-layout and encoding fixture, no producer or HTTP load | Monitor capacity at the derived identity/output geometry, not storage-engine performance |

All actual-workload paths reuse the reviewed runner/worker/adapter code, not
shorter stand-ins. Changes may separate admission and result projection and
introduce the explicitly defined two-level identity accounting below. That
prevalidation-only accounting evaluates the unchanged 539 rooted / 32
descriptor-only / 571 total geometry against the current context while also
enforcing the unchanged 4,096 instrument ceiling on the whole suite. It may
not substitute 4,096 for the current-context limit or affect the scored route.
Implementation review must verify both enforcement scopes. Changes must not
disable production harness monitoring, alter providers, alter the
storage PRAGMAs, replace the baseline collector, or bypass publication/recovery.
A reviewed call-path map must identify this reuse before admission.

The live schedule is unchanged: readiness at most 10 s, warmup 10 s, collection
at t=10 through t=44 with the three revision-4 providers at interval 1 s;
all A/B source ticks are processed. Requests are 20/s to `/cpu-burn?ms=10`,
at most two in flight, timeout 1 s, no redirects. Request coverage uses the
same scheduled population `[12s,42s)` as revision 4.

## Full-history and suite accounting

Each probe has a separate private capture context, never shared writable
ownership with another probe. Before probes 1, 2, 4, 5, 6, and 7, construct
63 immutable inventory-fixture slots in that context's retained-history root.
Each slot has four distinct regular files of exactly 512 apparent bytes.
One slot remains available for that probe's published or recovered package,
keeping at most 64 retained slots in that context. Probe 3 has 64 fixture slots
because E publishes no package.

These are **synthetic inventory fixtures**, not valid capture packages:
they test simultaneous directory/inode enumeration cost, are never opened as
captured evidence, and do not prove storage semantics or 2 GiB byte occupancy.
The derivation must separately show the real package/file count, native
temporary-file ownership, and encoded numeric-width bounds. Source files
left by F3 stay in its workspace and remain charged alongside recovery.
An unsealed F3 source is not a retained-history slot: it is an active source
package charged to the source-plus-recovery threshold, workspace bytes, and
current-context identity geometry. Only its immutable published recovery
occupies the 64th retained slot. Both file populations are observed concurrently;
excluding the source from the retained-slot count never excludes its bytes or
identities.

The geometry probe materializes the implementation's derived rooted layout
(539 identities at the reference commit), including 64 four-file history slots,
and accounts for the 32 descriptor-only allowance (571 total at that commit).
Its descriptor fixtures must have explicit positively justified charged
attribution; unclassified files cannot be relabeled complete for this probe.
It separately exercises the existing bounded summary/control encoding proof.
This establishes instrument capacity at a declared layout, not that native
workers fit it; both kinds of evidence are required.

Prevalidation contexts are deliberately independent, unlike the one scored
campaign's accumulating history. The 64-slot/2 GiB history limits apply to
each context. **The whole eight-probe suite still has one 3 GiB workspace
budget**, including every prior context, all fixtures, outputs, and raw evidence.
Nothing may be deleted, compacted, unlinked, or omitted to make a later probe
fit. This context separation is an explicit prevalidation-only topology, not
a change to scored-campaign retention.

The monitor observes the current context and all accumulated suite-owned
roots. It reports current-context and suite totals separately, without
double-counting common ancestors or file identities. The 4,096-identity
instrument ceiling applies to their union; the per-context 571/32 derived
geometry is checked against current-context roles, not against unrelated
completed contexts. Prior roots remain charged and observable.
Harness descriptors not attributable to a completed immutable context count
against the current context, never an uncharged coordinator bucket. A new
unclassified descriptor remains incomplete monitoring.

Before admission, a finite bound for the **entire eight-probe fixture and
control-file layout**, including reporting/sealing, must be derived from
actual creation paths and verified within 4,096 identities. The existing
571 figure alone is not that bound. If the layout does not fit, this proposal
cannot execute as written; do not fix it by hidden pruning or a larger limit.

## Bounds and stopping

The following apply prospectively; none can be increased after observing a
probe:

- One attempt per matrix entry. Write an immutable attempt receipt before
  fixture construction or launching its first process. Failure during either
  consumes that entry; missing/failed entries cannot be replaced.
- At most 120 s per entry, including its fixture preparation, process startup,
  collection, boundary observations, drain, finalization, reopen/recovery, and
  cleanup. Source and recovery share that same outer deadline.
- At most 1,200 s for the suite, including manifest admission, all eight entries,
  final enumeration and reporting. Admission/preflight has at most 120 s and
  final enumeration/sealing at most 120 s, all inside that shared deadline.
- Existing 5 s drain, shared 10 s finalization, and 2 s reopen-plus-first-query
  limits remain. Existing diagnostic/harness/target sampled RSS limits remain
  512/256/768 MiB; no extra profiling session is added.
- Existing 256 MiB observed package-plus-recovery threshold and exact
  quiescent package cap remain. History is at most 2 GiB/context; aggregate
  suite workspace is at most 3 GiB as specified above.
- Poll target 100 ms, maximum active-stage observation gap 1,000 ms;
  at most 4,096 identities/sweep and 4,096 UTF-8 bytes/observed path.
- At most 2,048 newline-framed 1,024-byte summaries per entry, within that
  entry's shared 8 MiB stdout budget; stderr at most 8 MiB. Source/recovery
  share these caps. Additional attribution details must fit the same
  existing evidence allowances, not an unbounded path dump per sweep.
- Fresh admission checks require at least 8 GiB free space and 2 GiB effective
  available memory, resolving the actual cgroup membership and ancestors.

Any monitoring incompleteness, exceeded limit, invalid source population,
unexpected termination, failed lifecycle, or missing required boundary stops
the entire suite. Preserve the failed outcome and mark remaining entries
`not-run`. There is no automatic retry or replacement suite. Timeouts and
unknown causes remain explicitly unknown; no candidate-blame inference.
If bounded finalization cannot seal the suite, preserve an explicitly partial
report/receipts rather than claiming an immutable complete result.

Only verified owned process identities may be signaled. Readiness failure
does not authorize host tuning, ptrace-scope changes, global quotas, adapter
durability changes, alternative native dependencies, or another backend.

## Evidence and decision firewall

The suite's question is **whether the monitoring implementation can observe the
declared real-worker paths within its limits**, not which backend is faster.

Persist source coverage and scheduling validity, lifecycle completions,
process identities/ownership transitions, classified and unclassified
descriptor counts, current-context/suite identity counts, apparent-byte
observations, actual sweep/gap timing, exact final inventories, and resource
or deadline violations. Keep root-observed and descriptor-only counts distinct.
Report fixture counts, measured counts, and derived limits as separate fields.
Classified runtime exclusions retain their identity proof and rationale.

Inherited worker code may emit bounded performance measurements while executing
the real paths. Retain them as unscored raw data rather than editing evidence,
but do not invoke the A/B decision engine, compute comparative ratios, rank
arms, tune implementations, or publish a preliminary winner from them.
The aggregate readiness report projects coverage/limits only.

Changing code, configuration, workload, attribution, limits, or host setup
after a probe result invalidates reuse for affected paths. A correction may
be developed and component-tested, but another real-scenario attempt requires
a newly reviewed prospective revision and explicit authorization; all earlier
outcomes remain linked. Fresh IDs alone do not reset the attempt budget.

Passing all eight entries is necessary, not sufficient for scored admission.
An independent reviewer must check actual observed populations, the
file-creation/ownership derivation across the remaining scored paths, full
history geometry, and evidence hashes. F3 does not silently stand in for
untested F1/F2/F4/F5-specific file states: existing component evidence plus
the call-path/resource derivation must cover them, or admission stays blocked.

An observed maximum is not a proven instantaneous/native upper bound.
Successful evidence supports **host/build-specific monitored feasibility**;
transients entirely between observations remain unknown, and the original
campaign must still stop on its own incomplete/excess observations.
The artifact must bind the complete evidence, not just replace
`descriptorOnlyCampaignFeasibilityEstablished=false` with an unchecked boolean.

## Manifest, authorization, and implementation gate

No prevalidation route exists in the reference implementation. This document
does not introduce a runnable schema or modify the existing manifest validator.
Implementation needs a separately reviewed, closed prevalidation schema that:

1. Pins this addendum's exact commit/hash, the unchanged revision-4 hash,
   the eight-entry order, fixture layout, all bounds, and scope
   `monitor-feasibility-only`.
2. Binds actual runner/monitor/adapter/pipeline commits, rebuilt managed and
   native binaries, runtime identity, host/cgroup/filesystem/clock facts,
   attribution map, fixed encoding, and prerequisite component evidence.
3. Uses a dedicated private root, ID namespace, eight-entry attempt ledger,
   and append-only link to the historical blocked-readiness report. Ordinary
   campaign receipts/results cannot be accepted as prevalidation artifacts,
   or vice versa.
4. Requires independent implementation acceptance and an immutable,
   manifest-bound **prevalidation-only** authorization receipt. It cannot
   call the scored route or produce a campaign/backend approval.
5. Separately validates and preserves final/partial outcomes and the
   approved evidence-to-campaign binding. Binary/configuration changes
   invalidate the binding unless independently shown not to affect it.

The implementation review must establish the bounded full-suite layout,
observer/cleanup behavior, result projection, and negative admission tests
before any real probe. This prerequisite does not require running the very
real-scenario probes it authorizes; existing small deterministic component
tests remain distinct from this eight-entry suite.

After independent proposal review, maintainer adoption is still required.
Then implementation and its independent review must complete, followed by
explicit prevalidation execution approval. Any later scored campaign also
requires its own readiness acceptance under the unchanged comparison rules.
DC6 production go, compatibility evidence, CLI/REPL/MCP integration, and
merging the draft stack remain outside this proposal.
