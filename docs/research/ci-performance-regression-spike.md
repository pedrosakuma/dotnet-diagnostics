# CI performance regression detection spike

Issue [#647](https://github.com/pedrosakuma/dotnet-diagnostics/issues/647) tests a
strict two-run model:

1. Clean BenchmarkDotNet launches measure time and allocation without the
   `DotnetDiagnosticsDiagnoser`.
2. A physically separate monitoring launch uses EventPipe only to attribute a
   moved cost. Its elapsed time never enters the regression verdict.

The workflow is advisory. It requires three compatible clean launches, repeated
threshold agreement, bounded variance, unchanged controls, and compatible
diagnostic provenance. One capture can never make a result gate-eligible.

## Durable evidence

The durable record is not just the final verdict. The verdict is derived and can
be regenerated. Long-lived artifacts retain the compact inputs that explain it:

- immutable schema version, build identities, capture time, runner class and
  hosted image version, runtime, OS, RID, architecture, and GC mode;
- workload identity, version, parameters, baseline/candidate identities, and
  control designation;
- each clean launch's BenchmarkDotNet mean, standard deviation, sample count,
  and bytes allocated per operation;
- bounded normalized attribution signals with metric name, stable method/type
  identity, value, unit, and preferred direction;
- sampling metadata and a short-lived raw-artifact reference containing a
  relative path, byte size, SHA-256 content hash, and retention period.

Raw EventPipe JSON is useful for investigation but is substantially larger and
need not be retained forever. The prototype retains it for 30 days. Compact
measurement and attribution documents are the comparison record; reports are
regenerable views. A baseline refresh writes a new immutable artifact and
requires review. It must not silently overwrite a prior baseline.

Compatibility is intentionally strict. Runtime, OS, RID, architecture, GC mode,
runner class/image, workload contract, and parameters must match. Capture IDs
and timestamps must be unique. Hosted and dedicated runners are different
environments even when their runtime versions match. Gate eligibility also
requires at least one complete low-variance unchanged control. Allocation
movement from a zero baseline must clear a 32 B/op absolute floor.

## Local evidence

The calibration environment was a Windows x64 virtual machine using concurrent
workstation GC and runner class `local-windows`. `global.json` requests .NET SDK
10.0.201 with feature-band roll-forward; the available SDK selected 10.0.302 and
the benchmark runtime was .NET 10.0.10. These results are not evidence about a
GitHub-hosted or dedicated runner.

Three independent clean launches produced:

| Pilot | Signal | Median delta | Baseline/candidate CV | Threshold agreement | Result |
| --- | --- | ---: | ---: | ---: | --- |
| Allocation churn | Time | +29.28% | 16.02% / 18.30% | 3/3 | Inconclusive: excessive variance |
| Allocation churn | Bytes/op | +19.83% | 0% / 0% | 3/3 | Regression |
| CPU string lookup | Time | +77.07% | 2.38% / 35.35% | 3/3 | Inconclusive: candidate variance |
| Sync over async | Time | +384,707.38% | 40.74% / 49.05% | 3/3 | Inconclusive: excessive variance |
| Sync over async | Bytes/op | 0 to 72 B | 0% / 1.59% | 3/3 | Regression |
| Unchanged control | Time | -22.93% | 25.32% / 25.04% | 0/3 regressions | Inconclusive, no false regression |

The separate diagnostic launch retained these compact signals:

- CPU: 1,540 samples; `CultureAwareLookupCandidate()` accounted for 96.04% of
  exclusive samples.
- Allocation: 14.4 GB sampled over three seconds; `System.String` dominated,
  and the normalized call-site list retained `AllocationCandidate()`.
- Waiting: no ThreadPool growth or hill-climbing signal was captured in the
  final three-second window. An earlier calibration observed worker growth, but
  the evidence was not reproducible and therefore does not satisfy attribution.

The local report verdict is `regression`, driven by deterministic allocation
signals, but its recommendation remains `advisory` and it is not gate-eligible.
Timing noise and missing waiting attribution are carried as uncertainty rather
than converted into a success-shaped result.

## Hosted-runner evidence

PR workflow run `29632122059` performed three clean launches and one separate
diagnostic launch on one `ubuntu-latest` VM. The compact artifact is
`performance-regression-signals-1`; the 30-day raw artifact is
`performance-regression-raw-1`. The environment was Ubuntu 24.04.4 LTS,
linux-x64, concurrent workstation GC, hosted image
`ubuntu24-20260714.240.1`, SDK 10.0.302 selected through the repository's
10.0.201 `global.json` roll-forward policy, and runtime .NET 10.0.10.

| Pilot | Signal | Median delta | Baseline/candidate CV | Threshold agreement | Result |
| --- | --- | ---: | ---: | ---: | --- |
| Allocation churn | Time | +14.10% | 0.59% / 7.48% | 2/3 | Regression |
| Allocation churn | Bytes/op | +19.83% | 0% / 0% | 3/3 | Regression |
| CPU string lookup | Time | +80.79% | 0.58% / 0.64% | 3/3 | Regression |
| Sync over async | Time | +221,567.82% | 1.80% / 5.02% | 3/3 | Regression |
| Sync over async | Bytes/op | 0 to 72 B | 0% / 0% | 3/3 | Regression |
| Unchanged control | Time | +0.17% | 0.43% / 0.28% | 0/3 regressions | Inconclusive, no false regression |

The separate attribution launch captured 2,775 CPU samples and assigned 91.68%
exclusive cost to `CultureAwareLookupCandidate()`. Allocation sampling retained
the expected `AllocationCandidate()` site and `System.String` as the dominant
type. ThreadPool sampling again captured no worker growth, hill-climbing,
starvation, or enqueue events, so the waiting regression remained unattributed.
The report therefore produced `regression` with an `advisory` recommendation,
`eligibleForGate: false`, and zero unchanged-control false positives.

This is persistent detection evidence, not a timing-gate calibration. Three
launches on one hosted VM do not measure variation between runner allocations,
images, or days. Additional advisory history is required before interpreting
the low within-job CV as a stable hosted-runner property.

No dedicated-runner evidence is available in this spike.

## Rollout conclusion

**Partial-GO.**

- **GO:** retain the advisory workflow and versioned compact evidence format.
  It can detect deterministic allocation movement and preserve causal signals
  without making raw captures the permanent comparison database.
- **Conditional GO:** allocation-only soft or hard gating may be considered
  after repeated compatible hosted runs show stable controls and attribution.
- **NO-GO:** do not hard-gate elapsed time on shared hosted runners. Require
  dedicated-runner evidence, repeated captures, low variance, and unchanged
  controls first.
- **NO-GO:** do not gate the waiting pilot until its diagnostic attribution is
  reproducible.

The next rollout step is advisory-only history collection. A later soft gate
should require repeated compatible low-variance results. Hard timing gates
remain out of scope until dedicated-runner evidence exists.

## Paired-ref operational experiment

Issue [#651](https://github.com/pedrosakuma/dotnet-diagnostics/issues/651)
turns the next step into a manual same-VM experiment rather than a gate. The
`paired-performance-experiment.yml` workflow:

1. checks out `main` and the selected PR ref into separate directories on one
   `ubuntu-latest` VM and records both resolved commit SHAs;
2. selects one SDK from `global.json`, verifies both refs resolve the same SDK,
   and restores/builds each benchmark project once;
3. runs three clean pairs in alternating order (`main -> PR`, `PR -> main`,
   `main -> PR`);
4. runs the existing EventPipe diagnostic fixture only after all six clean
   per-ref captures complete;
5. uploads immutable measurement/normalized-signal inputs separately from the
   regenerable policy-derived report.

The normal entry point is `workflow_dispatch`. GitHub does not index a new
dispatchable workflow until its file reaches the default branch, so the stacked
PR also supports one exact opt-in `run-paired-performance` label event. Adding
that label is a human action; other labels, synchronize events, and ordinary PR
activity cannot start the experiment.

The comparison contract is strict. A scenario is comparable only when workload
version, parameters, control designation, and the complete variant set match.
PR-only scenarios are `new_unbaselined`, main-only scenarios are `removed`, and
shared identities with changed contracts are `contract_changed`. These states
are reported but cannot contribute a regression verdict. A new workload becomes
comparable only after merge and reviewed baseline acceptance.

The policy-neutral manifest records pair order, capture IDs/timestamps, real
build SHAs, runner/runtime/image provenance, and feasibility. The versioned
`issue-651-advisory-v1` report applies thresholds afterward. Changing thresholds
therefore regenerates a report without mutating historical measurements.
Diagnostic elapsed time is carried only as a feasibility stage; the analyzer
never consumes it as a measurement.

Feasibility stages record duration and input bytes for checkout and
restore/build per ref, each clean pair, diagnostics, report generation, and the
two bulk artifact uploads. Total observed runner minutes include workflow setup
through final report generation. The tiny final metadata upload is excluded
from the duration model and called out in the workflow; its file sizes remain
visible. The report evaluates likely every-PR, selected/label-triggered,
nightly, and manual cadence under explicit policy budgets.

This first experiment remains **within-VM evidence**. Even a successful run says
nothing yet about variance across hosted runner allocations, image revisions,
or days, and it contains no dedicated-runner evidence. Those cohorts remain
required by #651 before any timing soft or hard gate can be considered. One
cohort always produces `eligibleForGate: false`, an `advisory` recommendation,
and at most a `partial_go` operational decision.

### Cross-allocation and cross-day calibration protocol

The next #651 phase keeps `paired-performance-experiment.yml` as the only
measurement implementation and makes it reusable as one allocation. The
`paired-performance-calibration.yml` caller launches three independent
GitHub-hosted jobs in parallel. Every job still performs three alternating
within-VM clean pairs and only then runs EventPipe attribution. It emits a
self-contained policy-neutral cohort document containing:

- unique workflow/cohort/allocation and clean-capture IDs;
- selected SDK, runtime, OS/RID, architecture, GC mode, runner class/label,
  hosted image, immutable ref identities, and workload contracts;
- all six clean per-ref measurement documents;
- within-job runner minutes and compact/raw input bytes.

The aggregate report applies versioned policy
`issue-651-calibration-advisory-v1`. Exact SDK/runtime/image/ref/workload
compatibility defines a group; incompatible environments are kept as separate
groups and never pooled. For each compatible group the report distinguishes
the original within-VM pairs from cross-allocation and cross-day evidence,
reports detection and false-positive rates with Wilson 95% intervals, reports
within-cohort and cross-allocation/day CVs, and sums runner minutes and artifact
volume. New/unbaselined, removed, and contract-changed workloads retain the
#652 classifications and remain excluded from cross-ref verdicts.

The schedule runs on three adjacent UTC days and automatically downloads up to
five prior successful scheduled calibration runs. This cadence maximizes the
chance of collecting three days on one exact hosted image version; image
revisions still form separate groups and are never pooled. Manual dispatches
can name prior run IDs explicitly. Three matrix jobs on one date count as three
hosted allocations, not as multi-day evidence. Policy requires three
exact-compatible allocations across three UTC days for a runner population
before its targets can pass.
Regardless of the result, the implementation always emits
`eligibleForGate: false` and an `advisory` recommendation.

At `2026-07-18T23:23:11.2661408Z`, an authenticated
`GET /repos/pedrosakuma/dotnet-diagnostics/actions/runners` observation
returned `{"total_count":0,"runners":[]}`. The paired authenticated
`GET /repos/pedrosakuma/dotnet-diagnostics/actions/variables` observation
returned `{"total_count":0,"variables":[]}`. A dedicated job is therefore
skipped unless an operator first verifies an online runner carrying every
label `self-hosted`, `linux`, `x64`, and `dotnet-diagnostics-perf`, then sets
`PERF_DEDICATED_RUNNER_ENABLED=true`. This timestamped repository-API
provenance and explicit two-part contract avoid queueing indefinitely on an
invented label. The reproduction sequence is:

```bash
gh api repos/pedrosakuma/dotnet-diagnostics/actions/runners \
  --jq '.runners[] | select(.status == "online") | {name, labels: [.labels[].name]}'
gh variable set PERF_DEDICATED_RUNNER_ENABLED \
  --repo pedrosakuma/dotnet-diagnostics --body true
gh workflow run paired-performance-calibration.yml \
  --repo pedrosakuma/dotnet-diagnostics --ref main \
  -f baseline_ref=main \
  -f candidate_ref=main
```

Until compatible multi-day hosted evidence and a separately qualifying
dedicated cohort both exist, timing soft and hard gates remain blocked. The
dedicated job accepts only scheduled or default-branch manual main-vs-main
calibration and never executes pull-request code on the persistent runner.

### Independent hosted allocation evidence

PR workflow run `29664191176` executed three separate `ubuntu-latest` jobs
against main `a091476291daf58350077afeea269d03483349fa` and PR
`116b0fb7a86f224a87706585d5f9336552688f8c`. GitHub assigned distinct runner
allocations `GitHub Actions 1000048778`, `1000048779`, and `1000048780`.
All three resolved Ubuntu image `ubuntu24-20260714.240.1`, SDK 10.0.302 through
the repository's 10.0.201 roll-forward policy, and runtime .NET 10.0.10. The
SDK/runtime/image/ref/workload compatibility group was exact, and all workload
contracts remained comparable.

| Evidence | Result |
| --- | --- |
| Hosted allocations / UTC days | 3 / 1 |
| Injected-regression detection | main 9/9, PR 9/9 = 100% (95% CI 70.09-100%) |
| Unchanged-control false positives | main 0/3, PR 0/3 = 0% (95% CI 0-56.15%) |
| CPU lookup cross-allocation timing CV | 0.24-1.09% |
| Waiting cross-allocation timing CV | 0.39-0.63% |
| Unchanged-control cross-allocation timing CV | 0.12-0.70% |
| Allocation-churn cross-allocation timing CV | 12.02-18.74% |
| Per-cohort runner time | 15.16m, 14.95m, 15.12m |
| Total measured cohort runner time | 45.24m |
| Logical compact/raw inputs | 152,813 B / 1,259,434 B |
| Complete downloaded artifact tree | 1,819,703 B across 17 artifacts |
| Compressed artifact storage | 344,482 B |
| Separate attribution | environment-compatible; CPU, allocation, and all waiting candidate/control launches matched in every cohort |

The point detection and false-positive targets passed, but their confidence
intervals remain wide. This one date supplies cross-allocation evidence, not
cross-day evidence. More importantly, all four allocation-churn timing rows
exceeded the 10% cross-allocation CV policy limit. The other timing pilots and
unchanged control were stable across allocations, so the result is
workload-specific rather than a blanket hosted-runner failure.

**Partial-GO:** retain scheduled/manual advisory collection and the compact
cohort format. **NO-GO for timing soft or hard gates:** allocation-churn timing
is too noisy, only one UTC day is represented, and no dedicated runner exists.
Allocation metrics remain deterministic evidence, but this calibration does
not enable any gate.

### Actual-main hosted paired evidence

Workflow run `29649248742` completed the three alternating pairs on one
`ubuntu-latest` VM after #649 merged. The immutable manifest identifies actual
`main` commit `4025bd0af4314ab4f4e5cbf88abba5358d38d5c9` and PR commit
`44961e5a162ad91c8256312ddb35b318ec646b49`; environment and diagnostic
attribution compatibility both passed. This supersedes the preliminary stacked
run `29644836857` for the merge-readiness decision. Repeated hosted
allocations/days and a dedicated runner are still required before timing gates.

The environment was Ubuntu 24.04.4 LTS, linux-x64, concurrent workstation GC,
hosted image `ubuntu24-20260714.240.1`, SDK 10.0.302 selected through
`global.json`, and runtime .NET 10.0.10. All four workload identities and both
variants had identical contracts across refs, so no workload was
`new_unbaselined`, `removed`, or `contract_changed`.

| Evidence | Result |
| --- | --- |
| Injected-fixture detection | main 3/3 (100%); PR 3/3 (100%) |
| Unchanged-control false positives | main 0/1 (0%); PR 0/1 (0%) |
| Cross-ref verdict | Inconclusive; no variant produced repeated regression agreement |
| Attribution | CPU and allocation matched; ThreadPool/wait remained unmatched |
| Total observed runner time | 13.88 minutes |
| Restore/build | 21.59s main; 17.85s PR |
| Clean pairs | 244.93s, 243.23s, 240.38s |
| Separate diagnostics | 45.32s |
| Report generation | 0.72s |
| Bulk uploads | 1.14s compact; 1.21s raw |
| Compact/raw inputs | 33,897 B / 378,219 B |
| Downloaded provenance/report | 6,466 B / 36,066 B |

The `issue-651-advisory-v1` cost policy classifies every-PR execution as
**unsuitable** (13.88m exceeds the 10m budget), selected/label-triggered PR
execution as **conditional**, and nightly/manual advisory execution as
**suitable**. This is a **partial-GO** for continued advisory evidence
collection, not a gate: detection and false-positive targets passed within this
one VM, but neither cross-runner/day variance nor dedicated-runner stability has
been measured.

## Waiting-attribution follow-up

Issue [#650](https://github.com/pedrosakuma/dotnet-diagnostics/issues/650)
keeps clean BenchmarkDotNet measurement and EventPipe attribution in separate
process launches. Both waiting variants execute eight one-millisecond delayed
operations and return the same sum. The baseline awaits the operations
concurrently; the candidate synchronously waits for each operation in sequence,
so inputs and outcome remain equivalent while the scheduling behavior differs.

The diagnostic-only fixture waits two seconds before activating the load to let
the EventPipe session start, then runs three independent candidate launches and
three unchanged controls. Compact matching reads the structured
`Data.HillClimbing[].Reason` payload and requires a positive `Starvation` or
`CooperativeBlocking` adjustment in every candidate launch. Generic summary
text, ordinary hill-climbing, enqueue counts, and worker growth alone are
retained as context but never treated as causal attribution. Any absent,
unrelated, or errored candidate launch keeps the waiting scenario ineligible
for a gate. Controls must retain zero equivalent blocking/starvation
attribution.

Local Windows evidence on .NET 10.0.10 produced the following result:

| Launch | Clean baseline | Clean candidate | Time delta | Candidate `CooperativeBlocking` adjustments | Workers added | Control equivalent attribution |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 15.41 ms | 124.09 ms | +705.41% | 62 | 106 | 0 |
| 2 | 15.51 ms | 123.01 ms | +693.13% | 52 | 127 | 0 |
| 3 | 15.55 ms | 124.70 ms | +702.11% | 41 | 57 | 0 |

The clean waiting measurements had 0.47% baseline CV and 0.69% candidate CV;
all three crossed the timing threshold. The physically separate diagnostic
experiment completed in 243.66 seconds. Its complete 25-file artifact tree,
including the regenerable report and logs, was 251,416 bytes. The durable
compact diagnostic document was 26,336 bytes, while the six short-lived hashed
raw ThreadPool references totaled 51,203 bytes. Diagnostic elapsed time is
feasibility evidence only and does not enter any benchmark measurement or
regression verdict.

**Partial-GO.** The parsed waiting attribution is reproducible enough for the
advisory pilot: three of three candidates retained causal cooperative-blocking
evidence and three of three controls did not. The generated report marks the
waiting regression's attribution consistent, but remains advisory and
gate-ineligible because the unchanged timing control was noisy on this local
runner. Gate enablement remains a **NO-GO** in this follow-up because it
provides one local runner's attribution evidence, not the paired
main-versus-PR and multi-runner calibration tracked separately in issue #651.
