# Allocation focused field selection, Protocol 10

Issue: [#1374](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1374)

Parent: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)

Blocked follow-up: [#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

Prerequisite #1372 merged as
`45f083f7621df6c435d03167d409ffd238f5f1d6`.

## Question and decision rule

Protocol 10 is a separately versioned comparison of:

1. no statistical fields;
2. categorical quality plus median/raw MAD; and
3. categorical quality plus bounded top-K membership Jaccard.

It asks which unique smallest model-visible package best reduces unsupported
certainty while preserving semantic claim support and safety.

The selector uses no weighted score. It applies these gates in order to the
complete held-out inventory:

1. complete inventory and assessability, exact citations, no unauthorized
   actions, valid artifact accounting, and no unresolved harness defect;
2. exclude any candidate that increases any unsupported-certainty category
   versus the no-statistical-fields control;
3. retain the arm or arms with the lowest total unsupported-certainty finding
   count;
4. retain the uniquely highest exact supported/total claim rate;
5. retain the lowest total unhelpful-next-step count;
6. retain the lower model-visible leaf-field count;
7. retain the lower diagnosing-plus-assessor transport-output byte count.

Any remaining tie or validity failure is `INCONCLUSIVE`. A unique control is
`NO-GO`; a unique median/MAD or Jaccard candidate is `GO`. Useful-next-step
count is descriptive only and cannot compensate for unsupported certainty or
claim-support loss.

## Preservation and field-only isolation

Protocols 1 through 9, their attempts, reports, denominators, hashes, and
conclusions remain unchanged. Protocol 10 hash-guards the preserved Protocol 9
file in addition to the existing Protocol 1 through 8 guards.

All three arms receive byte-identical:

- selected MCP tool definitions;
- summary framing;
- assumptions;
- limitations; and
- neutral guard wording.

The only model-visible arm differences are the declared candidate response
fields:

| Arm | Candidate leaf-field count |
|---|---:|
| No statistical fields | 0 |
| Categorical plus median/raw MAD | 10 |
| Categorical plus Jaccard | 16 |

Deterministic tests remove every candidate pointer and prove that the remaining
serialized response is byte-identical across arms. The response mutation
inventory remains byte-identical to Protocol 9 because Protocol 10 compares the
same three field packages; the projection identities, versions, sources, and
per-arm fingerprints are new.

## Deterministic evaluator closure

Before freeze, deterministic tests verified:

- exact citation replay and canonical document-root handling from #1364;
- retained MCP exchange and assessor artifact accounting;
- the strict `claims[*].text` alias normalization and conflict handling from
  #1368;
- complete bounded canonical validation from #1372, including required fields,
  types, enums, indices, duplicate identities, case-only ambiguity, canonical
  shadows, alias conflicts, contradictory next-step classifications, JSON
  depth, response size, diagnostic count, and diagnostic-byte bounds;
- successful ignored annotations remain unscored and produce byte-identical
  canonical scoring bytes;
- ignored-annotation diagnostic bytes are included exactly once in the
  validity gate's artifact accounting;
- field-only response and tool-definition isolation;
- every validity failure and every lexicographic selection/tie branch;
- the empty-output-root guard and at-most-once attempt policy; and
- all three arms collecting three fresh allocation captures plus one
  reproducibility query through the repository-built MCP server over real
  stdio.

The focused pre-freeze validation passed 44 tests: 41 deterministic protocol,
replay, validation, projection, accounting, and selector tests plus the three
real-MCP stdio arm tests.

## Independent pre-freeze review

An independent `claude-sonnet-5.5` review inspected the protocol, projections,
prompts, model freeze, schemas, budgets, failure policy, validator policy,
fresh identities, real-MCP wiring, accounting, selector, and repository diff.
The implementation was authored with `gpt-5.6-sol`, so the review was
cross-model.

The reviewer reported no significant findings. It independently confirmed:

- Protocol 9's file hash and unchanged status;
- all case, workload, capture, and arm-order identities are fresh;
- the three-capture sequence fits the frozen capture budget;
- ignored annotation bytes cannot bypass or overflow accounting;
- the selector implements the required gate order; and
- the diff changes evaluator and research surfaces only, not product source.

## Frozen Protocol 10 inventory

The machine-readable contract is
[`operational-mcp-allocation-focused-selection-v10.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-allocation-focused-selection-v10.protocol.json).

| Item | Frozen value |
|---|---|
| Protocol version | `10.0.0` |
| Frozen at | `2026-11-03 13:53:04 UTC` |
| Freeze commit | `cdb606c72aa95e856ebb498460bff84f96ad0f6f` |
| Protocol file SHA-256 | `91e52581d01c480c0982ebbbcef3410dc2147d4c4942ed00b3295a4903fbd3d2` |
| Protocol fingerprint | `2c2d19d50b4d967494cea69eb9ab497ccf20d7187513ea950fb034bddb320fe8` |
| Inventory fingerprint | `9313a6292282548dd707bc0ac65f456d3a8099e61d5f2d56eef29bc77ff6a0b8` |
| Projection inventory fingerprint | `5f55d7df5963728cc0c1c13bfb202ce0eb73b46ac491288bdfd7aaeae2d459eb` |
| Shared guard fingerprint | `a8dd298d1e3f32641ced8f982048b876d0aa896b5437f5da147ed080496dfdea` |
| Mutation inventory fingerprint | `841c4e31f37c34ff74eaadd2d4b72767cacdab842ccf1f57cbea1d3f59277f5d` |
| Shared tool-definition fingerprint | `84f67ea8a518dad9217a01c604431390557a3a42cb86e48e8f9ac44a21f7bebb` |
| Selected-tool catalog fingerprint | `c4615566f749f28885206ec16649843d4d8b337ea53c5fd08760b8be0365a0de` |
| Product source fingerprint | `be394d41d2a808b913e162c44be52ee323d6e13bc3bbdcb9cf825e1d29bb47cb` |
| Preserved Protocol 9 file SHA-256 | `aa5a37483b9212db115bec87f871c1def53fd73702a849ebb402ecf3234585bf` |
| Diagnosing model | `github-copilot-cli/gpt-5.3-codex`, default context |
| Assessor model | `github-copilot-cli/gpt-5.6-sol`, default context |

Per-arm response projection fingerprints:

| Arm | Fingerprint |
|---|---|
| No statistical fields | `c9ed69a2229eee6bed3be98d42eddaa483578cdc5c7101a812937abb9cecbc5b` |
| Categorical plus median/raw MAD | `6816ac02dbd8b291b1cb2ed9de74dcf4e01fa653ae638a71d9a9c54a2290b12c` |
| Categorical plus Jaccard | `772df87c4c870d29ed282d602e262b0a64ffb7ec4eb50c35cf3a2a40f0d4241d` |

The finite inventory contains:

- development: one positive and one healthy case per arm, six attempts total;
- held-out: one positive, one healthy, one competing-explanation, and one
  degraded-evidence case per arm, twelve attempts total.

Every case id, workload identity, capture identity, arm ordering, target
process, capture, attempt directory, and report artifact is fresh. Each
attempt is permitted to start at most once. Failures, aborts, annotations, and
`notAssessable` attempts remain in denominators without favorable retries or
replacement attempts.

## One-time empirical execution

The single permitted pilot invocation used GitHub Copilot CLI transport
`1.0.92`, `gpt-5.3-codex` for diagnosis, and `gpt-5.6-sol` for isolated
assessment. It ran from `2026-11-03 13:59:00 UTC` to `14:18:15 UTC`.

The assessor schema preflight passed before the first target started. The
development partition passed every prerequisite, so the complete held-out
partition ran:

- attempts: `6 / 6` development and `12 / 12` held-out;
- assessable attempts: `18 / 18`;
- successful predeclared MCP exchanges: `72 / 72`;
- failed MCP exchanges: `0`;
- follow-up MCP calls: `0`;
- unauthorized actions: `0`;
- ignored additive annotations emitted by the empirical assessor: `0`;
- `notAssessable` attempts: `0`; and
- attempts with an artifact-accounting mismatch: `0 / 18`.

No attempt was retried or replaced.

Development results are descriptive only and are not selection evidence:

| Arm | Attempts | Supported / total claims | Partial claims | Unsupported certainty by category | Useful | Unhelpful |
|---|---:|---:|---:|---|---:|---:|
| No statistical fields | 2 / 2 assessable | 7 / 10 (70.0%) | 3 | dominance 1; workload equivalence 1; causality 1 | 4 | 0 |
| Categorical plus median/raw MAD | 2 / 2 assessable | 5 / 10 (50.0%) | 5 | dominance 3; workload equivalence 1 | 5 | 0 |
| Categorical plus Jaccard | 2 / 2 assessable | 9 / 11 (81.8%) | 2 | dominance 1 | 4 | 0 |

The complete held-out denominators used by the selector were:

| Arm | Attempts | Supported / total claims | Partial | Unsupported | Unsupported certainty by category | Useful | Unhelpful | Transport bytes |
|---|---:|---:|---:|---:|---|---:|---:|---:|
| No statistical fields | 4 / 4 assessable | 9 / 17 (52.9%) | 7 | 1 | dominance 6; workload equivalence 1; causality 1 | 11 | 1 | 1,426,242 |
| Categorical plus median/raw MAD | 4 / 4 assessable | 12 / 20 (60.0%) | 8 | 0 | dominance 6; workload equivalence 2; severity 2 | 12 | 3 | 1,446,399 |
| Categorical plus Jaccard | 4 / 4 assessable | 15 / 20 (75.0%) | 5 | 0 | dominance 3; exactness 1; causality 1 | 10 | 2 | 1,406,621 |

Artifact accounting was exact:

| Artifact accounting | Bytes |
|---|---:|
| Raw MCP responses | 814,652 |
| Model-visible projections | 784,813 |
| Retained MCP exchange total | 1,599,465 |
| Retained assessor diagnostics/replay/annotations | 0 |
| Retained total | 1,599,465 |

The retained `pilot-report.json` is `3,987,095` bytes with SHA-256
`97b2695b5edbc7952b3502201ce95d9997e774aebef61f57ab062141ffb57930`.
It remains outside the repository because operational reports can contain
local paths and target-controlled names.

## Selector execution

The selector recorded every step:

1. **Validity: passed.** The complete held-out inventory was assessable with
   exact citations, no unauthorized actions, exact accounting, and no
   unresolved harness defect.
2. **Category safety: passed, with both candidates excluded.**
   - Median/MAD increased workload-equivalence findings from 1 to 2 and
     severity findings from 0 to 2 versus control.
   - Jaccard reduced total findings but introduced one exactness finding versus
     zero in control.
3. **Primary ordering: selected control.** After category-safety exclusions,
   the no-statistical-fields arm was the only survivor, with 8 total findings.
4. **Decision: `NO-GO`.** The no-statistical-fields control was the unique
   lexicographic selection.

The higher claim-support rate and lower total unsupported-certainty count of
the Jaccard arm cannot compensate for introducing a new unsupported-certainty
category because category safety is an earlier frozen gate.

## Decision

**Protocol 10 decision: `NO-GO`.**

Neither median/raw MAD nor bounded top-K membership Jaccard is promoted as a
model-visible allocation measurement-quality field package. The
no-statistical-fields control is the unique safe selection under the frozen
rule.

Protocol 10 authorizes no product formula, schema, collector, retention, tool,
catalog, wording, directional allocation implementation, or automatic
severity, dominance, causality, regression, health, leak, or
optimization-priority verdict.

#1350 remains blocked and unstarted because Wave 0 did not identify a safe
model-visible quality subset for directional allocation feasibility.

## Limitations

- The finite inventory used one diagnosing model and one assessor model
  through one provider and transport version.
- Provider token and cost usage were unavailable. Calls, turns, bytes, capture
  time, wall time, attempts, and artifact accounting remained bounded.
- No model made a follow-up MCP call, so the run does not compare the value of
  arm-specific follow-up evidence.
- The empirical assessor emitted no additive annotations. Deterministic tests,
  rather than empirical incidence, validate their canonical gating,
  score-neutrality, privacy bounds, and accounting.
- The result evaluates interpretation of the current allocation
  reproducibility response. It does not establish a baseline/current
  allocation quantity, workload normalization rule, delta, uncertainty
  interval, severity, regression, causality, health, or optimization priority.

## Boundaries

- Evidence and evaluator protocol only.
- Protocols 1 through 9 remain preserved and unchanged.
- No product source changed.
- No directional allocation product work was started.
- #1350 remains blocked.
