# Allocation directional baseline/current feasibility v4

Issue: [#1389](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1389)

Parents:

- [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)
- [#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

Prerequisite:

- [#1387](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1387), merged by
  [#1388](https://github.com/pedrosakuma/dotnet-diagnostics/pull/1388) as
  `9a46d91e160126253e962432ca37f77ccae3f646`.

## Decision

**INCONCLUSIVE.**

The separately frozen v4 inventory completed in full through the
repository-built MCP server over real stdio:

- 57 registered pairs;
- 108 independently started live sides;
- 108 passed, zero failed, and zero aborted live sides;
- 216 ledger v2 rows, exactly one start and one terminal row per live side;
- 108 unique attempt, process, and capture identities; and
- maximum observed allocation-event count `5,539`.

The maximum event count is `7.39%` of the predeclared `75,000` operating
limit and `5.54%` of the unchanged Core `MaxAllocationEvents=100,000` cap.
`MaxAllocationFrames=16,384` also remains unchanged.

The numeric gate did not pass. Blinded evaluation was therefore forbidden
and did not run. This result authorizes no Core, CLI, MCP, VS Code, collector,
schema, retention, tool, catalog, or product-contract change. No downstream
product design issue is created.

## Preservation and freeze

The v1, v2, and v3 protocols and results, the throughput calibration, and
bounded calibration v1-v6 artifacts remain unchanged. The v4 protocol embeds
portable SHA-256 guards for every preserved artifact. None was rerun,
replaced, deleted, reclassified, or reinterpreted.

The v4 inventory uses new workload, pair, attempt, capture, process, ordering,
and artifact identities. Its protocol was reviewed independently before
freeze. That review found one failure-path parity defect: a real failed side
records zero payload and retained values, while draft validation initially
required successful planned values unconditionally. The validator and
deterministic test were corrected before freeze so a failed side remains in
the denominator and produces a truthful report instead of aborting report
creation.

The frozen geometry is:

- one-second capture;
- 200 ms arm delay;
- 700 ms workload;
- one-millisecond pause after every eight operations for the calibrated
  low/increased and periodic bounded shapes;
- at least three independent pairs and two retained inliers per selector; and
- pair-first median/raw MAD summaries with retained modified-z outlier
  candidates.

No frozen source or runtime input changed between freeze and the one-time
empirical execution.

## Contracts

The primary quantity is estimated sampled allocation bytes divided by
completed workload operations. The unit is estimated sampled bytes per
operation. Operations per second is used only to establish the throughput
manipulation; sampled bytes per second remains descriptive.

The practical relative margin is:

`max(workload-specific robust A/A inlier envelope, 10%)`.

- Increase and lower-allocation movement must be strictly beyond the margin
  with retained-inlier sign agreement.
- Throughput must be strictly above `1.25x` operations/second while normalized
  bytes/operation remains inclusively within the margin.
- Warmup must move strictly beyond the early margin and later converge
  inclusively within it.
- Mix must keep the normalized total equivalent while type and site each show
  independently normalized absolute movement plus share movement.
- Fixed-stride periodic controls require both A/A shapes and both reorder
  directions, stable normalized totals, and abstention from type/site pressure
  or regression interpretation.
- Partial attribution, changed workload, and missing/zero-weight evidence use
  explicit per-level availability or abstention.

Measurement-quality fields remain evaluator-only. The model-visible quality
field inventory is empty.

## Numeric gate

| Scenario | Assessable | Expected behavior | Result |
|---|---:|---:|---|
| True allocation increase | Yes | Yes | Retained inliers passed; one `18.31%` A/A outlier remained reported and in the pair denominator |
| Stable-total allocation mix shift | Yes | Yes | Total remained equivalent; type and site passed independent absolute-plus-share movement |
| Throughput increase, stable bytes/operation | Yes | **No** | Throughput manipulation passed, but normalized bytes/operation did not remain equivalent |
| Lower allocation, unchanged retained memory | **No** | No | One retained A/A delta was `11.17%`, above the predeclared `10%` sanity bound |
| Warmup/transient convergence | Yes | Yes | Early movement passed and converged evidence returned within the practical margin |
| Changed workload/request mix | Yes | Yes | Incompatible workload identity caused abstention before direction |
| Fixed-stride periodic A/A and reorder | **No** | No | One retained periodic A/A delta was `10.71%`, above the predeclared `10%` sanity bound |
| Partial type/site attribution | Yes | Yes | Type remained available while site explicitly abstained |
| Missing or zero-weight evidence | Yes | Yes | Quantity remained unavailable and retained in the denominator |
| Healthy A/A | Yes | Yes | Two retained inliers stayed within the `10%` bound; one `151.29%` outlier remained reported and in the pair denominator |

The three throughput effect pairs established very large operations/second
multipliers (`65.66x`, `39.13x`, and `60.74x`), but their normalized
bytes/operation deltas were `36.88%`, `147.47%`, and `52.10%`. The
predeclared selector therefore rejected normalized equivalence rather than
mistaking throughput growth for a safe directional allocation comparison.

The lower-allocation effect itself was directionally strong
(`-88.70%`, `-88.70%`, and `-88.99%` normalized movement), but the scenario
was not assessable because its own A/A inventory crossed the strict `10%`
sanity bound. The fixed-stride reorder totals were mostly close, but its A/A
inventory likewise crossed the same predeclared boundary. Neither scenario
was threshold-fitted or rerun.

The healthy A/A selector excluded one `151.29%` modified-z candidate and
retained the other two pairs as inliers; the true-increase A/A selector
similarly retained an `18.31%` candidate as an outlier. Both pairs remain in
the fixture, ledger-derived accounting, report denominator, and documented
limitations. Their exclusion affects the robust selector summary only and
does not convert the overall result into GO.

## Error taxonomy and accounting

Every live side passed, so no empirical failure category was needed. The
frozen protocol nevertheless retains the reviewed truthful taxonomy:

- `PermissionDenied` is reserved for evidence-backed diagnostic endpoint
  authorization or socket access failures;
- `ResourceLimitExceeded` retains the public
  `allocation.capture.limit` identifier;
- transport, timeout, cancellation, cleanup, invalid-state, malformed,
  unavailable, and internal failures remain distinct;
- raw exception detail remains private and bounded; and
- no error may be converted into a success-shaped fallback.

The one-time execution guard refuses any existing fixture, ledger, or report
path. Failed and aborted sides would remain in denominators without retry or
replacement.

## Retained artifact fingerprints

Portable hashes use UTF-8 without BOM and canonical CRLF line endings.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| v4 protocol | 45,377 | `9147ed54248aa479330d0f4669f64b4777dd711b53cfdaccd4bcfa394e094e24` |
| v4 fixture | 496,905 | `29d7e7b50fe2ba4a4d00475e24366fe73465d6502458e81ac78a512c4840a7da` |
| v4 ledger | 73,277 | `57d8016c5fd845f02793b1a1fb5f21a20f20415a72a07724c639bde136034668` |
| v4 report | 5,520 | `98b6c9d22fb6843708456247261a95406fbf31900097278434759e3c8db53faa` |

## Conclusion

The calibrated bounded geometry completed across the full registered inventory
and stayed far below the event operating limit. That completion does not
establish general operational feasibility: retained same-workload outliers and
the registered throughput/lower/fixed-stride failures remain material
limitations. Because the numeric gate is false, operational value was not
evaluated and the final decision remains **INCONCLUSIVE**.
