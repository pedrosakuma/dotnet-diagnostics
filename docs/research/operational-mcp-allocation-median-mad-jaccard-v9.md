# Allocation median/MAD versus Jaccard, Protocol 9

Issue: [#1369](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1369)

Parent: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)

Blocked follow-up: [#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

Prerequisites:

- Protocol 8 merged as
  `9ee98a479cf0397537ca12eab2539140699efaff`;
- the strict assessor claim-text alias merged as
  `1d0718e94e1653ad26e4132270c5e59a8cbda2a3`.

## Question

Protocol 9 is a separately versioned focused comparison of:

1. no statistical fields;
2. categorical quality plus median/raw MAD; and
3. categorical quality plus bounded top-K membership Jaccard.

It asks which unique smallest model-visible package best reduces unsupported
certainty while preserving semantic claim support and safety.

Protocols 1 through 8, their reports, attempts, denominators, hashes, and
conclusions remain unchanged. Protocol 9 does not reuse or reinterpret any
Protocol 8 attempt.

## Field-only isolation

Every arm receives byte-identical selected tool definitions, summary,
assumptions, limitations, and neutral guard:

> These observations do not establish exactness, workload equivalence, zero
> activity from missing evidence, dominance, severity, causality, regression,
> health, or optimization priority.

The only model-visible arm differences are the frozen response fields:

| Arm | Candidate leaf-field count |
|---|---:|
| No statistical fields | 0 |
| Categorical plus median/raw MAD | 10 |
| Categorical plus Jaccard | 16 |

Deterministic tests count the projected candidate leaves and verify those exact
values. Every response byte outside the declared candidate pointers is
identical across arms.

## Frozen lexicographic selector

Protocol 9 uses no weighted score. It applies these gates in order:

1. complete held-out inventory and assessability, exact citations, no
   unauthorized actions, exact retained-artifact accounting, and no unresolved
   harness or identity defect;
2. exclude any candidate that increases any unsupported-certainty category
   versus control;
3. retain the arm or arms with the lowest total unsupported-certainty finding
   count;
4. retain the highest exact fully-supported/total-claim rate, with
   `partiallySupported` claims remaining only in the denominator;
5. retain the lowest total assessor-listed unhelpful-next-step count;
6. retain the lowest model-visible leaf-field count;
7. retain the lowest diagnosing-plus-assessor transport-output byte count.

A remaining tie is `INCONCLUSIVE`. A unique no-statistical-fields control is
`NO-GO`; a unique median/MAD or Jaccard selection is `GO`. Useful-next-step
count is reported but cannot compensate for unsupported certainty or
claim-support loss.

The report retains every selector step and its surviving arms. Deterministic
tests cover every validity failure, category-safety exclusion, primary
ordering, exact rational support comparison, equal support rates with different
claim counts, multi-step unhelpful aggregation, both complexity tie-breakers,
the final unresolved tie, the control `NO-GO` path, and the candidate `GO`
path.

## Independent pre-freeze review

An independent `claude-sonnet-5.5` review inspected the protocol, projections,
prompts, models, schemas, budgets, failure policy, fresh identities, real-MCP
wiring, accounting, and selector before freeze. It found two material
ambiguities:

1. the draft selector gave half credit to `partiallySupported` claims instead
   of using the issue's exact supported/total denominator; and
2. the draft selector counted attempts containing an unhelpful next step
   instead of counting the listed unhelpful steps.

Both were corrected before freeze. A narrow independent follow-up confirmed
the corrected production aggregation and identified missing direct regression
coverage. The tests were then extended to exercise mixed supported,
partially-supported, and unsupported claims, multiple unhelpful steps in one
attempt, and equal support rates with different numerators and denominators.

Before empirical execution:

- all 34 deterministic Protocol 9, citation replay, strict assessor alias,
  accounting, projection, selector, and no-retry tests passed; and
- all three repository-built real MCP stdio gateway arms passed, each
  completing three fresh allocation captures and one reproducibility query.

## Fresh frozen inventory

Protocol 9 uses fresh case ids, workload parameters, capture bounds, arm-order
seed, projection identities, output root, target processes, captures, attempt
paths, and artifacts. Deterministic tests compare every case id, workload
identity, capture identity, and arm ordering against Protocols 1 through 8.

The finite planned inventory was:

- development: one positive and one healthy case per arm, six attempts total;
- held-out: one positive, one healthy, one competing-explanation, and one
  degraded-evidence case per arm, twelve attempts total.

Every attempt was permitted to start at most once. Failed, partial, aborted,
budget-exhausted, transport-failed, model-invalid, assessment-missing,
malformed, timed-out, and `notAssessable` attempts remain in denominators. No
favorable retry or replacement attempt is permitted.

The machine-readable contract is
[`operational-mcp-allocation-median-mad-jaccard-v9.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-allocation-median-mad-jaccard-v9.protocol.json).

| Item | Frozen value |
|---|---|
| Protocol version | `9.0.0` |
| Frozen at | `2026-11-03 11:14:06 UTC` |
| Freeze commit | `fbda6d272816ca05c2208aa0de98ddb4562ca0d1` |
| Protocol file SHA-256 | `aa5a37483b9212db115bec87f871c1def53fd73702a849ebb402ecf3234585bf` |
| Protocol fingerprint | `896f4f3230a274b5f5678e685529aec8820b0613fe085142cca189e1e7586adb` |
| Inventory fingerprint | `9313a6292282548dd707bc0ac65f456d3a8099e61d5f2d56eef29bc77ff6a0b8` |
| Projection inventory fingerprint | `26a002bc5b240d7a2ce514573e1fa60b71ee737e4a95302c54a9357a657049ea` |
| Shared guard fingerprint | `a8dd298d1e3f32641ced8f982048b876d0aa896b5437f5da147ed080496dfdea` |
| Mutation inventory fingerprint | `841c4e31f37c34ff74eaadd2d4b72767cacdab842ccf1f57cbea1d3f59277f5d` |
| Shared tool-definition fingerprint | `84f67ea8a518dad9217a01c604431390557a3a42cb86e48e8f9ac44a21f7bebb` |
| Selected-tool catalog fingerprint | `c4615566f749f28885206ec16649843d4d8b337ea53c5fd08760b8be0365a0de` |
| Product source fingerprint | `be394d41d2a808b913e162c44be52ee323d6e13bc3bbdcb9cf825e1d29bb47cb` |
| Preserved Protocol 8 file SHA-256 | `9a94a6158c6e9da3f125a2f9fb151a0279bbe1ad3d33988a40ee9a2c3227fac3` |
| Diagnosing model | `github-copilot-cli/gpt-5.3-codex`, default context |
| Assessor model | `github-copilot-cli/gpt-5.6-sol`, default context |

Per-arm response projection fingerprints:

| Arm | Fingerprint |
|---|---|
| No statistical fields | `ff5d93458c21340615db1682e2db5814fba72db298e51d9248089a93199217b8` |
| Categorical plus median/raw MAD | `c89cff1c8a2f044ca18376ad4ca6acb56e13371c1b6b1af75e132361c7841901` |
| Categorical plus Jaccard | `7957a773b72574492855572b01565328403f5ff90b6d57aec4b34fbd70b92093` |

## One-time empirical execution

The single permitted pilot invocation used the report-recorded GitHub Copilot
CLI transport `1.0.92`, `gpt-5.3-codex` for diagnosis, and `gpt-5.6-sol` for
isolated assessment. It ran from `2026-11-03 11:18:25 UTC` to
`11:24:27 UTC`.

The assessor schema preflight passed before the first target started. The six
development attempts then ran once in their frozen orders. Every attempt
completed its evaluator-owned three-capture plus one-query sequence through the
repository-built MCP server over real stdio:

- attempts: `6 / 6` development, `0 / 12` held-out;
- successful predeclared MCP exchanges: `24 / 24`;
- failed MCP exchanges: `0`;
- follow-up MCP calls: `0`;
- unauthorized actions: `0`; and
- attempts with an artifact-accounting mismatch: `0 / 6`.

The first development attempt, Jaccard on the positive case, completed the
investigation and exact citation checks but returned an invalid automated
assessment:

> Unexpected assessment property `posture` at `$.claims[0].posture` with value
> kind `String`.

The failure remained `notAssessable` in the Jaccard denominator. It was not
retried or replaced. The retained failure evidence records:

| Field | Value |
|---|---|
| Raw response SHA-256 | `9028c0272b6cdd3f5855489ddf18739533fed13408da40041425596b18ea81ae` |
| Raw response bytes | `2,187` |
| Assistant content SHA-256 | `2dc8588ddba1311a2d475c5d94ae369d3f4d3158a43b7e27a581f7151b224598` |
| Assistant content bytes | `2,156` |
| Retained canonical replay bytes | `877` |
| Replay redacted | yes |
| Replay truncated | no |

Because development prerequisites require every development attempt to pass,
the held-out partition did not start. This is the frozen failure policy, not a
replacement or favorable stopping rule.

The retained development denominators were:

| Arm | Attempts | Assessable | Fully supported / total claims | Partial claims | Unsupported certainty by category | Useful steps | Unhelpful steps |
|---|---:|---:|---:|---:|---|---:|---:|
| No statistical fields | 2 | 2 | 5 / 10 (50.0%) | 5 | causality 2; dominance 2; severity 1; workload equivalence 2 | 5 | 1 |
| Categorical plus median/raw MAD | 2 | 2 | 5 / 9 (55.6%) | 4 | dominance 1; severity 1; workload equivalence 1 | 5 | 0 |
| Categorical plus Jaccard | 2 | 1 | 4 / 5 assessed claims (80.0%) | 1 | none in the one assessable attempt | 2 | 1 |

Those development values are descriptive only. They are not admitted as
held-out selection evidence.

Artifact accounting was exact:

| Artifact accounting | Bytes |
|---|---:|
| Raw MCP responses | 291,060 |
| Model-visible projections | 281,139 |
| Retained MCP exchange total | 572,199 |
| Retained rejected-assessor replay | 877 |
| Retained total | 573,076 |
| Attempts with a mismatch | 0 / 6 |

The retained `pilot-report.json` is `1,416,695` bytes with SHA-256
`836ebed87aa6a34e87bfd38beef4d8726b95f9047ae70e4b951c5954f7e29797`.
It remains outside the repository because operational reports can contain
local paths and target-controlled names.

The binary was built from the reviewed freeze working tree immediately before
that tree was committed as `fbda6d2`. Its assembly informational version
therefore records the pre-freeze parent
`1d0718e94e1653ad26e4132270c5e59a8cbda2a3`, while the protocol, inventory,
projection, tool, product, and per-arm fingerprints in the report match the
committed freeze exactly. No source or protocol content changed between that
build and the freeze commit.

## Decision

**Protocol 9 decision: `INCONCLUSIVE`.**

The validity gate failed before any category or lexicographic ordering could
admit held-out evidence:

- held-out evidence is unavailable;
- one Jaccard development attempt is not assessable; and
- the generic harness-validity predicate also failed because it includes
  successful completion of the assessment stage. No fingerprint, citation, MCP
  exchange, authorization, or retained-artifact accounting mismatch occurred.

The frozen selector therefore selected no arm. Development-only finite values
cannot produce `GO` or `NO-GO`, and the invalid assessment cannot be converted
into favorable evidence.

No allocation measurement-quality field group is promoted. Protocol 9
authorizes no product formula, schema, collector, retention, tool, catalog,
wording, or directional allocation redesign. #1350 remains blocked and was not
started.

## Limitations

- The held-out inventory did not run because one development assessor response
  was invalid.
- The finite development inventory used one diagnosing model and one assessor
  model through one provider.
- Provider token and cost usage were unavailable. Calls, turns, bytes, capture
  time, wall time, attempts, and artifact accounting remained bounded.
- No model made a follow-up MCP call, so the run does not compare the value of
  arm-specific follow-up evidence.
- The result evaluates interpretation of the current allocation
  reproducibility response. It does not establish a baseline/current allocation
  quantity, workload normalization rule, delta, uncertainty interval, severity,
  regression, causality, health, or optimization priority.

## Boundaries

- Evidence and evaluator protocol only.
- Protocols 1 through 8 remain preserved and unchanged.
- No product formula, schema, collector, retention, tool, catalog, wording, or
  directional allocation implementation change.
- No automatic severity, dominance, causality, regression, health, leak, or
  optimization-priority verdict.
- #1350 remains blocked and unstarted.
