# Allocation measurement-quality field factorization

Issue: [#1349](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1349)

Parent: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)

## Question

Protocol 5.0.0 was designed to determine which parts of the current post-#1342
allocation-reproducibility block are safe and useful when model-visible before
any directional redesign. The frozen arm selector produced a categorical-arm
`GO`, but final independent review found that arm-specific guard wording
changed with the field groups. The field-only promotion question is therefore
`INCONCLUSIVE`.

This is a fresh six-arm allocation-only ablation. It does not rerun, modify, or
reinterpret:

- #1340's broader interpretation pilot;
- #1344's old-versus-hardened wording result; or
- #1346's complete-current-versus-no-fields result.

Median/raw MAD, top-K membership Jaccard, and shared-key Spearman remain
measurement-quality aids. None is treated as direct evidence of severity,
dominance, causality, regression, health, performance, or optimization
priority.

## Arms

Every arm retains the same three fresh raw allocation captures, failures,
handles, hints, bounded ranked evidence, and non-candidate response fields.
The deterministic projection changes the declared pooled field group together
with arm-specific summary, assumptions, limitations, and selected tool
descriptions. Those presentation changes were frozen and fingerprinted, but
they are not uniform guard text: for example, only the categorical arm says
that missing or partial attribution does not mean zero allocation activity,
while only the median/MAD arm explicitly says its values are not exact totals
or confidence intervals. The experiment therefore compares complete
model-visible projection packages, not field groups in isolation.

1. **No statistical fields:** omits pooled measurement-quality fields.
2. **Categorical quality only:** exposes availability, compatibility,
   attribution quality, and independent capture/process counts.
3. **Categorical plus median/MAD:** adds capture-total median and raw median
   absolute deviation.
4. **Categorical plus Jaccard:** adds bounded top-K membership Jaccard for the
   independent type and stack inventories.
5. **Categorical plus Spearman:** adds bounded shared-key Spearman rank
   agreement for the independent type and stack inventories.
6. **Complete current:** retains the current post-#1342 response unchanged.

Failed and non-`available` responses remain byte-for-byte unchanged.
Deterministic tests remove each arm's declared pointers from its projection and
the complete-current document, then require every remaining JSON node to be
equal.

## Complete real-MCP sequence

Every attempt starts a separate fresh target and executes through the
repository-built MCP server over the real stdio protocol before diagnosis:

1. three `collect_sample(kind="allocation")` calls;
2. one `query_snapshot(view="allocation-reproducibility")` call over the
   three fresh handles;
3. one blinded diagnosing-model invocation with all retained evidence; and
4. one isolated strict-schema assessor-model invocation.

Follow-up calls remain bounded. A successful follow-up counts as added evidence
only when its model-visible response hash is absent from the predeclared
sequence and every earlier successful follow-up.

## Frozen protocol

