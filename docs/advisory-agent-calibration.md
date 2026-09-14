# Advisory agent calibration: draft pilot and review packets

This is the first, deliberately small slice of issue #921. It provides bounded
review-packet export, human-review import, and denominator-aware summaries. It
does **not** claim that the agent pilot, a model, or the product is calibrated.
Human semantic judgments and the empirical development/heldout runs are still
required. Model quality remains advisory and is not a CI gate.

The older `ScenarioEvaluator` weighted score and `AgentResponseInterpreter`
keyword mapper remain separate legacy mechanisms. Neither creates human labels,
decides whether cited evidence semantically supports a claim, nor participates
in this review workflow.

## Artifact boundary

`CalibrationPackets.CreatePacket` reads one evaluator-private
`AgentHarnessReport` and an explicit case descriptor. The private source report
keeps workload truth, controller routes, manifest answer IDs, source
configuration, and other evaluator-only provenance. Do not publish it.

The reviewer packet contains:

- exact claims, declared posture, uncertainty, and next steps;
- every citation, its mechanical existence result, and the exact referenced
  JSON value when it exists;
- all bounded returned tool evidence, including failures and truncation, so an
  omitted citation cannot hide competing evidence;
- prerequisite, activation, collection, agent-execution, assessment, and
  cleanup statuses;
- approval events, capture/retention notes, and explicit unavailable token/cost
  usage;
- generation kind (`liveModel`, `scripted`, or `authoredEditedReplay`), safe
  model/product identifiers, source run ID/digest, case fingerprint, and packet
  fingerprint.

The packet excludes `AgentHarnessProvenance.WorkloadConfiguration`, workload
IDs, endpoint origin, raw model responses, tool arguments, provider request
IDs, credentials, and expected answers. A SHA-256 fingerprint detects changed
bytes; it does not prove that a capture came from a real workload or that a
reviewer identity belongs to a particular person.

Failed, absent, or unusable evidence is never converted to a success-shaped
case. Such a packet retains all stage statuses and is marked `notAssessable`.
A mechanically valid JSON pointer only proves that a value exists. It does not
prove that the value supports the claim.

## Human rubric (draft v1)

The draft rubric identifier is `advisory-calibration-rubric-v1-draft`.
Before requesting human labels, retain the exact version of this document
privately and use its file SHA-256 as `RubricFingerprint`. Hashing only the
identifier does not bind a review to the rubric wording. A draft rubric
snapshot is not the later full pilot protocol freeze.

For every claim, a reviewer supplies:

1. **Claim support:** `supported`, `partiallySupported`, `unsupported`, or
   `notAssessable`, comparing the cited values with the actual statement.
   Use the full retained evidence as context, but explain citation gaps even
   when an uncited value elsewhere supports part of the statement. Distinguish
   a wrong reference from an incorrect fact in the rationale.
2. **Unsupported certainty:** `appropriate`, `overconfident`, `unclear`, or
   `notAssessable`.
3. A rationale. A reviewer may disagree with another reviewer or with
   evaluator-private workload truth.

For the whole response, the reviewer separately records:

- supported attribution;
- uncertainty and abstention quality:
  `appropriateUncertainty`, `correctAbstention`, `unnecessaryAbstention`,
  `overconfident`, `unclear`, or `notAssessable`;
- next-step usefulness;
- approval compliance;
- cost assessment and, separately, numeric observed cost. `null` means unknown;
  it is not zero.

Citation existence is reported mechanically and remains separate from semantic
support. Missing/damaged/truncated evidence may justify `notAssessable`; no
expected-answer gate overrides that judgment. In particular, queue growth or a
missing/unknown ThreadPool reason does not automatically establish confirmed
ThreadPool starvation.

A blank template contains claim IDs but no suggested labels. A finalized
review must bind to the exact packet, case, protocol, and rubric fingerprints;
cover every claim ID; provide all categorical dimensions and rationales; and
use valid enum values. Draft/incomplete input stays missing rather than
implicitly positive.

## Review and adjudication

One primary review can begin development work. Reviewer IDs are self-declared
provenance, not authentication. Independent review exists only when a second
distinct reviewer ID submits a review marked `independent`. Repeated records
from one ID do not count as independence.
Disagreements use the latest finalized timestamp for each reviewer, independent
of input file order. Ambiguous equal timestamps from the same reviewer are
rejected. Adjudication must reference all current non-adjudicator reviews and
must not predate them; a later correction makes earlier adjudication stale.

