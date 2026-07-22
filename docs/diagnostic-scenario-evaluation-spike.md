# Diagnostic scenario evaluation spike

Issue [#646](https://github.com/pedrosakuma/dotnet-diagnostics/issues/646)
asks whether subtle incidents can be activated deterministically, reduced to
stable structured evidence, scored without prose matching, and replayed
offline. This spike answers **GO**, with one explicit Linux batching
constraint described below.

## Decision

**GO** for a small diagnostic-scenario evaluation surface built from existing
Core collectors and `BadCodeSample` workloads:

- all 30 Windows trials and all 20 isolated Linux trials produced the expected
  evidence shape;
- each scenario attributes the observed cost or wait to the intended method,
  blocking frame, or correlated monitor owner;
- each tempting wrong hypothesis is refutable through a separate structured
  invariant;
- the one-trial smoke subset completed in 22 seconds on Windows and 12 seconds
  on Linux, below the approximately 90-second target;
- collection and replay require no external network and no new MCP tool.

**Partial NO-GO** for running many Linux live trials in one long-lived xUnit
testhost. A 10-repetition batch reproduced the native Linux host crash tracked
by issue #147 after eight persisted sync-over-async captures. Running each
trial in a fresh testhost completed 10/10 sync-over-async and 10/10 lock-storm
trials. Extended Linux evaluation should therefore isolate processes and use
the repository's existing testhost retry strategy. This is an environment
failure, not an evidence-evaluation failure.

`culture-lookup` live CPU collection remains Windows-only in this test surface.
Linux CI already quarantines EventPipe SampleProfiler tests because of the same
upstream crash. The committed Windows evidence replays on every platform.

## What the spike adds

`tests/DotnetDiagnostics.ScenarioEvaluation.Tests` is an isolated, non-packable
test project. It does not change `BadCodeSample`, BenchmarkDotNet, the MCP tool
surface, or production collector behavior.

The project contains:

- a versioned JSON `ScenarioManifest` schema and manifests for
  `culture-lookup`, `sync-over-async`, and `lock-storm`;
- deterministic workload drivers that launch the Release sample DLL directly,
  use loopback HTTP, honor EventPipe startup timing, and terminate the process
  tree after every trial;
- a bounded normalized evidence projection containing only selected metrics,
  diagnosis-agnostic signals, relevant frames, owner/waiter relations, and
  degradation notes;
- typed invariant evaluation for signal presence, signal buckets, counter
  comparisons, stack frames, and owner/waiter correlations;
- a structured interpretation score with separate evidence, attribution,
  next-action, causality, and unsupported-conclusion dimensions;
- Windows and Linux evidence fixtures captured from the live runner, plus
  deterministic offline replay tests.

The replay format deliberately excludes full traces, dumps, process ids,
object addresses, diagnostic handles, and sensitive values.

## Scenario contract

Each manifest declares:

- scenario id/version and supported live platforms;
- reported symptom and ground truth;
- setup, activation, cleanup, workload parameters, warm-up, and observation
  window;
- runtime and normalized-evidence budgets;
- typed expected-evidence invariants;
- misleading signals and tempting wrong hypotheses;
- acceptable hypothesis, attribution, and next-action ids;
- forbidden conclusion ids and the required causality posture.

Typed invariants are intentionally narrow. A general JSON query language would
add complexity before the first three scenarios demonstrate a need for it.

## Stage and failure model

Activation, collection, and interpretation are independently reportable:

| Stage | Success means | Failure classification |
|---|---|---|
| Activation | The isolated process started and workload requests were issued | `workload` or `environment` |
| Collection | Existing bounded collectors returned the normalized evidence projection | `collection` or `environment` |
| Interpretation | A structured interpretation met the weighted rubric | `evaluation` |

Unsupported platforms, attach permission failures, and native testhost crashes
are environment failures. HTTP activation failures are workload failures.
Collector exceptions are collection failures. Manifest/replay mismatches and
unsupported conclusions are evaluation failures.

## Structured interpretation rubric

The scorer preserves the dimensions proposed in #646:

| Dimension | Weight | Structured check |
|---|---:|---|
| Evidence correctness | 25% | Evidence citations refer to passed invariants, cover the required invariants, and support an acceptable hypothesis id |
| Attribution | 25% | The responsible method/resource id is acceptable for the scenario |
| Appropriate next action | 20% | The selected drilldown/action id is acceptable |
| Correlation versus causality | 15% | The interpretation uses the required causality posture |
| Avoid unsupported conclusions | 15% | No forbidden conclusion id is asserted |

Replay tests build both a fully supported interpretation and the scenario's
tempting wrong interpretation. The supported input scores 1.0; the wrong input
fails with unsupported evidence, attribution, next action, causality, and
conclusion dimensions.

An eventual agent grader should map an agent response into this structured
input while preserving evidence citations and uncertainty. Model output should
remain advisory until model/version controls and repeated grader agreement are
established.

## Repetition results

Captures used the SDK selected from the repository `global.json` and .NET
10.0.10 runtime on x64. Every trial launched a fresh Release
`BadCodeSample` process.

### Windows

| Scenario | Passed | Activation runtime, seconds (avg/min/max) | Stable evidence range |
|---|---:|---:|---|
| `culture-lookup` | 10/10 | 10.32 / 9.43 / 11.32 | Globalization hash self-time 34.4%-54.8% (required >=20%) |
| `sync-over-async` | 10/10 | 6.38 / 6.14 / 6.97 | CPU <=2.44%; queue >=81; blocking-frame matches >=25 |
| `lock-storm` | 10/10 | 2.43 / 2.26 / 2.91 | Contended-monitor and sleeping-owner waiter counts 15-17 |

### Linux (Ubuntu 24.04 under WSL2)

Long repetitions used one testhost per trial because of issue #147.

| Scenario | Passed | Activation runtime, seconds (avg/min/max) | Stable evidence range |
|---|---:|---:|---|
| `sync-over-async` | 10/10 | 6.25 / 6.16 / 6.43 | CPU <=1.52%; queue >=393; blocking-frame matches >=95 |
| `lock-storm` | 10/10 | 2.70 / 2.47 / 3.63 | Contended-monitor count 17; sleeping-owner waiter count 17 |
| `culture-lookup` | replay only | N/A | Windows live fixture passes the same evaluator on Linux |

The original monolithic Linux repetition attempt persisted eight valid
sync-over-async trials before the native testhost crashed. All eight completed
captures passed; the ninth trial produced no report because the process died.
That run is classified as an environment failure and is the reason extended
Linux runs must use process isolation.

## Wrong hypotheses refuted

| Scenario | Tempting answer | Refuting evidence |
|---|---|---|
| `culture-lookup` | ThreadPool/framework overhead or more compute is required | Inclusive dispatch frames are not the self-time leader; `CompareInfo.IcuGetHashCodeOfString` owns at least 34.4% of self-time |
| `sync-over-async` | CPU saturation requires scaling out | CPU remains below 2.44% while the queue grows and many `SpinThenBlockingWait`/`Task.WaitAll` frames appear |
| `lock-storm` | Parked workers imply unrelated I/O or GC | `correlation.thread-overlap` joins the `Thread.Sleep` owner to 15-17 monitor waiters |

The lock result remains a correlation until the owner/waiter relation and owner
wait state are both present. The manifest forbids claiming that the grouping
alone proves a source line.

## Running the evaluation

Build and replay all committed evidence:

```powershell
dotnet build tests\DotnetDiagnostics.ScenarioEvaluation.Tests\ -c Release
dotnet test tests\DotnetDiagnostics.ScenarioEvaluation.Tests\ -c Release --no-build `
  --filter "Category!=ScenarioEvaluationLive"
```

Run the supported one-trial smoke subset:

```powershell
dotnet test tests\DotnetDiagnostics.ScenarioEvaluation.Tests\ -c Release --no-build `
  --filter "Category=ScenarioEvaluationLive"
```

Persist normalized evidence and run more repetitions:

```powershell
$env:DOTNET_DIAGNOSTICS_SCENARIO_REPETITIONS = "10"
$env:DOTNET_DIAGNOSTICS_SCENARIO_OUTPUT_DIR = "$PWD\artifacts\scenario-evaluation"
dotnet test tests\DotnetDiagnostics.ScenarioEvaluation.Tests\ -c Release --no-build `
  --filter "Category=ScenarioEvaluationLive"
```

On Linux, run one repetition per testhost and set
`DOTNET_DIAGNOSTICS_SCENARIO_TRIAL_OFFSET` to preserve unique trial numbers.
Do not put many ClrMD/EventPipe live captures in one process while issue #147
remains open.

## Criterion-by-criterion result

| #646 criterion | Result |
|---|---|
| Expected evidence in at least 90% of supported-platform runs | **GO:** 50/50 isolated live trials |
| Attribution to intended method, wait source, or resource | **GO:** globalization hash, blocking task frames, and sleeping monitor owner |
| Wrong hypothesis refutable in every scenario | **GO:** separate typed invariants cover all three |
| PR smoke near 90 seconds or less | **GO:** 22 seconds Windows, 12 seconds Linux |
| Failure classified by stage | **GO:** workload, collection, environment, evaluation |
| No new MCP tool | **GO** |
| No fragile wall-clock-only recognition | **GO:** evidence uses relative self-time, counters, frames, and relations |
| No external network dependency | **GO:** loopback only |
| Collector does not erase the phenomenon | **GO:** evidence remains well above thresholds; Linux batching is isolated because the host can crash |

## Narrow follow-ups

1. Add a manual/nightly matrix only after a process-isolated launcher can upload
   per-trial JSON and retry Linux testhost crashes without hiding them.
2. Move the contract/evaluator into a reusable test-support assembly only when a
   second consumer appears; the spike does not justify production packaging.
3. Prototype an advisory agent-response mapper that emits the structured
   interpretation contract with evidence citations and uncertainty.
4. Add more scenarios only when they exercise a new evidence shape; do not grow
   a catalog of variants that reuse the same oracle.

## Addendum: agent-response mapper prototype (#681, item 3)

`ScenarioAgentResponseMapper` (`tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ScenarioAgentResponseMapper.cs`)
prototypes turning free-text-shaped diagnosis output into the scoreable
`StructuredInterpretation` contract, closing the gap that every prior test
(`ScenarioReplayTests`) only ever fed `ScenarioEvaluator` hand-authored gold
interpretations built directly from manifest ids -- never anything resembling
real natural-language phrasing.

- **Input**: `AgentScenarioResponse` -- free-text hypothesis/attribution/
  next-action/causality-statement/conclusions plus a narrative paragraph.
- **Mapping**: a deterministic, offline token-set (Jaccard similarity)
  match against each scenario manifest's own controlled vocabulary
  (`acceptableHypotheses` + `temptingWrongHypotheses`, `acceptableAttributions`,
  `acceptableNextActions`, and a small fixed causality-posture taxonomy drawn
  from the postures the shipped manifests actually use). No LLM call, no
  network access, no stemming/synonym table.
- **Unmapped-by-default**: a field that does not clear the match threshold, or
  that clears it for more than one candidate (an ambiguous response), is left
  empty and reported in `UnmappedFields` rather than guessed -- an unresolved
  field always scores as unsupported, never as an accidental match.
- **Negation-aware**: a candidate whose own tokens are immediately preceded,
  within the same clause, by a negation marker ("not", "never", ...) in the
  response is excluded even when the bag-of-words overlap is otherwise
  strong, so "not <candidate phrase>" cannot silently map to the accepted
  id. Clause boundaries (`.`, `,`, `;`, `:`, "but", "however", "although")
  bound the check so a contrastive "not X, but Y" only negates X, not the
  affirmed Y. This is a proximity heuristic, not real negation-scope parsing.
- **Uncertainty**: `UncertaintyAssessment` scans the separate narrative field
  for hedging phrases (e.g. "correlat...", "further investigation") versus
  overclaiming phrases (e.g. "definitely the cause", "no other possible
  explanation"), independent of the scored interpretation fields.
- **Validated** end-to-end in `ScenarioAgentResponseMapperTests`: a
  hedged/accepted narrative for `lock-storm` maps to every correct id and
  scores `1.0`; an overclaiming, wrong narrative fails to map attribution/
  next-action/causality, is flagged `OverclaimsCertainty`, and scores the
  `ScenarioEvaluationReport` as `Failed`; `sync-over-async` and
  `culture-lookup` resolve their controlled fields when phrased as the
  manifest's own hypothesis/attribution/next-action/causality text with
  hyphens replaced by spaces -- this exercises normalization, not
  independently authored prose the way the `lock-storm` cases do; a negated
  accepted hypothesis and a hypothesis ambiguous between the accepted and a
  tempting-wrong id are both reported as unmapped rather than guessed; an
  unrecognizable hypothesis is reported as unmapped too.

This stays advisory only -- it is not wired into any production MCP tool or
PR gate, and the token-overlap heuristic is a known-limited stand-in for an
eventual real agent-level rubric (#646 goal 7), not a claim that it can score
arbitrary free text reliably.
