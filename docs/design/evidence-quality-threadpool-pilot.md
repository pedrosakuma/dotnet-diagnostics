# Evidence quality inventory and ThreadPool pilot

Issue [#926](https://github.com/pedrosakuma/dotnet-diagnostics/issues/926) introduces a
small reusable evidence-quality contract and applies it only to the EventPipe ThreadPool
collector and its existing consumers. The gcdump collector is inventoried here because it
is the next intended adopter, but gcdump propagation remains issue #927.

## Existing signal inventory

### ThreadPool

Before this pilot, `ThreadPoolEventSnapshot` already exposed:

- provenance on adjustment reasons and worker/IOCP counts (`runtime-observed`,
  `runtime-unrecognized`, `missing`, `carried-forward`, `inferred-from-delta`, and
  `inferred-from-neighbor`);
- a persisted `ThreadPoolEvidenceSummary`, including nullable compatibility semantics for
  legacy captures;
- notes for the three independent 4,096-row ring-buffer evictions;
- notes when adjustment counts were inferred from neighboring worker observations;
- a no-hill-climbing-event capture-window warning;
- warnings when effective settings or work-item-origin call stacks were unavailable.

The EventPipe parser also exposes `EventsLost`, which is the only transport-loss count this
pilot reports. A parser exception is a detected processing failure and leaves any already
retained evidence explicitly partial. The collector does not infer loss from low event
volume, missing event kinds, or an empty window.

An unavailable lost-event count remains unobservable, not zero. A no-event window
does not identify its cause: startup or workload sequencing are possibilities, not
observed startup limitations, so no `Startup` category is emitted without timing
evidence. Even a long, steady-state capture can contain no adjustment events.

The summary-depth response removed timelines and hill-climbing rows, and ThreadPool query
views applied `topN`, but those response-only omissions previously had no structured
representation. Snapshot notes appeared only in the summary query view. Comparable
snapshots discarded all notes, and legacy comparable JSON therefore had no way to
distinguish unknown quality from a clean capture.

### gcdump

The current EventPipe gcdump path exposes:

- a timeout flag, rendered as a warning that the snapshot may be incomplete;
- a no-node warning, which may mean the runtime did not begin streaming before timeout;
- trace-export completion separately from heap-data completion;
- fixed mechanism gaps: no GC handles, static fields, delegate targets, finalizable types,
  or segment layout;
- `TopTypesByBytes` and `TopTypesByInstances` projections bounded by requested top-N.

Its parser propagates processing errors and drains the session tail after the induced GC
stop. It does **not** currently persist a reliable EventPipe lost-event count, distinguish
collector retention from top-N projection in structured metadata, or prove graph
completeness from the GC stop alone. Those are unknown/unavailable signals, not zero loss.
Issue #927 will map these existing facts to the contract; this pilot does not change gcdump.

`InvestigationSummaryExporter` supports its documented memory-summary inputs, not EventPipe
ThreadPool or gcdump heap artifacts. This work does not add unsupported export artifact
kinds merely to claim propagation parity.

## Minimal reusable contract

`EvidenceQuality` is a bounded list with at most one item per stable
category/scope pair. Categories distinguish:

- detected transport loss;
- processing failure;
- collector eviction;
- response/top-N projection;
- inferred values;
- capture-window and startup limitations;
- unavailable and unobservable mechanisms;
- legacy unknown metadata.

Each limitation has a stable category, scope, optional affected count, and bounded
explanation. It is deliberately not a completeness score or boolean.

`EvidenceConclusionPolicy` separately describes support for:

1. retained explicit positive evidence;
2. absence or exhaustive-count claims;
3. regression or healthy-control claims.

A retained runtime-observed starvation or cooperative-blocking adjustment remains useful
positive evidence even when earlier detail was evicted or transport loss was detected.
Absence, exhaustive counts, regression, improvement, and healthy-control conclusions
require an adequate capture. No-event windows, parser failure, detected or unobservable EventPipe loss,
hill-climbing eviction, incomplete reason provenance, and legacy input therefore make
those conclusions inconclusive.

## Propagation and compatibility

- Full ThreadPool artifacts persist `Quality`; summary trimming adds an
  `OutputProjection` limitation without deleting `Evidence`.
- Every ThreadPool query view carries quality. Ranked views add their own projection count;
  timeline views retain collector quality unchanged.
- MCP full journey diffs retain per-capture quality. Compact diffs include the same bounded
  quality array.
- CLI human summaries state when absence/healthy-control conclusions are inconclusive.
  JSON and session-query serialization preserve the structured object. CLI note
  sanitization changes only free text and cannot remove structured limitations.
- Comparable save/read preserves per-capture quality. Missing quality in legacy JSON maps
  to `LegacyUnknown`; ThreadPool comparison verdicts and directional labels become
  `inconclusive`/`n/a` instead of reporting improvement, regression, uniformity, or a
  healthy control.
- BenchmarkDotNet attribution keeps retained explicit positive causal evidence, emits
  worker-increase measurements only when both operands are runtime-observed, and labels
  legacy/degraded absence assessments inconclusive.

Operational safety, authorization, runtime gates, collector limits, the Core-only CLI
boundary, and the MCP tool surface are unchanged.