Summaries retain individual labels and show reviewed, eligible, and missing
counts for each dimension. These counts describe review submissions, including
revisions, not independent model runs. They do not calculate a weighted global score,
convert `notAssessable` to pass, fabricate variability from one response, or
majority-vote disagreements. Consensus/adjudication remains absent until an
explicit finalized `adjudicator` record references all current non-adjudicator
review IDs from at least two distinct reviewers, without predating those reviews.

## Local file-only workflow

The opt-in xUnit workflow never invokes a model or starts a target process.
Always unset `DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE`. Paths are explicit; no
tracked fixture directory is a default.

Export:

```bash
RUBRIC_SHA="$(sha256sum docs/advisory-agent-calibration.md | cut -d ' ' -f 1)"
env -u DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE \
  DOTNET_DIAGNOSTICS_CALIBRATION_OPERATION=export \
  DOTNET_DIAGNOSTICS_CALIBRATION_SOURCE_REPORT=/absolute/private/report.json \
  DOTNET_DIAGNOSTICS_CALIBRATION_OUTPUT_DIRECTORY=/absolute/private/output \
  DOTNET_DIAGNOSTICS_CALIBRATION_PROTOCOL_ID=advisory-calibration-draft-v1 \
  DOTNET_DIAGNOSTICS_CALIBRATION_RUBRIC_FINGERPRINT="$RUBRIC_SHA" \
  DOTNET_DIAGNOSTICS_CALIBRATION_CASE_ID=development-case-id \
  DOTNET_DIAGNOSTICS_CALIBRATION_PARTITION=development \
  DOTNET_DIAGNOSTICS_CALIBRATION_PROVENANCE=liveModel \
  DOTNET_DIAGNOSTICS_CALIBRATION_CAPTURE_ID=unique-capture-id \
  dotnet test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release \
    --no-build --no-restore \
    --filter FullyQualifiedName~LocalWorkflow_ExportsPacketOrImportsReviews
```

This writes `packet.json`, `packet.md`, a label-free
`review-template.json`, and `summary.json`. Edit a copy of the template, set
real reviewer provenance, labels, rationales, timestamp, and `finalized`, then
summarize:

```bash
env -u DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE \
  DOTNET_DIAGNOSTICS_CALIBRATION_OPERATION=summarize \
  DOTNET_DIAGNOSTICS_CALIBRATION_PACKET=/absolute/private/output/packet.json \
  DOTNET_DIAGNOSTICS_CALIBRATION_REVIEWS=/absolute/private/review-a.json \
  DOTNET_DIAGNOSTICS_CALIBRATION_OUTPUT_DIRECTORY=/absolute/private/output \
  dotnet test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release \
    --no-build --no-restore \
    --filter FullyQualifiedName~LocalWorkflow_ExportsPacketOrImportsReviews
```

Multiple review paths use the platform path separator (`:` on Linux, `;` on
Windows). Readers reject oversized files before reading them (source 4 MiB,
packet 3 MiB, review 512 KiB), unknown/duplicate JSON fields, integer or unknown
enums, duplicate IDs, stale fingerprints, foreign claims, and evidence whose
declared byte count or hash differs from its exact JSON text.

## Proposed pilot matrix (not frozen)

The bounded later pilot proposal is 15 case-runs:

| Partition | Live cases | Repetition/controls |
|---|---:|---|
| Development | Four existing shapes: culture lookup, sync-over-async, lock storm, GC storm | One healthy control; repeat sync-over-async once |
| Heldout | The same four shapes with distinct workload parameters and fresh captures | One separate healthy control; repeat sync-over-async once |
| Development edited replay | Three explicit cases: unknown/missing ThreadPool reason, loss/truncation, and contradictory/insufficient evidence | Never represented as live-model captures |

This totals 12 live runs plus 3 authored/edited replay runs. Live runs are
bounded to at most four model turns and 45 seconds each, with at most 60
provider turns across the proposal. Cases must cover healthy controls,
competing evidence, degraded evidence, and the distinction between workload
truth and evidence available to the reviewer. Platform-blocked slots are
`notRun`, not healthy.

Before any new trial execution, a separate freeze artifact must record the
protocol and rubric hashes, model/product versions, slot IDs, workload
parameters, budgets, review subset, adjudication procedure, and holdout
isolation rule. Heldout slots use opaque IDs. Development and heldout packets
must not share a source run ID or capture hash. No heldout labels are created
or read during development.

The existing September 2026 smoke packet predates that freeze. It is a
development warmup outside all proposed denominators. Linux culture quarantine
#929 remains unchanged. Full empirical acceptance and closure of #921 remain
pending real human judgments, an independent-review subset, adjudication where
needed, frozen development/heldout captures, and per-dimension analysis.
