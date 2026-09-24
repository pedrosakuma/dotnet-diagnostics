# Durable capture coverage: typed snapshot compatibility

## Scope and contract

`CaptureArtifactCodec` is an internal, version-1 compatibility codec in
`DotnetDiagnostics.Core.Captures`. It preserves **retained typed artifacts**, so
existing pure snapshot queries can run after restoration. It does not implement
SQLite storage, occurrence ingestion, a retention policy, capture lifecycle
integration, or an end-to-end durable capture feature. A serialized bounded DTO
is **not complete observation history**. Insertion-time loss, top-N projection,
pairing exclusions, sampling, unavailable measurements, nullable quality and
notes remain part of the evidence.

The inventory below follows `Safety/DiagnosticOperationCatalog.cs`,
`Collection/CollectionHandleKinds.cs`, actual `UseCases` registrations, and the
collection/CPU/heap/thread/off-CPU/catalog/DATAS/method-parameter dispatchers.
Operation names and handle kinds are deliberately distinct. There are 20 event
operation families and seven sample operation families. The codec allowlist has
27 entries: 17 event artifacts, seven sample artifacts, thread, heap, and the
returned-only requests-now result.

API:

```csharp
CaptureArtifactCodec.FormatVersion // 1
CaptureArtifactCodec.Encode(string kind, object artifact, int maxBytes) // byte[]
CaptureArtifactCodec.Decode(string kind, int representationVersion,
    ReadOnlySpan<byte> bytes, int maxBytes) // object
CaptureArtifactCodec.GetSupportedSnapshotViews(string kind, object artifact)
    // IReadOnlyList<string>
```

Kinds are exact, case-sensitive strings. Each is bound to one concrete CLR type.
There is no serialized CLR type discriminator, assembly loader, runtime
type-name resolver, arbitrary SQL execution, or import facility. Serialization
uses compile-time generic dispatch and an explicit source-generated metadata
context. Reflection is used only to read nullability annotations on properties
already admitted by that context, providing the same null checks on .NET 8/9/10;
it does not choose or instantiate types from input.

