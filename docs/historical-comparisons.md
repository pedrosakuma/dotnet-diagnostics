# Historical comparisons

Historical comparison reads two explicit retained artifacts in **one local capture
store**. The target processes and the collecting hosts need not still exist.
Captures may have been imported independently: select the **local IDs returned by
each import mapping**, never a bundle label, source ID, path, or remote host.
This operation does not attach to a process, import a package, register a temporary
handle, or modify either input.

## Supported matrix

| Artifact | Compared quantities | Required comparability | Deliberate limits |
|---|---|---|---|
| `cpu-sample` | Per-method retained exclusive sample counts and fractions of each artifact's recorded `TotalSamples` | Explicit matching evidence v1 backend/kind: EventPipe stack frequency, Linux perf on-CPU, or Windows ETW on-CPU, compared only within the same backend | No inclusive-count, CPU-time, elapsed-time, equal-work, or scheduler-state inference. Zero/missing population has no invented denominator. Root is not a method row. Metadata identity and closed signatures distinguish methods; unresolved symbols remain qualified. |
| `heap-snapshot` | Retained type shallow byte totals and instance counts | Same live/dump origin and known architecture; unique bidirectional match using available MVID/token/module/type identity | Deduplicates overlapping byte/instance rankings with the existing heap projector. Ambiguous matches have no delta. Missing top-N rows are not zero. Bytes are not transitive retained size. Runtime/version differences remain visible, not controlled experimentally. |
| `counters` | Last retained EventCounter interval mean or interval increment; interval rate when valid metadata exists | Exact provider/name, known equal units, equal `Mean`/`Sum` aggregation | No whole-capture mean, cumulative total, arbitrary time window, histogram, or Meter comparison. Snapshots containing Meter instruments are explicitly unsupported. Rate requires a valid actual `IntervalSec` and display rate scale, never requested capture duration. The initial interval can precede attachment. |

Only whole retained snapshots are supported. Filters, row selection, rank/depth/top
options, arbitrary SQL, remote federation, unsupported artifact families, and
forcing compatibility are rejected rather than silently ignored. Two inputs may
refer to the same artifact. Missing retained snapshots are an explicit error; a
normalized record stream is not silently substituted for a snapshot.

## Result and arithmetic

The new result schema is `dotnet-diagnostics/historical-comparison/v1`, independently
versioned from capture packages and legacy comparison results. It contains:

- `left` (baseline) and `right` (candidate): selected local references, kind,
  producer provenance, claimed portable source, source artifact ID, recovery
  lineage, reported window/duration, and CPU evidence or heap runtime/origin.
- `compatibility.status`: `qualified` for retained-only comparisons, or
  `incompatible` when families, CPU evidence, heap origin/architecture, or all
  retained metric definitions conflict. Structured reasons accompany the status.
  This first matrix never claims an unqualified `comparable` result.
- `quality.left/right`: original capture quality, record-stream metadata when
  present, retained notes, and structured heap evidence quality. Unknown loss
  counts stay unknown. Imported origin/quality are evidence claims, not authority.
- `metrics`: bounded keys, unit, semantics version, population, aggregation,
  normalization, nullable left/right values, absolute/relative deltas, actual
  denominators where applicable, and an unavailable reason. Counters also retain
  side-specific units/aggregations; conflicting units have no common `unit`.

`absoluteDelta = rightValue - leftValue`. `relativeDelta` is a **ratio**, computed
as `absoluteDelta / abs(leftValue)`, not a percentage. A zero baseline preserves
the absolute delta but leaves the relative delta null (`ZeroBaseline`), including
zero→zero. Missing rows/values do not become zero. Incompatible metrics preserve
their observations but suppress deltas. Numeric range failures yield null values
or deltas; relative overflow does not discard a valid absolute delta.
Signed 64-bit counts/bytes use decimal arithmetic without conversion through
double. Counter doubles use round-trip decimal text; unsupported decimal range
is explicit. No result asserts a causal regression, improvement, or healthy
control from these quantities alone.

## CLI

```bash
dotnet-diagnostics-cli compare --capture-root ./captures \
  --baseline-capture-id aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa \
  --baseline-artifact-id bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb \
  --candidate-capture-id cccccccccccccccccccccccccccccccc \
  --candidate-artifact-id dddddddddddddddddddddddddddddddd --json
```

All four references are mandatory lowercase 32-hex local IDs. Both normal output
and `--json` return the versioned result; safety warnings remain on stderr.
`session` inherits its configured capture root for this operation. Historical
selectors cannot mix with file inputs, `--save`, `--mode`, projections, or live
target/handle options. `--explain-risk`/`--acknowledge-risk` remain available.
Legacy file-based `compare before.json after.json` behavior is unchanged.

## MCP

Use the existing `compare_to_baseline` tool on HTTP or stdio:

