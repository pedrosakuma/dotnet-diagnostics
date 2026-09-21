# Advisory LLM semantic follow-up

This document defines the separately versioned semantic follow-up to the
partial development assessment recorded in
[`advisory-llm-assessment-results.md`](advisory-llm-assessment-results.md).
It does not rewrite that experiment, convert its three completed comparisons
into eight, create human review, estimate accuracy, or establish a CI gate.

The first frozen follow-up was blocked by CLI event compatibility; see
[`advisory-llm-followup-results.md`](advisory-llm-followup-results.md) for the
preserved five-call, zero-comparison outcome.
The separately declared second iteration completed four primary comparisons
and both order controls; see
[`advisory-llm-followup-02-results.md`](advisory-llm-followup-02-results.md).

## Two independent dimensions

The follow-up records technical schema compliance separately from semantic
usability.

A retained response can remain `schemaInvalid` under its original contract
while a new, frozen controller plan safely reads bounded strings from declared
JSON pointers. The raw response, SHA-256, source status, and source file remain
unchanged. The derived view assigns controller-only opaque claim IDs mapped to
the exact source text and citation-array pointers. It does not rename a source
field, fabricate a source hypothesis ID, repair a citation, or mark the old
response valid.

Unreadable, missing, duplicate, malformed, oversized, or wrongly typed semantic
fields fail closed during file-only preflight. Citation existence is recorded
mechanically and separately from the comparator's semantic support judgment.

## Frozen scope

The v1 follow-up plan contains exactly:

- the five live-origin slots, case 01 through case 05;
- retained Phase-A semantic extraction for cases 01 through 04;
- at most one fresh evidence-only Phase-A call for case 05;
- one primary Phase-B comparison for each usable case;
- reversed-order Phase-B controls for predeclared cases 01 and 03;
- at most eight new calls: one A and seven B, with no retry or fallback;
- at most two new calls per case, including any reversed-order control;
- exact source summary, case, response, normalized-payload, projection, prompt,
  and applicable recovered-seal hashes;
- one coherent continuation source inventory, plus the exact previous pilot
  summary hash recorded by that continuation;
- exact models, transport versions, byte/time budgets, rubric hash, and prompt
  template fingerprints;
- the glossary version, glossary hash, and controller-only source citations.

Cases 06 through 08 are not rerun or pooled with the live-origin follow-up.
The order controls compare source-mapped support, certainty,
uncertainty/abstention usefulness, and next-step usefulness. They do not compare
opaque candidate IDs, choose a favorable ordering, or select a winner.

## Revised comparator rubric

The frozen `advisory-semantic-followup-v1` rubric requires the comparator to
apply these rules:

- numerical levels alone do not demonstrate growth;
- relative terms such as "large" or "elevated" require a baseline or reference,
  otherwise they are heuristic or inferred;
- measurements without time alignment do not establish simultaneity or
  causation;
- a plausible alternative is not automatically an evidence-supported
  hypothesis;
- missing data supports uncertainty and a request for evidence, not a health or
  root-cause assertion;
- a proposed operation, including disabling truncation, must not be assumed
  available or safe;
- candidate agreement is not truth;
- declared posture is self-description, not evidential support.

The result contains no winner, accuracy score, weighted score, or CI verdict.

## Frozen counter measurement glossary

The follow-up freezes a neutral counter data dictionary before any calls:

- `value` is the last observed sample for that counter key, not a
  capture-wide mean or total;
- `maximumObserved` is the maximum raw value observed across collection ticks;
- `kind="Mean"` is the producer's interval mean for the last sample;
- `kind="Sum"` is the raw increment reported for the producer interval;
- the blinded projection does not expose the first sample, a time series,
  `IntervalSec`, or `DisplayRateTimeScale`.

The evaluator must not infer growth or a full-window total from `value`, and
must not divide it by `captureSeconds` to manufacture a rate. This glossary
defines measurement semantics only; it supplies no expected diagnosis.

The frozen plan and summary carry controller-visible provenance for source
revision `f9c2ef8e`:

- `src/DotnetDiagnostics.Core/Counters/CounterValue.cs:93-107`;
- `src/DotnetDiagnostics.Core/Counters/EventPipeCounterCollector.cs:462-473`;
- `src/DotnetDiagnostics.Core/Counters/EventPipeCounterCollector.cs:501-532`;
- `tests/DotnetDiagnostics.ScenarioEvaluation.Tests/BlindedDiagnosticToolGateway.cs:170-183`.

Only the neutral glossary text enters model prompts. The source revision, file
paths, line ranges, and provenance subjects remain outside prompts.

The retained Phase-A text for cases 01 through 04 predates this glossary. Its
old prompt hash is validated against the frozen v2 Phase-A builder, not against
the new follow-up prompt. Case 05 is the only Phase A that receives the glossary
and common-`id` contract. Phase B receives a neutral disclosure that some
candidate text predates the glossary, without receiving a candidate mapping,
source label, schema status, prior verdict, or expected diagnosis. This prevents
measurement-context omissions from being attributed solely to reasoning while
leaving semantic support judgments unchanged.

## Case 05 Phase A

Case 05 is the only slot allowed a new Phase-A invocation. It receives the same
unchanged evidence-only projection and no prior claims, hypotheses, case label,
source-model identity, schema status, score, or assessment result.

