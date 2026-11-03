# Allocation directional baseline/current feasibility v3

Issue: [#1385](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1385)

Parent: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)

Prerequisites:

- #1381 merged through PR #1383 as
  `9cb6966e249439925ce11cb5871b4f7d808dd9cc`;
- #1382 merged through PR #1384 as
  `9bf5701b879f7cdc85b93ec9efbf0e0cec76c548`.

## Decision

**INCONCLUSIVE.**

The separately versioned protocol retained all 54 predeclared live pairs,
108 independently started live sides, three controlled partial-attribution
pairs, and all 216 ledger rows without retry or replacement. Seventy-two live
sides passed and 36 failed with bounded `McpIsError` /
`ResourceLimitExceeded` evidence. No live failure was classified as
`PermissionDenied`.

The failures made true increase, lower allocation, fixed-stride bias, and
partial attribution incomplete. The complete stable-total mix, calibrated
throughput, and warmup scenarios also failed their frozen safety gates. The
changed-workload, missing-denominator, and healthy A/A controls behaved safely.

Incomplete inventory and assessability take precedence over unsafe complete
controls, so the final numeric decision is `INCONCLUSIVE`, not `NO-GO`.
The numeric gate is false. The blinded evaluation was therefore forbidden and
did not run.

This result authorizes no Core, CLI, MCP, VS Code, collector, retention,
formula, schema, tool, catalog, or product-contract change. It creates no
automatic regression, improvement, pressure, severity, health, leak,
dominance, causality, or optimization-priority claim. No downstream product
design issue was created.

## Preservation

Protocols v1 and v2, the v1 and v2 executions, final fixtures, ledgers,
reports, and the #1382 throughput-calibration fixture and report remain
unchanged. The v3 protocol hash-guards 13 preserved artifacts in addition to
the existing exact portable and Git-blob fingerprint tests.

No prior observation was rerun, replaced, deleted, reclassified, or
reinterpreted.

## Frozen v3 protocol

The v3 contract was independently reviewed with Claude Sonnet 5.5 while
GPT-5.6 Sol authored the implementation. The review covered plan parity,
fresh identities, quantities, units, normalization, selectors, practical
floor, error taxonomy, preservation, fingerprints, failure policy, and
decision gates.

The review found one high-confidence correctness issue: the missing-denominator
gate initially required sampled weight to be zero even though a target can
allocate background bytes while completing zero workload operations. The
requirement was removed; the gate now requires an absent denominator plus
explicit unavailability and abstention. The review also requested deterministic
execution-constant parity and historical-identity-disjointness guards. All
three findings were fixed before freeze.

| Frozen item | Value |
|---|---|
| Protocol version | `3.0.0` |
| Frozen at | `2026-11-03T21:09:12.0971378Z` |
| Source base | `9bf5701b879f7cdc85b93ec9efbf0e0cec76c548` |
| Protocol SHA-256 | `5650195a237c3066832b2bec5f2dcb833b1dd8448e774a3c9c75a81f033a0070` |
| Frozen source inputs | `30` |
| Frozen runtime artifacts | `60` |
| Preserved artifact guards | `13` |
| Live pairs / sides | `54 / 108` |
| Controlled projection pairs / sides | `3 / 6` |
| Total pairs | `57` |
| Required independent pairs | `3` per plan entry |

Every workload, pair, attempt, capture, and controlled-projection identity is
new and disjoint from the retained v1, v2, and throughput-calibration
inventories. Actual live process identities contain the independently started
PID and timestamp and are unique within the retained fixture.

The throughput scenario uses one `primary-paced` worker versus one `primary`
worker under the same capture topology and workload identity. Operations per
second establishes only the workload-rate manipulation. Estimated sampled
bytes per operation remains the normalized allocation quantity.

Warmup uses a stateful `warmup-transient` research fixture. The first request
per process performs the transient higher-allocation shape; a pre-capture
warmup request consumes that phase for steady and converged sides. The selector
uses the #1382 pair-first median/raw-MAD method, retained modified-z outlier
candidates, at least three valid pairs, at least two inliers, positive
non-outlier sign agreement, strict
`max(workload-specific A/A envelope, 10%)` early movement, and return inside
the A/A envelope after convergence.

The fixed-stride inventory includes separate `periodic-a` and `periodic-b`
A/A entries plus both A-to-B and B-to-A equal-payload reorder directions.

No measurement-quality field is model-visible. Quality, availability,
compatibility, provenance, fixed-stride limitations, and A/A envelopes are
evaluator-only gates or explicit abstention reasons.

## Deterministic validation

Before freeze, deterministic tests verified:

- corrected `data.allocation` projection and bounded provenance;
- exact v2 ledger start/terminal accounting and retry rejection;
- #1381 allocation failure classification, including evidence-backed
  permission denial and non-permission invalid-operation paths;
- #1382 throughput, pair-summary, modified-z, sign, practical-floor,
  convergence, and insufficient-pair selectors;
- protocol/plan parity and zero model-visible quality fields;
- primary-paced versus primary topology;
- v3/v1/v2/calibration identity disjointness;
- missing-denominator abstention with positive background sampled weight;
- source, runtime, and preserved-artifact frozen fingerprints; and
- synthetic complete-inventory numeric-GO and incomplete-inventory
  `INCONCLUSIVE` behavior.