```json
{
  "captureComparison": {
    "baseline": {
      "captureId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "artifactId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
    },
    "candidate": {
      "captureId": "cccccccccccccccccccccccccccccccc",
      "artifactId": "dddddddddddddddddddddddddddddddd"
    }
  }
}
```

The nested object accepts only these fields (no duplicates). It cannot mix with
summary JSON, comparable snapshot bodies, a mode, or nondefault top/depth options.
The result is inside the ordinary bounded `DiagnosticResult.data` envelope.
No eighteenth tool or historical Resource/handle is created; legacy summary and
journey calls retain their existing response behavior.

Both source leases are held through computation and current authorization is
rechecked before return. MCP requires `investigation-export` plus every existing
producer/kind/view scope for **both complete capture manifests**, not only the
selected artifacts. In particular, durable heap reads retain `heap-read` and
`ptrace` authorization requirements, without performing an attachment. Scope or
owner changes cannot release a partially authorized comparison. Unavailable
inputs produce a generic error without identifying which side exists.

## Bounds and acceptance

- At most 1,000 retained rows/tree nodes per side (heap counts both input ranking
  lists), and 1 MiB serialized snapshot per side, checked before blob materialization.
- At most 2,000 output metrics and 1 MiB serialized Core result. MCP may reject a
  smaller result if its full envelope exceeds the host bound; it never truncates
  or invents a partial comparison.
- Ten-second cancellation/deadline, at most ten million examined work units.
  JSON tokens, heap matching work and SQLite VM instructions are conservatively
  charged. Seal hashing checks between 64 KiB reads; SQLite validation/reads have
  a progress handler. Cancellation waits for synchronous operations to unwind and
  releases both leases; no detached task continues reading. This is not a promise
  to forcibly interrupt a stalled kernel filesystem syscall.
- Missing snapshots, unsupported definitions, or capacity violations are explicit
  failures. Recovery/import remain separate, explicitly requested operations.

Deterministic tests use real local capture stores without a target or import
worker. `HistoricalComparisonAcceptanceTests` contains two separately gated
acceptance cases: real owned workloads compared after both processes exit, and
two independent bundle imports compared after source deletion/destination reopen.
They are skipped unless `DOTNET_DIAGNOSTICS_HISTORICAL_ACCEPTANCE=1`; the import case
also requires explicit `DOTNET_DIAGNOSTICS_IMPORT_WORKER` and
`DOTNET_DIAGNOSTICS_SQLITE_LIBRARY`. These gates do not imply an executed native
acceptance run or release qualification.

`LiveKindsPortableAcceptanceTests` adds a separate opt-in acceptance for issue
#1054's live-kind transfer gap. It starts a real child `CoreClrSample`, retains
live CPU, counter, heap, thread, and batch-composition captures, exports a
labelled `.ddcapture` bundle, imports it into an independent destination with
the native worker, deletes the source store, then queries and compares the
imported evidence after the target has exited. It is skipped unless
`DOTNET_DIAGNOSTICS_LIVE_KINDS_PORTABLE_ACCEPTANCE=1`,
`DOTNET_DIAGNOSTICS_IMPORT_WORKER`, and
`DOTNET_DIAGNOSTICS_SQLITE_LIBRARY` are all set. This new gated test has been
added for separately authorized native execution.

The workload-only acceptance has passed for the reviewed comparison revision.
The first two-import attempt did **not** pass: the first bundle imported, while
the second stopped with `WorkerObservationGap` under the unchanged strict 10 ms
observation guard. Comparison, source deletion and destination reopen therefore
did not run. Later live-kind runs exposed the same scheduler-dependent policy,
including an 11.59 ms native-monitor interval with only 28.5 microseconds of
monitor CPU. Those failures are retained adverse evidence for the former
sampled-watchdog contract, not evidence of corrupted or lost capture data.

A separately authorized single invocation of the same case then **passed** at
the unchanged reviewed source `3981c561`, with the worker and SQLite assets
from producer run `36281250444`. Both bundles imported under distinct local identities. The
comparison returned the exact expected delta after source deletion and
destination reopen, and cleanup left no worker or test host running. The result
is recorded in [#1054](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1054#issuecomment-5877773502).
Compared with the integrated candidate, the acceptance test is unchanged. In
Core, the only differences are the capture ingestion path in `CaptureWriter.cs`
and the worker's compile-time header fallback, which the producer assets already
include. The other product changes are in CLI Docker bootstrap.

The integrated candidate now uses kernel-backed address-space containment and
records monitor scheduling gaps as telemetry. A separately authorized
final-SHA invocation qualified that changed contract: both distinct bundles
imported, source deletion and independent destination reopen succeeded, and the
comparison returned the exact expected delta of `5`. Cleanup confirmed that no
worker, test host or sample process survived. The earlier failures remain
adverse evidence for the former watchdog policy; the final pass does not prove
universal losslessness or deterministic scheduling.
