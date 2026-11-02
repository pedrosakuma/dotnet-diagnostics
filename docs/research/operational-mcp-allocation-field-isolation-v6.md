# Allocation measurement-quality field isolation

Issue: [#1356](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1356)

Parent: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)

Blocked follow-up: [#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

## Question

Protocol 6.0.0 is the corrective Wave 0 gate after Protocol 5.0.0 completed
all six real-MCP arms but varied arm-specific summary, limitation, and tool
wording together with the candidate fields. It asks whether any unique
smallest allocation measurement-quality field subset improves a primary
dimension without worsening unsupported certainty or safety when fields are
the only model-visible arm difference.

The result is **INCONCLUSIVE**. The frozen assessor schema preflight failed
before the first target or arm started. The no-retry failure policy therefore
retained an empty attempt inventory, blocked development and held-out
execution, and prohibited selecting any field subset.

This protocol does not reinterpret the complete Protocol 5 evidence. It also
does not authorize a product, schema, tool, catalog, or #1350 implementation
change.

## Corrective isolation design

The six frozen arms are:

1. no statistical fields;
2. categorical quality;
3. categorical plus median/raw MAD;
4. categorical plus bounded top-K membership Jaccard;
5. categorical plus bounded shared-key Spearman rank agreement; and
6. complete current data fields.

Every arm receives the same summary, assumptions, limitations, and selected
tool definitions. The shared neutral guard sentence is:

> These observations do not establish exactness, workload equivalence, zero
> activity from missing evidence, dominance, severity, causality, regression,
> health, or optimization priority.

The guard does not name or imply candidate calculations. The deterministic
projection removes `/data/method` identically from every arm because that value
would reveal which calculations were performed. Candidate data pointers are
bound exactly to their declared field mutations:

- `/data/availability`
- `/data/requestedCaptureCount`
- `/data/availableCaptureCount`
- `/data/independenceUnit`
- `/data/compatibility`
- `/data/estimatedBytes`
- `/data/allocationTicks`
- `/data/allocatedTypeStability`
- `/data/allocationStackStability`

For every arm, deterministic tests remove those pointers and require every
remaining response node and every selected tool definition to be byte
identical. The tests cover `available`, `incompatible`, and
`insufficientCaptureCount` responses, including candidate pointers whose
current value is absent. Structured failures remain unchanged. Raw MCP
responses, failures, quality states, and non-candidate evidence are preserved.

The planned empirical sequence used fresh Protocol 6 workload IDs, parameters,
targets, and captures:

- development: one positive and one healthy case per arm;
- held-out: one positive, one healthy, one competing-explanation, and one
  degraded/partial-attribution case per arm.

Each planned attempt would have started a fresh target and executed three
allocation captures plus one allocation-reproducibility query through the
repository-built MCP stdio server before isolated diagnosis and assessment.
Ordering, models, prompts, schemas, budgets, fingerprints, and failure policy
were frozen before execution.

## Frozen protocol

The machine-readable freeze is
[`operational-mcp-allocation-field-isolation-v6.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-allocation-field-isolation-v6.protocol.json).
Protocols 1 through 5 remain separately retained and hash-guarded.

| Item | Frozen value |
|---|---|
| Protocol version | `6.0.0` |
| Frozen at | `2026-11-02 20:30:00 UTC` |
| Reviewed freeze commit | `7480677` |
| Protocol fingerprint | `4f1253d4a7360189ba97d6065a8ff962620c836cc039a3f5cedfd99e6beea574` |
| Inventory fingerprint | `dbb9dcaf01f047e69302aa31e6a6638ab711e0fc9e09ed2bf1b713221af36259` |
| Projection inventory fingerprint | `52c2c4469df5bca88e64249d7237e59a7d462241faaee081d1145f48e5e92e80` |
| Shared guard fingerprint | `a8dd298d1e3f32641ced8f982048b876d0aa896b5437f5da147ed080496dfdea` |
| Mutation inventory fingerprint | `e2bc4f48b62d8fd9c08fba79825f5776bb1ec1aa93bfad6deedfd8cbb40e5635` |
| Shared tool-definition fingerprint | `84f67ea8a518dad9217a01c604431390557a3a42cb86e48e8f9ac44a21f7bebb` |
| Selected-tool catalog fingerprint | `c4615566f749f28885206ec16649843d4d8b337ea53c5fd08760b8be0365a0de` |
| Product source fingerprint | `0e6d9fb84e282772479f4868204507f4f5656a218e8eacfa773e9c4ff270322d` |
| Preserved Protocol 5 file fingerprint | `4ed218ae50b857bdb5ebe664762b8a8faebc3cee52175dbaa2f26b4c71886e94` |
| Diagnosing model | `github-copilot-cli/gpt-5.3-codex`, default context |
| Assessor model | `github-copilot-cli/gpt-5.6-sol`, default context |
| Diagnosing prompt fingerprint | `73481690b96cd07f5ad1307345441df34ea3cab0b22c25beb8c9f9e748f58d7b` |
| Assessor prompt fingerprint | `8a71e0b0499f610de85d49419f7605a643898a83d38b40cfd93ac9df6251b0a1` |
| Transport protocol fingerprint | `bd67cb84ac117d0f908fc4f3eda617ccabe2061f9aaf414eadaab331ee46c7f1` |

Per-arm response projection fingerprints:

| Arm | Fingerprint |
|---|---|
| No statistical fields | `24931327554ba30b0dca683a006a3328613ca68a7a592fd23165609d7d37299b` |
| Categorical quality | `f6b69a736c464fe4ce433d5bfbf9e24a3c70a3f6afb0897c0a3c579c525ebd6c` |
| Categorical + median/MAD | `8a91702feccf2b7c564340e0ddd3177f832773754facfe913b405346fbf68c8c` |
| Categorical + Jaccard | `04fb6b1126fcbd8e28958178b7e56e1beff26d4377677dda5275d9d243ec30e0` |
| Categorical + Spearman | `6d036a554106530e0801128cc3ed9ce8be88ec96253e841b9a378e0c33fa124f` |
| Complete current fields | `4eb6feebc326128df41a12223ac3893c393aa4941b4d701668620fb4c733f3a4` |

The mutation fingerprint independently changes with mutation kind, arm
membership, and the presence or absence of `currentValue`. Per-arm
fingerprints encode arm identity, mutation applicability, and current-value
presence.

## Independent pre-freeze review

An independent `claude-sonnet-5.5` review inspected the implementation and
experimental validity before freeze. It found three substantive issues:

1. the first shared guard draft named calculations absent from some arms;
2. non-`available` matching-schema responses bypassed guard and field
   projection; and
3. candidate pointers were not structurally bound to mutations, while the
   mutation fingerprint tests did not cover arm membership and absent current
   values.

All three findings were corrected before commit `7480677`. The final
deterministic protocol suite passed 21 tests, and all six repository-built
real stdio MCP gateway arms passed before the empirical freeze. No protocol,
prompt, schema, budget, model, ordering, fingerprint, or harness logic changed
after empirical execution began.

Final independent review after execution found one additional frozen-harness
defect: each successful retained MCP exchange would increment the artifact-byte
accounting twice. The budget admission check itself uses the correct
`rawBytes + projectedBytes` candidate size, but the duplicated retained total
would make subsequent exchanges reach the frozen artifact limit early and
would over-report retained artifact bytes. This path never executed because
assessor preflight failed before any target or gateway was created. Consistent
with the no-change/no-retry rule, the frozen harness was not silently repaired
and the experiment was not rerun. A future protocol must correct the accounting
under a new version and repeat review before execution.

## Single permitted empirical run

The frozen run used GitHub Copilot CLI transport `1.0.92`,
`gpt-5.3-codex` for diagnosis, and `gpt-5.6-sol` for isolated assessment. It
ran from `2026-11-02 20:56:30 UTC` to `20:57:06 UTC`.

The assessor preflight returned a JSON object with an unexpected `nextSteps`
property. The strict assessment schema rejected it as a prerequisite failure:

```text
Unexpected assessment property 'nextSteps'.
```

The failure occurred before any target process, allocation capture, arm, or
development attempt started. Under the frozen policy, preflights and started
arms are retained, each finite arm runs at most once, and no favorable retry is
permitted. The run was therefore not repeated.

| Retained evidence | Count |
|---|---:|
| Assessor preflights | 1 failed |
| Development attempts | 0 |
| Held-out attempts | 0 |
| Target processes | 0 |
| Allocation captures | 0 |
| Real MCP exchanges | 0 |
| Diagnosing-model invocations | 0 |
| Assessor attempt invocations | 0 |
| Unauthorized actions | 0 |
| `pilot-report.json` files | 1 |

The retained `pilot-report.json` is 29,954 bytes with SHA-256
`4875a5d4b99e32d8f0ff5ab7166bda7464d9c2f1f1738d33a4f820fcc4c61346`.
It is preserved outside the repository because operational reports can contain
local paths and target-controlled names. There were no raw captures to retain
because empirical target execution never started.

## Decision

**Field-only promotion decision: `INCONCLUSIVE`.**

No arm has a complete attempt inventory, no primary outcome can be compared,
and no unique smallest qualifying field subset can be selected. Unsupported
certainty, semantic claim support, and useful or unhelpful next steps are all
not assessable for every arm. Safety did not worsen because no attempt or
gateway action occurred, but absence of execution is not evidence of field
value.

Consequently:

- categorical quality, median/MAD, Jaccard, and Spearman remain provenance or
  advanced-only evidence;
- Protocol 5's arm-level observations remain confounded and do not become
  field-attribution evidence;
- #1350 remains blocked;
- no product, schema, tool, catalog, wording, or directional redesign change
  is authorized.

## Limitations

- This is a preserved prerequisite failure, not an empirical comparison of
  the six field projections.
- The strict preflight correctly prevented spending targets and captures on a
  run whose assessor response contract was not demonstrated.
- The no-retry rule prevents replacing the failed preflight with a more
  favorable response, but it also leaves every primary dimension
  unassessable.
- The failed response body is represented by the retained preflight metadata
  and failure detail; no arm-specific model evidence exists.
- The frozen gateway double-counts retained MCP artifact bytes after each
  admitted exchange. It did not affect this zero-attempt run, but Protocol 6
  must not be reused as evidence for the intended 8 MiB artifact budget.
- The protocol uses one diagnosing model and one assessor model through one
  provider. Distinct models and isolated contexts would not eliminate
  provider or training-data bias even if execution had proceeded.
- #1350 remains blocked pending a complete, unconfounded field-only inventory.
  Protocol 6 does not establish a baseline/current allocation quantity,
  normalization rule, delta, uncertainty interval, or directional product
  contract.
