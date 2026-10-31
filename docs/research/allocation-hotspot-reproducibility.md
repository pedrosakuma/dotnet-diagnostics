# Allocation hotspot reproducibility

Issue: [#1307](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1307)

Evidence-state contract: [#1304](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1304)

Statistical contract: [#1308](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1308)

Repeated-observation harness: [#1311](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1311)

## Verdict

**GO** for a separate minimal Core/CLI evidence-contract issue.

Repeated compatible captures supported a focused allocation investigation when the
same independently ranked allocated types and allocation stack sites remained near
the top. A controlled workload-mix change replaced both top-ranked keys and reduced
cross-arm top-K membership, while the repeated A/A arm preserved the same top keys
and rank order.

This evidence can prioritize a type inventory and a separate stack-site inventory.
It does not correlate a type to a stack, estimate exact allocated bytes or object
counts, establish retained memory, or produce a performance verdict.

## Protocol

- Platform: Windows 11, .NET 10.0.14, x64.
- Independent unit: one separately started three-second EventPipe allocation capture
  against one fresh `BadCodeSample` process.
- Repetitions: four captures each for the primary stable shape, an independent primary
  A/A repetition set, and a changed allocation mix; no retry-until-green behavior.
- Timing: the collector arms for 900 ms before a bounded 1.5-second allocation
  workload starts.
- Primary mix: two `System.Byte[]` allocations and one `System.Char[]` allocation per
  workload iteration.
- Changed mix: two `System.Int32[]` allocations and one `System.Char[]` allocation per
  workload iteration.
- Capture-level quantities: median and raw median absolute deviation (MAD) for sampled
  `GCAllocationTick` estimated bytes and tick counts, plus paired candidate-minus-
  baseline capture deltas.
- Ranked evidence: allocated types and allocation stack sites are independently
  allowlisted, bounded, and evaluated at top-K with `K=3`. Membership uses Jaccard;
  rank order uses Spearman after re-ranking only the shared top-K keys.
- Attribution: attributed and unattributed byte/tick shares are calculated from raw
  capture totals and retained stack-attributed totals.
- Bounds: at most eight captures per scenario, 16 retained type rows, 16 retained
  stack rows, and a 512 KiB replay fixture.

The harness does not compute a confidence interval over weighted allocation ticks.
Allocation events and frames inside one capture are correlated source evidence and do
not increase the independent observation count.

## Live results

Estimated bytes are runtime sampling weights, not exact allocation totals.

| Arm | Capture ticks | Tick median; raw MAD | Estimated bytes | Byte median; raw MAD | Median attributed tick / byte share |
|---|---|---:|---|---:|---:|
| stable primary | 4,782–7,483 | 5,601.5; 811.5 (14.49%) | 915,855,464–1,433,582,520 | 1,073,005,232; 155,568,884 (14.50%) | 100% / 100% |
| repeated A/A | 3,074–6,273 | 5,917; 273 (4.61%) | 588,627,616–1,201,651,936 | 1,133,458,448; 52,337,740 (4.62%) | 100% / 100% |
| changed mix | 5,079–6,505 | 5,652; 340.5 (6.02%) | 972,876,896–1,246,106,856 | 1,082,634,256; 65,225,988 (6.02%) | 100% / 100% |

One repeated-A/A capture was partial: 6,272 of 6,273 ticks and 1,201,542,744
of 1,201,651,936 estimated bytes had usable stack attribution. The arm-level state
therefore remains `partial` even though its median attributed shares round to 100%.

### Within-arm top-K stability

| Arm | Type Jaccard median / minimum | Type Spearman median / minimum | Stack Jaccard median / minimum | Stack Spearman median / minimum | Stable top key |
|---|---:|---:|---:|---:|---|
| stable primary | 1.000 / 1.000 | 1.000 / 1.000 | 1.000 / 1.000 | 1.000 / 1.000 | `System.Byte[]`; `AllocatePrimaryBytes` |
| repeated A/A | 0.833 / 0.667 | 1.000 / 1.000 | 0.833 / 0.667 | 1.000 / 1.000 | `System.Byte[]`; `AllocatePrimaryBytes` |
| changed mix | 0.833 / 0.667 | 1.000 / 1.000 | 1.000 / 1.000 | 1.000 / 1.000 | `System.Int32[]`; `AllocateChangedIntegers` |

Each row summarizes six pairwise comparisons across four independent captures.
Occasional third-ranked noise reduced membership without changing the dominant key or
the order of shared keys.

### A/A and changed-mix contrasts

| Contrast | Median estimated-byte delta | Median tick delta | Type Jaccard median / minimum | Stack Jaccard median / minimum | Result |
|---|---:|---:|---:|---:|---|
| stable vs repeated A/A | +60,453,216 | +315.5 | 1.000 / 0.667 | 1.000 / 0.667 | same top type and stack; shared rank correlation 1.000 |
| stable vs changed mix | -54,016,080 | -282 | 0.333 / 0.333 | 0.333 / 0.333 | both top keys changed |

The four A/A estimated-byte deltas ranged from -844,954,904 to +285,796,472,
and its tick deltas ranged from -4,409 to +1,491. The changed-mix totals also
overlapped the primary totals. Totals therefore remain descriptive and do not gate
the GO. The changed mix is distinguished by independent ranked inventories: the
type comparison had one two-key overlap with Spearman -1.000, while stack
comparisons shared only the common helper and therefore had no defined rank
correlation.

## Evidence-state matrix

The deterministic fixture and live-derived controlled projections keep absence,
weight, and retained-handle semantics separate.

| State | Bounded evidence | Interpretation |
|---|---|---|
| `raw_empty` | zero raw ticks and bytes after a completed capture | no allocation ticks were observed in this window; not proof of zero allocation |
| `no_stack` | raw ticks and type ranks retained; zero usable stack ticks | stack attribution unavailable; raw evidence remains usable |
| `zero_weight` | usable stack ticks retained; zero attributed byte weight | tick evidence exists, but byte-weighted stack ranking is unavailable |
| `partial` | low-sample projection retains 1 of 2 ticks and 50% of byte weight; one live A/A capture also lost one stack attribution | report attributed and unattributed shares; do not promote one capture to stable evidence |
| `complete` | raw and attributed totals reconcile | ranked type and stack evidence can be evaluated independently |
| `unavailable` | invalid or expired retained handle with raw totals preserved | derived stack coverage and shares are unavailable, not zero |

The low-sample case has one capture, two raw ticks, no MAD, and no top-K
comparison. It remains insufficient for a reproducibility claim.

## Decision gates

The validation-only protocol selected these predeclared gates:

- focused stable evidence requires at least four captures, only `partial` or
  `complete` attribution, top-K median Jaccard at least 2/3, minimum Jaccard at
  least 0.5, and a defined median shared-key Spearman of at least 0.5 for both
  independently ranked inventories;
- repeated A/A must satisfy those stability gates without changing the modal top
  type or stack;
- the controlled changed mix must change both top keys and produce median
  cross-arm Jaccard below 2/3 for both inventories;
- low-sample and unavailable attribution states must select their explicit
  non-promoting assessments;
- estimated-byte and tick medians, raw MAD, and paired deltas remain descriptive
  and do not become generic product thresholds.

All scenario gates selected their expected assessment. The GO approves a separate
minimal product-contract investigation, not these scenario DTOs, workload routes, or
validation thresholds as shipping APIs.

## Product contract cost measurement

The additive product contract reuses `query_snapshot`, its existing
`comparisonHandles` parameter, and the existing CLI JSONL `query` frame. It adds no
MCP tool and no shared statistical record to generated output schemas. Updated
parameter descriptions increase both shipping MCP catalogs by 132 bytes: the maximal
17-tool catalog is 283,977 bytes and the default 13-tool catalog is 250,809 bytes.

The deterministic maximum result fixture uses all 8 captures and top-K 16 for both
independent inventories. Its serialized payload is 37,670 bytes, below the explicit
128 KiB test ceiling. Smaller capture counts/top-K values serialize proportionally
less evidence. `AllocationReproducibilityTests` pins the schema/method identifiers,
the exact representative maximum payload, list bounds, and reordered-input stability
so a future additive field requires a deliberate cost/documentation update.

## Limitations

- The live characterization covers one Windows environment and one synthetic
  allocation shape. Other runtimes, operating systems, workload mixes, and capture
  durations require their own compatible evidence.
- Runtime-side EventPipe source loss is not independently observable. The harness
  distinguishes delivered raw evidence from retained stack attribution but cannot
  claim events the runtime never delivered.
- `GCAllocationTick` estimated bytes are sampling weights, not exact allocated
  bytes, object counts, retained bytes, or CPU time.
- Type and stack rankings are independent projections. Their matching workload
  proportions do not authorize a type-to-stack join.
- Four captures exercise reproducibility and variability under this bounded
  scenario; they do not establish a universal interval or threshold.

## Reproduction

From the repository root:

```powershell
dotnet test tests\DotnetDiagnostics.ScenarioEvaluation.Tests\DotnetDiagnostics.ScenarioEvaluation.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~AllocationHotspotTests"

$env:DOTNET_DIAGNOSTICS_ALLOCATION_HOTSPOT_REPETITIONS = "4"
$env:DOTNET_DIAGNOSTICS_ALLOCATION_HOTSPOT_FIXTURE_PATH = Join-Path `
  (Get-Location) `
  "tests\DotnetDiagnostics.ScenarioEvaluation.Tests\Fixtures\AllocationHotspot\allocation-hotspot.windows.v1.json"
dotnet test tests\DotnetDiagnostics.ScenarioEvaluation.Tests\DotnetDiagnostics.ScenarioEvaluation.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName=DotnetDiagnostics.ScenarioEvaluation.Tests.AllocationHotspotLiveTests.LiveCapture_ProducesBoundedReplayableAllocationEvidence"
```

The ordinary live test omits the fixture path and runs one capture per real arm as a
bounded smoke check. Supplying a fixture path defaults to four repetitions unless
the repetition variable is set explicitly.
