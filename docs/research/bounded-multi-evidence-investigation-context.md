# Bounded multi-evidence investigation context

Issue: [#1265](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1265)

Parent roadmap: [#1257](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1257)

Status: spike result; no product implementation

## Question

Can the VS Code extension compose CPU, allocation, off-CPU, contention, ThreadPool, GC,
and request evidence into one bounded investigation context without implying that
independent captures were simultaneous, retained, or causally linked?

The acceptance bar is stricter than presenting several result panels together. The
contract must preserve the actual owner and replacement policy of every item, identify
the target process and capture window, expose clock and overlap limitations, and make
expired or replaced evidence visibly unusable.

## Method and evidence boundary

This spike inventories the current Core, CLI streaming, and VS Code extension behavior.
It proposes a contract but does not implement a new collector, MCP tool, durable history,
automatic diagnosis, or composite UI.

The terms in this document are deliberately narrow:

- **Observed** means directly present in a retained collector result.
- **Temporally overlapping** means two windows are comparable in a declared shared clock
  domain and their intervals intersect. It does not mean one event caused another.
- **Adjacent** means the windows are ordered but do not overlap.
- **Unavailable** means the source did not provide the evidence or the evidence is no
  longer retained. It is never interpreted as zero.
- **Inference** means a conclusion derived from observations and must retain its stated
  limitations.

## Non-negotiable integrity rules

1. Every evidence item names its current owner. A context cannot extend the lifetime of
   a CLI handle, extension result slot, process connection, or live stream by reference
   alone.
2. Every item carries an immutable target identity and collection window. A PID without
   process-start identity is insufficient after reconnect or PID reuse.
3. Wall-clock timestamps are presentation metadata. Overlap claims require compatible
   interval/clock metadata; otherwise the relationship is `unknown`.
4. Replacement, explicit release, expiry, disconnect, target change, cancellation, and
   failure are distinct lifecycle events.
5. A failed or rejected capture cannot erase still-valid prior evidence unless the
   existing owner already does so.
6. Quality, truncation, sampling, loss, and backend limitations travel with the evidence
   item and constrain any aggregate statement.
7. Context metadata is bounded independently from the collectors. It cannot become a
   hidden capture history.
8. Raw diagnostic payloads remain potentially sensitive. Composition does not reduce
   their authorization, disclosure, export, or disposal requirements.

## Current ownership and lifetime inventory

The final inventory distinguishes extension state from CLI/Core ownership. “Retained”
means the evidence remains queryable or renderable after the initiating request returns;
it does not imply durable persistence.

| Evidence | Backend/source | Current owner | Replacement and release | Window and clock | Concurrency and quality | Bounds and sensitive content |
|---|---|---|---|---|---|---|
| CPU | EventPipe or OS sampler through `ICpuSampler` | Dedicated extension `cpuExport` plus CLI `cpu-sample` handle | A later success changes the extension's current handle but does not invalidate the prior CLI handle. JSONL uses a 20-minute TTL; Core/session defaults to 10 minutes. Child/store close, TTL, or capacity eviction ends drilldown. There is no individual CLI release command. | UTC `StartedAt` plus configured duration and internal phase timings. The extension summary has no process-start identity or observed end. | CPU has its own capture task. CPU and allocation queries share a single-flight guard. No per-request cancel frame; token/child close is the effective cancellation path. | Extension requests 10 seconds/top 10; protocol allows 1-300 seconds; Core allows up to five minutes and a 256 MiB circular EventPipe buffer. The main nettrace copy and total call-tree cardinality have no simple explicit byte/node ceiling. Stacks, modules, types, methods, source paths, and raw export are sensitive. |
| Allocation | EventPipe allocation sampler | Dedicated extension `allocationProfile`; CLI exposes the allocation handle | Later success replaces only the extension owner; prior handle survives until 10-minute TTL, capacity eviction, or store close. | UTC start plus configured duration; no process-start identity or observed end in the projection. | Generic captures share one extension task; queries share the CPU/allocation single-flight guard. Loss or cap hits fail without publishing a partial handle. | 64 MiB trace, 100,000 ticks, 4,096 types, 16,384 frame keys/tree nodes, depth 128. Type, stack, and method names are sensitive. |
| Off-CPU | Linux `perf sched`/optional syscalls or Windows equivalent | Shared generic extension slot. Core creates a 10-minute `off-cpu-snapshot` handle, but JSONL does not expose it. | Any later successful generic capture replaces the visible result. The hidden handle remains until TTL/capacity/store close and cannot be released individually. | Wall-clock start plus configured duration; no observed end or process-start identity in the generic projection. | Generic capture task serializes extension requests. High-risk platform preflight remains collector-specific. | Five-minute maximum; 128 MiB sched data, 64 MiB syscall data, 1,000,000 parsed syscalls, 500,000 intervals, and 8 labels per stack. Distinct final stacks/threads have no explicit cardinality ceiling. Kernel scheduling, stacks, thread names, and syscalls are sensitive; Linux needs compatible perf permissions and Windows needs elevated tracing rights. |
| Contention | EventPipe contention collector | Shared generic extension slot; hidden 10-minute Core handle | Successful generic capture replaces any prior generic kind; failure preserves it. Hidden handle follows TTL/capacity/store lifetime. | UTC start plus configured duration; events carry UTC timestamps, but the generic projection does not provide verified target lifetime or a common observed interval. | Serialized by the generic capture task. EventPipe startup/loss/quality remain collector-specific. | Retains the 200 largest events, 4,096 monitor IDs, exact percentiles through 4,096 samples then reservoir sampling. Pending starts by thread have no explicit whole-artifact cap. Stacks, types, methods, and target-controlled names are sensitive. |
| ThreadPool | EventPipe ThreadPool collector | Shared generic extension slot; hidden 10-minute Core handle | Same generic replacement and hidden-handle lifetime as contention. This event capture is distinct from the thread-snapshot `threadpool` query view. | UTC start plus configured duration, without verified process lifetime or observed end in the generic projection. | Serialized by the generic capture task. Provenance distinguishes directly observed starvation evidence from inference and missing data. | Three series retain the latest 4,096 samples each. Distinct work-item origins and observed event names do not have an explicit whole-artifact ceiling. Metrics and method/origin names can be target-controlled and sensitive. |
| GC | EventPipe GC collector; `gc-activities` coordinates GC and Activity sessions; live counters are a third independent owner | One-shot `gc` and `gc-activities` share the generic extension slot. Live counters/pauses keep their own stream lifecycle. Core registers hidden 10-minute handles. | Successful generic results replace each other; live observations do not. `gc-activities` publishes two handles that JSONL does not expose. | Plain `gc` Core data includes process start, observation start/end, actual duration, loss, and completion, but JSONL reduces it to start/duration plus metrics. `gc-activities` records dispatch and observed windows and proves only measured intersection/startup skew. | `gc-activities` is the only explicit concurrent two-collector case in this set. It uses one shared deadline, awaits both collectors, validates process identity before/after, and rejects readiness/causality inference. | Structured GC capture defaults to 200 retained events/intervals, hard maximum 100,000, 256 pending collections, 128 suspension states, and 16 runtime identities. Boundary loss disables authoritative suspension claims. Activity/request data can contain PII or secrets. Current CLI safety mapping treats `gc-activities` as ordinary GC, so its additional exposure is not represented in preflight metadata. |
| Requests | EventPipe in-flight requests; optional joint pending-request/completed-Activity capture | Ordinary `requests` uses the generic slot and a hidden handle. Joint capture has a separate one-slot CLI/extension owner. | Ordinary requests follow generic replacement. A new joint capture replaces/disposes the prior joint artifact; any non-joint capture in that CLI session invalidates it. Joint TTL is 10 minutes; the extension may preserve a display-only copy after drilldown expires. | Ordinary capture reports collector start/configured duration and per-request start. Joint capture reports requested and observed windows, process start/lifetime, loss, completion, and omissions. Structural Activity relationships are explicitly non-causal. | Ordinary requests use the generic task. Joint capture has request-ID cancellation, must not coexist with the live session, and has one active slot. | Ordinary capture retains the 100 oldest pending requests. Joint capture retains 100 pending requests, 200 completed Activities, 4,096 identity keys, and 100 query rows; accounted retention is at most 4 MiB and a frame at most 1 MiB. Paths and trace/span IDs remain sensitive; query strings, bodies, headers, tags, SQL, and destinations are not collected by the joint path. |

### Cross-cutting owner and connection facts

- The extension has dedicated owners for CPU, allocation, heap, thread snapshots, and
  joint request activities. Off-CPU, contention, ThreadPool, GC, GC/activity, ordinary
  requests, Kestrel, database, and networking results share one
  `genericCaptureKind`/`genericCaptureViews` owner
  (`vscode-extension/src/extension.ts`, result-state declarations and
  `setGenericCaptureOwner`).
- A successful generic capture replaces the generic owner only after success and marks
  the previous result “Not current”. Failed, busy, or rejected attempts preserve valid
  prior evidence.
- The normal JSONL child is reused while alive; Live Events uses a different child.
  Closing or replacing the normal child disposes its handle store. Rendered copies may
  remain visible but cannot be treated as live drilldown ownership.
- The Core handle store defaults to 32 entries, supports a configured range of 1-1,024,
  and evicts by expiry/age. Most listed artifacts use a 10-minute TTL; JSONL CPU uses
  20 minutes. The streaming protocol has no generic individual-release command
  (`DiagnosticHandleStoreOptions`, `MemoryDiagnosticHandleStore`, and
  `IDiagnosticHandleStore`).
- The CLI one-shot path is stateless. The `session` REPL retains the host/store and runs
  commands sequentially. Rebinding or clearing the target changes the PID/generation but
  does not invalidate self-contained artifacts; they remain until TTL, capacity eviction,
  or session exit.

### Cross-cutting concurrency and cancellation facts

- The extension serializes generic captures with `captureDiagnosticsTask`; CPU has a
  separate task; CPU/allocation queries share one guard; joint capture/query has its own
  single-flight state.
- The JSONL command loop handles ordinary command frames sequentially. A live
  counters/GC session may coexist at the target with a one-shot capture, while joint
  request activities explicitly refuses an active live session.
- Ordinary captures have no request-ID cancel frame. An extension timeout can stop
  waiting while target-side collection continues, and a later response must be rejected
  by evidence ID and target generation. Joint capture is the exception: it implements
  request-ID cancellation.
- Target selection is blocked during active work. Target changes clear owners and close
  the child. Generic capture dispatch revalidates target generation after prompts; the
  current CPU path lacks the same explicit post-acquisition generation check.

### Practical coexistence matrix

| Pair/topology | Current behavior | V1 rule |
|---|---|---|
| EventPipe CPU, allocation, contention, ThreadPool, GC, and ordinary requests | Core can keep sessions active concurrently. A process-global session-control gate serializes starts and stops to avoid runtime control deadlocks, so active windows can overlap but readiness has startup skew. Multiple sessions also multiply provider/buffer pressure and loss risk. | May be scheduled concurrently only by an existing owner that already supports it. Record each independent observed/reported window and quality. Never call equal requested durations simultaneous coverage. |
| Windows OS CPU, off-CPU, or another kernel ETW capture | One global kernel ETW gate covers the full capture lifetime. A concurrently scheduled task waits, shifting its window. | Treat as sequential. A context may retain both results but must not claim overlap. |
| Linux perf CPU and off-CPU | Raw perf recording may run concurrently, but both paths create, consume, and delete `/tmp/perf-<pid>.map` without capture-specific ownership or locking. This creates a real overwrite/delete race for managed symbolization. | Treat as sequential until JIT-map ownership/locking and a live coexistence test exist. |
| Joint request activities and the extension live session | The joint capture refuses an active live session. Its Core contract also says coexistence with another external DiagnosticSource session is not established. | Stop or avoid the live session before acquisition. Do not promise safety with an independent external DiagnosticSource collector. |
| Ordinary capture and the extension live session | They can coexist at the target and use independent sessions/clocks. | Preserve independent windows and quality. Do not merge the timelines merely because both use one CLI connection. |
| Generic capture and generic capture in VS Code/JSONL | Extension single-flight plus the serial JSONL loop makes them sequential; a later successful result replaces the shared visible slot. | Context acquisitions remain sequential and copy the bounded response before owner replacement. |
| MCP `collect_batch` | Resolves one PID, pre-authorizes all children, and dispatches up to four collectors with `Task.WhenAll`. Every child still opens its own EventPipe/ETW/perf session and owns its own handle. Child failures are isolated. Startup gates, extended counter duration, and backend restrictions still apply. | Batch dispatch is not an atomic capture, common clock, common readiness point, or evidence of simultaneity. It does not replace the proposed descriptor. |

`EventPipeSessionStart` confirms that diagnostic IPC returned a session, not that a
provider produced its first event. Shutdown separately budgets stop acknowledgement and
stream drain. Therefore “dispatched together” can be factual while “ready together”
cannot be inferred.

### Current boundedness and safety gaps

The proposed context caps do not repair collector gaps. They only prevent composition
from amplifying them. The current inventory found:

1. CPU trace output and total call-tree cardinality lack a simple explicit whole-artifact
   cap.
2. Off-CPU final distinct stacks/threads lack an explicit cardinality cap.
3. Contention pending starts lack an explicit whole-artifact cap.
4. ThreadPool distinct work-item origins lack an explicit whole-artifact cap.
5. Generic JSONL creates hidden handles that cannot be individually released.
6. `gc-activities` preflight inherits ordinary GC safety metadata despite additional
   Activity/request exposure.
7. Most generic projections expose UTC start plus requested/reported duration, while GC
   and joint requests expose observed endpoints and process lifetime. The heterogeneous
   clocks and windows do not support a general simultaneity or causal claim.

### Evidence map

The inventory above is grounded in these current owners and contracts:

- Extension state, connection reuse, target generation, result replacement, capture
  gates, and expiry behavior:
  `vscode-extension/src/extension.ts`, `vscode-extension/src/processHistory.ts`,
  `vscode-extension/src/requestActivities.ts`, and
  `docs/vscode-extension-reference.md`.
- Streaming protocol, CPU publication, generic result projections, hidden handles, and
  joint request-activity slot:
  `src/DotnetDiagnostics.Cli/CliStreamingProtocol.cs`,
  `src/DotnetDiagnostics.Cli/CliStreamingCaptureViews.cs`, and
  `src/DotnetDiagnostics.Cli/CliStreamingRequestActivities.cs`.
- REPL host/store lifetime, target rebinding, sequential execution, and queryable
  handles: `src/DotnetDiagnostics.Cli/SessionRepl.cs` and
  `src/DotnetDiagnostics.Cli/CliCommands.SessionQuery.cs`.
- Handle capacity, expiry, invalidation, and disposal:
  `src/DotnetDiagnostics.Core/Drilldown/DiagnosticHandleStoreOptions.cs`,
  `MemoryDiagnosticHandleStore.cs`, and `IDiagnosticHandleStore.cs`.
- CPU and allocation collection:
  `src/DotnetDiagnostics.Core/CpuSampling/EventPipeCpuSampler.cs`,
  `EventPipeAllocationSampler.cs`, and
  `src/DotnetDiagnostics.Core/UseCases/SamplerUseCases.cs`.
- Off-CPU collection:
  `src/DotnetDiagnostics.Core/OffCpu/PerfSchedOffCpuSampler.cs`,
  `OffCpuAggregator.cs`, and `OffCpuSample.cs`.
- Contention and ThreadPool collection:
  `src/DotnetDiagnostics.Core/Collection/EventPipeContentionCollector.cs` and
  `EventPipeThreadPoolCollector.cs`.
- GC and GC/activity coordination:
  `src/DotnetDiagnostics.Core/Collection/EventPipeGcCollector.cs`,
  `src/DotnetDiagnostics.Core/Gc/GcCaptureState.cs`, `GcSummary.cs`, and
  `src/DotnetDiagnostics.Core/UseCases/GcActivitiesCaptureUseCase.cs`.
- Request collection:
  `src/DotnetDiagnostics.Core/Collection/EventPipeInFlightRequestCollector.cs`,
  `RequestActivityCapture.cs`, `EventPipeRequestActivityCollector.cs`, and
  `src/DotnetDiagnostics.Core/InFlightRequestSnapshot.cs`.
- Session acknowledgement and collector concurrency:
  `src/DotnetDiagnostics.Core/Internal/EventPipeSessionControl.cs`,
  `EventPipeSessionStart.cs`, `EventPipeSessionShutdown.cs`, and
  `EventPipeCollectionRunner.cs`.
- Cross-backend scheduling:
  `src/DotnetDiagnostics.Core/Etw/KernelEtwSessionGate.cs`,
  `src/DotnetDiagnostics.Core/OffCpu/JitMapEmitter.cs`, and
  `src/DotnetDiagnostics.Mcp/Tools/CollectBatchTool.cs`.
- Canonical per-collector caps and tradeoffs: `docs/resource-boundedness.md`.
- Risk and disclosure metadata:
  `src/DotnetDiagnostics.Core/Safety/InvocationSafetyRegistry.cs` and
  `src/DotnetDiagnostics.Cli/CliInvocationSafety.cs`.

## Options

### Extension-owned metadata over existing evidence

The extension records identity, provenance, windows, quality, and lifecycle references,
but existing CLI/extension owners continue to own the underlying evidence. This is the
smallest option in code, but it is **NO-GO** as the whole contract. Current generic
results do not consistently expose process-start identity, window semantics, handle
expiry, or a common completion model. The extension would have to infer the properties
that this spike exists to make explicit.

### CLI-owned bounded bundle

The CLI retains several evidence payloads or handles as one aggregate object. This gives
one release boundary but duplicates ownership already present in collectors and turns
the CLI into a new scheduler and multi-capture store. It also requires a new generic
release surface and a cancellation model that the current streaming read loop does not
provide. This is **NO-GO for v1**.

### Hybrid

The CLI provides stable evidence identity and truthful lifecycle/provenance for each
result; the extension owns only a bounded investigation manifest and presentation
selection. Underlying artifacts and handles remain with their existing owners. The
extension copies only the already-bounded projection received in the response, so a
later replacement of an owner slot cannot silently change the evidence being displayed.

This is the selected **GO** option.

## Proposed versioned contract

The current CLI streaming protocol is version 2 and already negotiates additive,
sub-versioned capabilities. V1 therefore does not need a new top-level message or a
protocol-version increment. The `hello` response advertises:

```json
{
  "capabilities": {
    "investigationEvidence": {
      "version": 1
    }
  }
}
```

Each eligible terminal result may add one descriptor. Only a successful result with a
payload is admissible to the context; failed or cancelled descriptors explain the
attempt but do not replace retained evidence.

```json
{
  "investigationEvidence": {
    "version": 1,
    "evidenceId": "01JB6EW3TT7PXW6QG4W9B0WJ7C",
    "kind": "threadpool",
    "collectorKind": "threadpool",
    "target": {
      "processId": 4242,
      "processStartedAt": "2026-10-26T14:58:01.123Z",
      "identityStatus": "verified"
    },
    "window": {
      "kind": "reported",
      "start": "2026-10-26T15:00:00.100Z",
      "end": "2026-10-26T15:00:06.100Z",
      "clock": {
        "kind": "diagnostic-host-utc",
        "instanceId": "cli-4B1A7C8D",
        "comparison": "reported-only"
      },
      "completion": "normal"
    },
    "retention": {
      "projection": {
        "owner": "extension-context",
        "mode": "response-copy"
      },
      "drilldown": {
        "owner": "cli-connection",
        "storeId": "store-91D25171",
        "handle": "threadpool-01JB6E",
        "status": "available",
        "expiresNoLaterThan": "2026-10-26T15:10:06.100Z"
      }
    },
    "mayContainSensitiveData": true
  }
}
```

The contract intentionally describes evidence rather than conclusions.

### Target

- `identityStatus` is `verified`, `unknown`, or `changed`.
- `verified` requires a known, equal process start time read before and after the
  capture. `ProcessLifetime` already provides the reusable Core primitive, and the
  request-activity capture already applies equivalent before/after validation.
- `changed` rejects the item from the context.
- `unknown` can be displayed but cannot be aggregated as confirmed evidence from the
  same process instance.

PID equality by itself is not enough. The current process-history logic documents that
PID reuse remains indistinguishable when the available metadata also matches.

### Window and clock

- `window.kind` is `observed`, `reported`, or `requested`.
- `observed` means the backend measured both boundaries in the declared clock.
- `reported` means the collector reports an interval but the contract cannot prove
  complete observation of that interval.
- `requested` is only the requested duration and never establishes coverage.
- `completion` is `normal`, `early`, `cancelled`, `failed`, or `unknown`.

The clock object names the owning CLI process/connection instance. Current independent
captures use diagnostic-host UTC and `comparison=reported-only`; this distinguishes
child restarts but does not make wall clock monotonic or remove adjustment uncertainty.
V1 therefore never labels two independently acquired items `observed-overlap`. It may
say only that their reported intervals intersect, are adjacent/ordered, do not
intersect, or cannot be compared. A requested window cannot support an intersection
claim. No form of temporal relationship establishes causality.

### Retention

- `projection.owner=extension-context` identifies the bounded response copy that the
  context actually owns.
- Drilldown is either absent or names the owning CLI connection/store and borrowed
  handle. It never transfers handle ownership to the context.
- `expiresNoLaterThan` is an upper bound, not a guarantee. Capacity eviction,
  owner-slot replacement, explicit invalidation, store disposal, connection close, or
  process exit can make drilldown unavailable earlier.
- A failed lookup changes drilldown availability from `available` to `display-only`
  with an explicit reason: `expired`, `capacity-evicted`, `owner-replaced`,
  `invalidated`, `connection-closed`, or `unknown`. It does not invalidate the response
  copy.
- Releasing the context releases extension-owned copies only. V1 does not invent a
  generic CLI-handle release command.

The descriptor is small, additive, and strictly validated. Existing result payloads do
not change shape, and older clients ignore the extra field.

### Compound-result exclusion

`gc-activities` is not eligible for the v1 descriptor. It owns two independently
collected artifacts with separate requested/observed windows, quality, failure state,
handles, and retention. Collapsing them to one `gc` item, one `requests` item, or one
intersection would lose provenance and could imply coverage that only one side
established.

V1 composes separate `gc` and `requests` acquisitions instead. A later protocol version
may admit a compound terminal result only by emitting two component descriptors with
atomic admission and explicit item-count semantics.

## Lifecycle

The extension owns one memory-only context for one target generation:

```text
absent
  | open target
  v
open(target, generation)
  | start capture
  v
acquiring(kind, evidenceId)
  | success + valid descriptor + budget
  +-------------------------------------> open (replace same kind atomically)
  | failure/cancel/reject/late/mismatch
  +-------------------------------------> open (prior item unchanged)
  | target change / clear / dispose / deactivate
  v
releasing -> released
```

Rules:

1. Only one context acquisition is initiated at a time. V1 composes retained copies; it
   does not promise collector simultaneity.
2. While a new item is acquiring, the prior item of that kind remains current.
3. Success replaces only the item with the same normalized kind.
4. Failure, cancellation, permission rejection, descriptor validation failure,
   target-generation mismatch, late completion, or budget rejection preserves the
   prior item and reports the reason.
5. A target change never migrates evidence. It releases the whole context.
6. Generic cancellation remains accurately described as local abandonment when the
   collector cannot consume a cancellation message during capture. A late terminal
   result is discarded by `evidenceId` and target generation.
7. V1 has no wall-clock TTL and no workspace/global persistence. Its lifetime is the
   panel plus target generation, with explicit **Clear investigation** and cleanup on
   panel disposal or extension deactivation.

### Hard bounds

- At most **7 items**, one for each normalized kind:
  `cpu`, `allocation`, `off_cpu`, `contention`, `threadpool`, `gc`, and `requests`.
- At most **1 MiB UTF-8** per copied projection.
- At most **4 MiB UTF-8** for all copied projections and manifest metadata.
- Admission fails closed. The extension does not truncate silently or evict another
  kind to make a new item fit.

The 1 MiB item limit follows the existing request-activity frame limit, while the
4 MiB context ceiling matches its accounted-retention ceiling. The count bound prevents
the context from becoming hidden history even when payloads are small.

## Interpretation boundary

The composite lens may state facts such as:

- the evidence has the same verified PID and process start time;
- capture requests were dispatched together;
- two reported intervals intersected in one CLI clock instance;
- a collector that already owns a compound observation, such as the current GC/activity
  correlator outside v1 eligibility, reported an internal intersection; or
- observed trace/span identifiers form a direct-child or descendant relationship.

Every statement must retain backend, requested-versus-observed window, startup skew,
completion, loss, cap/omission, and projection limitations.

The lens must not state or imply that:

- a CPU hotspot caused request latency;
- GC caused an overlapping request delay;
- contention caused ThreadPool starvation;
- allocation caused a later GC;
- an off-CPU span caused a request delay;
- equal requested durations mean simultaneous coverage; or
- summed concurrent spans represent a critical path.

CPU, allocation, and off-CPU currently provide primarily host wall-clock start plus
configured duration. They support only conservative temporal placement. Contention,
ThreadPool, and ordinary requests carry event timestamps but do not all propagate a
complete observed window and loss state. GC and joint request activities provide the
strongest existing provenance, but two independently acquired items still use
wall-clock comparison only. Even verified relationships remain temporal or structural,
not causal.

## Minimum correlation scenario

Use the existing BadCodeSample `sync-over-async` workload with sustained, repeatable
load:

1. Capture ThreadPool evidence for at least six seconds and retain any directly observed
   starvation adjustments and queue/backlog observations with their quality.
2. Immediately afterward, capture request activities for ten seconds and retain long or
   pending requests plus any structurally related Activities.
3. Admit both items only when `identityStatus=verified`.
4. Calculate the window relationship from the descriptors. In the minimum sequential
   scenario the expected relationship is `adjacent` or `none`. If wall-clock timestamps
   unexpectedly intersect, report `unknown` and do not use the “separate windows”
   statement.
5. Render the bounded statement:

   > ThreadPool starvation evidence and long requests were observed in separate windows
   > of the same process.

6. Render the mandatory limitation:

   > This corroborates a recurring symptom under the reproduced workload; it does not
   > show that the retained requests were delayed by ThreadPool starvation or that
   > starvation caused their latency.

7. Recommend a controlled repeat or a more narrowly aligned next measurement. Do not
   emit a root-cause verdict.

This scenario is useful because it connects independently useful evidence and directs a
next action. It is honest because it does not promote adjacency, structural Activity
relationships, or recurring workload symptoms into a causal claim.

## Safety, privacy, and resource analysis

The context does not lower any collector's existing risk classification, acknowledgement
requirement, platform capability, or authorization rule. Acquisitions remain sequential,
and every collector keeps its own preflight.

V1 is memory-only and not exportable or persistent. It stores no raw trace or dump, but
the bounded projections can still contain symbols, module or source paths, host names,
sanitized SQL, correlation identifiers, exception text, and other business-sensitive
values already present in the source result. The composite lens therefore:

- keeps the existing sensitive-data warning;
- never restores the context after extension restart or target change;
- provides explicit clear/release;
- never logs payloads as context-management telemetry;
- applies the same redaction and disclosure rules as the source evidence; and
- does not claim that request query/fragment removal makes all request evidence
  non-sensitive.

Collector memory and event-volume caps remain the first line of defense. The independent
context byte/count limits bound the additional extension copy. A rejected oversize item
must be visible as unavailable for composition, not presented as a complete truncated
capture.

## Decision

**GO, conditionally, for the hybrid v1 contract above.**

The current surfaces are not sufficient for a truthful extension-only implementation:
they lack a common verified process identity, explicit window semantics, and consistent
retention state, while several evidence kinds replace one shared generic result. A small
additive CLI descriptor plus an extension-owned bounded reducer is sufficient; a
CLI-owned multi-evidence bundle, concurrent batch promise, durable history, new MCP tool,
and automatic diagnosis are not.

The VS Code composite lens remains blocked until the protocol/state scope below lands.

## Follow-up decomposition

No issues are created by this spike. The GO decision decomposes into exactly two
coordination-ready follow-ups:

1. **CLI streaming and extension state: investigation-evidence descriptor plus bounded
   reducer.** Add the capability and descriptor to the existing protocol; normalize the
   seven kinds while excluding compound `gc-activities` from v1; verify process lifetime
   before/after collection; classify window, clock-instance, and completion semantics;
   preserve older-client compatibility; and implement the
   extension reducer with 7-item, 1 MiB/item, 4 MiB-total bounds, atomic same-kind
   replacement, target-generation invalidation, late-terminal suppression, explicit
   borrowed-owner identity/early invalidation, explicit clear, and source
   safety/sensitivity metadata. Include protocol, reducer, identity, lifecycle,
   oversize, safety, and compatibility tests. No composite UI beyond test hooks.
2. **VS Code: optional composite lens and minimum correlation scenario.** Read the
   reducer without starting work on navigation; show target, window, source, quality,
   retention, and availability per item; calculate `reported-intersection`, `adjacent`,
   `none`, or `unknown`; preserve direct canonical views; and
   validate the `sync-over-async` scenario plus degraded identity, loss/quality,
   replacement, disconnect, and abstention cases. No persistence, export, automatic
   comparison, concurrent batch, or root-cause verdict.

Generic target-side cancellation or a CLI-owned bundle should become a separate issue
only if implementation evidence shows that local abandonment and bounded response copies
are insufficient.
