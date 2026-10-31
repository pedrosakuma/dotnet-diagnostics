# Retained growth versus allocation churn feasibility

Issue [#924](https://github.com/pedrosakuma/dotnet-diagnostics/issues/924) is the first
vertical scenario under [#1305](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1305).
It applies the approved statistical vocabulary from
[#1308](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1308), the bounded
repeated-observation harness from
[#1311](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1311), and the gcdump
fidelity limits established by
[#927](https://github.com/pedrosakuma/dotnet-diagnostics/issues/927) and
[#928](https://github.com/pedrosakuma/dotnet-diagnostics/issues/928).

## Verdict: GO for a separate minimal signal-contract follow-up

Existing objective evidence distinguished bounded positive retained growth from comparable
allocation churn in the known-shape scenario. The retained arm produced compatible positive
type-byte and instance deltas across three independent process runs, the paired byte change
persisted after another explicit full-GC stabilization and gcdump observation, and one growing
type dominated the observed common-type positive byte growth. The churn and stable controls
produced zero target-type byte/instance deltas and therefore left positive retained-growth
evidence **not established**.

This is not a memory-leak verdict. It does not establish a defect, cause, future trajectory,
or absence of retention in a control. The workload intentionally retains objects. A GO only
supports creating a separate, minimal Core/CLI objective-signal contract follow-up; MCP would
reuse the existing tool surface and VS Code would continue to consume CLI JSONL. This issue does
not implement that product contract.

## Bounded protocol

- One CoreCLR process per repetition; three repetitions per retained-growth, allocation-churn,
  and stable-heap arm.
- Three gcdump captures per repetition: stabilized baseline, stabilized post-workload
  candidate, and a second stabilized persistence observation.
- Seed shape: 4,000 retained `GcDumpPressureNode` objects with 512-byte payloads.
- Retained and churn workloads each allocate 20,000 additional nodes with 512-byte payloads in
  ten bounded batches. The retained arm keeps them; the churn arm releases each batch.
- Allocation traffic is captured separately for two seconds with
  `EventPipeAllocationSampler`; it is never substituted for retained heap.
- Every gcdump retains at most 4,096 type rows and has a 30-second timeout. The evaluator accepts
  at most eight process runs and uses 512 deterministic bootstrap resamples.
- Workload truth (requested counts, retained count, payload bytes, and intentional-retention
  flag) is serialized beside, but never passed into, the objective evaluator.
- The degraded replay keeps workload truth positive while candidate and persistence captures are
  incomplete/truncated; the evaluator abstains.

## Objective calculations

| Question | Method | Independence unit | Scenario rule |
|---|---|---|---|
| Compatible target-type byte and instance change | `paired-delta` v1 | process run | Baseline and candidate rows must both be observed positive evidence with compatible capture identity. Missing rows are not converted to zero. |
| Repeated paired retained-byte change | `window-median` v1 | process run | One derived byte delta per complete process-run pair. |
| Robust spread | raw `median-absolute-deviation` v1 | process run | Available only with at least three complete scenario runs, stricter than the registry's mathematical minimum of two. |
| Uncertainty interval | `percentile-bootstrap-interval` v1 over `window-median` | process run | Available only with at least three complete runs; 95% interval, 512 deterministic resamples. |
| Persistence | `wilson-score-rate` v1 | process run | A positive target-type byte/instance increase remains at least 95% present after another explicit stabilization and capture. |
| Post-GC stabilization | `wilson-score-rate` v1 | process run | Candidate and persistence observations must follow explicit blocking compacting collections. |
| Growing-type concentration | `window-median` v1 | process run | Largest positive byte delta divided by summed positive deltas only among type rows observed in both captures. It is not an exhaustive heap share. |
| Allocation traffic | `window-median` v1 | process run | Approximate GCAllocationTick bytes remain a separate quantity and do not contribute to retained-growth support. |

The scenario-local positive-evidence posture requires available positive byte and instance
deltas, a positive repeated byte-change median, persistence and stabilization rates of 1, and
an available largest-growing-type share of at least 0.5. Exact controls that do not meet these
positive conditions return `not_established`; incompatible, lossy, projected, truncated, or
incomplete required evidence returns `inconclusive`.

## Deterministic replay matrix

Four compact fixtures under
`tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/RetainedGrowth/` cover:

| Fixture | Target byte delta | Target instance delta | Allocation traffic | Positive retained-growth evidence |
|---|---:|---:|---|---|
| retained growth | +640,000 bytes | +20,000 | present | supported |
| allocation churn | 0 | 0 | comparable to retained arm | not established |
| stable heap | 0 | 0 | not applicable | not established |
| degraded/incomplete | unavailable | unavailable | truncated/loss-marked | inconclusive |

The replay tests also require process-run independence, registered method identities,
deterministic bootstrap provenance, explicit root/path availability, induced-GC limitations,
and serialization with no verdict field, `leak_suspected`, `no_leak`, or `healthy` conclusion.

## Bounded live evidence

Executed 2026-10-30 from base commit
`2332bc8b4b24d5e3f0037afb06041d9e1396df4f` on Microsoft Windows
`10.0.26300.0` AMD64 with SDK `10.0.403` selected under the repository's
`10.0.201`/`latestFeature` policy and Microsoft.NETCore.App `10.0.14`.

The one live matrix test completed in 35.3 seconds.

| Observation | Retained growth | Allocation churn | Stable heap |
|---|---:|---:|---:|
| Target-type median byte delta | +640,000 | 0 | 0 |
| Target-type median instance delta | +20,000 | 0 | 0 |
| Median paired byte change | +640,000 | 0 | 0 |
| Raw MAD of paired byte change | 0 | 0 | 0 |
| 95% deterministic bootstrap interval | [640,000, 640,000] | [0, 0] | [0, 0] |
| Persistence rate | 1.0 | 0.0 | 0.0 |
| Largest common growing-type share | 0.9305 (`System.Byte[]`) | 0.8219 (incidental common-type growth) | 0.8333 (incidental common-type growth) |
| Median sampled allocation bytes | 11,724,552 | 11,495,728 | not collected |
| Capture-induced Gen2 collections | 9 | 9 | 9 |
| Positive retained-growth evidence | supported | not established | not established |

The retained/churn allocation-traffic ratio was 1.02, while only the retained arm changed the
post-GC target-type inventory. This is the useful distinction: high allocation traffic can
coexist with stable observed retained type rows, and allocation traffic alone never establishes
retained growth.

## Exact limitations

1. Gcdump provides observed per-type totals, not an object-edge/root graph. Root/path evidence is
   `unavailable_by_mechanism` for every live capture. The result identifies growing types, not the
   retaining owner or causal field.
2. EventPipe loss remains unobservable for gcdump. Clean GC-stop and stream-completion signals,
   nonempty output, and no detected failure do not prove graph completeness or universal
   losslessness.
3. Every gcdump induced a blocking Gen2 collection. The protocol separately forced compacting
   full collections before candidate and persistence captures. These observer effects change the
   heap and may remove transient objects; they are useful for this post-GC retained-growth
   question but not neutral observation.
4. `GCAllocationTick` bytes are sampled estimates, not actual object counts or exact allocation
   totals. They are sufficient here only to show comparable traffic under identical workload
   shape.
5. A zero compatible target-type delta in the controls means positive retained growth was not
   established for this known type. Because gcdump does not support exhaustive absence, it is not
   proof that the whole process retained nothing.
6. The live evidence is one Windows/CoreCLR environment with three process runs per arm. The
   deterministic fixtures establish calculation behavior, not cross-runtime or cross-OS
   calibration.
7. The workload is intentionally known-shape and bounded. The observed threshold and 95%
   persistence tolerance are scenario rules, not universal production defaults.
8. Growing-type concentration is calculated over all type rows observed in both captures, not
   only the known workload type. Runtime bookkeeping produced high concentration values even in
   the churn and stable controls. The metric is descriptive supporting evidence only and must
   remain gated by a compatible positive target-type byte/instance change; by itself it cannot
   identify workload-specific retention.

## Follow-up boundary

A separate issue may define the smallest Core-owned objective retained-growth signal and CLI
human/JSON rendering needed by a concrete troubleshooting decision. It must preserve compatibility
identity, paired process-run counts, unavailable/degraded states, allocation traffic separation,
root/path availability, observer-effect limitations, and the no-leak-verdict posture. No new MCP
tool is justified.
