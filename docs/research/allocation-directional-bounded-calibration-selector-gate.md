# Allocation directional bounded calibration and selector gate

Issue: [#1387](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1387)

Parents:

- [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)
- [#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

Predecessor evidence:

- [#1385](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1385)
- [`allocation-directional-baseline-current-feasibility-v3.md`](./allocation-directional-baseline-current-feasibility-v3.md)

## Decision

**PASS for the bounded calibration/selector gate only.**

This is not a new allocation directional-feasibility protocol, does not alter
the frozen #1385 result, and authorizes no Core, CLI, MCP, VS Code, collector,
schema, tool, or product-contract change. No new final feasibility inventory
was run.

The selected calibration geometry is:

- one-second capture;
- 200 ms arm delay;
- 700 ms burst-paced workload;
- one-millisecond pause after every eight operations;
- three independently started pairs per plan entry; and
- retained four-second unpaced cap probes from calibration v2.

All 48 required operating sides passed once without retry or replacement.
The maximum observed allocation-event count was `938`, or approximately
`0.94%` of `MaxAllocationEvents=100,000` and `1.25%` of the separately
declared operating limit of `75,000`. The capture and workload windows are
respectively `25%` and `46.67%` of the retained unpaced probe geometry.

`MaxAllocationFrames` remains exactly `16,384`. It was not increased,
weakened, reinterpreted, or made conditional. Successful MCP allocation
responses expose allocation-event counts but not exact aggregate retained
frame or module occupancy, so those two quantities remain explicitly
unavailable rather than inferred.

## Preservation

The #1385 protocol document, execution fixture, ledger, reports, fingerprints,
observations, and `INCONCLUSIVE` decision remain byte-identical. They were not
rerun, replaced, deleted, reclassified, or reinterpreted. Shared research
harness and sample source was extended additively; the predecessor overloads
and default capture and pacing behavior remain unchanged.

Each calibration attempt used a new version and fresh workload, pair, attempt,
capture, and process identities. A failed or negative attempt was retained and
never rerun at the same path:

| Version | Geometry or event | Result |
|---|---|---|
| v1 | 1 s capture, 350 ms workload, 2 ms pacing | Bounded; max `64` events; low A/A retained `12.46%`, above the unchanged 10% sanity floor |
| v2 | 1 s capture, 600 ms workload, 1 ms pacing | Bounded; max `106` events; low A/A retained `13.70%`, above the unchanged 10% sanity floor; three probes retained exact `allocation.capture.limit` provenance |
| v3 | Planned burst pacing | Aborted during predecessor JSON setup before any required target started; only the start marker exists |
| v4 | 1 s capture, 700 ms workload, pause every four operations | Bounded; max `470` events; increased-shape A/A envelope `12.51%`, above the unchanged 10% sanity floor |
| v5 | 2 s capture, 1.2 s workload, pause every four operations | Selectors stabilized, but the gate correctly rejected the wider `50%` capture and `80%` workload fractions |
| v6 | 1 s capture, 700 ms workload, pause every eight operations | **Passed**; max `938` events; increase A/A `2.62%`, lower A/A `5.62%`; both directional effects established |

The v6 fixture reuses the retained v2 probes by portable SHA-256
`ae3b25b64da3e57316b129363e6ede44db4815457a4f260ac671c10bb25933ae`.
The probes were not started again. All retained calibration artifacts,
including the v3 setup-abort marker and negative v1/v2/v4/v5 reports, have
exact portable fingerprint guards.

## Quantities and selector semantics

The selector separates observed A/A variability from practical equivalence.
It does not tune thresholds to the retained results.

### A/A variability

Every decision is pair-first. Relative estimated-sampled-bytes/operation
deltas are summarized with median and raw MAD. Modified-z candidates remain
reported as outliers; they do not disappear from the fixture or pair
denominator. A selector requires at least three valid pairs and at least two
retained non-outlier pairs. Insufficient evidence abstains.

Healthy A/A is a separate safety gate. Every retained non-outlier absolute
relative bytes/operation delta must be at or below the predeclared `10%`
sanity bound. The exact boundary passes.

### Practical equivalence

The practical relative margin for normalized totals and throughput is:

`max(workload-specific robust A/A envelope, 10%)`.

The external rationale remains the relative `10%` default used by
BenchmarkDotNet's
[`StatisticalTestColumn.CreateDefault`](https://github.com/dotnet/BenchmarkDotNet/blob/master/src/BenchmarkDotNet/Columns/StatisticalTestColumn.cs).
Only that practical scale is borrowed. This gate does not import
BenchmarkDotNet's statistical test and makes no statistical-significance
claim.

- **Stable normalized total:** every retained non-outlier absolute relative
  estimated-sampled-bytes/operation delta may equal, but not exceed, the
  practical margin.
- **Throughput:** operations/second independently establishes the workload
  manipulation; estimated sampled bytes/operation independently establishes
  practical equivalence. Estimated sampled bytes/second is descriptive only.
- **Warmup:** early movement must be strictly greater than
  `max(A/A envelope, 10%)` with sign agreement. Converged
  bytes/operation may equal, but not exceed, the practical margin.
- **Directional increase/lower:** the median signed bytes/operation movement
  must be strictly beyond `max(A/A envelope, 10%)`, and all retained
  non-outlier signs must agree with the intended direction.

### Mix units

Stable total, type movement, site movement, and shares remain separate:

- total equivalence is measured in relative normalized
  estimated-sampled-bytes/operation;
- type and site absolute movement are independently normalized by the
  baseline total bytes/operation, then compared with the practical relative
  floor;
- type and site share movement remain fractions of their own independent
  inventories and are compared with workload-specific A/A share envelopes;
  the relative 10% bytes/operation floor is not incorrectly applied to
  percentage-point movement.

A broad total-equivalence margin cannot establish a mix shift by itself.
Independent type and site inventories must each show both material absolute
movement and share movement.

## Selector audit

| Surface | Required behavior |
|---|---|
| Boundary | Exact practical-equivalence and healthy-A/A boundaries pass; directional movement must be strict |
| Outlier | One modified-z candidate remains visible while median/MAD and at least two inliers drive the decision |
| Insufficient evidence | Fewer than three valid pairs or two inliers abstains |
| Sign | Every retained non-outlier directional effect agrees with the expected sign |
| Throughput | Workload rate and normalized bytes/operation are independent gates |
| Warmup | Early practical movement and practical convergence are independent gates |
| Mix | Stable total cannot replace independent type/site absolute-plus-share evidence |
| Cap provenance | Probe failures require MCP-response `ResourceLimitExceeded` plus `allocation.capture.limit` |
| Operating cap | `75,000` allocation events passes; `75,001` fails |
| Required side | Any required-side cap failure fails the bounded gate |
| Geometry | Capture fraction above `25%` or workload fraction above `50%` fails |
| Sources | Fixed-stride periodic/reorder shapes and independently available type/site inventories are required |

## Retained artifact fingerprints

Portable hashes use UTF-8 without BOM and canonical CRLF line endings. File
content still preserves timestamps and platform identity.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| v1 fixture | 202,777 | `0ade798aab5dc1450a6869c63a4034df7bf568ad8d0de73ce8f35b158e9ceee3` |
| v1 report | 2,808 | `846fdac112edb916e5ed0ef0f2802d9d8124798f3bdeaa57bd243c313d6e016a` |
| v1 start marker | 167 | `cdeb16d63378b32abdeb6d86e59d62e3a2c0162b4af5dc7f0a6ef6afa4736023` |
| v2 fixture | 205,083 | `ae3b25b64da3e57316b129363e6ede44db4815457a4f260ac671c10bb25933ae` |
| v2 report | 2,798 | `991ccef0dc4784fcc6512486082d8391ad25c45f4f367ffe30851863bb5b3877` |
| v2 start marker | 167 | `f032d0333b02925d7c8acb41bdf0d8c7ef2ef7d8f4385d1f324a56d259779a88` |
| v3 start marker | 167 | `1bf71b2e2c62ae05dbfd2002e835adebbf65b82c88278e54fa5ddad7cf588885` |
| v4 fixture | 212,813 | `215eadc0e432fa318b5507961816b318947dabc0d0f88a2ba61ddee3e6bf972e` |
| v4 report | 2,856 | `7da0bd28bd58ad9e713f9fd3825ad20832f8c85c5c12c7f1236fc3094b508471` |
| v4 start marker | 167 | `456ee60c3ca1e0bf5cff3a083bbd80de72c2423bacf6b596d212f459dc18b934` |
| v5 fixture | 215,620 | `fdb29838d79227c1011a55ffa0f71f14ebd42f8ecc810d683dfc6d0ec66706cb` |
| v5 report | 2,777 | `c7420315db8f718564bfc7778d7ce7e1e9054c866d6e3026146da241cae43c04` |
| v5 start marker | 167 | `bc4102753e571480f3b03287438020eb0a82e29b7e3ec86cc00012181c1e0a0c` |
| v6 fixture | 213,754 | `aee5adc17052d1f6d4733f8a8bd4fe67fe9c5b4eccd1b57643a2ed5ce62f669e` |
| v6 report | 2,734 | `53570bc294843e33a9ba209a3d8ea3bd5fdf5df9c0725ae1d435c44cd8184178` |
| v6 start marker | 167 | `29bda9807d639dfa7fcb15040ac79b2d356481875bbfbe44dadeb97a10a512a2` |

## Conclusion

The bounded low/periodic operating geometry and the audited selector semantics
are calibrated for a future separately frozen protocol. This gate does not
convert #1385 into a positive result, does not run that future protocol, and
does not authorize product changes.
