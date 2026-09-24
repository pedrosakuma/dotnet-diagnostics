# DC5 revision 7: comparison remains inconclusive

## Disposition

The four admitted campaigns below did not produce a complete 35-case
comparison. **There is no supported winner between direct SQLite (A) and
append-first with a derived SQLite index (B).** Results from different
campaigns must not be pooled into a complete comparison.

The latest campaign stopped on a hard descriptor-coherence observation.
Investigation did not establish an implementation defect that can be fixed
without changing the adopted observation contract. Preserve the failed
campaign and its inconclusive decision; do not retry unchanged inputs until
they pass or automatically broaden the admissible-loss classes.

This is not proof that every possible revision-7 execution would fail.
It is a statement about the retained evidence and the absence of a justified
contract-preserving correction. A prospective change to observation semantics
requires explicit adoption, new evidence and review. No production backend
decision, merge or implementation authorization follows from this report.

### Subsequent maintainer selection (2026-09-24)

The maintainer subsequently selected direct SQLite as the architectural
direction for simplicity and maintainability, explicitly separate from this
inconclusive comparison. The
[RFC decision record](../../design/durable-capture-rfc.md#maintainer-decision-sqlite-only-direction-2026-09-24)
records the rationale and remaining DC6 production gates. This does not change
any campaign outcome, establish performance equivalence, or authorize another
campaign or production implementation.

## Actual progression

All campaign names below refer to separate, preserved private evidence roots.
Each campaign used one attempt per execution and stopped under its existing
safety/monitoring rules. Every executed prevalidation listed here passed its
eight coverage predicates and sealed 2,438 artifacts; readiness was independently
reviewed and bound to the respective complete prospective campaign.

| Campaign | Implementation | Prevalidation | Passed | Stopping entry | Not run | Sealed artifacts |
| --- | --- | --- | ---: | --- | ---: | ---: |
| `campaign-unified-v7-01` | `aa3c33b` | `pv-unified-v7-04` | 4 | Q2/B: unclassified read-only descriptor | 30 | 72 |
| `campaign-unified-v7-02` | `aa3c33b` | `pv-unified-v7-05` | 6 | N1/A: descriptor-enumeration permission error | 28 | 95 |
| `campaign-unified-v7-03` | `35cf8f3` | `pv-unified-v7-06` | 10 | M1/A: adapter configuration rejection, then identity-loss alarm | 24 | 129 |
| `campaign-unified-v7-04` | `02a3104` | `pv-unified-v7-07` | 3 | Q1/B: descriptor identity changed during observation | 31 | 62 |

The earlier controlled findings and corrections are documented in
[runtime and worker handoff](campaigns-runtime-and-worker-handoff.md) and
[M1 configuration](campaign-m1-configuration.md). The private runtime copy
did not identify the first campaign's historical descriptor. The worker-exit
regression proved an ordering defect without reconstructing the second
campaign's exact kernel interleaving.

The M1 correction keeps its 262,144-byte producer reservation and the
adapter's frozen default limits separate. Campaign04 stopped before M1;
it therefore supplies no scored confirmation of that correction.

## Latest admission and execution

Source was clean at `02a31047338049ef392c8420be9aa78712a151dd`.
The unchanged private runtime contains 335 byte-identical, single-link files
totalling 109,656,640 bytes. The ordered binary inventory has 91 references,
including the intentional tool/sample duplicates. Child PATH resolution is
bound separately from the protocol-selected environment hash.

`pv-unified-v7-07` ran on 2026-09-24 from 17:56:47.556 to 18:00:11.230 UTC,
exit 0. Independent review recomputed all 2,438 sealed artifacts, 874 monitor
records, 91 binary references and 335 runtime files. The eight outcomes
satisfied the source, live request, recovery, geometry and cleanup predicates.
Twenty-five descriptor losses remain explicit unknown observations, not zero
bytes or complete monitoring.

Campaign04 ran on 2026-09-24 from 18:14:20.041 to 18:15:15.639 UTC, exit 3.
P0/shared, P1/E and Q1/A passed. Q1/B was inconclusive; the remaining 31
executions were not run. The verified seal contains 62 artifacts and the
original incomplete decision.

| Artifact | SHA-256 |
| --- | --- |
| Revision-7 protocol | `23919c85adaf688db41a286cbe2bc90fb4687b3b3d3222d8372c276b40d03149` |
| pv07 final manifest | `1cff811b776a28048e19b9e0f1ff9bb8649e127020f0c84e783d69b0d88f6ae9` |
| pv07 seal | `567620c28297bff211370bd6ae37babe144ec71258dc9a7e177b9ff98ce92e76` |
| Independent pv07/campaign04 review | `72317c447ea057ed52a5d0d569429427d709011d07f199527802aa2c88b311bf` |
| Campaign04 final manifest | `2d90792145e5d7db4068b1fcec25c7f362dfef4491a8d596b454eb95409edc8e` |
| Campaign04 authorization | `bfa8688821ae28bbe6786a6484455c14be01fe6887ccaaf266e7a58af1b51d40` |
| Campaign04 seal | `bd5ce764661a5637011ea3a4c7898be9fa5bc208b57870b4b6f1f5547c974f2d` |
| Q1/B raw monitor | `ca7ad254f894058f711dfa9328de1b96c84a16c05aca54db1207c20ed91fadab` |
| Coherence investigation report | `40a4c01b9d1f4d864732a88435bcf5fbf7be48dcaf8d3811bb7105e112c01582` |
| Controlled forensic records | `636652ffaadeb63e71a678283402690ac1af0933e8b4e9e31b0615485cf8de9c` |

Raw artifacts remain in the preserved session evidence, not in Git. Hashes
identify those artifacts; this report is not a replacement for the raw
streams or a portable copy of the complete evidence set.

## What the latest failure proves

The retained hard context identifies diagnostic FD 71, operation 5, change
mask 5: identity and target changed, while access flags did not. **The value
5 here is a change mask, not errno.** The original endpoint paths, native
types, link counts and failed proof predicate were not retained.

Across the Q1/B outcome there are 2,207 descriptor candidates, 2,203
classifications, three admitted pin losses and one hard residual.
The latter is not a fourth admissible loss. The worker had not reached its
package/workload gate, so this is not evidence of a B query-performance or
storage-correctness failure.

Revision 7 inherits closed descriptor-loss proofs from revision 5 and
positive stable-unlinked accounting from revision 6. A change mask alone
does not establish either proof. Endpoint validity, ownership, live process
identity and existing hard exclusions still matter.

Controlled native-handle observations through the unchanged observer
demonstrated:

- Replacing one linked, owned regular file with another can produce mask 5
  and correctly admit one coherence loss.
- Replacement involving an unlinked endpoint or a regular/device transition
  can produce the same mask and correctly remain hard.
- Same-inode unlink can instead produce mask 4, or a link-count-only change
  with mask 0. Neither reconstructs historical FD 71.
- Closing after a linked first snapshot can admit operation-3 ENOENT;
  the same errno after a positively unlinked first snapshot remains hard.

These are deliberately scheduled mechanism checks, not historical
attribution or natural failure-frequency measurements. Seventeen new
controlled cases and 100 existing cases passed across the retained runs.
The initial 116/117 run exposed a fixture's extra unsupported descriptor;
correcting only that fixture yielded 17/17 new cases. Both runs remain
retained. No production observer, classifier, protocol, workload or budget
was changed, and no scored campaign was rerun by this investigation.

## Additional unresolved validation evidence

The first post-publication component run for `02a3104` passed 625/626.
`SampledLossScriptedRecoveryPreservesLifecycleAndPopulation`, a revision-5
scripted F3/A component test, observed `SampledAdmissible=false`.
Its outcome, alarm and descriptor snapshots were not retained. An isolated
pass and a later 626/626 run do not establish or erase its cause.

This limitation was explicitly recorded in pv07's parent authorization and
the independent campaign04 readiness review. The successful real revision-7
prevalidation does not resolve that component failure. There is no evidence
equating it with the later Q1/B coherence failure.

## Boundary for further work

Keep the experiment inconclusive under revision 7. Do not combine partial
campaigns, infer a winning backend from the last failing candidate, or
convert hard observations to losses based on a mask, pathname or engine.

A separately authorized investigation could capture bounded endpoint
forensics while preserving every revision-7 guard. A broader transition
sampling contract would instead require a prospective design, precise
proofs and accounting, bounded encoding analysis, explicit adoption and
fresh source-bound prevalidation/readiness. Neither option can retroactively
reclassify FD 71, guarantees a complete comparison, or establishes an
instantaneous storage bound.
