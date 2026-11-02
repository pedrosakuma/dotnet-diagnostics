# Allocation reproducibility statistical-fields A/B

Issue: [#1346](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1346)

## Question

Protocol 4.0.0 measures whether the current post-#1342 allocation-
reproducibility statistical fields improve a diagnosing model's interpretation
compared with not exposing those fields.

This is not a rerun or reinterpretation of #1340 or #1344. Protocol 3.0.0 and
its report remain evidence that the hardened wording mitigated
overinterpretation relative to the old wording. They are not evidence that the
statistical fields add value relative to absence.

## Arms

- **No-statistical-fields control:** removes `method`, `estimatedBytes`,
  `allocationTicks`, `allocatedTypeStability`, and
  `allocationStackStability` from an otherwise current
  `allocation-reproducibility` response. It replaces only the summary,
  assumptions, limitations, and directly related tool descriptions with
  neutral wording that reproducibility was not assessed and that no zero,
  absence, health, equivalence, dominance, severity, regression, causality,
  performance, or optimization-priority conclusion follows.
- **Current indicator:** retains the current post-#1342 MCP response and
  selected tool definitions byte-for-byte.

The projection retains raw captures, handles, errors, hints, availability,
compatibility and attribution quality states. Deterministic tests remove the
declared candidate pointers from both projected documents and require every
remaining JSON node to be equal. Failed and non-`available` responses remain
byte-for-byte unchanged.

## Complete real-MCP sequence

Every attempt starts a separate fresh target and executes through the
repository-built MCP server over the real stdio protocol before diagnosis:

1. three `collect_sample(kind="allocation")` calls;
2. one `query_snapshot(view="allocation-reproducibility")` call over those
   three fresh handles;
3. a blinded diagnosing-model invocation with all retained evidence;
4. a separate strict-schema assessor-model invocation.

Follow-up calls remain bounded. A successful follow-up counts as added evidence
only when its model-visible response hash is not present in the predeclared
sequence or another successful follow-up.

## Frozen protocol

The machine-readable freeze is
[`operational-mcp-allocation-statistical-fields-ab-v4.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-allocation-statistical-fields-ab-v4.protocol.json).
Protocols 1.4.0, 2.0.0, and 3.0.0 remain separately retained and hash-guarded.

| Item | Frozen value |
|---|---|
| Protocol version | `4.0.0` |
| Frozen at | `2026-11-02 04:20:00 UTC` |
| Protocol fingerprint | `3f59d232f8a8353d4acab2fad66e3b04c9ce47743f42609b2bb2b03ca8fe78c3` |
| Inventory fingerprint | `3cd676e89ff0caa57d8091335b955fd0975903691bd0d12911bf7e5af53f143e` |
| Projection fingerprint | `1e1c806d2ef04f826eddd1ad73df69a5cef129124a2025b2bb3ab707af7a01c4` |
| Control projection fingerprint | `6c2ca7b9e5a103b4578363253f89da2c23f8616dd589c3ecd22b8db57d380c71` |
| Current projection fingerprint | `7aa5378cff407d8c94689a6480ad62695d79c2d84d9f3ced4155f0a2d7b999e9` |
| Selected-tool catalog fingerprint | `c4615566f749f28885206ec16649843d4d8b337ea53c5fd08760b8be0365a0de` |
| Control tool-definition fingerprint | `4f29907e0e7aee92277608a320d21c4c1db0529d1aa32b0d7c87e47aa7614f75` |
| Current tool-definition fingerprint | `cd0fad6654014544cb6f2a731be46ddf4b336bc9c112375ce1430afb8dc2b366` |
| Product source fingerprint | `0e6d9fb84e282772479f4868204507f4f5656a218e8eacfa773e9c4ff270322d` |
| Diagnosing model | `github-copilot-cli/gpt-5.3-codex`, default context |
| Assessor model | `github-copilot-cli/gpt-5.6-sol`, default context |
| Diagnosing prompt fingerprint | `73481690b96cd07f5ad1307345441df34ea3cab0b22c25beb8c9f9e748f58d7b` |
| Assessor prompt fingerprint | `8a71e0b0499f610de85d49419f7605a643898a83d38b40cfd93ac9df6251b0a1` |
| Transport protocol fingerprint | `bd67cb84ac117d0f908fc4f3eda617ccabe2061f9aaf414eadaab331ee46c7f1` |

Development contains one new positive and one new healthy case per arm. Held-out
contains new positive, healthy, competing-explanation, and degraded/partial-
attribution cases per arm. The parameters and case IDs are distinct from
protocol 3.0.0. Arm order is deterministic from
`issue-1346-allocation-statistical-fields-v4`.

## Frozen budgets and cost materiality

The capture, tool-call, turn, byte, token, cost, assessor, and whole-suite
budgets remain the bounded protocol 3 limits. A current-arm cost increase is
material only when one metric meets or exceeds both its absolute and relative
threshold:

| Metric | Absolute increase | Relative increase |
|---|---:|---:|
| Real MCP calls | 1 | 10% |
| Model-visible MCP payload | 4,096 bytes | 10% |
| Model transport output | 16,384 bytes | 10% |
| Investigation wall time | 10 seconds | 10% |

These independent thresholds are not combined into a weighted score.

## Outcomes and decision rule

Primary dimensions are semantic claim support, unsupported certainty, useful
next steps, and unhelpful next steps. Secondary dimensions remain citation
support, uncertainty/abstention, completion, unauthorized actions, bounded
operational cost, and non-duplicate follow-up MCP evidence.

- **GO:** current fields improve at least one primary dimension without
  worsening unsupported certainty or unauthorized-action safety in the
  complete assessable held-out inventory.
- **NO-GO:** current fields worsen unsupported certainty or unauthorized-action
  safety, or add no primary diagnostic value while materially increasing at
  least one frozen operational-cost metric.
- **INCONCLUSIVE:** the inventory is incomplete or not fully assessable, or the
  finite evidence satisfies neither rule.

Dimensions are reported separately. There is no weighted global score, CI
model-quality gate, human-calibration claim, or universal product/model verdict.

## Review and empirical execution

The protocol implementation passed deterministic projection tests and both
real-MCP arm tests. An independent `claude-sonnet-5.5` code/protocol review
identified one experimental-validity issue: the control had accidentally
removed non-candidate guidance to compare all three handles. The guidance was
restored and the fingerprints above were re-frozen before empirical execution.
The protocol was not altered after results were observed.

The single permitted run used GitHub Copilot CLI transport `1.0.92`,
`gpt-5.3-codex` for diagnosis, and `gpt-5.6-sol` for isolated assessment. The
assessor preflight passed before the first target started. The run interval was
2026-11-02 04:29:28-04:41:07 UTC.

All four development attempts passed every frozen prerequisite, admitting the
held-out partition:

| Development dimension | No statistical fields | Current indicator |
|---|---:|---:|
| Attempts completing every gate | 2/2 | 2/2 |
| Claim support | 94.4% across 9 claims | 95.0% across 10 claims |
| Unsupported-certainty findings | 1 | 0 |
| Finding categories | dominance 1 | none |
| Useful next steps | 2/2 | 2/2 |
| Unhelpful next steps | 0/2 | 0/2 |
| Follow-up MCP calls | 0 | 0 |
| Unauthorized actions | 0 | 0 |

The evaluator then ran all eight held-out attempts exactly once. Every attempt
started a separate target, completed the required four-call real-MCP sequence,
completed diagnosis, and produced mechanically valid citations. No attempt was
retried.

One no-statistical-fields healthy-control assessment returned a schema-invalid
document containing the unexpected property `nextSteps`. The strict assessor
retained that attempt as failed and `notAssessable`; no favorable retry was
permitted. The other seven held-out assessments passed.

Raw reports occupy 5,595,458 bytes outside the repository: twelve
`investigation.json` files, twelve `run-report.json` files, and one
`pilot-report.json`. They remain uncommitted because they can contain local
paths and target-controlled names.

## Held-out outcome

| Dimension | No statistical fields | Current indicator |
|---|---:|---:|
| Attempts | 4 | 4 |
| Completed investigations | 4/4 | 4/4 |
| Strict assessor executions | 3/4 | 4/4 |
| Mechanically valid citations | 4/4 | 4/4 |
| Assessable attempts | 3/4 | 4/4 |
| Claim support | 93.3% across 15 claims | 90.5% across 21 claims |
| Unsupported certainty | 0 across 3 assessable attempts | 4 across 4 assessable attempts |
| Finding categories | none | severity 2, dominance 1, missing-as-zero 1 |
| Appropriate uncertainty | 3/4 | 4/4 |
| `notAssessable` | 1/4 | 0/4 |
| Useful next steps | 3/4 | 4/4 |
| Unhelpful next steps | 1/4 | 0/4 |
| Unauthorized actions | 0 | 0 |
| Real MCP exchanges | 16 | 16 |
| Follow-up MCP calls | 0 | 0 |
| Non-duplicate follow-up evidence | 0 | 0 |
| Model-visible MCP payload bytes | 140,686 | 142,854 |
| Model transport output bytes | 1,103,838 | 1,364,827 |
| Investigation wall time | 157.6 s | 158.7 s |
| Provider token/cost reporting | unavailable | unavailable |

Claim support assigns 1 to `supported` and 0.5 to `partiallySupported` only
within that dimension. It is not a weighted score.

The current arm's four unsupported-certainty findings were:

- positive: one unsupported severity label (`high`);
- competing explanation: one unsupported workload-dominance claim;
- degraded/partial attribution: one missing-as-zero inference and one
  unsupported severity/pressure inference.

The control's one assessable positive attempt had no unsupported-certainty
finding, but suggested repeating captures in another time window before
explicitly requiring workload comparability; the assessor retained that as one
unhelpful next-step attempt.

Only model transport output crossed a frozen material-cost threshold:
1,364,827 versus 1,103,838 bytes, an increase of 260,989 bytes (23.6%).
MCP payload increased by 2,168 bytes (1.5%), calls were equal, and wall time
increased by 1.1 seconds, so those dimensions were not materially higher.

## Conclusion

**INCONCLUSIVE for current statistical fields versus absence in this frozen
allocation-only pilot.**

The frozen rule requires the complete held-out inventory to be assessable
before admitting GO or NO-GO. One control assessment failed strict schema
validation and remained `notAssessable`, so the rule stops at INCONCLUSIVE.

The finite observations do not support a favorable interpretation: among the
assessable attempts, current fields had four unsupported-certainty findings
versus none for the control, slightly lower claim support, and materially
higher model transport output. However, the missing control assessment prevents
the complete-inventory comparison required for a NO-GO label. Useful and
unhelpful next-step counts also have unequal assessable denominators.

This result answers value versus absence only for the frozen finite protocol.
It does not modify #1344's separate conclusion that hardened wording mitigated
overinterpretation relative to the old wording, and it does not establish a
universal product, model-quality, workload-health, or performance verdict.
