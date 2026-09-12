# Bounded known-graph gcdump validation

Issue [#928](https://github.com/pedrosakuma/dotnet-diagnostics/issues/928) asks what the
EventPipe gcdump path can empirically establish when the target contains an independently
known object graph. This is test-only validation, not a production graph analyzer and not a
target-instrumentation requirement for normal gcdump use.

## Predeclared protocol and budgets

These limits were committed to the experiment design before running the trials:

- one owned `CoreClrSample` process per test, killed with its process tree during bounded cleanup;
- three fixture object types: exactly 2 roots, 4 branches, and 8 leaves, with 4 root-to-branch
  and 8 branch-to-leaf references; roots are maintained by two private static fields;
- raw-oracle insertion budgets: 250,000 nodes, 500,000 object edges, 100,000 root edges,
  and 4,096 type rows; reaching any budget makes the structural result explicitly inconclusive;
- normal and projection trials: at most 30 seconds each; timeout trial: 100 milliseconds;
- cancellation trial: a pre-cancelled token with a 5-second collector timeout and a
  15-second test guard; the owned target must remain responsive and publish no trace;
- retained-heap pressure trial: synchronously prepare and retain at most 100,000 pressure
  objects with at most 1,024 payload bytes each before capture; the exercised cell uses
  80,000 objects with 512-byte payloads (about 39 MiB payload), records readiness and
  capture timestamps, and requires every pressure object plus its payload edge in the raw graph;
- one normal-control repetition in the required test path and up to three opt-in empirical
  repetitions; no hidden retry changes a failed or inconclusive trial into a pass;
- no committed `.nettrace`, dump, or gcdump binary. A concise UTF-8 JSON report is allowed;
  raw artifacts, if manually requested while debugging, remain outside git.

The fixture's unique sealed classes separate its nodes from framework objects. Assertions use
only type identity and declared references, never incidental object addresses, object sizes,
array layout, framework counts, or traversal order.

## Quality policy

The raw reader is a bounded test oracle. A deterministic normal control passes only when all
14 uniquely typed fixture nodes are present, the 12 declared fixture references can be
reconstructed from raw node/edge events, and both maintained root objects are reachable from
raw root events. Matching production type totals alone is not graph integrity. Root kinds are
reported separately; direct root-to-object identity is not required because the runtime may
insert an implementation-owned holder between a static field and its target. The test does not
infer root categories absent from the stream or generalize this fixture to every runtime root
mechanism.

Production `SnapshotTopTypes` is output projection, not collector retention. The production
collector has no node-retention cap, so this experiment does not invent one. EventPipe loss is
currently unobservable in the product contract; absence of a detected loss signal is never
reported as proof of a complete graph. Timeout, reader failure, projection omission, oracle
budget exhaustion, and fixture/mechanism gaps remain separate outcomes.

The pressure trial is a controlled retained-heap cell, not an allocator-throughput claim.
Pressure readiness must precede the collector call, the same retained set must remain present
after it, and the raw stream must contain exactly the prepared pressure nodes and payload
references. If readiness is not established, the harness emits an explicit `inconclusive`
record and fails rather than reporting a tested stress success. Raw GC-start, first-node,
last-node, and GC-stop relative timestamps distinguish the graph-dump interval from the
collector's wider type-table-flush/session lifetime. A missing prerequisite, unrecognized
runtime event shape, timeout, cancellation, or exhausted oracle budget is likewise an explicit
inconclusive/aborted result, never a success-shaped no-op.

## Results

Executed 2026-09-12 from base commit
`1a46917f28fa973936f788872e4c36f35da0219c` with SDK 10.0.201, .NET runtime
10.0.12, Linux `6.18.33.2-microsoft-standard-WSL2` x86-64, TraceEvent 3.2.2,
and `Microsoft.Diagnostics.NETCore.Client` 0.2.661903. The working-tree test
changes were intentionally uncommitted, so the base hash plus this diff identifies the code
that ran.

The refined run passed all 11 test cases: five live trials plus six deterministic timeout
classification cases. Its log is retained outside git at
`artifacts/issue-928/known-graph-refined-sdk-10.0.201.log`
(SHA-256 `d77138ad3f0a66e0982b4c1ff7a5e8e260ddef05903460c133b68bea0d204da2`).
Every exported `.nettrace` was deleted by the owning test; no trace, dump, or gcdump binary
remains.

| Trial | Product result | Raw bounded oracle | GC collection delta | Honest interpretation |
|---|---|---|---|---|
| normal control, `SnapshotTopTypes=4096`, 30s timeout | 1,326,154 bytes; 251.1084 ms; GC stop, stream completion, and trace export observed; 1,741 type rows retained | 16,929 nodes, 29,300 edges, 239 non-weak root edges, 2,177 type-table rows; fixture 2/4/8 nodes, 4 root→branch edges, 8 branch→leaf edges; both roots reachable from `Handle` roots; graph window 0.3805–14.422103 ms with nodes 5.683801–14.268003 ms; no oracle budget hit | Gen0 +1, Gen1 +1, Gen2 +1 | The 14-node, 12-reference fixture subgraph matched exactly. Separately, two fixture roots were observed reachable from emitted GC-root records. Production aggregates reported 2 roots/80 bytes, 4 branches/160 bytes, and 8 leaves/192 bytes. EventPipe loss remains unobservable. |
| projection-limited, `SnapshotTopTypes=1`, 30s timeout | 1,319,074 bytes; 301.2213 ms; one row in each product ranking; fixture types absent; `OutputProjection(snapshot-top-types, affected=1741)` | 16,916 nodes, 29,276 edges, 220 non-weak root edges, 2,178 type-table rows; exact 2/4/8 fixture nodes and 4/8 fixture edges; two fixture roots reachable from GC-root records; graph window 0.3701–10.886602 ms with nodes 4.640401–10.757102 ms; no oracle budget hit | Gen0 +1, Gen1 +1, Gen2 +1 | Output omission did not alter the captured raw fixture subgraph. This is projection, not collector eviction. |
| 100 ms timeout | no snapshot; no trace published | not run because collection aborted during the type-table flush | reset only | At 103.3412 ms the collector threw `IOException` with direct inner `SocketException(ConnectionAborted)`. The test accepts an I/O abort only when the elapsed timeout budget has been reached, the direct socket subtype is `ConnectionAborted`/`OperationAborted`, and the abort arrives within a 5-second shutdown grace. Unrelated, early, reset, or late I/O failures fail visibly. |
| retained-heap pressure, 80,000 × 512-byte payload objects, 30s timeout | 47,400,622 bytes; 301.356 ms; clean GC stop/stream/export; exact fixture and pressure aggregates retained | 176,935 nodes, 189,316 edges, 224 non-weak root edges, 2,179 type-table rows; exact 80,000 pressure nodes and 80,000 pressure→payload edges; exact 2/4/8 fixture nodes and 4/8 fixture edges; both fixture roots reachable; graph window 0.253601–46.34341 ms with nodes 14.369803–46.22121 ms; no oracle budget hit | capture only: Gen0 +1, Gen1 +1, Gen2 +1; preparation separately: +4/+2/+1 | Pressure readiness was recorded at 13:58:47.2279041Z, before the collector call at 13:58:47.2298365Z; the same retained set remained ready after completion at 13:58:47.542408Z. Exact raw and product pressure counts establish that this capture actually traversed the bounded pressure graph. This does not prove losslessness for other heaps or event volumes. |
| pre-cancelled token, 5s collector timeout | cancelled before a snapshot or trace was published | not run | reset only | `OperationCanceledException` was required; the owned sample remained responsive with both fixture roots present, and no partial trace file remained. Log: `artifacts/issue-928/cancellation-sdk-10.0.201.log` (SHA-256 `980f4d4de3800fc0fe71661833edc15f1fc1935ca9e36abab41affaecef7dddc`). |

The root stream did not point directly at either fixture object; both were reachable from
runtime `Handle` roots through captured edges. The oracle therefore verifies reachability, not
an assumed static-field event layout. It excludes weak-root records. Categories not emitted by
this runtime and any EventPipe events lost before parsing remain unverifiable.

### Commands and regression result

The SDK was selected through
`artifacts/issue-928/sdk-10.0.201/global.json` (ignored by git) with exact
`10.0.201`/`rollForward=disable`. Dependencies were already present, so the refinement did not
install or restore packages.

```text
dotnet build ../../../tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj \
  -c Release --no-restore
dotnet test ../../../tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj \
  -c Release --no-build --filter FullyQualifiedName~GcDumpKnownGraphTests
# 11 passed (5 live trials, 6 timeout-classifier cases)

dotnet test ../../../tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj \
  -c Release --no-build \
  --filter 'FullyQualifiedName~GcDumpKnownGraphTests|FullyQualifiedName~GcDumpTypeAggregatorTests|FullyQualifiedName~GcDumpCollector_EmitsTopTypes_OverEventPipe|FullyQualifiedName~GcDumpNativeAotGuardTests|FullyQualifiedName~LiveNativeAotGcDumpCapabilityTests'
# 24 passed
```

The combined validation log is
`artifacts/issue-928/targeted-validation-refined-sdk-10.0.201.log`
(SHA-256 `dc247d7b2ec09a028f6e00e74ace6a643ad333d7f807b1e262df4e7ac491969d`);
24 tests passed.

### Preserved failed development trials

No trial was silently retried into success. Disposable logs outside git preserve the harness
failures that led to the final bounded oracle:

- `known-graph-tests.log` (`d4d0a926...`): four failures; the first edge correlator incorrectly
  assumed node and edge blocks were interleaved.
- `known-graph-tests-run2.log` (`467aa842...`): two graph failures after flattening blocks, because
  the fixture used intermediate arrays; the timeout abort passed and pressure completion raced.
- `known-graph-tests-final.log` (`60e07561...`): direct-root assertions failed; the runtime emitted
  reachable `Handle` roots rather than direct root-to-fixture records.
- `known-graph-tests-final2.log` (`818515d0...`) and
  `known-graph-tests-evidence.log` (`df9d6b62...`): successful SDK 10.0.401 development runs,
  superseded by the required SDK 10.0.201 evidence above.

One earlier timeout-only SDK 10.0.201 diagnostic invocation was not redirected to an artifact. It
returned a structured timeout snapshot instead of throwing: 0 bytes, 136.1994 ms,
`TimedOut=true`, no GC stop, incomplete stream, no published trace, and
`RetainedExplicitPositiveEvidence=NotEstablished`. The immediately following preserved
timeout-only run aborted with `IOException` again. This variability is why the durable test
accepts either the structured timeout result or the narrowly classified, budget-associated
socket abort and never treats either as a complete capture. Deterministic classifier cases
reject an unrelated `IOException`, `ConnectionReset`, an abort before the budget, and an abort
outside the bounded shutdown grace.

The implementation/debugging sequence ran more live repetitions than the predeclared
one-required-plus-three-optional budget. That is a protocol deviation, preserved here rather
than omitted: three extra runs were used to correct edge-block ordering, remove fixture-array
indirection, and replace an invalid direct-root assumption. Independent review then required
replacing the ineffective delayed allocation wave and broad I/O catch, so the table above uses
the first complete refined SDK 10.0.201 five-live-trial run. The combined rerun is a regression
check, not another sample folded into an average.

These failures changed the test oracle and fixture assumptions, not production collection
behavior. Unresolved limits are deliberate: the product retains histograms only, lost-event
count remains unobservable, root-category completeness is not proved, and this single
CoreCLR/Linux run is not a runtime/OS matrix.
