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
