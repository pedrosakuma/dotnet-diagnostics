# Contention concentration characterization

Issue: [#1310](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1310)

Roadmap: [#1305](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1305)

Statistical contract: [#1308](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1308)

Repeated-observation harness: [#1311](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1311)

## Verdict

**GO** for a separate minimal Core/CLI evidence-contract issue.

Stable top-1/top-3 wait-duration concentration safely changes the next contention
drilldown when the lock inventory is complete:

- stable dominant concentration focuses the next collection on one lock and, when
  observed, its owner relationship;
- diffuse concentration keeps the investigation broad;
- owner-unavailable evidence can still focus on the dominant lock, but not an owner;
- a short burst requires time alignment and another capture instead of being treated
  as sustained contention;
- a truncated top-lock inventory remains inconclusive;
- no events in healthy bounded captures means no contention was observed in those
  windows, not that waiting was zero.

Concentration is prioritization evidence. It does not establish the cause of
contention, a source line, or a performance verdict.

## Protocol

- Platform: Windows 11, .NET 10.0.14.
- Independent unit: one separately started four-second EventPipe capture against one
  fresh process run.
- Repetitions: four captures per scenario; no retry-until-green behavior.
- Nested evidence: non-overlapping one-second windows, retained for descriptive
  timing only and never counted as independent trials.
- Bounded aggregation: at most 32 distinct lock keys, eight reported top locks, and
  eight windows per capture.
- Quantities:
  - top-1 and top-3 shares of delivered contention wait duration;
  - median and raw MAD across captures;
  - per-capture event count plus p50/p95/max wait duration;
  - owner-overlap observation rate with a Wilson 95% interval at capture level.
- Owner-unavailable scenario: the workload and contention capture are live, then
  owner metadata is intentionally withheld to exercise the unavailable path.
- Healthy control: the same armed capture against a live process without a
  contending workload.

The reusable runner and evaluator live in
`tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ContentionConcentrationLive.cs`
and `ContentionConcentrationHarness.cs`. The compact replay fixture is
`Fixtures/Contention/contention-concentration.windows.v1.json`.

## Results

| Scenario | Events per capture | p50 wait ms | p95 wait ms | Median top-1 | Median top-3 | Top-1 capture MAD | Active-window fraction | Owner overlap (Wilson 95%) | Safe next focus |
|---|---:|---:|---:|---:|---:|---:|---:|---|---|
| dominant lock | 45–54 | 757.99–769.93 | 1515.94–2759.86 | 99.990% | 100.000% | 0.0037 pp | 100.0% | 4/4, 100%; [51.01%, 100%] | dominant lock and observed owner relationship |
| diffuse locks | 187–291 | 81.65–94.33 | 369.54–635.20 | 19.998% | 59.991% | 0.0019 pp | 100.0% | 4/4, 100%; [51.01%, 100%] | keep investigation broad |
| owner unavailable | 46–50 | 748.18–760.61 | 2230.80–2425.46 | 99.992% | 100.000% | 0.0002 pp | 100.0% | unavailable | dominant lock only; owner claim unavailable |
| short burst | 2–4 | 50.73–367.63 | 335.02–413.88 | 99.996% | 100.000% | 0.0042 pp | 37.5% | 0/4, 0%; [0%, 48.99%] | time-align and repeat |
| truncated inventory | 632–684 | 54.20–61.20 | 170.95–252.98 | 8.332% | 24.993% | 0.0004 pp | 100.0% | 0/4, 0%; [0%, 48.99%] | inconclusive because five lock keys are omitted |
| healthy control | 0 | unavailable | unavailable | unavailable | unavailable | unavailable | 0% | unavailable | bounded capture observed no contention |

The owner-overlap result is deliberately secondary. Both dominant and diffuse
workloads observed overlap in all four captures, so overlap alone does not select
one-lock versus broad drilldown. Concentration provides that distinction. The
four-capture Wilson intervals are correspondingly wide and are retained as
uncertainty, not converted into a binary owner verdict.

## Decision gates

The validation-only protocol selected these predeclared gates:

- dominant focus requires available top-1 and top-3 capture medians, top-1 at least
  65%, top-3 at least 80%, and top-1 raw MAD no greater than 15 percentage points;
- a capture set with any top-lock inventory truncation or aggregation overflow is
  inconclusive before concentration thresholds are considered;
- the short-burst scenario selects time alignment when the median active-window
  fraction is at most 50%;
- missing concentration remains unavailable and is never substituted with zero.

All six scenarios selected their expected safe focus. The dominant/diffuse split was
large relative to A/A capture variability: top-1 medians differed by about 80
percentage points while each scenario's raw capture MAD was below 0.005 percentage
points.

## Limitations

- Runtime-side EventPipe source loss is not independently observable in this
  validation contract. The bounded callback aggregates every delivered event and
  records collector retention notes, but cannot claim events that the runtime never
  delivered.
- Lock identity is capture-local. Concentration compares shares and stability, not
  lock addresses across process runs.
- Windows is the characterized live environment because the owner-overlap path uses
  a live ClrMD snapshot and Windows EventPipe contention events. Linux needs its own
  capability-valid evidence before using the same owner-overlap expectations.
- Four independent captures are enough to exercise the registered method and expose
  variability, but the Wilson intervals remain wide. Product work must preserve the
  counts and interval rather than hide that uncertainty.
- The reported top-lock inventory is bounded to eight entries. Absence from that
  list never means zero wait duration.

## Reproduction

From the repository root after a Release build:

```powershell
$env:DOTNET_DIAGNOSTICS_CONTENTION_REPETITIONS = '4'
$env:DOTNET_DIAGNOSTICS_CONTENTION_REPORT_PATH = 'artifacts\contention-concentration.windows.v1.json'
dotnet test tests\DotnetDiagnostics.ScenarioEvaluation.Tests\ `
  -c Release --no-build `
  --filter "FullyQualifiedName=DotnetDiagnostics.ScenarioEvaluation.Tests.ContentionConcentrationLiveTests.LiveCapture_ContentionShapes_AreBoundedAndReplayable"
```

The ordinary Windows live test runs one capture per scenario as a bounded smoke
check. Supplying the report path defaults to six captures unless the repetition
variable is set explicitly.
