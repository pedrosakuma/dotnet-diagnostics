# Allocation throughput-normalization falsifier v2

Issue: [#1393](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1393)

Parents:

- [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348)
- [#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

Preserved prerequisite:

- [#1390](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1390),
  implemented by [#1392](https://github.com/pedrosakuma/dotnet-diagnostics/pull/1392)
  and squash-merged as `5a46a6168a12db5f2bf6e2caed735b1206a7bb34`

## Decision

**NO-GO.**

The powered protocol retained a complete valid one-shot execution:

- 25 registered pairs and 50 independently started real-MCP stdio sides;
- 50/50 sides passed;
- 100 ledger v2 rows retained exactly one start and one completion per side;
- every attempt, process, and capture identity was unique;
- maximum observed allocation events were `53,109`, below the predeclared
  `75,000` operating limit and unchanged Core caps;
- no retry, replacement, deletion, or reinterpretation occurred; and
- no bootstrap, confidence-interval selector, or blinded evaluation ran.

The healthy unchanged-rate `primary` control passed with five valid pairs,
five retained inliers, and a robust A/A envelope of approximately `2.179%`.

Both registered throughput cohorts were complete, assessable, established the
strictly-greater-than-`1.25x` manipulation, and had retained-inlier median
absolute estimated-sampled-bytes/operation deltas strictly above their
predeclared `max(robust A/A, 10%)` equivalence margins:

| Cohort | Pacing-only manipulation | A/A retained inliers | Effect retained inliers | Throughput median | A/A envelope | Equivalence margin | Effect inlier median absolute bytes/op delta | Result |
|---|---|---:|---:|---:|---:|---:|---:|---|
| `primary-every-operation` | `PrimaryOperation`, 1 ms after every operation -> unpaced | 4/5 | 4/5 | `63.000x` | `9.859%` | `10.000%` | `32.859%` | Normalization failed |
| `low-every-eight-operations` | `LowOperation`, 1 ms after every eight operations -> unpaced | 5/5 | 5/5 | `293.409x` | `6.452%` | `10.000%` | `13.296%` | Normalization failed |

The predeclared terminal rule therefore yields **NO-GO** for estimated sampled
bytes per logical operation as a stable directional allocation quantity under
the current fixed-stride sampler.

## Frozen power increase

The separately versioned protocol changes only statistical replication from
v1:

- five independently started A/A pairs per throughput cohort;
- five independently started effect pairs per throughput cohort;
- five independently started healthy unchanged-rate control pairs;
- 25 pairs / 50 live sides total; and
- at least four retained inliers in every A/A, effect, throughput, and control
  cell.

It preserves unchanged:

- the exact 10% practical equivalence floor;
- workload-specific robust A/A envelope and `max(A/A, 10%)` rule;
- the `primary-paced -> primary` and independently paced
  `low-bounded-v4 -> low` cohorts;
- allocation operation, requested payload, code path, worker, retained value,
  capture geometry, and logical-operation-counting invariants;
- one-second capture, 200 ms arm delay, 700 ms workload, and 75,000-event
  operating limit;
- pair-first median/raw MAD summaries and the strict modified-z outlier rule;
- the strictly-greater-than-`1.25x` operations/second manipulation threshold;
  and
- the failure, abstention, and terminal-decision precedence.

Fresh v2 workload IDs, pair prefixes, attempts, processes, captures, ordering,
fingerprints, and artifacts were used. No v1 observation was reused as
decision evidence.

## Preservation and review

The protocol source base is the exact #1390 implementation merge
`5a46a6168a12db5f2bf6e2caed735b1206a7bb34`. The v2 freeze hashes:

- all reviewed v2 protocol, plan, harness, live-runner, and deterministic test
  sources;
- all v1 protocol implementation sources;
- the complete v1 protocol, fixture, ledger, and report; and
- every earlier preserved allocation-directional artifact.

An independent cross-model review completed before freeze. It confirmed the
powered selector composition and fresh identity construction, then identified
that preservation should hash v1 implementation sources without revalidating
workspace-specific rebuilt v1 runtime DLLs. The fix was applied before freeze.

## Selector interpretation

The decision quantity is estimated sampled allocation bytes divided by
completed logical operations. Operations per second independently proves the
manipulation. Estimated sampled bytes per second and allocation-event counts
remain descriptive provenance only.

The unchanged selector reports the all-pair median and raw MAD, retains
modified-z outlier indexes, and computes the terminal predicate from the
retained-inlier median. The powered wrapper requires five valid pairs and at
least four retained inliers in every cell. Exact equivalence boundaries pass;
the throughput and NO-GO boundaries remain strict.

## Limitations

`GCAllocationTick` estimated bytes are fixed-stride sampled weights. This study
tests only whether the normalized sampled quantity is stable under the two
registered pacing manipulations. It does not establish exact allocated bytes,
retained memory, object counts, severity, health, regression, or causality.

The NO-GO result does not authorize a Core, CLI, MCP, VS Code, collector,
retention, formula, schema, tool, catalog, or other product implementation.
No downstream design issue is created.

## Frozen artifact fingerprints

Portable hashes use UTF-8 without BOM and canonical CRLF line endings.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| Protocol | 35,238 | `9e6a5be34c474d7085ebe4a885adb737385274d7e6a3a662c20abc634c1f2804` |
| Fixture | 217,734 | `d4101ed984a4a5c1c495a33e624d62603e4b2a994e04f7849a55261d711bfbd9` |
| Ledger | 34,293 | `82fdbfdd38b1306d873682b6372bb26196c531c23108e8a05060517ba5033801` |
| Report | 5,746 | `4cded16413ab923a6c5c81e6ba88d68b86c2869b09db1684c8d33d51bcabecc9` |
