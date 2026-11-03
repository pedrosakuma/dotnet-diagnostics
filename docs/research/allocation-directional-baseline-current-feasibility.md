# Allocation directional baseline/current feasibility

Issue: [#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

Parent: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)

Prerequisite Protocol 10 merge:
`402c3d2fefc8bb094b7341e5482559ce953f9395`.

## Decision

**INCONCLUSIVE.**

The frozen numeric gate did not obtain the three valid independent healthy
A/A pairs required before any directional scenario could be assessed. All 72
predeclared live MCP sides started exactly once and were retained as failures.
The conditional blinded evaluation therefore did not run. This result
authorizes no product, schema, collector, retention, tool, catalog, CLI, or
VS Code contract, and it does not justify a downstream design issue.

The passing live xUnit invocation means that the bounded one-time run completed
and persisted its fixture, ledger, and report. It does not mean that the
numeric feasibility gate passed.

## Frozen question and boundaries

The study asked whether baseline/current allocation evidence could support
directional interpretation across the issue's known shapes while preserving:

- separately started target processes for every baseline and current side;
- workload identity, operation denominators, quantities, and units;
- healthy and workload-specific A/A controls;
- fixed-stride ordering bias, partial attribution, changed workload, and
  missing-denominator controls;
- every failure and abort in its original denominator; and
- an explicit `GO`, `NO-GO`, or `INCONCLUSIVE` decision.

Protocol 10 selected `noStatisticalFields`. Accordingly, the frozen study
contains no model-visible median/MAD, Jaccard, Spearman, categorical-quality,
or other measurement-quality field. Compatibility, availability,
attribution, sampling limitations, and A/A envelopes are evaluator-only
abstention or safety gates.

