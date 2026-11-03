# Allocation measurement-quality field isolation, Protocol 8

Issue: [#1366](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1366)

Parent: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)

Blocked follow-up: [#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

Evaluator reliability prerequisite:
[#1364](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1364)

## Question

Protocol 8 is a separately versioned rerun of the uniform-guard allocation
field isolation after the evaluator reliability correction from #1364. It asks
whether any unique smallest allocation measurement-quality field subset
improves semantic claim support or next-step usefulness without worsening any
unsupported-certainty category or unauthorized-action safety when fields are
the only model-visible arm difference.

Protocols 1 through 7, their reports, denominators, hashes, and conclusions
remain unchanged. Protocol 7 remains **INCONCLUSIVE**; Protocol 8 does not
reinterpret or replace it.

## Reliability contracts inherited from #1364

Protocol 8 freezes and deterministically verifies the corrected evaluator
contracts before empirical execution:

- citation resolution accepts retained-document RFC 6901 pointers and the
  exact Copilot CLI virtual-envelope form `#/json` or `#/json/...`;
- a real top-level `json` property retains precedence over the virtual
  envelope;
- invalid, unavailable, outside-evidence, malformed, and ambiguous locations
  remain categorized failures without fuzzy lookup;
- rejected assessor responses retain SHA-256, exact UTF-8 byte counts,
  failure category, JSON path, property, value kind, truncation state,
  redaction state, and bounded canonical replay evidence;
- replay JSON is privacy-safe and capped at 16 KiB plus the remaining
  per-attempt artifact budget;
- retained artifact accounting is exactly the sum of retained MCP exchange
  bytes and retained assessor-failure replay bytes.

The original 42 Protocol 7 citation locations still replay successfully
through the exact `#/json/...` namespace contract. Synthetic root and nested
`text` failures still reproduce the same strict-schema failure category, path,
property, and value kind after canonical redaction.

## Field-only isolation

The six arms are:

1. no statistical fields;
2. categorical quality;
3. categorical plus median/raw MAD;
4. categorical plus bounded top-K membership Jaccard;
5. categorical plus bounded shared-key Spearman rank agreement; and
6. complete current data fields.

Every arm receives byte-identical selected tool definitions, summary,
assumptions, limitations, and neutral guard:

> These observations do not establish exactness, workload equivalence, zero
> activity from missing evidence, dominance, severity, causality, regression,
> health, or optimization priority.

After the declared candidate pointers are removed, every remaining
model-visible response node is byte-identical across arms. Structured failures,
unavailable states, raw evidence, quality states, and non-candidate evidence
remain retained.

## Fresh frozen inventory

Protocol 8 uses fresh case ids, workload parameters, capture bounds, arm-order
seed, target processes, captures, attempt paths, and output root. Deterministic
tests compare case ids, workload identities, capture identities, and arm
ordering against every preserved Protocol 1 through 7 artifact.

The finite inventory is:

- development: one positive and one healthy case per arm;
- held-out: one positive, one healthy, one competing-explanation, and one
  degraded-evidence case per arm.

Each arm precollects three fresh allocation captures and one
allocation-reproducibility query through the repository-built MCP server over
real stdio before isolated diagnosis and assessment.

Every preflight and started arm is retained. Failed, partial, aborted,
budget-exhausted, transport-failed, model-invalid, assessment-missing,
malformed, timed-out, and `notAssessable` attempts remain in denominators. Each
finite arm may start at most once. No favorable retry or replacement attempt is
permitted.

The machine-readable contract is
[`operational-mcp-allocation-field-isolation-v8.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-allocation-field-isolation-v8.protocol.json).

| Item | Frozen value |
|---|---|
| Protocol version | `8.0.0` |
| Frozen at | `2026-11-03 08:25:11 UTC` |
| Post-run freeze-state commit | `f302fdf` (`2026-11-03 09:05:04 UTC`) |
| Protocol file SHA-256 | `9a94a6158c6e9da3f125a2f9fb151a0279bbe1ad3d33988a40ee9a2c3227fac3` |
| Protocol fingerprint | `be4235d3f7063a8752e5c6dfc9be4d9d0494bcee1ad8a1e303b1252620003888` |
| Inventory fingerprint | `dbb9dcaf01f047e69302aa31e6a6638ab711e0fc9e09ed2bf1b713221af36259` |
| Projection inventory fingerprint | `cfe5de2db20b2c592a55d3f7a04a7e5df4cb03f09e55567722a4b0c8936339d2` |
| Shared guard fingerprint | `a8dd298d1e3f32641ced8f982048b876d0aa896b5437f5da147ed080496dfdea` |
| Mutation inventory fingerprint | `e2bc4f48b62d8fd9c08fba79825f5776bb1ec1aa93bfad6deedfd8cbb40e5635` |
| Shared tool-definition fingerprint | `84f67ea8a518dad9217a01c604431390557a3a42cb86e48e8f9ac44a21f7bebb` |
| Selected-tool catalog fingerprint | `c4615566f749f28885206ec16649843d4d8b337ea53c5fd08760b8be0365a0de` |
| Product source fingerprint | `be394d41d2a808b913e162c44be52ee323d6e13bc3bbdcb9cf825e1d29bb47cb` |
| Preserved Protocol 7 file SHA-256 | `5325b3f59416ada38ee6569f75e93ed0650cf940e2f0a50e15b69f5fef78accd` |
| Diagnosing model | `github-copilot-cli/gpt-5.3-codex`, default context |
| Assessor model | `github-copilot-cli/gpt-5.6-sol`, default context |
| Assessor additive property | root `nextSteps`: `stringArray` |

The family-level inventory fingerprint intentionally equals Protocol 7 because
that object records the same one eligible family and selected tool surface.
Fresh cases and execution identities are part of the full protocol
fingerprint. The complete-current projection intentionally retains its product
source identity; the other five projection identities are newly versioned.

Per-arm response projection fingerprints:

| Arm | Fingerprint |
|---|---|
| No statistical fields | `2cb73aa2cfeb9a7e3acd608ecf886b38f4ade4c37c77278cdb88e0e72f9d79df` |
| Categorical quality | `3bbe2dfc49d41f0f263c876fd3e7a88a466780557504345d8e7d7f9edcefc2a6` |
| Categorical + median/MAD | `19c0fb5a05f07c021659edcf38d3e8a0090e53e24061546e074641dbb92faf8c` |
| Categorical + Jaccard | `e07bf5ff2af2a022c5154bd9411dda0aa87957bf7692fec9f59f1307eb130198` |
| Categorical + Spearman | `0fc78b057753e112576573cc7e08901717efc494666534d15a1801f613da2c2a` |
| Complete current fields | `4eb6feebc326128df41a12223ac3893c393aa4941b4d701668620fb4c733f3a4` |

## Decision rule

Every candidate arm is compared independently with the no-statistical-fields
reference. Unsupported certainty is compared by category, not as one
offsettable total: any category increase is a worsening and blocks `GO`; a
category decrease is an improvement only when no category increases.

Select only a unique smallest field subset that improves semantic claim
support, useful next steps, unhelpful next steps, or an unsupported-certainty
category without worsening any unsupported-certainty category or
unauthorized-action safety. Equal-complexity ties, incomplete inventory,
incomplete assessability, or unresolved harness validity remain
**INCONCLUSIVE**. There is no hidden weighted score.

## Independent pre-freeze review

An independent `claude-sonnet-5.5` review inspected the protocol, prompts,
schemas, budgets, models, projections, accounting, failure policy, evaluator
reliability, fresh identities, preservation hashes, and tests before the
freeze. It found two medium-severity issues:

1. the draft decision code compared unsupported-certainty totals rather than
   each category independently; and
2. the draft freshness test compared too little of the Protocol 8 identity
   against only Protocol 7.

Before freeze, the decision code was changed to reject any per-category
worsening and the tests were expanded across Protocols 1 through 7, workload
and capture identities, per-case arm ordering, output root, and per-arm
projection identities. A second independent narrow review found no actionable
issue. All 32 deterministic protocol-contract tests and all six
repository-built real stdio gateway arms passed before empirical execution.

## One-time empirical execution

The single permitted pilot invocation used GitHub Copilot CLI transport
`1.0.92`, `gpt-5.3-codex` for diagnosis, and `gpt-5.6-sol` for isolated
assessment. It ran from `2026-11-03 08:30:06 UTC` to `09:04:05 UTC`.

A shell variable-name error occurred before the test process was invoked,
before the output root was created, and before any protocol preflight or
attempt started. The corrected command invoked the frozen pilot exactly once.
No empirical attempt was retried or replaced.

Assessor preflight passed. All 36 fresh attempts completed their
evaluator-owned three-capture plus one-query MCP stdio sequence. The inventory
retained 12 development and 24 held-out attempts, 144 successful real-MCP
exchanges, and no model-requested follow-up MCP calls.

Artifact accounting matched every serialized exchange and rejected assessment
exactly:

| Artifact accounting | Bytes |
|---|---:|
| Raw MCP responses | 1,495,024 |
| Model-visible projections | 1,439,725 |
| Retained MCP exchange total | 2,934,749 |
| Retained rejected-assessor replay | 1,567 |
| Retained total | 2,936,316 |
| Attempts with an accounting mismatch | 0 / 36 |

The complete held-out denominators were:

| Arm | Attempts | Assessable | Claim support | Unsupported certainty by category | Useful next steps | Unhelpful next steps |
|---|---:|---:|---:|---|---:|---:|
| No statistical fields | 4 | 4 | 80.0% across 20 claims | dominance 4; exactness 1; workload equivalence 3; causality 1; missing-as-zero 1 | 4 | 0 |
| Categorical quality | 4 | 4 | 78.6% across 21 claims | dominance 3; exactness 2; workload equivalence 2; causality 1; severity 1 | 4 | 0 |
| Categorical + median/MAD | 4 | 4 | 86.8% across 19 claims | dominance 1; workload equivalence 1 | 4 | 1 |
| Categorical + Jaccard | 4 | 4 | 85.7% across 21 claims | dominance 4 | 4 | 2 |
| Categorical + Spearman | 4 | 3 | 83.3% across 15 claims | dominance 2; causality 1; severity 1 | 3 | 1 |
| Complete current fields | 4 | 4 | 84.2% across 19 claims | dominance 4; exactness 1 | 4 | 0 |

All 36 citation assessments passed through the corrected namespace contract.
Thirty-five automated assessments passed. Unauthorized actions remained
`0`. One held-out attempt remained incomplete and stayed in its denominator:

- categorical plus Spearman, degraded-evidence case: the assessor returned an
  unexpected string property at `$.claims[0].text`.

The rejection retained category `unexpectedProperty`, property `text`, value
kind `String`, raw-response SHA-256
`d96ec6e902be106c4ecb0c602e95cf399adeb6a063b8e0a21a8927c1c05f739a`,
raw-response byte count `4,858`, assistant-content SHA-256
`72dfdc871b76fbd2ae076a1e7bd961945d2fa0326adb267d3eee3ee76b2f2dbb`,
assistant-content byte count `4,827`, and `1,567` bytes of bounded canonical
replay evidence. The replay was redacted and not truncated. The attempt was
not rerun.

The retained `pilot-report.json` is 7,428,956 bytes with SHA-256
`c4604aab86986d74774e6bff2a0677633d2fdd1dac64fa1dade42952e91faeef`.
It remains outside the repository because operational reports can contain
local paths and target-controlled names.

The freeze was an independently reviewed working-tree state, not a commit that
predated execution. Commit `f302fdf` was created one minute after the run from
that unchanged state. The report's `evaluatorCommit` is therefore the
pre-branch parent `97d0466562c85d616c735502d0707f414de9fddf`, and evaluator
and decision-code identity rests on the recorded review rather than a
pre-execution commit hash. The report's protocol, inventory, projection,
per-arm projection, catalog, tool-definition, and product fingerprints match
the post-run freeze-state commit.

## Decision

**Field-only promotion decision: `INCONCLUSIVE`.**

No unique smallest field subset satisfies the frozen selection rule.

- categorical quality worsened exactness and introduced severity findings;
- median/MAD and Jaccard both showed higher finite claim support and no
  per-category unsupported-certainty worsening, but they are equal-complexity
  candidates rather than one unique smallest subset;
- median/MAD retained one unhelpful-next-step attempt and Jaccard retained two;
- Spearman was not fully assessable and introduced severity findings; and
- complete-current showed a finite support improvement without a category
  increase, but it is not smaller than the tied median/MAD and Jaccard
  candidates.

The single unassessable held-out attempt independently forces the complete
family gate to remain inconclusive. It is not converted into favorable
evidence. The equal-complexity finite tie also prevents selection of a unique
smallest subset. Therefore no categorical, median/MAD, Jaccard, Spearman, or
complete-current field group is selected for promotion.

Protocol 8 authorizes no product, schema, collector, retention, tool, catalog,
wording, or directional allocation redesign. #1350 remains blocked.

## Limitations

- The inventory is finite and uses one diagnosing model and one assessor model
  through one provider.
- One held-out attempt was incomplete; its failure remains in its arm
  denominator and in the family assessability gate.
- Provider token and cost usage were unavailable. Calls, turns, bytes,
  capture time, wall time, attempts, and artifact accounting remained bounded.
- No model made a follow-up MCP call, so the run does not compare the value of
  arm-specific follow-up evidence.
- The result evaluates interpretation of the current allocation
  reproducibility response. It does not establish a baseline/current
  allocation quantity, workload normalization rule, delta, uncertainty
  interval, severity, regression, causality, health, or optimization priority.

## Boundaries

- Evidence and evaluator protocol only.
- No product formula, schema, collector, retention, tool, catalog, wording, or
  directional allocation implementation change.
- No automatic severity, dominance, causality, regression, health, leak, or
  optimization-priority verdict.
- Protocols 1 through 7 remain preserved and unchanged.
- #1350 remains blocked; Protocol 8 does not start its implementation.
