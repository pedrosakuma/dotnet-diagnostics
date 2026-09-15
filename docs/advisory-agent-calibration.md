# Advisory agent calibration: frozen v1 pilot and review packets

The v1 protocol is frozen in
`tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Calibration/advisory-calibration-v1.protocol.json`.
It provides a bounded 15-slot case matrix plus review-packet export,
human-review import, and denominator-aware summaries. Freezing the protocol
does **not** claim that the agent pilot, a model, or the product is calibrated.
No v1 development or held-out run has been executed, and no new independent
human review has been obtained. Model quality remains advisory and is not a CI
gate.

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

## Human rubric (v1)

The frozen rubric identifier is `advisory-calibration-rubric-v1`. The protocol
records this file's SHA-256 as `rubricFingerprint`; hashing only the identifier
would not bind a review to the rubric wording. Any rubric wording change
requires a new protocol version and fingerprint before another v1 run.

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

## Frozen live-slot execution

`FrozenProtocol_RunsOnePredeclaredLiveSlot` is the only protocol-aware
real-model entry point. It accepts exactly one slot per explicit invocation,
uses that slot's frozen budget, checks the actual harness commit/version before
inference, and writes only the evaluator-private source report. Build the
harness from the `product.commit` recorded in the public protocol and supply
the protocol by an absolute path from the freeze commit. The Copilot transport
probes its executable with `--no-auto-update --version`; it does not trust a
caller-supplied version string.

```bash
DOTNET_DIAGNOSTICS_CALIBRATION_RUN_SLOT=dev-live-sync-01 \
DOTNET_DIAGNOSTICS_CALIBRATION_PROTOCOL=/absolute/freeze/advisory-calibration-v1.protocol.json \
DOTNET_DIAGNOSTICS_AGENT_REPORT_PATH=/absolute/private/dev-live-sync-01.report.json \
DOTNET_DIAGNOSTICS_AGENT_TRANSPORT=copilot-cli \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_PATH=/absolute/path/to/copilot \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_HOME=/absolute/empty/copilot-home \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_WORK_ROOT=/absolute/empty/work-root \
DOTNET_DIAGNOSTICS_AGENT_MODEL=gpt-5.4-mini \
dotnet test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release \
  --no-build --no-restore \
  --filter FullyQualifiedName~FrozenProtocol_RunsOnePredeclaredLiveSlot
```

For an opaque held-out slot, also set
`DOTNET_DIAGNOSTICS_CALIBRATION_PRIVATE_DEFINITION` to the evaluator-private
definition whose exact byte hash matches `holdout.privateDefinitionSha256`.
The runner validates the commitment before resolving the workload. Never run a
held-out slot during development tuning.

Export:

```bash
RUBRIC_SHA="$(sha256sum docs/advisory-agent-calibration.md | cut -d ' ' -f 1)"
env -u DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE \
  DOTNET_DIAGNOSTICS_CALIBRATION_OPERATION=export \
  DOTNET_DIAGNOSTICS_CALIBRATION_SOURCE_REPORT=/absolute/private/report.json \
  DOTNET_DIAGNOSTICS_CALIBRATION_OUTPUT_DIRECTORY=/absolute/private/output \
  DOTNET_DIAGNOSTICS_CALIBRATION_PROTOCOL="$PWD/tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Calibration/advisory-calibration-v1.protocol.json" \
  DOTNET_DIAGNOSTICS_CALIBRATION_PROTOCOL_ID=advisory-calibration-v1 \
  DOTNET_DIAGNOSTICS_CALIBRATION_RUBRIC_FINGERPRINT="$RUBRIC_SHA" \
  DOTNET_DIAGNOSTICS_CALIBRATION_CASE_ID=dev-live-sync-01 \
  DOTNET_DIAGNOSTICS_CALIBRATION_PARTITION=development \
  DOTNET_DIAGNOSTICS_CALIBRATION_PROVENANCE=liveModel \
  DOTNET_DIAGNOSTICS_CALIBRATION_CAPTURE_ID=unique-capture-id \
  DOTNET_DIAGNOSTICS_CALIBRATION_CAPTURE_HASH=sha256-of-exact-capture-input \
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
  DOTNET_DIAGNOSTICS_CALIBRATION_PROTOCOL="$PWD/tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Calibration/advisory-calibration-v1.protocol.json" \
  dotnet test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release \
    --no-build --no-restore \
    --filter FullyQualifiedName~LocalWorkflow_ExportsPacketOrImportsReviews
```

