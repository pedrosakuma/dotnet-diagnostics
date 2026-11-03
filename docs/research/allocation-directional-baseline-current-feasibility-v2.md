# Allocation directional baseline/current feasibility v2

Issue: [#1379](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1379)

Parent: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)

Corrected-harness prerequisite:
`4209ebc66b7c1c9c5cbe6fc80d901216585ffdb9` (#1377 through #1378).

## Decision

**INCONCLUSIVE.**

The separately versioned protocol retained all 48 live pairs, 96 independently
started live sides, three controlled partial-attribution pairs, and all 192 v2
ledger rows without retry or replacement. Seventy-two live sides passed and 24
failed with bounded `McpIsError` / `PermissionDenied` evidence.

The failed sides made true increase, lower allocation, fixed-stride bias, and
partial attribution incomplete. The complete throughput and warmup scenarios
also violated their workload-specific A/A expectations. Because the frozen
decision rule states that incomplete inventory or assessability remains
`INCONCLUSIVE`, the numeric gate did not pass and the blinded evaluation did
not run.

This result authorizes no product, schema, collector, retention, tool, catalog,
CLI, or VS Code change. It creates no automatic regression, improvement,
pressure, severity, health, leak, dominance, causality, or optimization-priority
claim. No downstream design issue was created.

## Preservation of the first protocol

The five #1350 artifacts remain unchanged:

- the v1 protocol;
- the execution fixture;
- the provenance-corrected fixture;
- the append-only ledger; and
- the numeric report.

Tests retain both checkout-independent canonical fingerprints and exact
committed Git-blob fingerprints for those files. No v1 side was rerun,
replaced, or reinterpreted.

## Frozen v2 protocol

Independent Claude Sonnet 5.5 review completed before freeze while GPT-5.6 Sol
authored the protocol. Four review passes covered plan parity, quantities,
units, normalization, scenario expectations, failure policy, fresh identities,
source and runtime fingerprints, and decision gates.

| Frozen item | Value |
|---|---|
| Protocol version | `2.0.0` |
| Frozen at | `2026-11-03T18:18:48.8722185Z` |
| Prerequisite/source base | `4209ebc66b7c1c9c5cbe6fc80d901216585ffdb9` |
| Protocol SHA-256 | `20af784db457c573ae0c3b7c5cb21ff5db99b94cd4cc6ea8567f899e60699d53` |
| Frozen source inputs | `24` |
| Frozen runtime artifacts | `60` |
| Live pairs / sides | `48 / 96` |
| Controlled projection pairs / sides | `3 / 6` |
| Total pairs | `51` |
| Required independent pairs | `3` per plan entry |

The runtime inventory hashes every DLL, deps file, and runtime-config file in
the exact Release MCP and BadCodeSample output directories. Execution refuses a
Debug MCP or sample. The corrected `data.allocation` projector, bounded
stage-specific provenance, real process identities, and v2 one-start/one-terminal
ledger are frozen inputs.

Every live observation records:

- a fresh workload, pair, attempt, capture, and actual process identity;
- the quantity `estimated sampled allocation bytes`;
- the unit and baseline/current role;
- the completed-operation denominator and normalization formula;
- workload and sampler assumptions;
- availability and abstention reason; and
- independent type-level and site-level availability.

No measurement-quality field is included in the diagnosing-model projection.
Compatibility, availability, fixed-stride limitations, attribution, and
workload-specific A/A envelopes are evaluator-only gates.

## One-time execution

The only empirical execution used the repository-built MCP over real stdio on
Windows x64. Every side started a separate BadCodeSample process.

| Accounting | Result |
|---|---:|
| Live sides started | `96 / 96` |
| Terminal live sides | `96 / 96` |
| Passed live sides | `72` |
| Failed live sides | `24` |
| Aborted live sides | `0` |
| Ledger rows | `192 / 192` |
| Total retained pairs | `51 / 51` |

All 24 failures were explicit `CallToolResult.IsError` outcomes with structured
kind `PermissionDenied`. The retained privacy-safe evidence does not establish
the underlying permission cause, so no cause is inferred.

| Scenario / shape | Failed sides |
|---|---:|
| True increase / `low` | `9` |
| Lower allocation / `low` | `3` |
| Fixed-stride / `periodic-a` | `9` |
| Fixed-stride / `periodic-b` | `3` |

Failed and aborted observations remain in their original pair and scenario
denominators. The run did not retry, replace, delete, or favorably select any
side.

## Numeric gate

| Scenario | Attempted pairs | Valid pairs | Assessable | Expected behavior |
|---|---:|---:|---|---|
| True increase | 6 | 0 | No | Not established |
| Stable-total mix shift | 6 | 6 | Yes | Observed |
| Throughput increase | 6 | 6 | Yes | **Control failed** |
| Lower allocation, stable retained | 6 | 3 | No | Not established |
| Warmup transient | 9 | 9 | Yes | **Control failed** |
| Changed workload | 3 | 0 compatible | Yes | Correctly abstained |
| Fixed-stride bias | 6 | 0 | No | Not established |
| Partial attribution | 3 | 0 | No | Not established |
| Missing/zero-weight | 3 | 0 compatible | Yes | Correctly abstained |
| Healthy A/A | 3 | 3 | Yes | Observed |

The healthy A/A maximum absolute relative movement in estimated sampled bytes
per operation was approximately `0.00008372`. Its maximum type-share and
site-share movements were approximately `0.00005882` and `0.00005567`.
Those values are provenance-only and were not model-visible diagnostic fields.

The numeric gate is `false`. Blinded evaluation was therefore forbidden and
did not run.

## Execution-report parity correction

The execution-time evaluator returned `NO-GO` because complete throughput and
warmup controls were unsafe, even though four required scenarios were
incomplete. That contradicted the frozen rule that incomplete inventory or
assessability is `INCONCLUSIVE`.

The execution fixture and execution-time report are preserved byte-for-byte.
The evaluator parity correction added an explicit per-scenario `assessable`
state and gives incompleteness precedence over `NO-GO`. The final fixture adds
only a provenance note; the final report was regenerated from the same
observations and ledger without live capture, retry, replacement, or changed
scenario evidence. The source hash guard now rejects any rerun against the
post-execution evaluator.

## Artifacts

Portable hashes use UTF-8 without BOM and canonical CRLF line endings.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| Frozen v2 protocol | 33,043 | `20af784db457c573ae0c3b7c5cb21ff5db99b94cd4cc6ea8567f899e60699d53` |
| Execution fixture | 436,823 | `a29d43bddba2d955969c275a5e3f275d222cd947be6a7329b29626bfa48664fa` |
| Execution-time report | 4,657 | `cb2cf09c3044df97f0c904ebb0717c1cf63d2167078e46a1d6fa886a920d403a` |
| Final fixture with provenance note | 437,408 | `d5c639412cf1e0ee446e1ed4018c4448ac672c6187b73134aa4ee5d3566b7a89` |
| V2 ledger | 64,106 | `d61e4526ffe456823c8a16c989d9500700dc0355a24b083a5c728133e9d266dd` |
| Final report | 4,939 | `ebaf3f6c2ee3c8aa1ddbf457a6c7faa6a5dc56b7753d885a694bc6acc103570f` |

## Conclusion

Allocation baseline/current directional feasibility remains unestablished.
The correct action is abstention. The observed permission failures, incomplete
required scenarios, throughput-normalization failure, and warmup-control
failure prevent a safe directional product contract. A new experiment would
require a separately authorized issue and protocol; this frozen inventory
cannot be rerun.