The machine-readable protocol is
[`allocation-directional-baseline-current-v1.protocol.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v1.protocol.json).
Its `capturePlan` is the single workload declaration and is mechanically
checked against the executable plan.

## Freeze and independent review

The protocol was reviewed independently with `claude-sonnet-5.5` while the
implementation was authored with `gpt-5.6-sol`. Three review passes occurred
before execution. They found and caused correction of:

- synthetic-by-construction scenarios;
- workload-mismatched or unbounded A/A envelopes;
- missing attribution being treated as zero movement;
- incomplete failure retention and timeout handling;
- failed sides satisfying control behavior;
- incomplete protocol-to-plan parity; and
- hard-coded rather than derived pair/side inventories.

No empirical attempt started until those findings were addressed, the seven
deterministic protocol tests passed, the reviewed sources were hash-guarded,
and the freeze commit was created.

| Frozen item | Value |
|---|---|
| Protocol version | `1.0.0` |
| Frozen at | `2026-11-03 15:49:07 UTC` |
| Source base / Protocol 10 merge | `402c3d2fefc8bb094b7341e5482559ce953f9395` |
| Freeze commit | `184cc9af11f8e14f431a1bab3b02f753eae93baa` |
| Canonical protocol SHA-256 | `ef12d0f6caa48cc2e4be51317f19142094e3d3e8c75af5b8ffd4a7bc2610713a` |
| Execution-time raw Windows protocol SHA-256 | `9d60b47bdc30dffe7c3ec08ebb4d8673bded60091c1af43a67208c94090987d3` |
| Required independent pairs | `3` per predeclared capture-plan entry |
| Live MCP pairs / sides | `36 / 72` |
| Controlled projection pairs / sides | `3 / 6` |
| Total pairs | `39` |
| Capture window | `4 s` |
| Per-side timeout | `45 s` |
| MCP completion timeout | `30 s` |

The protocol records SHA-256 fingerprints for the SDK pin, sample workload,
contracts, evaluator, JSON handling, live runner, executable plan, and
protocol validator. The live runner validates those fingerprints before
starting the first ledger entry.

Independent final review found that the original hashes were calculated from
raw Windows CRLF bytes and would not reproduce on an LF checkout. A
representation-only provenance correction now defines all text fingerprints
as UTF-8 without BOM with canonical CRLF line endings. The original protocol
and fixture hashes remain preserved as execution-time provenance; the
canonical protocol and corrected fixture add checkout-independent
fingerprints. No live observation, outcome, denominator, scenario rule, or
decision changed, and no empirical side was rerun.

The correction also made the only post-execution code edits: additive
provenance fields in `AllocationDirectionalContracts.cs` and
`AllocationDirectionalLive.cs`, plus the canonical text-hash implementation in
`AllocationDirectionalProtocol.cs`. The executed runner remains exactly the
freeze-commit `184cc9a` version; its original source hashes are recoverable
from that commit's protocol. These later edits did not participate in or alter
any executed attempt.

## One-time empirical execution

The only permitted run executed on Windows x64 with .NET `10.0.14` from
`2026-11-03 15:50:25 UTC` through `15:57:26 UTC`. It used the
repository-built MCP server over real stdio. Every live side started a fresh
`BadCodeSample` process. No side was retried or replaced.

| Accounting | Result |
|---|---:|
| Predeclared live sides started | `72 / 72` |
| Live sides with terminal ledger rows | `72 / 72` |
| Passed live sides | `0` |
| Failed live sides | `72` |
| Aborted live sides | `0` |
| Controlled projection sides retained | `6 / 6` |
| Total fixture pairs retained | `39 / 39` |
| Ledger rows | `144` |

The live-side failures remained distributed across every frozen scenario:

| Scenario | Frozen live pairs | Failed live sides |
|---|---:|---:|
| Healthy A/A | 3 | 6 |
| True increase | 3 | 6 |
| Mix shift, stable total | 3 | 6 |
| Throughput A/A plus intensity | 6 | 12 |
| Fixed-stride A/A plus reordered | 6 | 12 |
| Lower allocation, stable retained bytes | 3 | 6 |
| Warmup early plus converged | 6 | 12 |
| Changed workload | 3 | 6 |
| Missing operation denominator | 3 | 6 |

The three partial-attribution pairs were controlled projections of the
retained true-increase sides. They were never counted as independent empirical
support.

## Failure evidence

The preserved live observations contain two failure categories:

| Failure category | Live sides | Shapes |
|---|---:|---|
| `KeyNotFoundException` | 54 | `primary`, `changed`, `increased` |
| `InvalidOperationException` | 18 | `low`, `periodic-a`, `periodic-b` |

Source inspection after the run explains the 54 `KeyNotFoundException`
failures without rerunning anything. The unified `collect_sample` response
nests the allocation payload at `data.allocation`, while the frozen research
runner attempted to read `totalBytes`, `totalEvents`, `topByBytes`, and
`topBySite` directly from `data`. Those sides reached a successful response
payload but failed during research-only projection.

The exact cause of the 18 `InvalidOperationException` sides is not recoverable
from the retained artifact. The frozen runner intentionally retained the
exception type but not the exception message, and that type can represent
either an MCP error envelope or another projection invariant. All such sides
remain failures; no favorable diagnosis or replacement is inferred.

These are study-harness failures, not evidence that the production allocation
collector is directionally safe or unsafe. The run did use the real MCP
surface, and its inability to produce assessable frozen evidence is itself the
feasibility outcome.

## Post-run harness reliability correction

Issue [#1377](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1377)
corrected the research harness without rerunning, replacing, or reclassifying
any frozen observation. The corrected projector:

- requires an object root and object `data` envelope;
- requires the `data.kind` discriminator to be the string `allocation`;
- reads the allocation payload only from `data.allocation`;
- validates `totalBytes`, `totalEvents`, `topByBytes`, `topBySite`, and every
  retained row before constructing a successful observation; and
- classifies malformed envelopes, wrong schemas, unavailable payloads, missing
  fields, and type mismatches explicitly instead of treating broad
  `GetProperty` exceptions as semantic evidence.

Deterministic non-empirical fixtures cover `primary`, `changed`, `increased`,
`low`, `periodic-a`, `periodic-b`, zero-weight, and missing-attribution
success envelopes. Separate replay fixtures cover process start/readiness,
warmup, workload, MCP transport, `CallToolResult.IsError`, structured root
errors, projection/schema failures, denominator and retained-value invariants,
timeout, cancellation, cleanup, and unexpected harness exceptions. No target
process or MCP protocol was executed for this correction.

New attempt provenance is additive and optional, so the five frozen artifacts
and their canonical fingerprints below remain byte-for-byte unchanged. For a
future protocol it records exactly one failure category plus the source-owned
stage/operation identifier, `IsError`, the validated schema identity, a
sanitized bounded error kind, redacted-detail state/byte count, raw-response
SHA-256 and byte count with digest-only retention, exception type, and cleanup
state. It never retains unrestricted messages, response payloads, credentials,
paths, stacks, symbols, or target-controlled names. The v2 append-only ledger
requires exactly one start row and one terminal row for every predeclared live
attempt and rejects retry/replacement accounting.

The original protocol's source hashes intentionally no longer match the
corrected harness. `ValidateFrozen()` therefore rejects any attempt to rerun
the #1350 protocol. A future empirical study must freeze a separately reviewed
protocol and cannot reuse these results as successful evidence.

## Gate evaluation

The evaluator first requires three valid healthy A/A pairs. It received zero.
Therefore:

- no A/A envelope was available;
- no known-shape directional result was assessable;
- `numericGatePassed` is `false`;
- `blindedEvaluationRequired` is `false`; and
- the final decision is `INCONCLUSIVE`.

No median/MAD, Jaccard, Spearman, categorical quality, sampling-quality
headline, or diagnostic-quality signal was exposed to a diagnosing model.
There was no diagnosing-model execution because the numeric prerequisite did
not pass.

## Preserved artifacts

The table uses the declared UTF-8/no-BOM/canonical-CRLF representation for
portable byte counts and SHA-256 values.

| Artifact | Canonical bytes | Canonical SHA-256 |
|---|---:|---|
| Execution-time protocol at freeze commit `184cc9a` | 14,511 | `9d60b47bdc30dffe7c3ec08ebb4d8673bded60091c1af43a67208c94090987d3` |
| Checkout-independent corrected protocol | 14,564 | `ef12d0f6caa48cc2e4be51317f19142094e3d3e8c75af5b8ffd4a7bc2610713a` |
| Original execution fixture | 65,282 | `06b917e17524f9f4b2ffb4ebfa0dd504327a49a2fa59b2fb8efc37df92bc7dfc` |
| Fixture with provenance correction | 65,727 | `c251312571df99ea1a4d2e5c10b53c8adf4a2347ea2a24b0ef67dedbde061c50` |
| Append-only attempt ledger | 18,657 | `087402d4a6813092a4c3f250b34713ba8c2e263eb269cec207036daa7dcaa690` |
| Numeric report | 483 | `12ec9b0ecf6f551111af899e50af6ce9e635f3be2a6b7b505dea3773e058e7c4` |

## Conclusion and boundaries

The requested baseline/current allocation direction is **not established** by
this finite evidence gate. The correct action is abstention, not a product
implementation and not a favorable retry.

- No product or public contract was created.
- No automatic regression, improvement, severity, health, leak, dominance,
  causality, or optimization-priority verdict is authorized.
- Measurement quality remains internal/provenance-only.
- No downstream design issue should be opened because the result is not `GO`.
