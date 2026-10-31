# Scenario-first statistical evidence contract

**Status:** Foundation design for [#1308](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1308)
under roadmap [#1305](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1305).
This document defines vocabulary, method identities, compatibility, ownership,
and compatibility behavior. It does **not** approve a public shared DTO, a broad
statistics engine, a collector change, a new MCP tool, scenario thresholds, or
automatic performance verdicts.

The machine-readable companion is
[`statistical-evidence-method-registry.v1.json`](./statistical-evidence-method-registry.v1.json).
It is a design registry used by deterministic tests, not a shipping wire contract.

## 1. Product rule

Statistical evidence is an objective description of bounded observations. It may
report an estimate, difference, spread, slope, rate, or interval. It does not decide
whether the target regressed, improved, leaked, starved, contended, passed, failed,
or caused an observed symptom.

Every scenario must first name the troubleshooting decision that the evidence can
change. The scenario issue then owns:

- the selected method or methods;
- the minimum useful evidence above the mathematical minimum in this document;
- capture duration, repetition count, resample count, confidence level, and any
  scenario-specific threshold;
- which acquisition loss or approximation remains admissible;
- the concrete next action and the conditions that require inconclusion.

No method in this registry is universal for every collector. A method identifier
means only the calculation defined here; it is not approval to apply that calculation
to a new population.

## 2. Vocabulary

| Term | Contract |
|---|---|
| **observed quantity** | A stable quantity ID plus unit, aggregation, and normalization. For example, request latency in milliseconds, aggregated as one median per bounded window and normalized per accepted request. |
| **observation** | One finite input value accepted by the method. It may summarize many correlated source events. |
| **independence unit** | The smallest unit the method treats as independently repeatable: event, bounded time window, capture, process run, or environment allocation. |
| **estimate** | The method's point estimate in the observed quantity's unit. Missing means unavailable, never zero. |
| **baseline / candidate estimate** | Point estimates for two compatible arms. The names identify comparison position, not quality. |
| **absolute delta** | `candidate - baseline`, in the observed quantity's unit. |
| **relative delta** | `(candidate - baseline) / abs(baseline)`, a unitless ratio. It is unavailable for a zero baseline and is not a percentage until a renderer explicitly multiplies it by 100. |
| **dispersion** | A method-defined variability estimate. The method and scale must be named; `0` is a measured value only when the method was available. |
| **slope** | Change in the observed quantity per declared x-axis unit. It is descriptive and does not establish causation or forecast future values. |
| **rate** | `positiveCount / observationCount` for an explicitly defined binary event over independent units. A zero denominator is unavailable, not a zero rate. |
| **interval** | Lower and upper bounds produced by a named interval method at an explicit confidence level. It describes that method's uncertainty under its assumptions; it is not a verdict. |
| **count** | An integer denominator or acquisition count. Counts are not interchangeable: events, retained samples, windows, captures, repetitions, and independence units must remain distinct. |
| **assumption** | A precondition accepted by the scenario protocol but not established by the result itself. |
| **limitation** | An observed or intrinsic boundary on interpretation, including loss, truncation, approximation, missing dimensions, and legacy metadata. |

All numeric values must be finite. NaN and infinities are excluded before calculation
and counted. If exclusions leave fewer inputs than the method minimum, the result is
`insufficient_observations`.

## 3. Availability

The closed v1 availability vocabulary is:

| State | Meaning |
|---|---|
| `available` | The method ran over compatible, admitted evidence and produced its required outputs. Optional outputs may still be unavailable for a separately named reason. |
| `insufficient_observations` | Compatible admissible evidence exists, but the method's minimum input count or a scenario's predeclared minimum was not met. |
| `incompatible` | Required identity dimensions differ, pairing is invalid, the method/version is unsupported, or legacy evidence lacks required semantics. Inputs are not pooled. |
| `loss_detected` | Observed source, collector, storage, or transport loss invalidates the requested population-level calculation under the scenario policy. |
| `unsupported_approximation` | The available representation is approximate, truncated, projected, or sampled in a way the selected method has not admitted. |
| `not_applicable` | The requested field has no meaning for this evidence shape, such as a paired delta without a baseline arm. |

When multiple states could apply, choose the first applicable state in this order:

1. `not_applicable` when the requested concept has no semantic operand;
2. `incompatible` before examining pooled numeric values;
3. `loss_detected` when known loss invalidates the method;
4. `unsupported_approximation` when the only retained representation is not admitted;
5. `insufficient_observations`;
6. `available`.

Additional conditions remain in bounded `limitations`; the primary state must not
hide them. A scenario may keep a point estimate `available` while declaring an
optional interval unavailable, but only when the method contract explicitly permits
that split. A missing property, JSON `null`, or absent legacy block never means zero,
unchanged, insignificant, healthy, or no loss.

### 3.1 Field-level unavailability

A future product record must pair each absent optional numeric field with a stable
reason. The prospective measurement fixture models this as
`unavailableFields[{field, reason}]`; exact product placement remains deferred.

- `estimate` is unavailable when the primary method is not `available`.
- `delta.absolute` is unavailable when either arm is missing, units/normalization
  differ, or pairing/compatibility fails.
- `delta.relative` is additionally unavailable when the baseline estimate is zero.
- `interval` and `confidenceLevel` are unavailable together when the parent estimate
  is unavailable, the interval method minimum is unmet, loss/approximation is not
  admitted, resampling parameters are absent, or any resample produces an invalid
  result.
- dispersion is unavailable below its method minimum or when the retained values
  are not valid observations of the declared independence unit.
- slope is unavailable with fewer than two distinct x coordinates, incompatible
  units, or an x-axis whose ordering/spacing is not defined.
- standardized effect size is not registered in v1 and therefore remains unavailable.
  Absolute and relative deltas must not be relabeled as standardized effect sizes.

## 4. Independence units

Every method invocation names exactly one independence unit:

| Unit | Meaning |
|---|---|
| `event` | One source event is independently generated for the method's purpose. This is rejected by default for EventPipe and time-series observations. |
| `bounded_time_window` | One predeclared, non-overlapping time window contributes one value. Overlap, carry-forward values, and shared warm-up can make windows dependent and must be disclosed. |
| `capture` | One separately started and stopped capture contributes one value. Reusing one underlying session does not create independent captures. |
| `process_run` | One independently launched target run contributes one value. Multiple captures from one run remain nested evidence unless the scenario proves otherwise. |
| `environment_allocation` | One independently allocated runner/VM/container environment contributes one value. Repetitions within the allocation do not increase this count. |

Source-event count may describe precision within a window, but it does not increase
the independent-unit count. Event-level independence is explicitly rejected for
correlated EventPipe events, counters, sampled stacks, GC sequences, request traces,
and time-series points unless a scenario supplies evidence for that assumption.

Paired calculations resample and count complete pairs, never individual arms.
Nested designs report every level: for example, 30,000 events → 30 windows →
6 captures → 3 process runs. The method chooses one level and retains the others
as descriptive counts.

## 5. Compatibility identity

Compatibility is evaluated before calculation. A bare boolean is insufficient;
the result must retain the compared dimensions and all mismatches.

The minimum identity contains:

1. **target provenance:** runtime flavor and version policy, OS, architecture, and
   target/process-run identity;
2. **backend:** EventPipe, ClrMD live, dump, perf, ETW, BenchmarkDotNet clean run,
   or another exact backend and backend version;
3. **quantity semantics:** quantity ID, unit, aggregation, normalization, filters,
   and whether values are exact, reservoir sampled, projected, or otherwise approximate;
4. **capture settings:** duration/windowing, providers and keywords, sampling period,
   buffer/retention caps, top-N/projection settings, and any enrichment options;
5. **workload dimensions:** scenario/workload version, input parameters, concurrency,
   deployment shape, and other scenario-declared dimensions;
6. **independence unit:** including nesting and pairing key;
7. **comparison axis:** the one dimension expected to differ, such as baseline versus
   candidate build or earlier versus later capture.

Canonical fingerprints may accompany these fields, but a hash never replaces the
human- and machine-readable normalized dimensions. Target IDs, capture IDs, and
timestamps remain provenance; they are not automatically compatibility invariants.

Scenario contracts declare which identity dimensions must match exactly and which
dimension is the comparison axis. Unrecognized or missing required dimensions make
the evidence `incompatible`; they are never filled from the current live target.

## 6. Method registry

Method IDs are lower-kebab-case semantic names. `version` is a positive integer.
Changing a formula, finite-value treatment, pairing rule, rounding point, or resampling
algorithm requires a new method version. Rendering changes do not.

The design registry fixes these v1 minimums:

| Method ID | Mathematical minimum | Independence units | Output and rules |
|---|---:|---|---|
| `window-median` | 1 | window, capture, process run, allocation | Median of finite input values. For an even count, average the two central values. If source events are first summarized into windows, the method count is windows, not events. |
| `paired-delta` | 1 complete pair | capture, process run, allocation | Baseline estimate, candidate estimate, absolute delta, and relative delta when baseline is non-zero. Pair identity and order are retained. One pair permits a descriptive point delta, not a defensible uncertainty interval. |
| `median-absolute-deviation` | 2 | window, capture, process run, allocation | Raw, unscaled `median(abs(x - median(x)))` in the quantity's unit. A normalized or consistency-scaled variant would require parameters or a new version. |
| `wilson-score-rate` | denominator 1 | capture, process run, allocation | Binary rate and Wilson score interval at an explicit confidence level. `0 <= positiveCount <= observationCount`. A zero denominator is insufficient. Event-level binary trials require a separately demonstrated independence claim. |
| `theil-sen-slope` | 2 finite values with distinct x | window, capture, process run, allocation | Median of all finite pairwise slopes with distinct x coordinates. x unit, ordering, and spacing are explicit. No causal or forecast claim. |
| `percentile-bootstrap-interval` | 2 | window, capture, process run, allocation | Percentile interval over the parent estimator. Resample whole independence units (or whole pairs), declare confidence level and resample count, and derive the deterministic PRNG seed from method version, compatibility identity, ordered input digest, and scenario protocol. |

These are mathematical minima, not sufficient-evidence thresholds. Scenario issues
may require more evidence but may not silently require less.

### 6.1 Deterministic bootstrap requirements

The interval is reproducible for identical canonical inputs:

- the resampling population is the declared independence unit;
- paired inputs remain paired;
- the canonical input order and digest are recorded;
- the seed derivation and PRNG algorithm are versioned;
- the resample count and confidence level are method parameters;
- the quantile rule is versioned;
- failed/nonfinite resamples are counted; any policy for tolerating them is
  scenario-declared before execution.

No global resample count or confidence level is approved here.

## 7. Assumptions and limitations

Every available result names assumptions required for interpretation, including as
applicable:

- independence or exchangeability of the declared unit;
- stationarity within a bounded window;
- stable workload and capture settings across compatible arms;
- representative sampling/retention;
- correct pairing and no carry-over between arms;
- monotonic and meaningfully spaced x coordinates for slope;
- binomial-trial interpretation for Wilson rates.

Every observed boundary is a limitation, including:

- EventPipe or transport loss;
- collector eviction, reservoir sampling, top-N projection, or truncation;
- incomplete pairing, missing windows, failed/aborted runs, or excluded nonfinite data;
- legacy or unknown identity dimensions;
- backend-specific observability gaps;
- a confidence interval that covers only resampling variability and not systematic bias.

Failed and aborted runs remain in protocol denominators. A retry is a new recorded run,
not a replacement that erases variability.

## 8. Existing implementation inventory and reuse boundary

### 8.1 Core

Core has a small internal bounded percentile mechanism, not a general statistical
engine:

- `BoundedPercentileSampler` keeps the first 4,096 values exactly, then uses a
  fixed-size reservoir with deterministic `Random(0)` replacement. It reports total
  and retained counts plus `IsApproximate`.
- `BoundedDurationSampler` clamps negative durations, delegates percentiles to that
  sampler, and tracks an exact maximum.
- contention, Kestrel, networking, and database aggregation use these helpers for
  bounded p50/p95-style summaries. Networking exposes retained percentile sample
  counts; collectors add approximation notes after the exact cap.
- `SnapshotDiffer` separately computes median, population standard deviation, and
  coefficient of variation for existing journey dispersion heuristics. Those
  heuristics and their verdict thresholds are not registry methods.

**Decision:** reuse the bounded samplers only when a scenario needs their existing
streaming percentile behavior and explicitly admits reservoir approximation. A
reservoir element is not an independence unit. Do not use the sampler as a bootstrap
population, and do not treat its deterministic PRNG as the bootstrap PRNG contract.

### 8.2 BenchmarkDotNet

The BenchmarkDotNet project already references Core; Core must never reference
BenchmarkDotNet. Existing regression/calibration code provides prior art:

- compatible same-VM pairs, alternating pair order, medians, percent deltas, sample
  coefficient of variation, and threshold agreement;
- exact-compatible calibration groups across independent runner allocations/days;
- Wilson 95% intervals for detection and false-positive rates;
- within-cohort, cross-allocation, and cross-day coefficient-of-variation reporting;
- explicit advisory/gate/verdict policy layered above immutable evidence.

Important incompatibilities with this foundation remain:

- BenchmarkDotNet's zero-denominator rate helper returns numeric zero while its Wilson
  helper returns `[0, 100]`; the new evidence contract instead makes a zero denominator
  unavailable.
- coefficient of variation is not the registered robust dispersion method.
- BenchmarkDotNet thresholds, regression/improvement verdicts, calibration decisions,
  and rounding are policy-specific and do not enter the shared vocabulary.

**Decision:** reuse definitions, compatibility lessons, and deterministic test vectors,
not production classes or verdict code. A future Core implementation may independently
implement a registered formula. BenchmarkDotNet adoption would be a separate reviewed
change and must preserve its current artifacts and verdicts unless an explicit issue
authorizes otherwise.

## 9. Surface ownership

| Surface | Ownership |
|---|---|
| **Core** | Eventual production home for collection-independent calculations, compatibility checks, method IDs/versions, and scenario-specific evidence records. This foundation adds no public Core record. A shared public record is considered only after at least two vertical scenarios prove the same fields useful. |
| **ScenarioEvaluation** | May host validation-only implementations and fixtures before product GO. These are not product APIs and must use the registered identities and independence rules. |
| **CLI human** | Renders the Core result without recomputing it. It names availability and reasons; unavailable values render as “unavailable”, never `0`, “no change”, or “not significant”. |
| **CLI JSON / JSONL** | Serializes the Core result with stable machine names. Additive optional fields follow protocol compatibility rules. The CLI remains Core-only. |
| **MCP** | Reuses existing tools and their result envelopes. No new statistical MCP tool is authorized. Exact placement is chosen by the scenario issue and measured against `tools/list` before merge. |
| **VS Code** | Consumes CLI JSONL only, does not call MCP, and does not independently calculate thresholds, intervals, or verdicts. |
| **BenchmarkDotNet** | Remains a separate product surface and prior-art boundary. No behavior change is made by this contract. |

## 10. Versioning and old artifacts

Three versions remain separate:

1. the evidence-envelope schema major version;
2. the method ID and method version;
3. the scenario protocol/version that selected settings and sufficiency thresholds.

Within one envelope major version, new optional fields may be added only when their
absence preserves the old meaning. A semantic formula change requires a method-version
bump even if the JSON shape is unchanged. A breaking field/availability interpretation
requires an envelope-major bump.

Old-artifact rules:

- no statistical block means statistical evidence was not recorded;
- a missing value remains unavailable and is never backfilled from a current process,
  current defaults, or another artifact;
- a block missing required schema, method, identity, or independence metadata is
  represented by a derived reader view as `incompatible` with
  `legacy_contract_missing`; the sealed source artifact is not rewritten;
- unknown method IDs/versions and unknown required envelope majors preserve raw bytes
  where applicable but do not produce calculations;
- documented unknown optional fields in a compatible major may be ignored by an older
  reader; required unknown fields fail explicitly;
- readers never reinterpret an old method version using a newer formula;
- re-rendering may change presentation, not stored evidence or availability.

## 11. Exact examples

These examples show semantics, not an approved public DTO.

### Available positive delta

```json
{
  "availability": "available",
  "method": { "id": "paired-delta", "version": 1 },
  "independenceUnit": "process_run",
  "counts": { "events": 30000, "windows": 30, "captures": 6, "repetitions": 3, "independenceUnits": 3 },
  "estimate": 12.5,
  "delta": { "baselineEstimate": 10.0, "candidateEstimate": 12.5, "absolute": 2.5, "relative": 0.25 },
  "limitations": ["The positive delta is objective evidence, not a regression or causal verdict."]
}
```

### Insufficient observations

```json
{
  "availability": "insufficient_observations",
  "method": { "id": "median-absolute-deviation", "version": 1 },
  "independenceUnit": "process_run",
  "counts": { "independenceUnits": 1 },
  "estimate": null,
  "unavailableFields": [
    { "field": "dispersion", "reason": "minimum_independence_units_not_met" }
  ]
}
```

### Incompatible evidence

```json
{
  "availability": "incompatible",
  "method": { "id": "paired-delta", "version": 1 },
  "estimate": null,
  "unavailableFields": [
    { "field": "delta", "reason": "capture_settings_mismatch" }
  ],
  "limitations": ["baseline samplingPeriod=1ms; candidate samplingPeriod=10ms; observations were not pooled"]
}
```

### Unsupported approximation

```json
{
  "availability": "unsupported_approximation",
  "method": { "id": "percentile-bootstrap-interval", "version": 1 },
  "estimate": null,
  "unavailableFields": [
    { "field": "interval", "reason": "reservoir_approximation_not_admitted" }
  ],
  "limitations": ["Only a 4096-value reservoir remains; the scenario did not admit bootstrap over that representation."]
}
```

### Detected loss

```json
{
  "availability": "loss_detected",
  "method": { "id": "window-median", "version": 1 },
  "counts": { "lostOrDropped": 17 },
  "estimate": null,
  "unavailableFields": [
    { "field": "estimate", "reason": "source_loss_detected" }
  ]
}
```

### Not applicable

```json
{
  "availability": "not_applicable",
  "method": { "id": "paired-delta", "version": 1 },
  "estimate": null,
  "unavailableFields": [
    { "field": "delta", "reason": "baseline_arm_not_present" }
  ]
}
```

## 12. No-verdict language

Shared Core/CLI/MCP/VS Code evidence may say:

- “candidate median is 2.5 ms above baseline”;
- “the 95% bootstrap interval is [1.1, 3.8] ms”;
- “the interval excludes zero under the declared resampling assumptions”;
- “evidence is incompatible because capture settings differ”;
- “the slope is unavailable because only one distinct time point remains”.

It must not say:

- statistically significant / insignificant;
- regression / improvement;
- leak / no leak;
- starvation / healthy ThreadPool;
- contention caused the latency;
- pass / fail, anomalous / normal, or safe / unsafe.

Scenario-specific human guidance may explain a next diagnostic action, but it must
attribute that action to the scenario protocol rather than turn the statistical
record into a global verdict.

## 13. Schema and payload cost

`ToolCatalogBudgetTests` measures the real shipping catalog and a synthetic,
non-shipping representative evidence record.

On 2026-10-31 with the repository's pinned SDK/serializer and the first approved
scenario vertical slice (#1323) present:

| Measurement | UTF-8 bytes |
|---|---:|
| Current maximal 17-tool catalog | 286,320 |
| Current default 13-tool catalog | 253,152 |
| Representative evidence JSON schema | 3,493 |
| Prospective maximal catalog when the typed record is added to `collect_events`, `collect_sample`, and `compare_to_baseline` | 296,868 |
| Prospective default catalog with the same three existing tools | 263,700 |
| Catalog increase, maximal and default | 10,548 |
| Representative `available` payload | 1,058 |
| Representative unavailable payload range | 1,006–1,047 |

Tool counts remain 17/13. The prospective maximal catalog exceeds the current
287,000-byte ceiling by 9,868 bytes. This placement is deliberately representative,
not approved architecture, but it proves that result DTOs are not free: one 3,493-byte
record repeated in three generated output schemas costs 10,548 catalog bytes.

Therefore:

- this issue does not extract a shared public record;
- each scenario GO must measure its exact existing-tool placement and representative
  payloads before approving product fields;
- a shared record after two vertical slices requires a deliberate catalog-budget
  decision or a proven reusable-schema mechanism, not an assumption that shared
  result types cost zero;
- opaque/untyped MCP data is not an automatic workaround when clients need stable
  evidence semantics.

The executable command is:

```bash
dotnet test tests/DotnetDiagnostics.Mcp.IntegrationTests/ \
  -c Release \
  --filter FullyQualifiedName~ToolCatalogBudgetTests \
  --logger "console;verbosity=detailed"
```

## 14. Deferred to scenario issues

This foundation intentionally does not decide:

- which scenario uses which method;
- practical minimum repetition/window/capture counts;
- capture duration, window length, warm-up, spacing, or pairing order;
- confidence level, bootstrap resample count, or tolerated failed resamples;
- scenario thresholds, effect-size methods, or decision boundaries;
- whether a specific loss or reservoir/top-N approximation remains admissible;
- the exact Core record, CLI command/JSON property, MCP host tool, or VS Code presentation;
- whether BenchmarkDotNet should ever consume a shared implementation.

Those decisions require scenario evidence. A scenario may close NO-GO with preserved
evidence; this contract does not require product implementation.