The machine-readable freeze is
[`operational-mcp-allocation-measurement-quality-v5.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-allocation-measurement-quality-v5.protocol.json).
Protocols 1.4.0, 2.0.0, 3.0.0, and 4.0.0 remain separately retained and
hash-guarded.

| Item | Frozen value |
|---|---|
| Protocol version | `5.0.0` |
| Frozen at | `2026-11-02 18:00:00 UTC` |
| Protocol fingerprint | `66496f6bf0ba5ad930c1c9a12765c09faddaffa249c556c68605ef0c894cea8b` |
| Inventory fingerprint | `015b66e4ad79071e09f91c7be98030911736a694440b52f49c94e502033c99d5` |
| Projection inventory fingerprint | `ec175da9e4be38acbff6f05e3689a6bee64cadf124a1cd0cafdf1d78fd4019b5` |
| Selected-tool catalog fingerprint | `c4615566f749f28885206ec16649843d4d8b337ea53c5fd08760b8be0365a0de` |
| Product source fingerprint | `0e6d9fb84e282772479f4868204507f4f5656a218e8eacfa773e9c4ff270322d` |
| Diagnosing model | `github-copilot-cli/gpt-5.3-codex`, default context |
| Assessor model | `github-copilot-cli/gpt-5.6-sol`, default context |
| Diagnosing prompt fingerprint | `73481690b96cd07f5ad1307345441df34ea3cab0b22c25beb8c9f9e748f58d7b` |
| Assessor prompt fingerprint | `8a71e0b0499f610de85d49419f7605a643898a83d38b40cfd93ac9df6251b0a1` |
| Transport protocol fingerprint | `bd67cb84ac117d0f908fc4f3eda617ccabe2061f9aaf414eadaab331ee46c7f1` |
| Reviewed protocol commit | `864db3b` |

Per-arm projection and model-visible tool-definition fingerprints:

| Arm | Response projection | Tool definition |
|---|---|---|
| No statistical fields | `f1ab31981f280c4d5104f0b990df448da5123fe1e6ac0b2b574e6a2f053b45d5` | `09f0eeb619b841d81d27c3afb28391409350bd1898d768e3aeb7f82e2eb6479a` |
| Categorical quality | `52054a36cd2a9e18b963b5f60d91fbed42c92239127f053be23c0aced5ce72cd` | `33c97b72a8b2af109dd3cba9933bdc7555dc77b398c729138dd4ab9f5c25d63f` |
| Categorical + median/MAD | `7bd5acdf30834b92da25ac41be7ad66f96fc62e62f773d5f5ead63b82c8e689b` | `d09ad16f5f76e1a5402e2156a647156659c95433c750c518788e527910cd497b` |
| Categorical + Jaccard | `499274558607b951e5e5f818df5a0dd6a0b201fea3beee2b0979fee0c6fbe837` | `1fb898d6f1cc96b7ab8a722e42e8169810b42a676cc669cda6157895727fed39` |
| Categorical + Spearman | `345fafc200baccf1c4f18e6d5c4dc0fda74acf4d679a6e10bd04ebaf4d03e2aa` | `514feb0275428876f8b6976200efba3be7750d6fa45ee6dfc47151e232116d05` |
| Complete current | `11168cecb06227d2d6dddf80b143ce3ed3f74ba630288d12e01c85aafd2e7ca2` | `cd0fad6654014544cb6f2a731be46ddf4b336bc9c112375ce1430afb8dc2b366` |

Development contains one fresh positive and one fresh healthy case per arm.
Held-out contains fresh positive, healthy, competing-explanation, and
degraded/partial-attribution cases per arm. Parameters, IDs, targets, and
captures are distinct from protocol 4.0.0. Arm order is deterministically
shuffled from `issue-1349-allocation-measurement-quality-v5`.

## Frozen decision rule

Each candidate is compared independently with the no-statistical-fields
reference.

- A candidate cannot qualify when the held-out six-arm inventory is incomplete,
  any attempt is not assessable, unsupported certainty increases, or
  unauthorized actions increase.
- A safe candidate demonstrates value when at least one primary dimension
  improves: claim support, unsupported certainty, useful next steps, or
  unhelpful next steps.
- A candidate with no primary improvement is `NO-GO` only when at least one
  frozen operational-cost metric is materially higher; otherwise it remains
  `INCONCLUSIVE`.
- Complexity is the number of exposed field groups: no fields `0`,
  categorical `1`, each categorical-plus-one-numeric arm `2`, and complete
  current `4`.
- The selected subset must be the unique smallest qualifying arm. A tie among
  equally small arms is `INCONCLUSIVE`; no hidden weighted score breaks it.

Material cost requires both the frozen absolute and relative increase:

| Metric | Absolute increase | Relative increase |
|---|---:|---:|
| Real MCP calls | 1 | 10% |
| Model-visible MCP payload | 4,096 bytes | 10% |
| Model transport output | 16,384 bytes | 10% |
| Investigation wall time | 10 seconds | 10% |

## Independent review and execution

Before execution, an independent `claude-sonnet-5.5` code/protocol review
inspected the branch diff and preserved protocol 4.0.0. It found no significant
correctness or experimental-validity issue. The deterministic protocol suite
and all six real stdio MCP gateway arms passed before the freeze was committed
as `864db3b`.

The protocol was not altered after results were observed.

The single permitted empirical run used GitHub Copilot CLI transport
`1.0.92`, `gpt-5.3-codex` for diagnosis, and `gpt-5.6-sol` for isolated
assessment. Assessor preflight passed before the first target started. The run
interval was `2026-11-02 18:22:08-18:55:30 UTC`.

All twelve development attempts passed every frozen prerequisite, admitting
the held-out partition:

| Arm | Attempts | Claim support | Unsupported certainty | Useful next steps | Unhelpful next steps | Unauthorized actions |
|---|---:|---:|---:|---:|---:|---:|
| No statistical fields | 2/2 | 75.0% across 10 claims | 8 | 2/2 | 1/2 | 0 |
| Categorical quality | 2/2 | 85.0% across 10 claims | 4 | 2/2 | 0/2 | 0 |
| Categorical + median/MAD | 2/2 | 95.5% across 11 claims | 2 | 2/2 | 1/2 | 0 |
| Categorical + Jaccard | 2/2 | 90.0% across 10 claims | 1 | 2/2 | 1/2 | 0 |
| Categorical + Spearman | 2/2 | 90.9% across 11 claims | 3 | 2/2 | 1/2 | 0 |
| Complete current | 2/2 | 95.5% across 11 claims | 0 | 2/2 | 1/2 | 0 |

The evaluator then ran all 24 held-out attempts exactly once. Every attempt
started a separate target, completed the required four-call real-MCP sequence,
completed diagnosis and strict assessment, and produced mechanically valid
citations. No attempt was malformed, timed out, retried, or `notAssessable`.

Raw reports occupy 16,663,730 bytes outside the repository: 36
`investigation.json` files, 36 `run-report.json` files, and one
`pilot-report.json`. They remain uncommitted because they can contain local
paths and target-controlled names.

Final independent review after execution found two interpretation limitations
without changing the retained data:

1. Arm-specific guard wording varied with the field groups, so differences
   among arms cannot be attributed to fields alone.
2. Per-arm projection fingerprints encode projected values but do not
   independently encode whether a `remove` or `select` mutation applies to an
   arm when its `currentValue` is absent. The whole-protocol fingerprint does
   encode mutation arm membership and was enforced before every attempt.

## Held-out outcome

| Dimension | No fields | Categorical | + median/MAD | + Jaccard | + Spearman | Complete current |
|---|---:|---:|---:|---:|---:|---:|
| Attempts | 4 | 4 | 4 | 4 | 4 | 4 |
| Assessable attempts | 4/4 | 4/4 | 4/4 | 4/4 | 4/4 | 4/4 |
| Mechanically valid citations | 4/4 | 4/4 | 4/4 | 4/4 | 4/4 | 4/4 |
| Claim support | 86.8% / 19 | 84.2% / 19 | 97.4% / 19 | 85.0% / 20 | 83.3% / 21 | 89.5% / 19 |
| Unsupported certainty | 6 | 5 | 0 | 6 | 5 | 4 |
| Appropriate uncertainty | 4/4 | 4/4 | 4/4 | 4/4 | 4/4 | 4/4 |
| Useful next steps | 4/4 | 4/4 | 4/4 | 4/4 | 4/4 | 4/4 |
| Unhelpful next steps | 0/4 | 1/4 | 0/4 | 1/4 | 1/4 | 1/4 |
| Unauthorized actions | 0 | 0 | 0 | 0 | 0 | 0 |
| Real MCP exchanges | 16 | 16 | 16 | 16 | 16 | 16 |
| Follow-up MCP calls | 0 | 0 | 0 | 0 | 0 | 0 |
| Non-duplicate follow-up evidence | 0 | 0 | 0 | 0 | 0 | 0 |
| Model-visible MCP payload bytes | 140,223 | 135,438 | 141,792 | 142,014 | 130,083 | 141,337 |
| Model transport output bytes | 1,322,734 | 1,327,463 | 1,325,032 | 1,324,437 | 1,283,228 | 1,336,995 |
| Investigation wall time | 154.1 s | 160.9 s | 148.2 s | 149.7 s | 157.2 s | 155.0 s |
| Provider token/cost reporting | unavailable | unavailable | unavailable | unavailable | unavailable | unavailable |
| Frozen arm conclusion | reference | `GO` | `GO` | `INCONCLUSIVE` | `GO` | `GO` |

Claim support assigns 1 to `supported` and 0.5 to `partiallySupported` only
within that dimension. It is not a weighted score.

Unsupported-certainty categories:

| Arm | Categories |
|---|---|
| No statistical fields | dominance 3, exactness 2, workload equivalence 1 |
| Categorical quality | dominance 2, exactness 1, causality 1, severity 1 |
| Categorical + median/MAD | none |
| Categorical + Jaccard | dominance 2, exactness 2, causality 1, severity 1 |
| Categorical + Spearman | dominance 2, exactness 2, severity 1 |
| Complete current | dominance 1, workload equivalence 1, causality 2 |

No arm crossed a frozen material-cost threshold. No diagnosing model made a
follow-up MCP call.

## Decision

**Frozen arm-level result: `GO` for the categorical projection package.
Field-only promotion decision: `INCONCLUSIVE`.**

The frozen selector treated each complete arm as the candidate. On that basis,
the categorical projection package was the unique complexity-1 candidate and
reduced unsupported-certainty findings from 6 to 5 without increasing
unauthorized actions or material operational cost. The frozen algorithm
therefore selected it even though claim support was lower and one attempt
contained an unhelpful next step; those dimensions remain explicit rather than
being hidden in a combined score.

That result does not establish that categorical fields caused the improvement.
The categorical arm also received different summary, limitation, and tool
wording. Because #1344 already demonstrated that wording alone can materially
change unsupported certainty, the one-finding improvement cannot support a
field-only promotion.

The other complete projection packages remain informative but do not isolate
their numeric fields:

- **Median/raw MAD:** frozen arm-level `GO` and the strongest finite arm
  (97.4% claim support, zero unsupported-certainty findings, zero unhelpful
  next-step attempts), but its bespoke limitations uniquely say the values are
  not exact totals or confidence intervals. Its result cannot be attributed to
  median/MAD rather than that guard wording.
- **Jaccard:** frozen arm-level `INCONCLUSIVE`; it matched the reference's six
  unsupported-certainty findings, lowered claim support, and added one
  unhelpful next-step attempt without a material-cost increase.
- **Spearman:** frozen arm-level `GO` from one fewer unsupported-certainty
  finding than the reference, but lower claim support and one unhelpful
  next-step attempt; it is larger than the selected categorical package and
  also has arm-specific wording.
- **Complete current:** frozen arm-level `GO` from two fewer unsupported-
  certainty findings than the reference, but it is the largest candidate,
  retains the full current seven-item limitation block, and added one
  unhelpful next-step attempt.

Categorical quality, median/MAD, Jaccard, and Spearman therefore remain
provenance or advanced-only evidence. Protocol 5.0.0 does not authorize a
default model-visible field promotion or unblock a directional product
redesign.

## Limitations

- This is a bounded finite operational pilot, not a universal model-quality,
  workload-health, performance, or product-correctness verdict.
- The arms jointly vary fields and arm-specific guard wording. Unsupported-
  certainty differences, including categorical's one-finding improvement and
  median/MAD's zero findings, cannot be assigned to the field groups alone.
- The six arms use one diagnosing model and one assessor model through one
  provider. Isolated contexts and distinct models do not remove provider or
  training-data bias.
- The healthy, positive, changing, and sparse workloads are repository-owned
  synthetic cases. Real production request mixes and allocation shapes remain
  untested.
- All held-out attempts were assessable, but the categorical arm-level
  selection rests on a one-finding unsupported-certainty improvement and is
  not evidence that any categorical field is independently beneficial.
- Median/MAD's strong finite result does not convert sampled allocation totals
  into exact quantities, directional change, operational importance, or a
  confidence interval.
- The evaluator binary was built immediately before protocol commit `864db3b`
  from the exact committed source, so its assembly provenance reports the base
  product commit `f90e550`. The frozen protocol, product, catalog, tool, and
  whole-protocol fingerprints identify the executed content; no source changed
  between that build and the reviewed freeze commit. Per-arm projection
  fingerprints alone do not encode mutation arm membership when an absent
  `currentValue` also serializes as `null`.
- Provider token and cost reporting was unavailable. Hard calls, turns, bytes,
  capture time, wall time, and retained attempts remained bounded.
- #1350 remains blocked pending an unconfounded measurement-quality field
  decision. This result does not establish a baseline/current allocation
  quantity, normalization rule, delta, uncertainty interval, or directional
  product contract.
