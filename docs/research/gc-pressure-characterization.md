# Persistent versus transient GC/LOH pressure characterization

**Issue:** [#1309](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1309)  
**Roadmap:** [#1305](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1305)  
**Statistical contract:** [#1308](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1308)  
**Repeated-observation harness:** [#1311](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1311)  
**Retained-growth coordination:** [#924](https://github.com/pedrosakuma/dotnet-diagnostics/issues/924)

## Verdict

**GO for a separate scenario-specific Core/CLI contract follow-up.**

Six bounded non-overlapping windows distinguished the required known shapes and
changed the safe next action:

- sustained allocation pressure selected allocation investigation;
- persistent positive LOH slope without cooldown recovery selected retained-heap
  investigation;
- a transient burst and stable control selected continued observation;
- collector loss/truncation selected justified inconclusion.

This is a feasibility verdict for objective signals, not a product implementation,
GC-health score, leak verdict, causal attribution, or authorization for a new MCP
tool. Retained-growth evidence only prioritizes the separate graph/root investigation
owned by #924.

## Bounded protocol

- One fresh CoreCLR process per scenario, workstation GC, loopback workload.
- Six separately started three-second EventPipe windows: one baseline, up to three
  active windows, and at least two cooldown windows.
- At most eight windows, 512 retained GC details per normal window, and one retained
  detail in the deliberate-loss scenario.
- At least five valid windows are required. Loss and truncation are not admitted.
- Window median and Theil-Sen slope use the #1308/#1311 deterministic harness with
  `bounded_time_window` as the independence unit.
- Window p95 uses the exact bounded R7 quantile over at most eight window summaries.
- Gen2 frequency is completed Gen2 collections divided by observed window seconds.
- Pause duration is the p95 of authoritative
  `GCSuspendEEStop`-to-`GCRestartEEStart` intervals within each window.
- Time in GC is authoritative suspension time divided by observed window duration.
  The `System.Runtime/time-in-gc` EventCounter was not used for cooldown decisions:
  its last value can remain latched across later windows with no GC and would make a
  recovered transient burst look persistent.

The predeclared decision geometry is:

- pressure window: LOH is at least 16 MiB above baseline, Gen2 rate is at least
  0.5/s, pause p95 is at least 2 ms, or time in GC is at least 5%;
- allocation candidate: Gen2 rate, pause p95, or time in GC crosses its pressure
  threshold in at least three windows; this takes precedence over a late LOH maximum
  left by allocation churn;
- retained-growth candidate: fewer than three GC-pressure windows, Theil-Sen LOH
  slope of at least 1 MiB/window, and final cooldown windows that do not return to the
  baseline envelope;
- recovered/healthy: at most two pressure windows and no failed cooldown recovery;
- otherwise, or when any required statistic is unavailable: justified inconclusion.

Cross-signal first-observed window ordering is retained only as a temporal
observation. It never establishes that LOH movement caused Gen2, pause, or
time-in-GC movement.

## Deterministic replay matrix

The compact fixture
`tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/GcPressureCharacterization/gc-pressure-characterization.v1.json`
contains all five shapes. Replay tests verify deterministic serialization, bounded
counts, loss precedence, sufficient-window requirements, and the no-causality/no-leak
language.

| Shape | Pressure windows | Cooldown | Robust LOH shape | Next action |
|---|---:|---|---|---|
| Stable healthy | 0 | stable | zero slope | Continued observation |
| Transient burst | 1 | recovered | zero slope | Continued observation |
| Sustained allocation | 3 | recovered after load | zero/non-retained slope | Allocation investigation |
| Retained growth | 3+ | not recovered | positive slope | Retained-heap investigation |
| Capture loss | unavailable | unavailable | unavailable | Justified inconclusion |

## Live evidence

The final local Windows run used .NET 10.0.14 and passed all five live cases in
1 minute 40 seconds. Values below are descriptive window statistics from that run;
they are not cross-machine thresholds.

| Shape | Availability | LOH median / p95 | LOH slope | Gen2 median / p95 | Pause median / p95 | Time-in-GC median / p95 | Decision |
|---|---|---:|---:|---:|---:|---:|---|
| Stable healthy | available | 0 / 0 MiB | 0 MiB/window | 0 / 0 per s | 0 / 0 ms | 0 / 0% | Continued observation |
| Transient burst | available | 3.243 / 3.243 MiB | 0 MiB/window | 0 / 4.331 per s | 0 / 2.211 ms | 0 / 0.797% | Continued observation |
| Sustained allocation | available | 3.816 / 3.816 MiB | 0 MiB/window | 19.227 / 40.223 per s | 1.098 / 3.219 ms | 3.733 / 9.648% | Allocation investigation |
| Retained growth | available | 38.201 / 55.250 MiB | 11.366 MiB/window | 0 / 0.321 per s | 0 / 1.391 ms | 0 / 0.075% | Retained-heap investigation |
| Capture loss | `loss_detected` | unavailable | unavailable | unavailable | unavailable | unavailable | Justified inconclusion |

The retained-growth workload deliberately keeps 48 MiB of bounded buffers reachable.
That known workload truth validates the temporal shape only. The observed positive
LOH slope does not prove why an unknown application retains memory; #924 graph/root
evidence remains necessary.

## GO gate assessment

| Gate | Evidence |
|---|---|
| Transient versus sustained | One pressure window with cooldown recovery versus three consecutive pressure windows. |
| Sustained versus retained | High Gen2/pause/time-in-GC persistence with flat LOH slope versus low GC pressure with positive LOH slope and failed cooldown recovery. |
| Healthy control | Zero pressure windows and stable cooldown. |
| Truncation/loss | A live one-detail GC cap produced explicit dropped counts and `loss_detected`; no success-shaped fallback was emitted. |
| Sufficient observations | Median, p95, persistence, stabilization, and slope require at least five valid windows. |
| Causality posture | Timing is labeled co-observation only; decisions select investigation paths rather than causes or verdicts. |
| Bounds | Fixed process, window, duration, event-detail, observation, and output limits are asserted by tests. |

## Limitations

- The committed thresholds are scenario geometry, not universal production defaults.
- One process run per shape demonstrates objective separability, not population-level
  uncertainty across machines or environment allocations.
- Separate EventPipe sessions add startup and observation effects to every window.
- LOH size can change at collection boundaries and does not identify retaining roots.
- The live loss case intentionally constrains retained details; it validates abstention,
  not a recovery or estimation strategy.
## Product contract delivered by #1324

The product follow-up promotes the decision geometry into the scenario-local
`DotnetDiagnostics.Core.Gc.GcPressureCharacterizer` contract and exposes it through
the offline `dotnet-diagnostics-cli gc-pressure --file <capture.json>` command.
The Core result retains capture/runtime/backend identity, availability, method
identity/version, bounded counts, median/p95/slope evidence, persistence, cooldown
recovery, and non-causal timing observations. It does not add a collector, MCP
tool, automatic heap capture, or extension UI.

Human, JSON, and one-line JSONL output render the same Core result without
recalculation. Allocation-pressure persistence takes precedence over a late LOH
maximum. Retained growth selects the separate retained-heap graph/root
investigation without identifying a retaining cause. Thresholds remain the named
scenario geometry above, not universal production defaults.

Old artifacts are never backfilled or rewritten. Missing or unsupported schema
metadata produces `justified-inconclusion` with explicit incompatible
availability. Loss and truncation take precedence over insufficient-window
estimates, and unsupported approximate representations remain unavailable.

No MCP type references the new record, so the registered `tools/list` schema cost
is zero. A representative sustained six-window capture measured 2,084 input bytes,
5,271 bytes for the indented JSON envelope, and 3,779 bytes for the compact JSONL
envelope. The CLI reader bounds input at 256 KiB and the output regression test
keeps the representative JSON below 20 KiB.
