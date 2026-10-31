# Operational LLM interpretation A/B with predeclared MCP evidence

Issue: [#1340](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1340)

## Question

Protocol 2.0.0 measures whether the existing statistical blocks improve an
LLM's interpretation of retained MCP evidence. It does not measure whether the
model can discover or complete the required multi-capture collection plan.

The evaluator, not the diagnosing model, executes the frozen family-specific
sequence through the repository-built MCP server over the real stdio protocol.
Those retained results are supplied as conversation evidence before the first
diagnosing-model turn. The model can make bounded follow-up calls through the
same `collect_sample` and `query_snapshot` surface, but the report distinguishes
follow-up calls that add evidence from calls that repeat a retained payload.

Protocol 1.4.0 and its four preserved #1338 development attempts are not
modified, rerun, or reinterpreted.

## Eligible families

The live catalog inventory remains unchanged:

| Family | Status | Frozen sequence |
|---|---|---|
| Allocation reproducibility | Eligible | Three fresh `collect_sample(kind="allocation")` calls, then one `query_snapshot(view="allocation-reproducibility")` over all three handles |
| CPU hotspot capture profiles | Eligible | Three fresh `collect_sample(kind="cpu")` calls, then one `query_snapshot(view="hotspot-variability")` per handle |
| CPU hotspot movement comparison | Excluded | MCP does not emit the caller-constructed capture-set document required by the paired comparison |
| Contention concentration | Excluded | The MCP projection omits the Core/CLI-only concentration block |
| GC pressure characterization | Excluded | No MCP response placement |
| Retained growth | Excluded | Not reachable through the current MCP catalog |
| ThreadPool backlog | Excluded | No MCP argument requests and populates the frozen backlog protocol |
| Request latency distribution | Excluded | MCP cannot collect the required statistical window sets |

No tool, collector, public schema, or product response was added for this
evaluation.

## Deterministic v2 corrections

Protocol 2.0.0 converts the observed protocol 1.4.0 mechanics into pre-freeze
tests:

- document-root citations use `tool-result://<call-id>#`; the resolver also
  accepts the previously emitted `#/` root spelling;
- model-visible MCP payload bytes and model-transport output bytes have
  independent finite limits and are reported as separate operational
  dimensions;
- the frozen assessor model must pass a bounded no-tool transport and strict
  schema preflight before the first target process starts;
- predeclared MCP results are retained in transcript turn zero so citation
  resolution and isolated assessment operate on exactly the evidence shown to
  the diagnosing model;
- failed, partial, malformed, cancelled, timed-out, budget-exhausted, and
  `notAssessable` states remain explicit and cannot become positive or negative
  semantic evidence;
- held-out execution remains blocked unless every development arm passes
  activation, cleanup, diagnosis, citation resolution, assessment, the complete
  predeclared sequence, and real MCP exchange retention.

## Frozen protocol

The machine-readable freeze is
[`operational-mcp-ab-v2.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-ab-v2.protocol.json).
The v1 file remains separately retained and hash-guarded.

| Item | Frozen value |
|---|---|
| Protocol version | `2.0.0` |
| Protocol fingerprint | `df1db2c83d7d71967fa582153f49e88b0201017f3f4486602f2da49a2604c43d` |
| Inventory fingerprint | `5502f0bfd8617e77faf3315fe5e8087976f8667d08c353b01093c4be95463f67` |
| Projection fingerprint | `9ab6488c85a32d5fa89426411068866c6966832e41cfa899d2c348e8ced47696` |
| Selected-tool catalog fingerprint | `7dbea518d1298f8d3404b9367b8cc19df84cac5027585f96d8be39958c7dcdf2` |
| Product source fingerprint | `a7f88789b8bed4f02b4edcf4e475bfd97d770e4ccf9017fa62879e23fabad77b` |
| Diagnosing model | `github-copilot-cli/gpt-5.3-codex`, default context |
| Assessor model | `github-copilot-cli/gpt-5.6-sol`, default context |
| Diagnosing prompt fingerprint | `73481690b96cd07f5ad1307345441df34ea3cab0b22c25beb8c9f9e748f58d7b` |
| Assessor prompt fingerprint | `7d189f4d022c2321c1489b6f7532f98ccbfa8d4b453ea398800009f5b5667362` |
| Transport protocol fingerprint | `bd67cb84ac117d0f908fc4f3eda617ccabe2061f9aaf414eadaab331ee46c7f1` |

Development contains one fresh positive case per family and arm. Held-out
contains fresh positive, healthy/control, competing-explanation, and
degraded/insufficient cases per family and arm. Every arm starts a separate
target process and creates fresh captures.

## Frozen budgets

| Budget | Limit |
|---|---:|
| Whole suite wall time | 10,800 seconds |
| Investigation wall time per arm | 300 seconds |
| Assessor preflight wall time | 180 seconds |
| Automated assessment wall time per arm | 180 seconds |
| Total MCP calls per arm, including predeclared sequence | 10 |
| Diagnosing model turns per arm | 5 |
| Aggregate capture time per arm | 18 seconds |
| MCP payload per response | 256 KiB |
| Diagnosing model transport output per response | 512 KiB |
| Assessor input | 256 KiB |
| Assessor transport output | 512 KiB |
| Retained MCP artifacts per arm | 8 MiB |
| Input tokens, when reported | 60,000 |
| Output tokens, when reported | 7,200 |
| Estimated cost, when reported | USD 2.00 |

Provider token and monetary usage can remain unavailable. Attempts, calls,
turns, capture seconds, MCP payload bytes, model transport bytes, wall time,
and retained artifact bytes remain hard-bounded.

## Assessment and reporting

The isolated assessor receives the candidate diagnosis, the same retained
model-visible MCP evidence, mechanically invalid citations, the forbidden
certainty list, useful-next-step contract, execution states, and unauthorized
action count. It returns a strict no-tool JSON document.

The report keeps these dimensions separate:

- claim and citation support;
- unsupported certainty;
- appropriate uncertainty, unnecessary abstention, overconfidence, and
  `notAssessable`;
- useful and unhelpful next steps;
- unauthorized actions;
- predeclared and follow-up MCP calls;
- whether follow-up calls added non-duplicate evidence;
- capture, MCP payload, model transport, artifact, wall-time, usage, and cost
  evidence.

There is no weighted global score, automatic product verdict, human-calibration
substitute, or model-quality CI gate.

## Empirical outcome

The frozen implementation must pass build, deterministic tests, live MCP
sequence validation, and independent cross-model review before empirical
execution. Development runs first. Held-out runs only if every frozen
development gate passes. The final retained outcome is recorded here after the
single permitted execution.