Multiple review paths use the platform path separator (`:` on Linux, `;` on
Windows). Readers reject oversized files before reading them (source 4 MiB,
packet 3 MiB, review 512 KiB), unknown/duplicate JSON fields, integer or unknown
enums, duplicate IDs, stale fingerprints, foreign claims, and evidence whose
declared byte count or hash differs from its exact JSON text.

## Frozen v1 pilot matrix

The protocol predeclares 15 case-runs:

| Partition | Live cases | Repetition/controls |
|---|---:|---|
| Development | Four existing shapes: culture lookup, sync-over-async, lock storm, GC storm | One healthy control; repeat sync-over-async once |
| Heldout | The same four shapes with distinct workload parameters and fresh captures | One separate healthy control; repeat sync-over-async once |
| Development edited replay | Three explicit cases: unknown/missing ThreadPool reason, loss/truncation, and contradictory/insufficient evidence | Never represented as live-model captures |

This totals 12 live runs plus 3 authored/edited replay runs. Each live run is
bounded to 45 seconds, 3 diagnostic tool calls, 4 model turns, 10 capture
seconds, 10,000 input tokens, 1,200 output tokens, USD 0.25 of
provider-reported cost, a 128 KiB response, and 384 KiB of retained evidence.
The 12 live slots therefore permit at most 48 provider turns, below the
protocol-wide ceiling of 60. Provider token/cost fields remain unavailable
when Copilot CLI does not report them; that absence is not treated as zero.
Platform-blocked slots are `notRun`, not healthy.

The public freeze records the protocol and rubric hashes, model alias,
unavailable provider build-version evidence, Copilot CLI version, exact
product commit/version, slot IDs, public workload parameters, budgets, the
five-slot independent-review subset, and mandatory adjudication of every
disagreement. The subset spans development live, healthy control, edited
replay, and held-out cases.

The product baseline commit intentionally contains the complete executable
protocol validation but precedes the data-only commit that adds this manifest
and documentation. Build and run the harness from that exact baseline, supply
the frozen protocol by absolute path, and preserve the naturally reported
commit/version. Do not override `GITHUB_SHA` to force a provenance match; it
is honored only when `GITHUB_ACTIONS=true`. Local builds use the SDK-embedded
source revision.

Held-out slots use only opaque IDs in the repository. Their parameters and
workload truth, including per-slot evidence-quality markers, are in an
evaluator-private definition whose committed SHA-256 is recorded by the public
protocol. The public artifact exposes only aggregate marker coverage, never
the marker-to-held-out-slot mapping. That private definition must remain
unavailable to the diagnosing agent and prompt/rubric tuning until development
is frozen. Every held-out run needs a fresh capture ID; development and
held-out packets cannot share a source run ID or capture hash. Recognizable
public sample shapes remain a contamination risk and must be reported with the
outcomes.

The existing September 2026 smoke packet predates that freeze. It is a
development warmup outside all proposed denominators. Linux culture quarantine
#929 remains unchanged. The frozen model baseline is the same `gpt-5.4-mini`
alias used by that warmup through Copilot CLI 1.0.83; the provider did not
expose an immutable model-build version, and the protocol records that
limitation instead of fabricating one. Full empirical acceptance and closure of
#921 remain pending all 15 case-runs, real human judgments, independent reviews,
adjudication where needed, and dimension-separated outcomes.