Encoding writes directly to a bounded buffer, not an unbounded JSON string
followed by truncation. Actual committed bytes cannot exceed `maxBytes`.
`Utf8JsonWriter` can reserve up to six bytes per UTF-16 character before knowing
the escaped length; its separate workspace is capped at `6 * maxBytes + 4096`
(also limited by the platform's maximum array length). The returned copy is at
most `maxBytes`. Traversal state and restored lists scale with this finite
representation, never the duration of the original capture. CPU rows flush as
they are written; deep trees do not build a second unbounded flat array during
encoding. The backing byte workspace is cleared on disposal.

Decoding checks the input byte bound and JSON depth (64) before constructing
DTOs. Unknown kinds, wrong concrete types, unsupported representation versions,
missing stored fields, unknown or duplicate fields, invalid reference nulls,
malformed JSON, trailing data, and oversized representations fail explicitly.
There is no truncate-and-return or default-object fallback. Required properties
include nullable properties: an explicit null preserves unknown/unavailable,
whereas an absent property is malformed for this version. Future schema changes
must account for this strict versioned format.

UTC timestamps, other original offsets, `TimeSpan` ticks, microsecond fields,
nullable values, strings, and quality metadata are not normalized into other
units. Valid Unicode, newlines, embedded NULs, delimiters, redaction markers and
JSON text strings remain data. Unpaired UTF-16 surrogates fail rather than being
silently replaced. No new redaction is performed here; upstream privacy policy
and downstream authorization/encryption remain necessary.

## Event families (20)

In the final column, **hooks needed** describes separate occurrence-ingestion
work, **not implemented by this codec slice**. Those hooks need capture identity,
ordering/timestamps where observable, finite writer/backpressure behavior,
termination/completeness metadata, and explicit loss accounting. Merely expanding
these DTOs or their top-N limits does not implement those hooks.

| Operation | Handle kind / artifact | Retained detail and snapshot queries | Hooks needed for observation history |
|---|---|---|---|
| `counters` | `counters` / `CounterSnapshot` | Latest/retained EventCounter and meter values, tags, units, interval/rate scales and notes; `summary`, `byProvider` | Every accepted counter/meter observation before latest-value replacement, with series identity and actual interval |
| `exceptions` | `exception-snapshot` / `ExceptionSnapshot` | Type counts and bounded recent exceptions; `summary`, `byType`, `recent` | Exception observations before the recent-ring cap, including exception identity and producer timestamp |
| `crash-guard` | `crash-guard-snapshot` / `CrashGuardSnapshot` | Bounded exceptions/stacks, final exception, exit/drain observation and notes; `summary`, `exceptions`, `stack` | First-chance/unhandled observations plus explicit stream completion/exit/drain facts; do not infer termination from the last first-chance event |
| `gc` | `gc-events` / `GcSummary` | Retained collection rows, generation aggregates, heap-stat samples and versioned suspension evidence; `summary`, `events`, `pauseHistogram`, `timeline`, `longestPauses`, `byGeneration`, `heap-stats` | Collection/heap-stat/suspension observations before detail caps; preserve separate collection elapsed and fully-suspended boundaries, pairing exclusions and loss |
| `datas` | `gc-datas` / `GcDatasSnapshot` | Retained sample, tuning and full-GC tuning rows plus parse stats; `overview`, `tuning`, `samples`, `gen2` | Decoded DATAS events before retention caps, including microsecond/budget units and parse/version failures |
| `catalog` | `event-catalog` / `EventCatalogSnapshot` | Provider/event/level counts and bounded sampled occurrences; `catalog`, `byProvider`, `events` | Event metadata occurrences before the sample cap; catalog counts are not payload history |
| `event_source` | `event-source` / `EventSourceCapture` | Retained custom-provider events and string payload maps; `summary`, `byEventName`, `events` | Accepted provider events before event caps, with existing payload privacy policy |
| `activities` | `activities` / `ActivityCapture` | Retained completed spans, tags, source/operation aggregates, retention and optional HTTP destination correlation; `summary`, `bySource`, `byOperation`, `activities`, `trace` | Start/stop and accepted span observations before retained-stop caps; filter/match/loss and correlation provenance must survive |
| `logs` | `log-snapshot` / `LogSnapshot` | Level/category aggregates and bounded recent structured entries; `summary`, `byCategory`, `byLevel`, `recent`, `errors` | Accepted log occurrences before recent-entry eviction, preserving privacy/redaction and optional structured values |
| `jit` | `jit-snapshot` / `JitSnapshot` | Retained per-method aggregates, tier/ReJIT/OSR information and notes; `summary`, `topMethods`, `tierDistribution`, `reJIT` | Compilation/tiering/IL-map observations before tracked-method and pending-correlation caps |
| `threadpool` | `threadpool-snapshot` / `ThreadPoolEventSnapshot` | Bounded worker/IOCP/hill-climbing timelines, origins, settings, evidence and quality; `summary`, `timeline`, `hillClimbing`, `workItemOrigins` | Runtime events before timeline-ring eviction and work-origin aggregation |
| `contention` | `contention-snapshot` / `ContentionSnapshot` | Longest retained contention events, monitor/owner/call-site facts and aggregate durations; `summary`, `byCallSite`, `byOwner` | Start/stop facts and paired contention rows before longest-event selection; distinguish unmatched/capped pairs |
| `db` | `db-snapshot` / `DbSnapshot` | Bounded command-shape, N+1 and pool aggregates with sanitized strings; `summary`, `byCommand`, `n+1`, `connectionPool` | Accepted command/pool observations before shape overflow and pending TTL/eviction; do not restore unsanitized SQL |
| `kestrel` | `kestrel-snapshot` / `KestrelSnapshot` | Counts, bounded operation/queue rows, latest counters, TLS/config and notes; `summary`, `byOperation`, `queues`, `tls`, `config` | Connection/request/TLS/counter observations before pairing, group and queue caps |
| `networking` | `networking-snapshot` / `NetworkingSnapshot` | HTTP/DNS/TLS/socket aggregates, retained operation groups/counters, correlation partitions and capture quality; `summary`, `byOperation`, `queue`, `tls`, `dns` | Accepted transport observations before aggregation; preserve identity-history saturation, duplicate/reuse exclusion, failures and latency populations |
| `requests` | `in-flight-requests` / `InFlightRequestSnapshot` | Oldest retained still-pending requests, age/threshold fields and notes; `summary`, `requests`, `longRunning` | Request starts/stops before pending-request admission caps; a final in-flight list is not a request-completion log |
| `startup` | `startup-snapshot` / `StartupSnapshot` | Bounded assembly/module/DI/timeline lists and aggregates; `summary`, `assemblies`, `modules`, `di`, `timeline` | Loader and DI events before per-list caps, with capture-window/startup exclusions |
| `sweep` | Composed `SweepResult`, not a separately registered artifact codec | Child counters/GC/exceptions/threadpool artifacts have codecs; resource snapshot, triage, child handles and partial failures belong to the composition | Link each child capture and its occurrence stream; separately retain resource/triage/failure metadata rather than inventing a unified event stream |
| `distributed_trace` | Composed `DistributedTraceTimeline`, not a separately registered artifact codec | Retained activity-derived spans with pod/source and coverage information; constituent `ActivityCapture` codecs apply | Per-target activity capture hooks plus parent-child capture/target/coverage links; timeline rows alone are not all spans |
| `replica_counters` | Composed `ReplicaCounterSkew`, not a separately registered artifact codec | Per-replica projected readings, dispersion/outlier result and warnings; underlying counter snapshots use their codec | Link per-replica counter observations and fan-out failures; preserve selection and metric projection metadata |

DATAS is **registered** as `gc-datas` at this revision, not merely returned-only.
The family list does not imply that custom providers disclose arbitrary raw
runtime payloads or that retained per-method/operation aggregates can reconstruct
their individual events.

## Sample families (7)

The CPU-tree snapshot views below mean exactly
`call-tree`, `top-methods`, `by-module`, `by-namespace`, `hot-path`,
`caller-callee`, and `triage`, as dispatched by `CpuSampleQueryDispatcher`.
No top-N-only substitution is made.

| Operation | Handle kind / actual registered type | Retained detail / snapshot queries | Hooks needed, and limits |
|---|---|---|---|
| `cpu` | `cpu-sample` / `CpuSampleTraceArtifact` | Entire retained merged tree, ordered children, counts, per-node and capture self-sample splits, method identities/instantiations, resolved sources, backend/evidence, notes and trace-path provenance; all CPU-tree views | Individual accepted stack observations before merging, including actual thread/time/backend facts when observed; EventPipe stack frequency must not become measured on-CPU time |
| `allocation` | `allocation-sample` / `AllocationSampleArtifact` | Allocation summary plus retained byte-weighted tree and identities; all CPU-tree views | Accepted allocation-tick observations before type/site/tree aggregation; sampled ticks are not every allocation |
| `off_cpu` | `off-cpu-snapshot` / `OffCpuSnapshotArtifact` | Stack/thread microsecond rollups, frame/IP identities, syscall attribution and censored-span evidence; `topStacks`, `byThread`, `stack` | Accepted scheduler spans before stack/thread folding, preserving censoring, state and syscall-correlation availability |
| `native-alloc` | `native-alloc-sample` / **`CpuSampleTraceArtifact`** | Retained native allocation call tree; all CPU-tree views | Accepted sampled allocator calls before merging; the returned `NativeAllocSampleResult` wrapper and its summary are not the registered artifact and are rejected as this kind |
| `native-lock-contention` | `native-lock-contention-sample` / **`NativeLockContentionArtifact`** | Sampled lock-call activity summary plus retained tree; all CPU-tree views | Sampled lock-call observations before merging; these are activity, not proven blocking durations. `NativeLockContentionSampleResult` is not the registered object |
| `method-params` | `method-params-capture` / `MethodParameterCaptureArtifact` | Allowlisted methods, retained invocations/typed string previews, sequence/UTC time, caps, stop reason, redaction/truncation/drop counters and notes; `summary`, `events` | Accepted profiler invocations before local admission caps under existing privileged opt-in/security policy; never synthesize values omitted or redacted upstream |
| `cpu-efficiency` | `cpu-efficiency-sample` / `CpuEfficiencySample` | Concrete aggregate perf/PMU result, nullable counts/rates and notes; no existing pure `query_snapshot` view | Persist actual aggregate measurement/provenance only. No individual samples are available to invent |

At this revision `SamplerUseCases` registers the CPU-efficiency result under
`cpu-efficiency-sample`; registration does not imply an existing query dispatcher.
`GetSupportedSnapshotViews` therefore returns an empty list for that kind.

Call trees use flat parent-index rows with iterative reconstruction. Deep stacks
are not constrained by recursive JSON nesting; every node still consumes the
finite byte budget. Cycles, invalid parents and duplicate symbol keys fail.
`SymbolRef` keys are structured `(Module, MethodFullName)` pairs, so embedded
delimiters cannot collide. Shared nodes may be represented as repeated tree
occurrences; object-reference identity is not a query contract.

## Thread, heap and returned-only snapshots

| Family | Codec / current retained detail | Self-contained queries | Native/live-only limitations and occurrence work |
|---|---|---|---|
| Thread (live or dump) | `thread-snapshot` / `ThreadSnapshotArtifact`: retained threads, ordered frames/identities/addresses, monitors, optional pool state, warnings and origin. The three lock-role flags hidden from public JSON are explicitly retained | `threads-summary`, `stack`, `lock-graph`, `deadlocks`, `top-blocked`, `unique-stacks`, `async-stalls`, `wait-chains`, `threadpool`; absent pool data remains absent | No `resolve-address` or `frame-vars` live/dump reattachment. A point-in-time walk is not a thread-transition history; separate walk observations and completeness/loss facts are needed |
| Heap (live, dump or gcdump) | `heap-snapshot` / `HeapSnapshotArtifact`: runtime/heap/type aggregates, selected derived lists, warnings, quality and gcdump completion state | Always `top-types`. For non-gcdump origins, only when the corresponding retained field is non-null: `retention-paths`, `roots-by-kind`, `finalizer-queue`, `fragmentation`, `static-fields`, `delegate-targets`, `gchandles`, `async`, `timers`, `alc` | No retained object/edge graph. `object`, `gcroot`, `objsize`, `duplicate-strings` are **never advertised as self-contained**. Retained duplicate-string summaries do not bypass the existing sensitive-value/native capability boundary. Gcdump supports only type aggregates, even if inconsistent optional lists were supplied. Object/root/edge occurrence capture would be separate bounded work |
| Requests-now | `requests-now` / `RequestsNowSnapshot`: retained HTTP rows and captured top frames, original window/timestamps and queue-loss notes | Returned-only `inspect_process(view="requests-now")` result; no existing `query_snapshot` views | Only rows with retained frames survive upstream queue admission. A codec cannot reconstruct dropped requests or request history; request/snapshot observations need separate hooks |
| Bytes, modules, method bytes, dumps and exported traces | No arbitrary byte/dump codec | None added here | Native artifacts need their own lifetime, access controls, retention/quota and export policy. A path in a DTO is provenance/dependency metadata, not copied content or permission to reopen it |

Decoding never opens these provenance paths, attaches to a process, verifies a
PID, reads a dump, downloads sources, or resolves live symbols. Integration must
route restored snapshots only through the advertised pure views; placing a
restored live-origin object into a generic handle store does **not** authorize a
native/live query. Origin/PID/path metadata is preserved rather than rewritten
to suggest the original capture happened elsewhere.

Trace retention remains **opt-in**. A null trace path remains null; a non-null
path is not proof that a trace exists or is retained by SQLite. The codec creates
no trace dependency and does not make unsupported native/live views available.

## Combined results and cross-snapshot queries

- `GcActivitiesCapture` registers separate `GcSummary` and `ActivityCapture`
  artifacts. Encode each child; preserve the result-level links/failures/window
  metadata separately. `gc-overlay` is deliberately absent from the *single*
  activity snapshot's supported views: it additionally requires a compatible
  retained GC artifact and existing correlation validation.
- Sweep, distributed activity timelines, replica counters and `collect_batch`
  are compositions, not additional arbitrary-object codecs. Parent/child
  associations, per-target outcomes and coverage are storage integration work.
- CPU/heap `diff`, heap `growth`, and baseline comparisons require multiple
  captures and their compatibility checks. They are not single-snapshot views
  advertised by this API.
- Inline summary/result wrappers are not interchangeable with the registered
  artifact. Do not silently persist an inline top-N response in place of its
  full retained backing artifact.

See [resource boundedness](../resource-boundedness.md) for collector-specific
caps and loss semantics. The codec preserves those limits and reported evidence;
it does not remove them or claim that occurrence hooks listed here already exist.
