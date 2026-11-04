# Allocation throughput-normalization falsifier v1

Issue: [#1390](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1390)

Parents:

- [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)
- [#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

Preserved prerequisite:

- [#1389](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1389),
  squash-merged as `56c2ef4a9eb296beb1f64d33b8f47ccf693250a3`
- [#1386](https://github.com/pedrosakuma/dotnet-diagnostics/pull/1386),
  squash-merged as `6777e0a05ca68a5a55e1d6191860ff36012db450`

## Decision

**INCONCLUSIVE.**

The focused protocol retained a complete valid one-shot execution:

- 15 registered pairs and 30 independently started real-MCP stdio sides;
- 30/30 sides passed;
- 60 ledger v2 rows retained exactly one start and one terminal row per side;
- every attempt, process, and capture identity was unique;
- maximum observed allocation events were `53,694`, below the predeclared
  `75,000` operating limit and unchanged Core caps;
- no retry, replacement, deletion, or reinterpretation occurred; and
- no blinded evaluation ran.

The healthy unchanged-rate `primary` control passed with a robust A/A envelope
of approximately `0.406%`.

The registered throughput cohorts did not produce the uniform terminal evidence
required for either NO-GO or normalization-survives:

| Cohort | Pacing-only manipulation | Throughput median | A/A envelope | Equivalence margin | Effect inlier median absolute bytes/op delta | Result |
|---|---|---:|---:|---:|---:|---|
| `primary-every-operation` | `PrimaryOperation`, 1 ms after every operation → unpaced | `58.315x` | `10.453%` | `10.453%` | `31.415%` | Inassessable because workload-specific A/A exceeded the strict predeclared 10% sanity bound |
| `low-every-eight-operations` | `LowOperation`, 1 ms after every eight operations → unpaced | `266.234x` | `5.114%` | `10.000%` | `10.178%` | Assessable; normalization failed because the inlier median strictly exceeded the margin |

NO-GO required every registered throughput cohort to be assessable and to have
an inlier median absolute estimated-sampled-bytes/operation delta strictly above
its own `max(robust A/A, 10%)` margin. The primary cohort was not assessable.

Normalization-survives required every complete cohort to remain inclusively
within its margin. The low-allocation cohort exceeded its margin.

The predeclared precedence therefore yields **INCONCLUSIVE**. The complete
inventory is not reclassified as NO-GO from the single assessable failure, and
the failed normalization cohort is not hidden by the inassessable cohort.

## Frozen protocol

The separately versioned protocol uses:

- protocol ID `allocation-throughput-normalization-falsifier`;
- version `1.0.0`;
- source base
  `56c2ef4a9eb296beb1f64d33b8f47ccf693250a3`;
- one-second capture, 200 ms arm delay, and 700 ms workload;
- one worker and equal requested payload, operation code path, retained bytes,
  capture geometry, and logical-operation counting within every pair;
- three A/A and three effect pairs per pacing cohort;
- three healthy unchanged-rate pairs;
- pair-first median/raw MAD summaries with retained modified-z outliers;
- minimum three valid pairs and two inliers;
- throughput manipulation strictly above `1.25x`; and
- equivalence
  `max(workload-specific robust A/A inlier envelope, 10%)`.

The protocol freezes source, runtime, and every prior allocation-directional
artifact hash. All #1389 and earlier protocols, fixtures, ledgers, reports, and
decisions remain unchanged.

## Equal-allocation construction

Each effect pair changed pacing only:

- `primary-paced` and `primary` execute `PrimaryOperation`, request
  `288,000` payload bytes per operation, use one worker, and differ only by a
  one-millisecond pause after every operation;
- `low-bounded-v4` and `low` execute `LowOperation`, request `64,000` payload
  bytes per operation, use one worker, and differ only by a one-millisecond
  pause after every eight operations.

The fixture validated the returned requested payload, worker count, capture
duration, workload identity, operation denominator, retained value, attempt
provenance, process identity, and capture identity. The frozen source hashes
bind the reviewed shape names to their operation and pacing implementations.

## Selector interpretation

The decision quantity is estimated sampled allocation bytes divided by
completed logical operations. Operations per second independently proves the
manipulation. Estimated sampled bytes per second and allocation-event counts
remain descriptive provenance only.

The selector reports the all-pair median and raw MAD, retains outlier indexes,
and computes the terminal failure predicate from the retained-inlier median.
This prevents an excluded outlier from moving a cohort across the NO-GO
boundary. Exact equivalence boundaries pass; the throughput and NO-GO
boundaries are strict.

## Limitations

`GCAllocationTick` estimated bytes are fixed-stride sampled weights. This study
tests only whether the normalized sampled quantity is stable under the two
registered pacing manipulations. It does not establish exact allocated bytes,
retained memory, object counts, severity, health, regression, or causality.

The primary cohort's A/A envelope exceeded the strict sanity boundary by
approximately `0.453` percentage points. The protocol forbids threshold tuning,
retry, replacement, or a favorable reinterpretation after execution.

This result authorizes no Core, CLI, MCP, VS Code, collector, retention,
formula, schema, tool, catalog, or product implementation. No downstream design
issue is created.

## Frozen artifact fingerprints

Portable hashes use UTF-8 without BOM and canonical CRLF line endings.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| Protocol | 33,151 | `899c834399cd26b1068a867d2f48be569fdf586f21bb900eb75d8d6571630df5` |
| Fixture | 129,880 | `601d3c5513b5929000546d220d6d15d6677ea4c02a5984f72ba661bb37fc91cb` |
| Ledger | 20,576 | `578f61cb20fce63634852a0ea6a1a48edc2640d1dfdfd5b4d641a6f587818164` |
| Report | 5,301 | `131f3ceaf101af5cebaf969c2c9a8cc0df998688b5c3d5ab8eb16ec57e52ae77` |
