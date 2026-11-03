# Allocation directional calibration gate

Issue: [#1382](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1382)

Parent: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)

Frozen predecessor:
[#1379](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1379).

## Scope and decision

**PASS for pre-freeze design calibration.**

This is not a new directional-feasibility protocol, final inventory, product
contract, or reinterpretation of #1379. The six-pair calibration fixture is
separate from the 51 frozen #1379 pairs and uses fresh processes, attempts,
captures, workload identity, fixture identity, and report identity.

The gate establishes two design prerequisites for a future separately reviewed
protocol:

1. an explicit pacing manipulation reliably produces more than `1.25x`
   operations/second under the same real-MCP allocation-capture topology while
   estimated sampled bytes/operation stays stable; and
2. directional selectors use pair-first median/MAD summaries with explicit
   outlier, sign, practical-effect, convergence, and insufficient-pair rules.

No Core, CLI, MCP, VS Code, collector, schema, retention, tool, or catalog
contract changes.

## Why the original throughput fixture did not increase throughput

The frozen v2 plan used one versus two concurrent HTTP workers, but each worker
ran the same allocation-heavy loop inside one target process for the same
duration. One worker already consumed the process's allocation/GC throughput.
The second worker therefore contended for the same CPU and GC resources rather
than introducing an independent workload-rate control. The three frozen effect
pairs retained only `0.91318x`, `0.95114x`, and `0.86767x`
operations/second.

The calibration fixture replaces worker-count scaling with an explicit pacing
control:

- baseline: one `primary-paced` worker, which performs the exact primary
  `288,000` requested payload bytes/operation and then pauses for 1 ms;
- current: one unpaced `primary` worker with the same operation and requested
  payload bytes/operation;
- A/A: paced baseline versus independently started paced current;
- capture topology: separately started BadCodeSample processes, repository-built
  MCP over real stdio, four-second allocation capture, 900 ms arm delay,
  1,500 ms workload request, and the same operation-denominator projection as
  the frozen study.

The pause is an intentionally strong fixture manipulation, not a production
recommendation and not a threshold fitted to #1379.

## Throughput calibration result

The one-time Windows x64 calibration retained three A/A pairs and three effect
pairs. Every side passed without retry or replacement.

| Quantity | Result | Decision role |
|---|---:|---|
| Effect operations/second multiplier | median `82.60185x`; range `64.33036x` to `89.28431x` | Establishes workload manipulation |
| Workload A/A maximum absolute relative bytes/operation delta | `0.00299348` (`0.29935%`) | Compatibility/variability envelope |
| Effect absolute relative bytes/operation delta | median `0.00082702` (`0.08270%`) | Normalized allocation stability |
| Effect sampled-bytes/second multiplier | median `82.53354x` | Descriptive only |

The workload manipulation is robustly above the strict `>1.25x` gate, and every
non-outlier effect pair remains inside the workload-specific bytes/operation A/A
envelope. Sampled bytes/second rises because more operations complete; it is
not the normalized allocation claim when the operation denominator exists.

Artifacts:

Portable fingerprints canonicalize text to UTF-8 without BOM and CRLF line
endings, matching the existing allocation directional artifact convention.

- `allocation-directional-throughput-calibration-v1.windows-x64.json`
  - bytes: `53,577`
  - SHA-256:
    `948f2100b8551dc921662d08f9c3af709232abc22e7062d23fc12d6724b65e18`
- `allocation-directional-throughput-calibration-v1.windows-x64.report.json`
  - bytes: `2,062`
  - SHA-256:
    `9c62676f514bc70f0f0405f0186c39f515f59d336353e80707a1af9826c1b4bc`

## Predeclared pair-summary method

Every directional quantity is calculated within each compatible
baseline/current pair before aggregation. At least three valid independent
pairs are required.

The repeated-pair summary reports:

- the median pair delta;
- raw median absolute deviation (MAD);
- every original pair;
- candidate outlier indexes; and
- the retained non-outlier values used for sign and boundary checks.

[NIST defines MAD](https://www.itl.nist.gov/div898/software/dataplot/refman2/auxillar/mad.htm)
as `median(abs(x - median(x)))` and describes it as a robust alternative to
standard deviation. Candidate outliers use the Iglewicz-Hoaglin modified
z-score `0.6745 * (x - median) / MAD` with strict `abs(z) > 3.5`; when MAD is
zero, only values unequal to the median are flagged. Outliers remain retained
and reported. A gate still requires at least two non-outlier pairs.

This is not a hidden strict-all or majority rule:

- the median/MAD summary is the decision statistic;
- non-outlier signs must agree with the intended direction;
- a flagged outlier does not silently reverse the robust center;
- fewer than three valid pairs or fewer than two non-outlier pairs abstains.

## Throughput selector

The throughput gate is assessable only when it has three valid A/A pairs, three
valid effect pairs, A/A relative variability no greater than `0.25`, and at
least two non-outlier effect pairs.

It passes only when:

1. the median operations/second multiplier is strictly greater than `1.25`;
2. every non-outlier operations/second multiplier is strictly greater than
   `1.25`;
3. the median absolute relative estimated sampled bytes/operation delta is
   inside the workload-specific A/A envelope; and
4. every non-outlier bytes/operation delta is inside that envelope.

If the operations/second manipulation is not established, the selector
abstains before interpreting allocation stability. Estimated sampled
bytes/second is retained only as a descriptive quantity.

## Warmup selector

The warmup gate separately requires three valid A/A, early, and converged pairs.
It uses signed early bytes/operation deltas and absolute converged
bytes/operation deltas.

The practical-effect floor is a predeclared relative `0.10` (`10%`), separate
from observed A/A variability. The rationale is deliberately external to the
frozen observations: BenchmarkDotNet's
[`StatisticalTestColumn.CreateDefault`](https://github.com/dotnet/BenchmarkDotNet/blob/master/src/BenchmarkDotNet/Columns/StatisticalTestColumn.cs)
uses a relative `10%` threshold for its default equivalence test. This gate
borrows only that practical-significance scale; it does not import
BenchmarkDotNet's Mann-Whitney test or claim statistical significance.

The early phase passes only when:

1. its median signed relative delta is strictly greater than
   `max(A/A envelope, 10%)`; and
2. every non-outlier early delta is positive.

The converged phase passes only when:

1. its median absolute relative delta is inside the A/A envelope; and
2. every non-outlier converged delta is inside that envelope.

An exact `10%` early median does not pass. An exact A/A boundary does pass.
Excessive A/A, non-outlier sign disagreement, missing phase pairs, fewer than
two inliers, or failure to converge produces abstention. Flagged outliers stay
visible but do not replace the robust pair summary with a hidden all-pairs or
majority decision.

## Selector audit

The audit compares the frozen v2 selector with the pair-first method declared
for future directional work. Frozen code and evidence remain unchanged; the
new selector is a separate pre-freeze design surface.

| Selector | Demonstrated mismatch | Calibration-gate correction |
|---|---|---|
| True allocation increase | Frozen selector required every effect pair beyond A/A despite the declared repeated-pair summary | Signed pair deltas use median/MAD, explicit outliers, and non-outlier positive-sign agreement |
| Lower allocation | Frozen selector required every effect pair below negative A/A | Signed pair deltas use median/MAD, explicit outliers, and non-outlier negative-sign agreement |
| Throughput with stable allocation/operation | Protocol prose discussed rising sampled bytes/second while the selector actually tested operations/second plus bytes/operation | Operations/second proves manipulation; bytes/operation tests normalized stability; bytes/second is descriptive |
| Warmup/transient convergence | Frozen selector used strict `All` for early and converged pairs and had no practical-effect floor | Pair-first median/MAD summary, source-backed 10% early floor, sign rule, convergence rule, outlier retention, and insufficient-pair abstention |
| Stable-total mix shift | Absolute-plus-share and independent type/site requirements are scenario invariants, not a demonstrated summary mismatch | No change |
| Changed workload, partial attribution, missing/zero weight, healthy A/A | These are compatibility, availability, or variability gates rather than directional effect summaries | No change |
| Fixed-stride reorder | Conservative unsafe-if-any absolute/share movement is the declared sampling-bias safety control | No change |

Deterministic tests cover throughput manipulation failure, stable
bytes/operation boundaries, warmup early and converged boundaries, maximum A/A,
the practical floor, non-outlier sign disagreement, retained outliers, signed
increase/decrease summaries, and insufficient pairs.

## Preservation

The frozen #1379 protocol, execution fixture, final fixture, reports, ledger,
fingerprints, observations, and `INCONCLUSIVE` decision were not modified,
rerun, replaced, or reevaluated. This gate authorizes only a future,
separately reviewed freeze after the allocation error-classification dependency
is also merged.
