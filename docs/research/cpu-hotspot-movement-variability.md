# CPU hotspot movement against A/A capture variability

Issue: [#1313](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1313)<br>
Roadmap: [#1305](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1305)<br>
Foundation: [#1308](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1308),
[#1311](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1311)<br>
Verdict: **GO for a separate minimal Core/CLI objective evidence issue**

## Question and boundary

Can repeated capture-level statistics distinguish a deterministic sampled hotspot-share
movement from ordinary A/A capture variability?

The study evaluates retained sample counts and shares. It does not treat individual
samples as independent, convert samples into elapsed CPU time, normalize by throughput,
or emit an improvement, regression, performance-impact, or causal verdict.

## Protocol

- One complete non-overlapping CPU capture is the independence unit. Four complete
  baseline/candidate capture pairs are required.
- Pair order alternates baseline-first and candidate-first.
- The live fixture uses the existing EventPipe SampleProfiler backend and a bounded
  `BadCodeSample.CpuHotspotWorkload` route. Eight dedicated workers produce either a
  7:1 alpha/beta mix or a 1:7 mix.
- The workload starts 400 ms before collection and stays active for 4.5 seconds around
  the complete 3-second capture. This removes variable idle/request-startup fractions
  from the retained denominator.
- The selected method metric is inclusive stack membership divided by the capture's
  retained total sample population. The existing CPU hotspot summary is also
  inclusive-first. Exclusive leaves remain the basis for unresolved-symbol share.
- Every capture retains absolute total samples, unresolved exclusive samples, both
  workload-method inclusive sample counts, backend/evidence kind, capture settings,
  offered load, process-run identity, pair identity and order.
- Capture-level distributions report median, R7 quartiles, IQR, minimum and maximum.
- Paired movement is the median candidate-minus-baseline capture-share delta through
  the #1311 `paired-delta` method. Samples inside a capture never become inferential
  observations.
- Top-2 overlap is the fraction of baseline top-2 method identities retained in the
  candidate top-2. Rank stability separately reports the fraction of pairs retaining
  the selected method's rank and the median absolute rank delta.
- The scenario routing boundary is an absolute median paired share movement of at
  least 7 percentage points. Overall GO additionally requires the deterministic shift
  to exceed the maximum absolute A/A paired delta by at least 5 points, stable selected
  rank in every A/A pair, and a median selected-rank movement of at least one in the
  shift case. These are fixture-study boundaries, not product-wide thresholds.
- Bounds are fixed at 8 cases, 12 captures per arm, 16 retained methods per capture,
  top-K 2, and the stricter #1311 global statistical-evidence limits.

The deterministic replay fixture is
`tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/CpuHotspotMovement/cpu-hotspot-movement.v1.json`.

## Compatibility and abstention

The workload variant is the declared comparison axis and may differ. Pooling requires
matching runtime flavor/version policy, OS, architecture, process run within each pair,
backend, evidence kind, duration, sampling period, workload scenario/version and
offered load.

| Condition | Availability | Semantics |
|---|---|---|
| Compatible exact captures, at least 4 pairs and at least 80 samples per capture | `available` | Capture-level medians, variability, paired deltas and rank stability may be reported |
| Offered-load mismatch | `incompatible` | Preserve each capture, but do not pool or attribute share movement |
| Backend or evidence-kind mismatch | `incompatible` | EventPipe stack frequency and OS on-CPU observations are not interchangeable |
| Any capture above 35% unresolved exclusive share, or a paired unresolved-share change above 20 points | `unsupported_approximation` | Preserve counts and unresolved share; method movement/rank is unavailable |
| Non-exact retained representation | `unsupported_approximation` | Approximate method inventories are not admitted by this protocol |
| Fewer than 4 complete pairs, fewer than 80 samples in any capture, or selected method missing from a pair | `insufficient_observations` | Preserve capture evidence; no pooled movement action |

Missing or unavailable values never mean zero or unchanged.

## Deterministic matrix

| Case | Evidence | Result |
|---|---|---|
| Repeated A/A | Four 60%-class alpha-share pairs with bounded capture variation | Available; no movement established; rank stable |
| Deterministic shift | Alpha share moves from approximately 55% to 17% | Available; movement observed; alpha/beta rank swaps |
| Changed offered load | Same nominal shares, offered load 4 versus 8 | Incompatible; no normalization or pooling |
| Unresolved-symbol degradation | Candidate unresolved exclusive share is 50% | Unsupported approximation; no method attribution |
| Backend incompatibility | EventPipe stack frequency versus Linux perf on-CPU semantics | Incompatible |
| Low sample count | 30 samples per capture | Insufficient observations |

The complete matrix is required for GO. Removing an abstention case produces NO-GO.

## Bounded live evidence

Captured locally on Windows on 2026-10-31. Three consecutive complete live-test rounds
passed without retrying a failed round. Across the 48 real EventPipe captures, absolute
retained populations ranged from 4,212 to 27,892 samples. Percentages below are shares
of each capture's retained sample population.

| Round | A/A baseline/candidate median | A/A IQRs | Maximum absolute A/A paired delta | Shift baseline/candidate median | Shift IQRs | Median shift delta | Separation beyond A/A |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 40.51% / 38.89% | 2.08 / 0.87 points | 2.29 points | 40.03% / 5.72% | 2.80 / 0.40 points | -34.31 points | 32.03 points |
| 2 | 41.18% / 40.75% | 1.08 / 1.73 points | 0.86 points | 41.18% / 5.88% | 1.08 / 0.15 points | -35.29 points | 34.43 points |
| 3 | 40.03% / 39.50% | 2.80 / 2.00 points | 1.06 points | 38.89% / 5.79% | 0.68 / 0.30 points | -33.10 points | 32.04 points |

A/A baseline and candidate capture ranges both stayed within 36.84%-41.18%.
Shift baselines stayed within 36.84%-41.18%, while shift candidates stayed within
5.26%-5.88%.

A/A retained alpha at rank 1 in every pair. Every shifted pair moved alpha by one rank.
Top-2 membership remained 100% in both cases, demonstrating that membership alone
missed a stable rank/share movement. Unresolved exclusive share was zero at the median
in every real A/A and shift round.

The live reports also project bounded control cases from the captured evidence:
changed offered load (8 versus 16 workers) is incompatible, 50% unresolved share
abstains, a backend/evidence-kind change is incompatible, and 30-sample captures are
insufficient.

An earlier short-overlap protocol started sampling before a 1.5-second workload inside
a 2.5-second capture. Independent review reproduced A/A paired deltas as large as the
engineered shift because variable idle/startup fractions dominated the capture
denominator. That protocol was rejected rather than weakening the separation gate.

## Indicator decision

| Indicator | Decision |
|---|---|
| Absolute retained samples per capture | GO as required context and sufficiency evidence; never CPU time or completed work |
| Unresolved exclusive share | GO as a mandatory attribution-quality/abstention gate |
| Median selected-hotspot inclusive share | GO as capture-level descriptive magnitude |
| Capture-level IQR and range | GO for A/A variability characterization |
| Paired capture-share delta | GO as the action-changing movement indicator under exact compatibility |
| Top-K membership | NO-GO alone; top-2 membership stayed unchanged during the deterministic shift |
| Selected-method rank stability | GO as corroborating evidence; rank was stable in A/A and swapped in every shifted pair |
| Cross-backend comparison | NO-GO; exact backend and evidence-kind compatibility is required |
| Throughput/offered-load normalization | NO-GO; changed offered load is incompatible until separate evidence justifies normalization |

## Product follow-up boundary

A follow-up may define a minimal Core-owned capture-level CPU hotspot movement record
and CLI human/JSON rendering. It should expose objective counts, unresolved share,
capture-level variability, paired share deltas and rank stability with the abstention
states above.

The follow-up must not ship the scenario-local DTOs or thresholds as-is, infer CPU time,
normalize changed offered load, compare incompatible backends, emit a performance
verdict, add an MCP tool, or make the VS Code extension collect EventPipe data. MCP
must reuse existing CPU sample/query contracts and the extension must continue to
consume CLI JSONL.

## Reproduction

```powershell
dotnet build tests\DotnetDiagnostics.ScenarioEvaluation.Tests\DotnetDiagnostics.ScenarioEvaluation.Tests.csproj -c Release

dotnet test tests\DotnetDiagnostics.ScenarioEvaluation.Tests\DotnetDiagnostics.ScenarioEvaluation.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~CpuHotspotMovementTests"

$env:DOTNET_DIAGNOSTICS_CPU_HOTSPOT_REPORT_PATH = "cpu-hotspot-live-report.json"
dotnet test tests\DotnetDiagnostics.ScenarioEvaluation.Tests\DotnetDiagnostics.ScenarioEvaluation.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~CpuHotspotMovementLiveTests"
```
