# Allocation reproducibility wording A/B

Issue: [#1344](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1344)

## Question

Protocol 3.0.0 measures whether the allocation-reproducibility wording hardened
in #1342 reduces the unsupported certainty observed in #1340.

This is a fresh allocation-only experiment. It does not rerun, overwrite, or
reinterpret protocol 2.0.0 or its results. Every arm starts a separate target
process, creates three fresh allocation captures, and executes the complete
frozen sequence through the repository-built MCP server over the real stdio
protocol before diagnosis:

1. three `collect_sample(kind="allocation")` calls;
2. one `query_snapshot(view="allocation-reproducibility")` call over those
   three handles;
3. a blinded diagnosing-model invocation with the retained evidence;
4. a separate strict-schema assessor-model invocation.

The diagnosing model can make bounded follow-up calls, but no development or
held-out attempt did so.

## Compared arms

The hardened arm receives the current MCP response and current `tools/list`
descriptions unchanged. The prior arm applies a deterministic projection that
restores the exact pre-#1342 LLM-visible wording from commit
`dc2aacda10a390e9ad7340d5ab94056954f3d0d3`; the hardened wording comes from
commit `2830e46e1836da650d4f16f67b81ad2140a4ca36`.

The response projection changes only:

- the result summary;
- assumptions 1 and 2;
- limitations 0, 4, and 5;
- the two limitations added by #1342, which are removed from the prior arm.

The tool projection changes only the `view`, `topN`, and `comparisonHandles`
parameter descriptions for `query_snapshot`.

Deterministic tests prove that the hardened response remains byte-for-byte
equal to the raw MCP response, MCP failures and non-`available` results remain
byte-for-byte unchanged and retained, and an `available` prior response differs
only at the declared JSON pointers. They separately prove that formulas,
schemas, objective values, quality states, compatibility identities, hints,
and every other non-wording field remain equal. The same invariant is enforced
for tool definitions. Independent targets intentionally produce fresh
measurements, so byte equality applies to each raw response and its projection,
not between separate target processes. All twelve empirical attempts produced
the `available` response shape covered by the frozen A/B projection.

No product calculation, collector, response schema, or MCP tool changed.

## Frozen protocol

The machine-readable freeze is
[`operational-mcp-allocation-wording-ab-v3.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-allocation-wording-ab-v3.protocol.json).
Protocols 1.4.0 and 2.0.0 remain separately retained and hash-guarded.

| Item | Frozen value |
|---|---|
| Protocol version | `3.0.0` |
| Frozen at | `2026-11-02 02:05:00 UTC` |
| Protocol fingerprint | `14acb71144eb46de2eeecc02fa539715bef4e836e91cbb3ae32ac86456ec5d3e` |
| Inventory fingerprint | `609784811346f0884822ab5cdf89f16224cce0dc6b3dd364d753b5ef7566221a` |
| Projection fingerprint | `8ac13f73347459025b031af8a3bb5883c5fdbe4060ae324e07f6c56ee3bd198b` |
| Prior wording fingerprint | `c6be7304bcc301468770f8ca4b4b2b8c89bba05ccb735f1fe9c7e9654f3f5464` |
| Hardened wording fingerprint | `fdc8482d84f4d92f42d44bcdf0c30b9514ffe97058de99fe3d4c49a45121f011` |
| Selected-tool catalog fingerprint | `c4615566f749f28885206ec16649843d4d8b337ea53c5fd08760b8be0365a0de` |
| Prior tool-definition fingerprint | `a8e2e08947c9db9efbb9e9182039bb7a30687afa6cde9be7ccbc9e2f3afce1fc` |
| Hardened tool-definition fingerprint | `cd0fad6654014544cb6f2a731be46ddf4b336bc9c112375ce1430afb8dc2b366` |
| Product source fingerprint | `0e6d9fb84e282772479f4868204507f4f5656a218e8eacfa773e9c4ff270322d` |
| Diagnosing model | `github-copilot-cli/gpt-5.3-codex`, default context |
| Assessor model | `github-copilot-cli/gpt-5.6-sol`, default context |
| Diagnosing prompt fingerprint | `73481690b96cd07f5ad1307345441df34ea3cab0b22c25beb8c9f9e748f58d7b` |
| Assessor prompt fingerprint | `8a71e0b0499f610de85d49419f7605a643898a83d38b40cfd93ac9df6251b0a1` |
| Transport protocol fingerprint | `bd67cb84ac117d0f908fc4f3eda617ccabe2061f9aaf414eadaab331ee46c7f1` |

Development contains one fresh positive and one fresh healthy case per arm.
Held-out contains fresh positive, healthy, competing-explanation, and
degraded/partial-attribution cases per arm. Arm order is deterministic from
`issue-1344-allocation-wording-v3`. Every started attempt is retained and no
favorable retry is permitted.

## Frozen budgets

| Budget | Limit |
|---|---:|
| Whole suite wall time | 10,800 seconds |
| Investigation wall time per arm | 300 seconds |
| Assessor preflight wall time | 180 seconds |
| Automated assessment wall time per arm | 180 seconds |
| Total MCP calls per arm, including predeclared sequence | 8 |
| Diagnosing model turns per arm | 4 |
| Aggregate capture time per arm | 12 seconds |
| MCP payload per response | 256 KiB |
| Model transport output per response | 512 KiB |
| Assessor input | 256 KiB |
| Assessor transport output | 512 KiB |
| Retained MCP artifacts per arm | 8 MiB |
| Input tokens, when reported | 60,000 |
| Output tokens, when reported | 7,200 |
| Estimated cost, when reported | USD 2.00 |

## Assessment

The isolated assessor returns a strict no-tool JSON document. Unsupported
certainty is retained by claim index and by category:

- dominance;
- exactness;
- workload equivalence;
- causality;
- severity;
- regression;
- health;
- optimization priority;
- missing-as-zero.

Claim and citation support, uncertainty/abstention, next-step usefulness,
completion, unauthorized actions, and operational costs remain separate. There
is no weighted global score, human-calibration claim, automatic model-quality
gate, or automatic product-adoption verdict.

## Empirical execution

The implementation passed deterministic tests, both real-MCP wording-arm
tests, and an independent `claude-sonnet-5.5` code/protocol review before the
post-review freeze. The review found no actionable defect.

The single permitted run used GitHub Copilot CLI transport `1.0.92`,
`gpt-5.3-codex` for diagnosis, and `gpt-5.6-sol` for isolated assessment. The
assessor preflight passed before the first target started. The run interval was
2026-11-02 02:07:43-02:18:49 UTC.

All four development attempts passed every frozen prerequisite, admitting the
held-out partition:

| Development dimension | Prior wording | Hardened wording |
|---|---:|---:|
| Attempts completing every gate | 2/2 | 2/2 |
| Unsupported-certainty findings | 5 | 0 |
| Finding categories | causality 1, dominance 1, severity 2, workload equivalence 1 | none |
| Follow-up MCP calls | 0 | 0 |
| Unauthorized actions | 0 | 0 |

The evaluator then ran all eight held-out attempts exactly once. All twelve
development and held-out attempts used fresh targets, completed the required
four-call real-MCP sequence, produced valid citations, completed strict
assessment, and remained assessable. No attempt failed, became
`notAssessable`, or was retried.

Raw reports occupied 5,568,136 bytes outside the repository: twelve
`investigation.json` files, twelve `run-report.json` files, and one
`pilot-report.json`. They remain uncommitted because they can contain local
paths and target-controlled names.

## Held-out outcome

| Dimension | Prior wording | Hardened wording |
|---|---:|---:|
| Attempts | 4 | 4 |
| Completed investigations | 4/4 | 4/4 |
| Strict assessor executions | 4/4 | 4/4 |
| Mechanically valid citations | 4/4 | 4/4 |
| Assessable attempts | 4/4 | 4/4 |
| Claim support | 88.1% across 21 claims | 91.7% across 18 claims |
| Unsupported certainty | **5** | **1** |
| Appropriate uncertainty | 4/4 | 4/4 |
| `notAssessable` | 0/4 | 0/4 |
| Useful next steps | 4/4 | 4/4 |
| Unhelpful next steps | 0/4 | 0/4 |
| Unauthorized actions | 0 | 0 |
| Real MCP exchanges | 16 | 16 |
| Follow-up MCP calls | 0 | 0 |
| Frozen capture time | 24 s | 24 s |
| Model-visible MCP payload bytes | 137,550 | 140,713 |
| Model transport output bytes | 1,320,955 | 1,306,639 |
| Investigation wall time | 150.8 s | 146.8 s |
| Provider token/cost reporting | unavailable | unavailable |

Claim support assigns 1 to `supported` and 0.5 to `partiallySupported` only
within that dimension. It is not a weighted score.

The single comparative family conclusion is **GO (hardened versus prior)**.
The frozen rule requires strictly fewer hardened unsupported-certainty
findings, with claim support, appropriate uncertainty, and useful next steps
not lower, and unhelpful next steps and unauthorized actions not higher.

The prior wording produced five unsupported-certainty findings:

- positive: one unsupported workload-dominance claim;
- healthy: two unsupported severity claims;
- degraded/partial attribution: one exactness claim and one workload-
  equivalence claim.

The hardened wording removed all five of those categories in their matched
held-out cases. It introduced one different severity finding in the positive
case: the diagnosis called the sampled estimated-byte magnitude "high" without
a baseline, impact measurement, or universal threshold. Competing-explanation
attempts produced no unsupported-certainty finding in either arm.

No held-out diagnosis asserted unsupported causality, regression, health,
optimization priority, or missing-as-zero certainty. Both arms retained
appropriate uncertainty and useful next steps in every held-out attempt.

## Conclusion

**GO for the #1342 wording hardening in this frozen allocation-only pilot.**
Unsupported-certainty findings fell from 5 to 1 across four fully assessable
held-out attempts per arm, an absolute reduction of four findings. Claim
support increased slightly, while citation support, uncertainty quality,
next-step usefulness, completion, unauthorized actions, and follow-up behavior
did not regress.

The hardening materially reduced but did **not eliminate** overinterpretation:
one unsupported severity label remained. This finite result does not establish
universal model quality, causal product impact, human calibration, or a
performance verdict for any diagnosed workload.
