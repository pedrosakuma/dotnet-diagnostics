# Allocation measurement-quality field isolation, Protocol 7

Issue: [#1362](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1362)

Parent: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)

Blocked follow-up: [#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

## Question

Protocol 7 is a separately versioned correction to Protocol 6. It asks whether
any unique smallest allocation measurement-quality field subset improves
semantic claim support or next-step usefulness without worsening unsupported
certainty or safety when fields are the only model-visible arm difference.

Protocols 1 through 6 remain preserved and hash-guarded. Protocol 6's failed
assessor preflight, zero-attempt inventory, and **INCONCLUSIVE** result are not
modified, reinterpreted, rerun, or replaced.

## Corrections relative to Protocol 6

### Retained artifact accounting

Each admitted real-MCP exchange contributes exactly:

```text
rawResponseBytes + modelVisibleResponseBytes
```

to retained artifact accounting. The budget admission check and the retained
total use the same sum. An admitted exchange is added once; the Protocol 6
duplicate increment is removed. Deterministic tests cover repeated admission,
rejection without mutation, and retained synthetic failure bytes. The six
repository-built real stdio gateway arms also assert that the retained total
equals the sum serialized on their exchange records.

### Assessor additive-property policy

Protocol 6 had no additive-property declaration, so its strict rejection of
the returned root `nextSteps` property remains part of its preserved result.
Protocol 7 explicitly freezes this root-property policy:

- all canonical assessment fields remain required and strictly type-checked;
- canonical enums, claim indices, uniqueness rules, and nested object
  properties remain strict;
- undeclared root properties remain invalid;
- Protocol 7 declares only `nextSteps`, with required kind `stringArray`;
- the additive `nextSteps` value is retained only as tolerated transport
  content and is not substituted for canonical `usefulNextSteps` or
  `unhelpfulNextSteps`;
- a malformed declared addition still fails validation;
- the same canonical next step classified as both useful and unhelpful is a
  contradictory assessment and fails validation.

The deterministic conformance fixture includes the exact Protocol 6 failure
shape: a valid canonical assessment plus an additive root `nextSteps` array.
It also includes malformed `nextSteps`, an undeclared addition, contradictory
canonical next-step classifications, and an invalid required enum.

## Field-only isolation

The six arms remain:

1. no statistical fields;
2. categorical quality;
3. categorical plus median/raw MAD;
4. categorical plus bounded top-K membership Jaccard;
5. categorical plus bounded shared-key Spearman rank agreement; and
6. complete current data fields.

Every arm receives the same selected tool definitions, summary, assumptions,
limitations, and neutral guard:

> These observations do not establish exactness, workload equivalence, zero
> activity from missing evidence, dominance, severity, causality, regression,
> health, or optimization priority.

After declared candidate pointers are removed, deterministic tests require all
remaining response nodes and selected tool definitions to be byte-identical.
Structured failures, unavailable states, raw evidence, quality states, and
non-candidate evidence remain retained.

## Frozen execution contract

Protocol 7 uses fresh case IDs, workload parameters, target processes,
captures, output root, and attempt inventory. It precollects three allocation
captures and one allocation-reproducibility query through the repository-built
MCP server over real stdio before isolated diagnosis and assessment.

The frozen product-source fingerprint normalizes CRLF to LF for known text
source formats before hashing. The same tracked source therefore has one
identity on Windows and Linux rather than inheriting checkout line-ending
configuration.

The finite inventory is:

- development: one positive and one healthy case per arm;
- held-out: one positive, one healthy, one competing-explanation, and one
  degraded-evidence case per arm.

Every preflight and started arm is retained. Failed, partial, aborted,
budget-exhausted, transport-failed, model-invalid, assessment-missing,
malformed, timed-out, and `notAssessable` attempts remain in denominators. Each
finite arm may start at most once. No favorable retry is permitted, and an
already-started arm is never repeated after interruption or infrastructure
failure.

The machine-readable contract is
[`operational-mcp-allocation-field-isolation-v7.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-allocation-field-isolation-v7.protocol.json).

| Item | Frozen value |
|---|---|
| Protocol version | `7.0.0` |
| Frozen at | `2026-11-03 04:11:42 UTC` |
| Protocol fingerprint | `100e1cba23022481b6323678ab6e75c53180b4a74af6326518db0dbc6532f044` |
| Inventory fingerprint | `dbb9dcaf01f047e69302aa31e6a6638ab711e0fc9e09ed2bf1b713221af36259` |
| Projection inventory fingerprint | `93dcaedb2f80ba933e62ed354935d3de1ac8d8976ca3f9ba5c848ba0e0ddc017` |
| Shared guard fingerprint | `a8dd298d1e3f32641ced8f982048b876d0aa896b5437f5da147ed080496dfdea` |
| Mutation inventory fingerprint | `e2bc4f48b62d8fd9c08fba79825f5776bb1ec1aa93bfad6deedfd8cbb40e5635` |
| Shared tool-definition fingerprint | `84f67ea8a518dad9217a01c604431390557a3a42cb86e48e8f9ac44a21f7bebb` |
| Selected-tool catalog fingerprint | `c4615566f749f28885206ec16649843d4d8b337ea53c5fd08760b8be0365a0de` |
| Product source fingerprint | `be394d41d2a808b913e162c44be52ee323d6e13bc3bbdcb9cf825e1d29bb47cb` |
| Preserved Protocol 6 file fingerprint | `74a2878e5736e4b6c69f9cd2506c2c4ec055109f73943b7a3c77f2b04d4ba080` |
| Diagnosing model | `github-copilot-cli/gpt-5.3-codex`, default context |
| Assessor model | `github-copilot-cli/gpt-5.6-sol`, default context |
| Assessor additive property | root `nextSteps`: `stringArray` |

Per-arm response projection fingerprints:

| Arm | Fingerprint |
|---|---|
| No statistical fields | `24725cf230005e3f9fcd2362340f7c39e0e3f16bbbb33dbfba8d869e1a13034b` |
| Categorical quality | `b512801f27893abdf0e2b05620daed3272fa9970e0e70e045457fcaab6a74703` |
| Categorical + median/MAD | `4974fced5a2e984d19058f94d38158218f5a58c6e913e2bfb834204ce9c10671` |
| Categorical + Jaccard | `6e64e31c111050bf53ec32c04cfa6897d017a83228a553d9bc18a401d45102e9` |
| Categorical + Spearman | `2a1eeb2c318f5c356ad2937c68ce7d7c835e1e6107fcfbce5824712b207f4f25` |
| Complete current fields | `4eb6feebc326128df41a12223ac3893c393aa4941b4d701668620fb4c733f3a4` |

## Decision rule

Select only a unique smallest field subset that improves a primary dimension
without worsening unsupported certainty or safety. Equal-complexity ties,
incomplete inventory, incomplete assessability, or unresolved harness validity
remain **INCONCLUSIVE**. There is no hidden weighted score.

Primary outcomes are unsupported certainty by category, semantic claim
support, and useful or unhelpful next steps. Secondary outcomes are citation
and uncertainty quality, completion and unauthorized actions, MCP/model bytes,
turns, wall time, and artifact-budget accounting.

## Independent pre-freeze review

An independent `claude-sonnet-5.5` review inspected the complete protocol,
prompt/schema policy, projection isolation, artifact accounting, budgets,
failure policy, fresh identities, preservation hashes, and tests. It found no
blocking defect. Two low-severity observations were corrected before freeze:

1. payload and aggregate artifact admission were consolidated in the tested
   accounting helper, including exact-fit and rejection boundaries; and
2. the product-source fingerprint was made independent of CRLF/LF checkout
   conversion for known text source formats.

A second narrow review of those corrections found no actionable issue. The 24
deterministic Protocol 7 tests and all six repository-built real stdio gateway
arms passed before this freeze.

## Empirical result

Pending the single permitted Protocol 7 inventory execution.

## Boundaries

- Evidence and evaluator changes only.
- No product formula, schema, collector, retention, tool, catalog, wording, or
  directional allocation implementation change.
- No automatic severity, dominance, causality, regression, health, leak, or
  optimization-priority verdict.
- #1350 remains blocked until the complete reviewed Protocol 7 decision.
