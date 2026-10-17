# Prospective counter evidence contract

This page specifies a future, explicit opt-in counter projection for the blinded
scenario-evaluation harness. It does not change, reinterpret, or rescore any completed
`human-v1` assessment. The frozen baseline at revision `f9c2ef8e` remains immutable.
A future assessment must declare a separate assessment and evidence-contract version
before consuming this projection.

## Version boundary

`CounterEvidenceContractVersion.LegacyV1` remains the gateway default. Its counter JSON
shape is unchanged: `value` is the last retained sample and `maximumObserved` is the
maximum raw sample. `ProspectiveV2` is available only through an explicit gateway
constructor argument. Existing advisory entry points do not select it, so a strict
`human-v1` whitelist continues to fail closed rather than accepting new fields.

The prospective response identifies itself with
`counterEvidenceContract: "prospective-v2"` and represents each counter with:

- `lastSample`: the final retained raw sample for the counter key;
- `maximumRawSample`: the maximum retained raw sample across observed ticks;
- per-sample `actualIntervalSec` and `displayRateTimeScaleSeconds`, taken from that
  sample's own `CounterValue`;
- explicit `null` when metadata or a maximum sample is unavailable;
- bounded `metadataIssues` values when a numeric value or metadata value is invalid;
- `timeSeriesAvailable: false`, `sampleAlignmentAvailable: false`, and
  `ratesDerivedByHarness: false`.

The projection does not divide by capture duration, derive a rate, infer growth, or
pretend that last and maximum samples form an aligned series. Maximum-sample metadata
comes from the maximum sample, not from the last sample. The existing System.Runtime
filter, ordinal name ordering, 60-counter cap, omission accounting, response budgets,
and provider-name non-disclosure remain in force.

The source model for these semantics is
[`CounterValue` and `CounterSnapshot`](../src/DotnetDiagnostics.Core/Counters/CounterValue.cs):
`Counters` retains the last sample per key, while `MaxCounters` retains the maximum raw
sample and its own interval/display-scale metadata. This is a projection correction,
not evidence of an EventPipe collector defect and not a proposal for a new retained
time series.

## Future interpretation guidance

Future rubrics should keep three claims separate:

1. **Magnitude:** a documented reference scale can justify describing an observed
   number as large or small relative to that scale.
2. **Abnormality:** calling a value abnormal requires a relevant workload baseline,
   expectation, or comparison. A reference scale alone is insufficient.
3. **Causal impact:** attributing latency, throughput loss, or another performance
   effect requires matching performance evidence. Counter magnitude alone does not
   establish causality.

Recommendations must also be capability-aware:

- Check the actual target runtime, operating system/backend, diagnostic socket access,
  UID, and required permissions before recommending another collector.
- `dotnet-trace` and `dotnet-counters` use the same EventPipe/diagnostic-IPC path as
  EventPipe collectors. They are not independent fallbacks for runtime support,
  diagnostic-socket, or permission failures. The shared attach mechanism is described
  in the [runtime compatibility matrix](runtime-version-compat-matrix.md#whats-validated-per-collector-family).
- NativeAOT may expose EventPipe counters, GC, and exception evidence even when managed
  CPU stacks are unavailable. CPU-stack recommendations must select an available
  native backend such as `perf` on Linux or ETW on Windows and account for its
  permissions; see the [NativeAOT coverage matrix](aot-coverage.md).
- Do not recommend automatic instrumentation or deployment changes. Standard EventPipe
  diagnostics require no target modification; privileged instrumentation is a distinct,
  explicitly authorized capability, as summarized in the
  [documentation index](README.md#cross-cutting).

These rules are prospective guidance only. They must not be applied retroactively to
change scores or claims in completed assessment artifacts.