Its new contract uses a common `id` property for observations, hypotheses, and
alternatives. Strict schema compliance is recorded independently. If a bounded,
syntactically safe response fails that schema but the declared semantic fields
remain safely readable, the format failure stays visible and the semantic view
may proceed. The response is never rerun to obtain a desired interpretation.
The raw response and semantic view are sealed with create-new semantics before
Phase B.

## File-only freeze and preparation

### Frozen transport byte domains

The plan binds `copilot-cli-jsonl-transport-budget-v1`, evidenced by transport
revision `c2cdcfe3997435f10015d64ee0e51d9b3170c177`:

- decoded `assistant.message.data.content`: 65,536 bytes, equal to the existing
  `MaximumResponseBytes`;
- per-assistant JSON event envelope: 262,144 bytes;
- cumulative non-answer JSONL framing: 1,048,576 bytes;
- insertion-time line cap: `6 * 65,536 + 262,144 = 655,360` bytes;
- stderr: 32,768 bytes.

These are finite transport safety policies, not promises about Copilot CLI
output size. The decoded assistant payload cap remains 65,536 bytes. The older
transport incorrectly applied that cap to the complete JSONL stdout stream,
which can contain prompt echo, reasoning, and answer events. The exact event or
stream that overflowed in each historical call was not retained and remains
unknown.

Before drafting the plan, run only the target-free version check:

```bash
/absolute/copilot --version
```

Freeze that exact output into both model `transportVersion` fields. Do not copy
the pilot's historical CLI version. Preparation and execution reject differing
phase versions, and execution rejects an installed version that changed after
the plan was frozen. Version detection does not invoke a model.

The controller first creates a draft conforming to
`Calibration/advisory-llm-followup-v1.plan.schema.json`, with a blank
`planFingerprint`. Freezing verifies every scoped source binding and semantic
pointer before writing the canonical plan:

```bash
DOTNET_DIAGNOSTICS_ADVISORY_LLM_OPERATION=followup-freeze \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_PROTOCOL=/absolute/frozen-v2-protocol.json \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_FOLLOWUP_DRAFT=/absolute/followup.draft.json \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_FOLLOWUP_PLAN=/absolute/followup.plan.json \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_PATH=/absolute/copilot \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_HOME=/absolute/empty-home \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_WORK_ROOT=/absolute/outside-repository \
/home/pedrotravi/.dotnet/dotnet \
  /home/pedrotravi/.dotnet/sdk/10.0.201/dotnet.dll \
  test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release \
  --filter FullyQualifiedName~ExplicitLocalWorkflow_PreparesOrRunsFrozenProtocol
```

The freeze entrypoint detects the installed CLI version before writing and
rejects a draft that binds a different value. It does not authenticate or
invoke a model.

Optional preparation writes create-new projections and the case 05 Phase-A
prompt without invoking a model or target:

```bash
DOTNET_DIAGNOSTICS_ADVISORY_LLM_OPERATION=followup-prepare \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_PROTOCOL=/absolute/frozen-v2-protocol.json \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_FOLLOWUP_PLAN=/absolute/followup.plan.json \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_OUTPUT_DIRECTORY=/absolute/new-preflight-output \
/home/pedrotravi/.dotnet/dotnet \
  /home/pedrotravi/.dotnet/sdk/10.0.201/dotnet.dll \
  test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release \
  --filter FullyQualifiedName~ExplicitLocalWorkflow_PreparesOrRunsFrozenProtocol
```

Both operations are file-only. They use one declared continuation source root
and scoped `continuation-summary.json`,
`<slot>/continuation-case-result.json`, and recovered-seal paths. They bind the
previous pilot summary by hash but do not open it, discover neighboring reports,
or access held-out inputs.

## Explicit execution

Execution requires a new output directory, the isolated local Copilot CLI
configuration, and a follow-up-specific opt-in:

```bash
DOTNET_DIAGNOSTICS_ADVISORY_LLM_OPERATION=followup \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_FOLLOWUP=1 \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_PROTOCOL=/absolute/frozen-v2-protocol.json \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_FOLLOWUP_PLAN=/absolute/followup.plan.json \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_OUTPUT_DIRECTORY=/absolute/new-followup-output \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_PATH=/absolute/copilot \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_HOME=/absolute/empty-home \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_WORK_ROOT=/absolute/outside-repository \
/home/pedrotravi/.dotnet/dotnet \
  /home/pedrotravi/.dotnet/sdk/10.0.201/dotnet.dll \
  test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release \
  --filter FullyQualifiedName~ExplicitLocalWorkflow_PreparesOrRunsFrozenProtocol
```

Normal tests cannot execute models. A global isolation, authentication, model,
or CLI prerequisite failure, including an unsupported CLI event type, aborts
remaining calls. Other unavailable or
invalid inputs skip only their dependent comparisons. All attempted calls,
bounded raw responses, unknown token/cost fields, skips, and failures remain in
the output. Technical completion means the declared safe semantic operations,
five primary comparisons, and two controls completed; it says nothing about
whether any claim was supported.

Transport byte-domain limits are frozen into the plan and summary. They remain
controller-only and do not enter model prompts. This protocol does not silently
raise them or promise unavailable token, dollar, or provider-version
guarantees.