The focused validation passed 33 scenario-evaluator tests and 12 Core error
classification tests before freeze. The frozen protocol validation then passed
against all source, runtime, and preserved-artifact hashes.

After the one-time execution and artifact retention, a post-execution guard
deliberately changed a frozen source input. `ValidateFrozen()` now fails with a
frozen-source hash mismatch before any target process can start. Choosing a
different fixture output path cannot authorize a second execution.

## One-time real-MCP execution

The only empirical execution used the repository-built MCP server over real
stdio on Windows x64. Every live side started a separate BadCodeSample process.
The run completed in 10 minutes 48 seconds.

| Accounting | Result |
|---|---:|
| Live pairs retained | `54 / 54` |
| Live sides started | `108 / 108` |
| Terminal live sides | `108 / 108` |
| Passed live sides | `72` |
| Failed live sides | `36` |
| Aborted live sides | `0` |
| Controlled projection sides passed / failed | `3 / 3` |
| Ledger rows | `216 / 216` |
| Unique live attempt IDs | `108 / 108` |
| Total retained pairs | `57 / 57` |

All 36 live failures were explicit `CallToolResult.IsError` outcomes with
structured kind `ResourceLimitExceeded`. Their bounded retained evidence names
the owned operation `mcp.call-tool-result-is-error`; it does not expose raw
exception messages, paths, stacks, payloads, or credentials.

| Scenario / failed shape | Failed live sides |
|---|---:|
| True increase / `low` | `9` |
| Lower allocation / `low` | `3` |
| Fixed-stride / `periodic-a` | `12` |
| Fixed-stride / `periodic-b` | `12` |

Failed observations remain in their original pair and scenario denominators.
The run did not retry, replace, delete, or favorably select any side.
The post-execution source-hash guard prevents rerunning the frozen inventory.

## Numeric gate

| Scenario | Attempted pairs | Valid pairs | Assessable | Expected behavior | Safety |
|---|---:|---:|---|---|---|
| True allocation increase | 6 | 0 | No | Not established | Incomplete |
| Stable-total mix shift | 6 | 6 | Yes | Not observed | Unsafe complete control |
| Calibrated throughput | 6 | 6 | Yes | Manipulation established; normalized stability failed | Unsafe complete control |
| Lower allocation, stable retained | 6 | 3 | No | Not established | Incomplete |
| Warmup transient convergence | 9 | 9 | Yes | Early effect observed; convergence failed | Unsafe complete control |
| Changed workload | 3 | 0 compatible | Yes | Correctly abstained | Safe |
| Fixed-stride bias | 12 | 0 | No | Not established | Incomplete |
| Partial attribution | 3 | 0 | No | Not established | Incomplete |
| Missing/zero denominator | 3 | 0 compatible | Yes | Correctly abstained | Safe |
| Healthy A/A | 3 | 3 | Yes | Observed | Safe |

Healthy A/A retained maximum absolute relative estimated-bytes/operation
movement `0.0001186351` (approximately `0.01186%`), maximum type-share
movement `0.0000868853`, and maximum site-share movement `0.0001239544`.
These values are evaluator-only and were not model-visible diagnostic fields.

### Calibrated throughput

The workload-rate manipulation was robust:

- operations/second effect multipliers:
  `91.46392x`, `91.00000x`, and `89.44444x`;
- median multiplier: `91.00000x`, well above the strict `>1.25x` gate.

However, the workload-specific A/A maximum absolute relative
estimated-bytes/operation movement was approximately `0.00375627`, while all
three effect movements were approximately `0.00446` to `0.00460`. The
throughput manipulation therefore succeeded, but normalized allocation
stability did not remain inside A/A.

### Warmup

The early pair deltas were approximately `0.99975` to `0.99997`, with median
`0.99987947`, so the early movement robustly exceeded the strict 10% practical
floor with positive sign agreement.

The converged absolute pair deltas were approximately `0.00000194`,
`0.00005221`, and `0.00009325`. The workload-specific A/A maximum was
approximately `0.00007437`; the largest converged non-outlier remained outside
that envelope, so convergence failed under the frozen rule.

### Stable-total mix

The mix workload A/A maximum absolute relative normalized-total movement was
approximately `0.00007552`. Two effect pairs moved by approximately
`0.00010844` and `0.00011529`, outside that envelope. The scenario therefore
did not establish stable normalized total together with the required
independent absolute-and-share type/site movement.

## Artifacts

Portable hashes use UTF-8 without BOM and canonical CRLF line endings.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| Frozen v3 protocol | 35,702 | `5650195a237c3066832b2bec5f2dcb833b1dd8448e774a3c9c75a81f033a0070` |
| V3 execution fixture | 464,964 | `051cfc304bb4f7837505d8bc9bb106955fa75c2741bbbd8fabf82fb443577f81` |
| V3 ledger | 72,116 | `5d8ff0be8bd0d902d041ec981b122ce5449152f1084dcb1cd7b32f499bb0d70b` |
| V3 numeric report | 5,113 | `228d515322bbc7c9bd8a4f94d6223d92cc3bacbbcc70eb61c72c2ae748305ce1` |

## Conclusion

Allocation baseline/current directional feasibility remains unestablished.
The correct action is abstention. The corrected taxonomy prevents false
permission claims, and the calibrated throughput manipulation works, but the
finite inventory still has incomplete required scenarios and unsafe complete
controls. Because the numeric gate did not pass, blinded evaluation and
downstream product design remain unauthorized.
