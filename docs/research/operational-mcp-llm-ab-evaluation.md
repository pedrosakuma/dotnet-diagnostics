# Operational LLM A/B evaluation through the real MCP surface

Issue: [#1338](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1338)

## Outcome

Protocol 1.4.0 completed exactly one bounded development pilot through the
repository-built MCP stdio server. The frozen development prerequisites did not
pass, so the held-out partition was not run. This is a protocol gate outcome,
not an automatic product verdict.

The family-level evidence labels are **inconclusive** for both eligible
families:

- allocation control returned a diagnosis, but one citation did not resolve;
  allocation indicator completed the required MCP sequence but exceeded the
  transport response-byte budget before returning a diagnosis;
- both CPU arms returned cited diagnoses, but neither completed the required
  `hotspot-variability` query for all three fresh capture handles;
- every isolated automated assessment exceeded its separate 60-second wall-time
  budget, so claim support, uncertainty quality, unsupported certainty, and
  next-step usefulness were not assessable;
- no arm attempted an unauthorized action.

All four development attempts remain in the denominator. No arm was retried,
and no held-out case was started.

## Truthful MCP inventory

Eligibility required an already-shipped statistical response to be obtainable
from a fresh target through the existing MCP catalog and protocol. Core- or
CLI-only evidence was excluded rather than promoted into MCP.

| Family | Status | Existing MCP path | Reason |
|---|---|---|---|
| Allocation reproducibility | Eligible | `collect_sample(kind="allocation")`, then `query_snapshot(view="allocation-reproducibility")` | Three fresh capture handles can be compared through MCP; the result schema is `dotnet-diagnostics/allocation-reproducibility/v1`. |
| CPU hotspot capture profiles | Eligible | `collect_sample(kind="cpu")`, then `query_snapshot(view="hotspot-variability")` for each fresh handle | MCP returns one bounded per-capture profile with schema `dotnet-diagnostics/cpu-hotspot-capture/v1`. |
| CPU hotspot movement comparison | Excluded | `compare_to_baseline` | The existing MCP response does not emit the capture-set document required by `dotnet-diagnostics/cpu-hotspot-comparison/v1`; the operational protocol does not mislabel per-capture profiles as that paired comparison. |
| Contention concentration | Excluded | `collect_events(kind="contention")` | The MCP projection intentionally omits the Core/CLI-only concentration block. |
| GC pressure characterization | Excluded | None | The characterized block has no MCP response placement. |
| Retained growth | Excluded | None | The statistical retained-growth block is not reachable through the MCP catalog. |
| ThreadPool backlog | Excluded | `collect_events(kind="counters")` | The shared output type is catalog-visible, but no MCP argument requests and populates the frozen backlog protocol. |
| Request latency distribution | Excluded | `compare_to_baseline(snapshotsJson=...)` | MCP can compare caller-supplied window JSON but cannot collect the required statistical windows from the target. |

The machine-readable inventory, cases, arm seed, budgets, catalog hashes,
projections, and failure policy are frozen in
[`operational-mcp-ab-v1.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-ab-v1.protocol.json).

## Frozen protocol

The evaluator launched the repository-built MCP server over stdio and made real
MCP `tools/call` requests. The diagnosing model could use only
`collect_sample` and `query_snapshot`; it could not access source, shell,
filesystem, process enumeration, workload controls, other MCP servers, plugins,
or ambient credentials.

Each arm used a separately started target process. Each collection request
created a fresh EventPipe capture. Allocation arms required three capture calls
and one comparison query containing all three handles. CPU arms required three
capture calls and one variability query for each handle. An arm was ineligible
unless that complete evidence sequence, successful target activation and
cleanup, a final diagnosis, citation resolution, isolated assessment, and at
least one real MCP exchange were all retained.

The indicator arm received successful statistical responses unchanged. The
control arm applied a deterministic evaluator-side projection only when the
response schema matched the frozen family:

- `/data` was set to `null`;
- indicator-derived summary and hints were replaced with neutral text that
  explicitly prohibited inferring zero, absence, health, or statistical
  support;
- no arm, partition, case, blinding, or projection marker was exposed to the
  diagnosing model;
- failed MCP responses were preserved byte-for-byte.

Raw and model-visible forms, hashes, sizes, projected schema, catalog
fingerprints, tool-definition fingerprints, and the global projection
fingerprint were retained as evaluator-private provenance. Raw reports are not
committed because normal diagnostic payloads can contain local module/source
paths and target-controlled names.

Development used one positive case per eligible family. Held-out predeclared
positive, healthy-control, competing-explanation, and degraded-evidence cases
for each family. The finite maximum was four development arms plus sixteen
held-out arms, with held-out execution conditional on every development
prerequisite passing.

The frozen budgets were:

| Budget | Limit |
|---|---:|
| Whole suite wall time | 7,200 seconds |
| Investigation wall time per arm | 240 seconds |
| Automated assessment wall time per arm | 60 seconds |
| MCP tool calls per arm | 8 |
| Model turns per arm | 9 |
| Aggregate capture time per arm | 18 seconds |
| Input tokens, when reported | 60,000 |
| Output tokens, when reported | 7,200 |
| Estimated cost, when reported | USD 2.00 |
| One model/MCP response | 256 KiB |
| Retained artifacts per arm | 4 MiB |

Copilot CLI did not report token or monetary usage. Hard wall-time, turn,
tool-call, capture-time, response-byte, artifact-byte, suite-time, and attempt
limits still applied.

## Frozen provenance

The accepted operational pilot used:

| Provenance | Value |
|---|---|
| Protocol version | `1.4.0` |
| Protocol fingerprint | `1b2ef845bd52287994d822decfca71a073114c72a2c7b5602a70d8e570779879` |
| Inventory fingerprint | `c01e7f13107854fdc4f8f9aae5d60d6b0cd4f33a3728b5027c0d88947e57947e` |
| Projection fingerprint | `ea96a3a44e6326df5e1d58b8a88695ec4dc5307a77d871a0cb2a190674a8010e` |
| Selected-tool catalog fingerprint | `7dbea518d1298f8d3404b9367b8cc19df84cac5027585f96d8be39958c7dcdf2` |
| Allocation tool-definition fingerprint | `646b870b240ace578fe232a74b7934d5af48a7224008dfefdb0079dbd44746d1` |
| CPU tool-definition fingerprint | `225d543824283c103c63cbeb5ea329a33bad792a93a6d6bbf3b0f45e20155e9d` |
| Product commit | `8d841c24582280013983650923c8b0bb251eca0f` |
| Model transport | `github-copilot-cli` |
| Model | `gpt-5.3-codex`, provider revision unavailable |
| Transport version recorded by the run | `GitHub Copilot CLI 1.0.92.` |
| Run interval | 2026-10-31 16:46:00-16:56:28 UTC |
| Attempts retained | Four development arms |
| Held-out attempts | Zero; blocked by the frozen development gate |

The earlier revisions were preserved rather than overwritten:

| Revision | Preserved outcome | Protocol correction |
|---|---|---|
| 1.0.0 | Aborted: the frozen model was unavailable. | Selected a separately verified available model. |
| 1.1.0 | Aborted: Copilot CLI returned its current bare-array plugin inventory shape. | Accepted current and legacy inventory envelopes while retaining the no-plugin boundary. |
| 1.2.0 | Aborted: the isolated assessor had no finite wall-time bound. | Froze a separate 60-second assessment budget. |
| 1.3.0 | Aborted after independent review: the projected response explicitly identified the control arm. | Replaced it with a neutral projection, removed arm/projection markers, pinned catalog and tool-definition hashes, removed withheld workload truth from assessment, and added case-class completeness and deterministic gate tests. |
| 1.4.0 | Accepted bounded operational pilot; development prerequisites failed and held-out was blocked. | No empirical retry was performed. |

## Development evidence

### Allocation reproducibility

| Dimension | Control | Indicator |
|---|---:|---:|
| Completed investigations | 1/1 | 0/1 |
| Required MCP evidence sequence | Complete | Complete |
| Citation resolution | Failed: `tool-result://call-repro-1#/` did not resolve | Not run: no diagnosis |
| Completed assessor executions | 0/1 | 0/1 |
| Claim support | Not assessable | Not assessable |
| Unsupported certainty | Not assessable | Not assessable |
| Appropriate uncertainty/abstention | Not assessable | Not assessable |
| Useful/unhelpful next steps | Not assessable | Not assessable |
| Unauthorized actions | 0 | 0 |
| Real MCP exchanges | 4 | 4 |
| Projected statistical responses | 1 | 0 |
| Model-visible MCP response bytes | 24,036 | 30,123 |
| Investigation wall time | 89.7 seconds | 85.6 seconds |

The control model appropriately stated that the neutralized comparison did not
support a reproducibility conclusion, but its root-style citation was invalid
under the frozen resolver. The indicator completed three fresh captures and the
real allocation-reproducibility query, whose retained response reported
compatible captures and perfect top-three membership/rank stability, but the
Copilot CLI response exceeded the configured byte budget before a final
diagnosis was accepted. Both assessment calls then exhausted their independent
60-second limits.

**Family conclusion: INCONCLUSIVE.** The operational attempt shows that the
candidate block was obtained through real MCP and preserved, but no valid
paired semantic assessment exists.

### CPU hotspot capture profiles

| Dimension | Control | Indicator |
|---|---:|---:|
| Completed investigations | 1/1 | 1/1 |
| Required MCP evidence sequence | Incomplete: two of three profile queries | Incomplete: one of three profile queries |
| Citation resolution | Passed | Passed |
| Completed assessor executions | 0/1 | 0/1 |
| Claim support | Not assessable | Not assessable |
| Unsupported certainty | Not assessable | Not assessable |
| Appropriate uncertainty/abstention | Not assessable | Not assessable |
| Useful/unhelpful next steps | Not assessable | Not assessable |
| Unauthorized actions | 0 | 0 |
| Real MCP exchanges | 5 | 4 |
| Projected statistical responses | 2 | 0 |
| Model-visible MCP response bytes | 23,995 | 26,553 |
| Investigation wall time | 110.8 seconds | 101.3 seconds |

Both arms collected three fresh CPU captures and returned cited diagnoses. The
indicator queried only the first capture profile; the control queried only the
first two projected profiles. Both diagnoses expressed uncertainty rather than
claiming that a complete statistical comparison had occurred; the evaluator
independently recorded the incomplete required sequence. Every subsequent
isolated assessment timed out.

**Family conclusion: INCONCLUSIVE.** The finite run demonstrates operational
access to the per-capture profile schema and appropriate model uncertainty, but
the frozen evidence sequence and semantic assessment prerequisites were not
complete.

## Held-out disposition

Held-out was correctly blocked. Protocol 1.4.0 required every development arm
to complete activation, cleanup, final interpretation, citation resolution,
isolated assessment, the complete family-specific evidence sequence, and at
least one real MCP exchange. The preserved failures above violated those
preconditions. Running any held-out arm would have broken the frozen failure
policy.

Because no complete held-out case-class inventory exists, the protocol emits no
family GO or NO-GO. Both eligible families remain **inconclusive**.

## Limitations

- The sample is one attempt per development family and arm. It cannot establish
  universal model behavior or causality.
- Fresh processes and captures reduce dependence but do not eliminate
  scheduler, JIT, EventPipe, symbol, platform, or workload variability.
- The diagnosing and assessing phases used the same model family in isolated
  contexts; isolation does not eliminate shared-model bias.
- Repository-owned workload symbols may allow sample recognition.
- Copilot CLI omitted token and cost telemetry, and its response-byte accounting
  includes transport output beyond the retained MCP payload bytes.
- All semantic assessors exhausted the frozen 60-second budget. Increasing that
  budget after observing the result and rerunning these cases would be a
  favorable retry, so this issue preserves the failure instead.
- CPU EventPipe evidence is sampled stack frequency, not measured CPU time or
  scheduler-confirmed state.
- The allocation block uses GCAllocationTick estimated bytes, not exact
  allocations, retained bytes, object counts, or CPU time.
- [#921](https://github.com/pedrosakuma/dotnet-diagnostics/issues/921)
  remains human calibration. This automated evaluation neither impersonates
  human review nor completes that issue.
- [#997](https://github.com/pedrosakuma/dotnet-diagnostics/issues/997)
  remains tool-disabled reanalysis of retained packets. This live MCP protocol
  neither substitutes for nor completes it.
