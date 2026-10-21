# VS Code extension reference

Detailed usage, troubleshooting, lifecycle and contributor information for
[.NET Diagnostics for VS Code](../vscode-extension/README.md).

This desktop/workspace-host extension provides live runtime counters, GC pauses and EventPipe
EventSource events, plus a point-in-time CPU capture, backed by Core-owned EventPipe/CPU-sampling sessions in
`dotnet-diagnostics-cli`. It does not collect diagnostics in TypeScript and does not use the MCP
server as a data transport.

## Screenshots

> These images are **illustrative**. They render the extension's real webview panels with
> hand-written fixture data (a fictional `OrdersApi` process); they are not captures of a live .NET
> process. Regenerate them with `npm run media`.

**Live counters and GC pauses**

![Live runtime counters chart and GC pauses table (illustrative fixture)](https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/vscode-extension/media/live-counters.png)

**Heap snapshot with drilldown views**

![Heap snapshot top types and GC roots drilldown (illustrative fixture)](https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/vscode-extension/media/heap-drilldown.png)

**Thread snapshot with deadlock candidates**

![Thread snapshot table and inferred deadlock candidates (illustrative fixture)](https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/vscode-extension/media/thread-snapshot.png)

## Sharing results

Use **Export redacted JSON** or **Export redacted CSV** in a result panel to open an editable
document containing only allowlisted measurements from the most recent result. Save it locally
with the editor's normal Save action. These intentionally lossy exports exclude application
text, symbols, identifiers, paths, arbitrary object values and diagnostic interpretations.
Unavailable values remain unavailable, not zero; explicit omission counts include excluded data
and presentation caps. These documents are not raw evidence or re-importable capture packages.

After a CPU capture, **Export redacted investigation summary** calls the CLI's `export-summary`
in the same connection that owns the capture, then applies the same measurement-only projection
before opening it. This needs a CLI build containing the export protocol; if the connection has
closed or its handle has expired, capture again. It never calls a fresh CLI with an expired handle.

Raw traces are a separate, unredacted, local-only option. Choose **Capture CPU + raw trace** and
explicitly acknowledge the sensitive-data warning before collection. **Export raw trace locally**
requires another confirmation and a native local Save dialog. The CLI materializes bytes only
from the trace associated with that CPU handle under its artifact root; the webview cannot provide
a source path. No automatic upload occurs. Raw traces can contain secrets and must be reviewed
before sharing. A normal CPU capture does not retain a raw trace.

In a **Saved Captures** panel, choose a candidate artifact/view and **Compare with saved baseline**.
Select a baseline from the latest 50 sealed captures and one of its artifacts. The CLI, not the
extension, evaluates compatibility and calculates deltas. The panel preserves measurement units,
normalization, retained populations, unavailable reasons and qualified/incompatible status.
Relative delta is a ratio, not percent; a zero baseline does not imply an infinite improvement.
Comparison does not contact the original process or prove equivalent workloads or causation.

### Evidence-dependent actions

Initial capture actions, result tabs and Command Palette entry points remain available without
requiring an investigation wizard. Result actions follow these prerequisites:

| Action | No result / loading | Ready | Failed attempt / disconnected |
|---|---|---|---|
| CPU redacted JSON/CSV | Unavailable until a CPU result exists | Host-held measurements; no CLI request | Previous successful measurements remain exportable, including while a new capture runs |
| CPU profile queries / investigation summary | Unavailable without a retained CLI profile; wait during capture/query/export | Uses the displayed capture's CLI handle | Previous valid profile survives rejected/failed captures; disconnect or an expired-handle response requires recapture |
| Raw trace export | Requires **Capture CPU + raw trace** and an idle retained connection | Separate acknowledgement and local destination | Unavailable after disconnect or replacement by a capture without a trace |
| Allocation exploration | Hidden without a retained profile; busy operations temporarily disable queries | Uses the allocation profile, independently of CPU | Table-only and disconnected results remain readable with recapture guidance |
| Saved-view export / comparison | Unavailable on selection change, unsupported views, loading or failure | Only the successfully loaded selection; comparison waits for the current comparison | Stale responses cannot enable another selection; a successful new selection restores actions |

Unavailable result actions keep keyboard focus and explain their prerequisite in a tooltip;
CPU/allocation status text also describes the retained evidence. If an allocation explorer holding
focus becomes unavailable, focus returns to **Capture allocations** before the explorer is hidden.
Sidebar **Stop Live Counters** has no action without an active counter session. Live-time reset,
Events **Clear**, and permission revocation require retained observations/GC rows, retained events,
and remembered live-read grants respectively. UI availability does not replace host-side validation,
authorization, request correlation or measurement-only privacy projection.

## Prerequisites

- VS Code 1.96 or newer with a Node.js extension host.
- `dotnet-diagnostics-cli` built from this repository and a compatible .NET 10 runtime, or a
  self-contained CLI executable for the host platform.
- Permission to attach to the selected process's diagnostic IPC endpoint. The CLI must run as the
  same OS user as the target. Linux remote-process privilege requirements remain those documented
  by the CLI/Core.

Install the CLI explicitly; the extension never installs or updates tools:

```sh
dotnet tool install -g dotnet-diagnostics-cli
```

This installs the latest stable release (`dotnet tool install` has no version-range syntax).
Use CLI **0.31.0 or later** for every feature in extension **0.3.0**. CLI 0.29.0 supports the
base JSONL protocol-v2 workflows; 0.30.0 adds capture, address-navigation and export requests.
Allocation exploration and sensitive-value/array-range reads require 0.31.0.
The handshake reports the CLI version, so Check Environment can distinguish those base
workflows from the full extension feature set and offer an explicit update action.
CLIs without protocol v2 are rejected with a protocol mismatch error.
Update explicitly with `dotnet tool update -g dotnet-diagnostics-cli`.

New to the extension? Open the **Get started with .NET Diagnostics** walkthrough (`.NET Diagnostics:
Open Getting Started`). **`.NET Diagnostics: Check Environment`** runs the configured
`dotnetDiagnostics.cliPath` for the protocol-v2 handshake and `doctor --json`, then reports a missing
executable or .NET runtime, outdated protocol or feature support, and ptrace/UID/permission problems
with the CLI's own fix. The check is host-only, so target-specific permissions (socket UID) are reported as not verified.
For a custom `cliPath` it gives path-specific update guidance instead of the global update command. It can copy the install/update command and recheck, but never runs it for you.

The extension adds a **.NET Diagnostics** icon to the Activity Bar. Open its **Live Diagnostics**
view and select **Start Live Counters**, or use the same command from the Command Palette. Choose a
discovered process, then use the panel's Stop/Start controls. One streaming session now carries
both the live counters chart and a **GC pauses** table (timestamp, generation, reason, type, pause
duration, plus a running pause count and total paused time) — both signals start and stop together.
The Live view provides start/end range controls for a fixed selection within **retained receipt
time**, plus **Follow live / reset zoom**. The chart and GC rows respect that selection without
changing collection or retention; selected data may expire and an empty range is never shown as
zero. Counter points are spaced by receipt time, not sample index. Click the chart or use
Left/Right/Home/End to inspect a nearest retained observation (no interpolation); select a GC row
with click or Enter/Space for event details. Target timestamps remain visible in details but are
not assumed to share a clock with the webview. Capture-wide GC totals remain capture-wide.
The extension runs `dotnet-diagnostics-cli processes --json` for discovery and starts
`dotnet-diagnostics-cli stream --protocol jsonl` for live samples. Stdout is parsed only as protocol
frames. The CLI path can be set in the machine-scoped `dotnetDiagnostics.cliPath` setting; the
extension host must be able to execute it. Runtime, missing-tool, and protocol-version errors are
shown with actionable guidance.

The **Events** tab starts an independent `event_source` EventPipe session; it can run while the
counters/GC stream is stopped or active. It asks for 1–8 explicit provider names, an event level
(0–5), and a signed 64-bit decimal keyword mask, then requires acknowledgement because provider
payloads can contain secrets or personal data. It does not use native Windows ETW, wildcard
providers, the MCP server, or persistent storage. Pause following freezes the visible page while
collection and bounded retention continue; Clear removes the retained UI events without stopping
the stream. The table supports bounded search, paging and one selected event detail view. Managed
exception details are labeled only for the runtime provider's schema-verified
runtime exception event (provider `Microsoft-Windows-DotNETRuntime`, ID 80, version 1, exact
`ExceptionType`, `ExceptionMessage`, `ExceptionEIP`, `ExceptionHRESULT`, `ExceptionFlags`, and
`ClrInstanceID` payload fields). TraceEvent may expose a task label rather than
`ExceptionThrown_V1` in its event-name field for this runtime event, so specialization uses the
provider, numeric identity, version and exact payload schema; the original event name remains visible.
Other events and incompatible schemas remain generic.
Source-loss availability, Core queue drops, CLI dispatch drops, host-delivery drops, UI evictions,
and payload/metadata truncation are shown separately, never combined into an apparent total. The
exact frame schema and limits are in the [CLI stream reference](cli-reference.md#eventsourcelivekind).
Component accounting budgets and their distinction from measured RSS are in
[`resource-boundedness.md`](resource-boundedness.md#eventpipe-eventsource-live-events-1201).

The **.NET Processes** view adds refresh, a name/PID/runtime/platform filter, and context actions
for counters and CPU/heap/thread captures. Click a process once to select it; context actions
reuse that target without another process picker. The last selection is remembered per workspace
as a hint, never automatically attached after restart. Actions refresh and validate the inventory
first. The CLI does not expose process start time, so PID reuse with identical metadata cannot be
distinguished. Process command lines are not retained in the tree or workspace state.
The view displays at most 500 processes and explicitly reports an inventory cap.

After selecting a process, browse the **Live Diagnostics** action tree:

| Group | Existing actions |
|---|---|
| CPU | CPU sample; CPU sample with raw trace (explicit acknowledgement) |
| Memory & GC | Allocations; heap snapshot (Live or GC Dump source picker); garbage collections; GC and activities |
| Threads & waits | Thread snapshot; off-CPU blocking; lock contention; thread pool events |
| Requests & networking | In-flight requests; Kestrel; database; networking (HTTP, DNS, TLS, sockets, not file/disk I/O) |
| Exceptions | Capture Exceptions, then choose Managed exceptions or Crash guard |
| Monitoring | Start/Stop Live Counters (including GC pauses); Start Live Events (independent provider-configured stream) |

Expanding a group only navigates. Selecting a capture enters the same existing parameter,
source and consent flow, using the selected process or asking for one when needed.
**Capture Diagnostics** stays available beside the groups and in the Command Palette; its
searchable ten-kind picker uses matching domain headings. Allocations there and under
Memory & GC use the same capture and Allocations result, not separate products.
GC/activity overlap remains overlap evidence, not a causal latency diagnosis.

The live panel keeps **Live**, **Workspace**, **Exceptions**, **Diagnostics**, and **Events** as
its result tabs; the Workspace domain selector now includes **CPU**, **Memory & GC**, and
**Threads & waits**. Its compact **Domain → Evidence → View** controls navigate one content panel.
Thread snapshots and off-CPU, contention, and thread-pool captures appear under Threads & waits;
the point-in-time snapshot and the generic event capture remain separate evidence kinds.
Those three generic kinds share the same single replaceable result slot used by GC and the other
generic captures. A successful capture replaces that slot across domains, and the previous domain
reports that its evidence is no longer current. A failed attempt does not replace the slot.
Requests & networking, Exceptions, Monitoring, and offline dump/saved panels retain their existing
result surfaces. Groups organize collection entry points, not permissions: actions still require
their actual evidence prerequisites. Retention paths, thread wait chains, heap object inspection
and other drilldowns remain beside their evidence, not separate collectors.
**Analyze Dump File** remains a standalone action for an existing file with no selected process
required. **Saved Captures** remains a sibling view; **Capture and Save Snapshot** stays on the
process context menu and explicitly collects a new heap/GC dump/thread snapshot. Grouping does not
add persistence for other captures or convert current results into saved evidence.

**Saved Captures** lists the CLI's durable capture catalog, 50 entries per page (up to 500 loaded,
with an explicit cap notice), with refresh,
reopen and confirmed CLI-managed deletion. It survives extension and CLI process restarts;
ordinary panel results and ephemeral handles are not history entries. On a process, choose
**Capture and Save Snapshot** to collect a **new** live heap, gcdump or thread snapshot, rather
than converting an older panel result. Saving requires explicit high-risk acknowledgement
(ptrace/suspension for live heap/threads, a blocking Gen2 GC for gcdump). Saved evidence can contain
sensitive data and remains on disk until deleted.

Reopened panels query only advertised offline views by capture/artifact IDs, never contact the
original process, and reuse the shared drilldown renderer. Heap types, thread summaries and GC
measurements also have structured saved-evidence tables. Unavailable data and omissions remain
visible. Views needing unsupported parameters are labelled **use CLI**; interrupted captures
must be recovered with the CLI first. CPU/exception persistence is not exposed by this UI.
History calls inherit the CLI artifact root (including `MCP_ARTIFACT_ROOT`), have a 60-second
deadline and a combined 8388608-character output cap; failures are explicit, not partial JSON.

Open **Workspace**, choose **CPU** under **Domain**, and select **Capture CPU sample** (also available as the tree-view action or
Command Palette command `dotnetDiagnostics.captureCpu`) to take a single point-in-time CPU sample
of the selected process.
Unlike the counters/GC timeline, a capture is a one-shot snapshot: it reuses the panel's active
streaming connection if one is open, or opens a capture-only CLI connection otherwise.
The connection is retained while the panel owns a CPU export or allocation-explorer handle and closes with the panel.
The capture renders its own top-hotspot table (module, method, inclusive/exclusive sample
counts) that is replaced by the next capture rather than appended to a history. A capture does not
start or stop the live counters/GC session, and it is not compared against any baseline.
The live panel keeps CPU, allocation and heap evidence within the **Workspace** domain/evidence
selectors; other existing tabs remain available for their current result surfaces. Starting or
completing an explicit capture selects its corresponding domain or result tab. CPU
captures automatically show a triage overview and can be queried on the captured artifact for a
call tree, top methods, hot path, module/namespace aggregates, and callers/callees of a selected
method. These are in-memory views of the same sample, not additional captures; the owning CLI
connection must remain open. The result panel keeps the capture summary and drilldown together.
The initial hotspot table remains available under **Capture hotspot summary**, collapsed by
default so it does not duplicate the selected drilldown.

The single **Grouped stacks** tab shows group counts, shares, wait reasons, canonical frames
and representative thread IDs. Select a managed-thread link to inspect its captured details in
**Thread explorer**; **Back to grouped stacks** restores the same groups, filter and expanded
sections without another read. A representative absent from the inline thread snapshot is
explicitly identified as unavailable, not silently read from the live process.
Groups with no returned managed frames display **Stack unavailable**. The empty group does not establish that
its threads have no stack or share an execution path; the UI does not invent a missing-stack cause.
Thread evidence views and the explorer's three modes (returned threads, waits and inferred cycles) use tabs rather than filled action buttons
or a dropdown. Arrow keys and Home/End switch tabs; filters and selected threads survive switching
and late evidence updates without another diagnostic read.

Object-value reveal/hide is located in the **String preview** card for strings, or above the
fields for other objects. It applies to all bounded value previews of that object, not just
the string; the separate one-request sensitive-data consent remains required.

Heap type exploration uses an adjacent details pane in wide panels, with independent scrolling
for the returned type list and details. Selecting a type no longer opens its details below the
whole table. At widths of 850 px or less, the same pane replaces the list; **Back to types**
restores the existing list, filters, sort and page scroll position. In the domain workspace,
**View → Heap details** navigates to the captured details without choosing a type; **Snapshot totals**
returns to the type list. The separate dump-analysis surface retains **Explore heap details**.
Both routes use the same responsive pane and start no additional diagnostic reads.

The **Call tree** view has linked tree-table and **Flame graph (icicle)** modes.
The tree-table aligns method/call path, **Total %** (including descendants) and **Self %**
(excluding descendants), both using the full capture denominator. CPU percentages are shares
of captured stack samples, not measured CPU time or utilization. Allocation percentages instead
use estimated sampled bytes, not CPU time or retained heap bytes. Raw weights and full method/module
names remain available in selected-method details and metric tooltips. A narrow panel scrolls
the bounded table horizontally rather than dropping either metric.
**Expand hotpath** expands only the dominant known loaded branch without a query, preserving
manual expansion, selection and focus. Its marked path is not a global hotpath claim: expansion
stops when omitted descendants could dominate. The separate **Hot path** view remains an
explicit retained-profile query when not already cached.
Click a tree row or press Enter/Space on it to select that exact call-path occurrence only:
selection does not expand, focus or query. The selected row uses the editor's selection colors.
A single contextual area immediately above the independently scrolling tree names the selected
branch and provides **Focus branch** beside **Expand hotpath**. With no selection it asks for a row,
and Focus is disabled. Raw inclusive/self counts and available on-CPU, heuristic-wait and unknown
evidence appear there. **Focus branch** (or clicking a flame block) zooms into that exact loaded
branch; breadcrumbs and **Reset focus** navigate back. Repeated method names remain separate
call paths. Keyboard users can use the table's disclosure buttons, or Tab and Enter/Space
on flame blocks (Escape moves to the parent). **Find method or module** highlights matches
in the loaded focus without hiding nodes or changing sample totals.

Both modes share the same bounded projection (at most 64 nodes / depth 8), not the whole
profile. Flame widths are proportional to inclusive sample counts, not duration or CPU
utilization; self and omitted/unattributed descendants keep their own labelled gray space,
so omitted branches are not redistributed to visible children. Percentages explicitly name
the capture or focused-branch denominator. Flame blocks display short type/method names and
their share of the current focus; hover reveals the complete signature and metrics, and focusing
a block shows its details. Stable pastel colors distinguish methods, not severity; yellow marks
search matches. Dark text has at least 7:1 contrast against block fills in light/dark themes;
high-contrast themes use editor/system colors. Narrow panels scroll horizontally instead of
shrinking the labels. **Query subtree by method** requests another
bounded projection from the retained artifact. This backend query chooses the highest-ranked
occurrence containing the method name, not necessarily the selected call path.
Retained-capture queries are in a separate disclosure, not the local branch-action area.
**Full capture tree** queries the original root, still bounded. **Callers / callees**
uses the same method-name lookup; **Back to CPU profile** restores the cached projection,
mode, search and local focus without a new request. A successful new CPU capture replaces the old
profile; preparing, rejected or failed attempts preserve still-valid evidence.
Returning to CPU in the **Workspace** restores its selected view and method filter without
querying; an unloaded view still requires **Load view**. A successful new capture resets the
selection and view cache. After disconnect or handle expiry, cached projections remain readable
but additional CLI queries, investigation exports and raw trace exports are unavailable.
Redacted measurement exports remain available offline.

The **Allocations** tab provides **Capture allocations** and a sampled Allocation Explorer.
The allocation option in **Capture Diagnostics** opens this same tab. It reuses the tree/flame,
search, exact loaded-path focus, breadcrumbs, bounded subtree queries and callers/callees navigation,
but weights every stack by its allocation tick's **estimated bytes**, not tick count or CPU time.
Top methods, hot path, modules and namespaces use the same byte weighting.
The capture's type/site tables remain under **Allocation types, sites and capture quality**.
Types and stacks are independent aggregates: choosing a type does not filter the merged tree.
Capture totals include ticks without stacks; the profile reports its attributed share rather than
redistributing missing bytes. Subtree queries still use the highest-ranked method-name match.
Allocation profiles have separate state from CPU profiles, retain their owning CLI connection and
never recollect on navigation. A new allocation capture replaces the displayed profile.

This is not exact object instrumentation or per-object lifetime tracking: `GCAllocationTick` fires
roughly per 100 KB allocated and names the most recent object. Counts are ticks, not objects;
bytes describe allocation traffic, not retained heap size. Start with a short capture.
Collection, runtime rundown and ETLX conversion can be expensive even with sampling.
The collector rejects lost-event traces and explicit resource-limit hits rather than returning a
plausible partial profile. Limits are 64 MiB raw trace, 100,000 ticks, 4,096 types, 16,384 distinct
frames and nodes per tree, and 128 frames per stack. Conversion has additional memory/disk cost:
these bounds are **not a hard RSS budget**. See [resource boundedness](./resource-boundedness.md#allocation-profiling).
Older CLIs can still return the allocation tables, but a missing handle or missing explicit
byte-weight confirmation displays an update message instead of mislabelling a tick tree as bytes.

Use **Capture Diagnostics** (`dotnetDiagnostics.captureDiagnostics`) for allocations, off-CPU
blocking, lock contention, thread pool, GC, GC and activities, in-flight requests, Kestrel,
database or networking. A single picker asks for duration (1-300 seconds, default 10), rows per
table (1-100), and applicable symbol path, ActivitySource filters or request threshold.
Results contain structured metrics/tables, collector notes, omissions and evidence quality.
Unknown values remain **Unavailable**, not zero; GC elapsed time is not labelled as pause time.
Off-CPU requires explicit high-risk acknowledgement and platform tracing permissions
(`perf`/CAP_PERFMON on Linux or ETW privileges on Windows). CLI errors remain visible.
Results are not saved. Start the capture before reproducing the problem; startup takes time.
Captured names, paths, request identifiers and sanitized SQL may contain sensitive data.

Diagnostic tables support **Filter returned rows** and sortable column headings.
Rows do not offer a generic expansion that merely repeats their visible cells. Meaningful
disclosures, such as inline struct fields and retention chains, and object/address navigation
remain available. These controls explore only returned evidence: they do not reconstruct omitted events,
stack trees, a request waterfall or parent/child trace relationships from aggregates.
Metrics and evidence-quality notes remain visible; an **Evidence table** selector shows one
specialized table at a time, listing returned/omitted counts for each choice. Switching keeps
that table's filter and sort without another request.

Use **Capture Exceptions** (`dotnetDiagnostics.captureExceptions`, also available in the
Activity Bar and live panel) for a 10-second exception window. Choose **Managed exceptions**
for type counts and retained messages, or **Crash guard** for available managed stack frames
and separate process-exit/unhandled-exception evidence. Neither option forces a crash.
The shared drilldown renderer shows type counts, top retained messages, first/last **retained**
timestamps, per-event details and capture notes. Messages and stacks are rendered as text,
never markup, and may contain sensitive application data. The default cap is 100 retained
events and 25 returned types; explicit notes report omissions. Retained-message counts and
timestamps are not whole-capture statistics. Plain managed-exception events have no stack frames;
crash-guard stacks may also be unavailable.

**Explore retained occurrences** connects type and message selectors to occurrence buttons and
the selected event's message/available stack. Changing type resets the message selection; **Reset
exception filters** restores all retained events. The all-observed type counts stay separate and
do not change with these retained-event filters. An empty message is selectable independently
from **All retained messages**.
Type/message aggregate tables are retained under **Type counts and retained-message aggregates**
rather than always stacked above the occurrence explorer.

Start capture **before** load: after the request, the panel says to allow EventPipe about a
second to start and then **reproduce the problem now**. This is timing guidance, not an
armed-session acknowledgement. A busy connection rejects exception capture with retry guidance
instead of queueing it behind an earlier capture, query or session startup. Captures
do not start/stop the live counters session. These capture kinds require a CLI build containing
the #1166 JSONL extension; older protocol-v2 CLIs report the kind as unsupported with update guidance.

Use **Capture Heap Snapshot** (tree-view action or Command Palette command
`dotnetDiagnostics.captureHeap`) to take a single point-in-time heap snapshot. Unlike CPU sampling,
this capture requires choosing a **source**: **Live** (ClrMD via `ptrace`) or **GC Dump** (EventPipe).
Both sources are classified High risk / Acknowledge in Core's invocation-safety registry (CPU
sampling is only Moderate), so after picking a source the extension shows a native modal describing
its impact — a live walk attaches with `ptrace`, suspends the target, and exposes heap type and
object-graph metadata; a GC dump induces a managed GC and exposes aggregate heap type metadata; both
may expose possibly confidential data — and only sends the capture request once you select
**Acknowledge and Capture**. The result panel shows the top types by total bytes (type, instance
count, bytes), the suspend duration for `live` captures, and any concerning GC-dump completion flags
(e.g. timed out, reader failed) for `gcdump` captures; each capture replaces the previous rendering.
A live heap walk needs the same `ptrace`/`CAP_SYS_PTRACE` access as other live memory readers (see
AGENTS.md's "🪪 `CAP_SYS_PTRACE` for live memory readers" section) and fails with an actionable
permission-denied message when that access is unavailable. A GC dump instead goes through the
diagnostic IPC channel but induces a blocking Gen2 GC pause on the target while it runs.

The top-types table supports search and sorting over the **returned top types**, not the entire
heap. Selecting a type shows its instance count and direct bytes, and focuses matching **captured**
retention paths by type-name substring. Paths arriving later respect the selection; **Clear type
focus** restores them. This does not enumerate instances, compute dominator-retained size or read
the live process again. Root-to-object paths are expandable reference chains with address links.

Use **Capture Thread Snapshot** (tree-view action or Command Palette command
`dotnetDiagnostics.captureThreadSnapshot`) to take a single point-in-time snapshot of the selected
process's managed threads. Unlike heap capture, there is only one source: a live ClrMD attach (the
CLI's offline `--dump-file` option does not apply to a live VS Code panel), so no source QuickPick is
shown. It is classified High risk / Acknowledge in Core's invocation-safety registry, the same tier
as heap's `live` source, so the extension shows a native modal before sending the request —
explaining that a live thread snapshot attaches with `ptrace`, briefly suspends the target, and
exposes stack, type, and method names, some of which may be confidential — and only sends the
capture request once you select **Acknowledge and Capture**. The result panel shows each thread's
managed thread id, state (flagged when likely blocked), inferred wait reason, and its top stack
frame, bounded to a summary-first table; each capture replaces the previous rendering. A live thread
snapshot needs the same `ptrace`/`CAP_SYS_PTRACE` access as the heap capture's `live` source (see
AGENTS.md's "🪪 `CAP_SYS_PTRACE` for live memory readers" section) and fails with an actionable
permission-denied message when that access is unavailable.

The **Thread explorer** connects searchable returned threads, expandable stack groups,
representative group member IDs, inferred cycle participants and wait-chain owners. Selecting
a thread shows all its returned inline frames and related wait edges; selecting an owner moves
to that participant. A participant omitted from the inline list is explicitly unavailable, not
invented from another thread's stack. Group counts may exceed the representative IDs returned.
All of this is snapshot navigation, not a scheduler timeline; a new snapshot resets selections.
Its mode selector switches between **Returned threads**, **Grouped stacks**, **Wait chains**
and **Inferred cycle candidates**, preserving the selected thread. The original inline table
remains available under **Inline thread summary**.

**Follow-up drilldown views.** After a heap or thread capture succeeds, the extension issues
follow-up `query` requests against the capture's handle (sequentially, over the same streaming
connection). Heap and thread results have an **Evidence views** switcher instead of stacking
all sections vertically. Switching displays one result at a time and preserves its controls and
selection, without querying again; new results do not steal the selected view. Unavailable views
are labelled in the switcher, whose always-visible status also reports failures and inferred
deadlock warnings. Thread explorer is the default thread view; the detailed query responses remain
available as alternatives, including their metadata, limitations and errors.

- **Heap** (7 always queried): `roots-by-kind`, `finalizer-queue`, `fragmentation`, `gchandles`,
  `async`, `timers`, `alc`. Four more are opt-in via a multi-select QuickPick shown before a
  `live` capture (all unchecked by default): **Static fields** (`static-fields`), **Delegate
  targets** (`delegate-targets`), **Retention paths** (`retention-paths`) and **Retained
  exceptions** (`retained-exceptions`). The `gcdump` source
  skips the picker and the follow-up views, since the CLI rejects every drilldown view for it.
- **Thread snapshot** (4): `deadlocks` (automatically expanded with a warning only when inferred
  cycle candidates exist; empty results are neutral), `unique-stacks`, `wait-chains`, `threadpool`. `thread-statics` needs an exact type name,
  so it is never queried automatically: after a thread snapshot, type the exact type name into the
  **Query thread statics** box and press the button (an empty name is rejected in the panel; one
  query runs at a time, and the result is capped server-side with explicit omitted counts).
- **Analyze Dump File** runs the same heap/thread views, with the same opt-in picker shown after
  you choose the file.

Drilldown results use purpose-built summaries, metric cards, tables, and expandable chains rather
than a generic JSON dump. The same renderer is used for live captures and dump analysis; it keeps
provenance and evidence limitations visible, labels inferred deadlocks as candidates, and treats
retained exception messages as potentially sensitive application data. Unknown future views use a
compact key/value fallback. Heap and thread object addresses are serialized as hexadecimal strings
so JavaScript preserves all 64 bits.

**Address navigation.** Every valid `0x` address in the heap drilldown views is shown with a
**Copy** button, and in heap views (and, in the dump panel, thread views such as deadlock lock
addresses, which come from the same dump) it is also a link. Following a link opens a fixed
right-side **Object inspector**, keeping the source view in place, with **Close** / Escape,
**Back**/**Forward** buttons and a breadcrumb: *retention path -> object
(fields and array elements link onward) -> GC root path / Referencing statics*. The
**Referencing statics** view filters the already-captured static fields by that exact address, so
it needs no extra read and only exists when you opted into static fields before capturing.

**Arrays and value-type entries.** The inspector reads a bounded range of at most 8 array
elements. **Previous array range**, **Next array range**, and **Start index / Read array range**
let you inspect other ranges without loading the full array. Indices are zero-based linear
storage positions; multidimensional arrays use row-major order, including arrays with non-zero
dimension lower bounds. Jagged-array references open the inner array as a separate object.
The displayed interval is end-exclusive and reports how many slots are outside the range;
per-element read failures remain explicit in inspection warnings.

Value-type elements (including `Dictionary<TKey,TValue>.Entry`, nullable values and user-defined
structs) expand in place to a field table. Nested struct fields use dotted paths. Only managed
reference fields have clickable object addresses; an inline struct is not a standalone object.
Field expansion has a shared 256-row budget per array range and depth limit 3, with explicit
warnings when fields are omitted. No collection-specific active-entry filtering is inferred:
dictionary backing arrays can contain free/unused slots. Strings and primitive field values stay
redacted unless explicitly revealed. Moving to a different range always starts metadata-only;
**Reveal values of this object** preserves the selected range but asks for fresh sensitive-value
consent. Each range read still obeys the live-read or offline-dump policy.

**Live-read permissions.** In the live Heap tab, expand this section to inspect active grants
and the last 30 decisions/uses, or **Revoke remembered live reads**. Native prompts offer
**Acknowledge and Read** (once), **Allow for This Snapshot Session**, or cancellation. Object
and GC root permissions are separate; remembering one never authorizes the other. Grants are
in-memory, limited to the current heap snapshot and its CLI connection, and cleared on a new
heap capture, connection closure, observed target exit or panel closure. Revoking while a prompt
is open cancels that pending acknowledgement; it cannot interrupt a read already dispatched.
Captures, raw traces and exports keep their separate acknowledgements. Dump reads do not acquire
live-read grants. The bounded history records operation/decision/time, not addresses or values.
**Object** and **GC root path** are read on demand through the CLI's `query` request with
`address`. For a live panel that read re-attaches via ptrace and suspends the target (High risk), so
the extension asks for an explicit modal acknowledgement unless that operation has an active
snapshot-session grant, and shows the result as read
live, not part of the captured snapshot; Back/Forward use cached results and never read again. In a
dump panel the read is offline (Moderate risk, nothing is attached or suspended), so no
acknowledgement is requested.
**Reveal values of this object** requests bounded string, field and array-element previews after
a separate native **Reveal Values Once** confirmation about secrets and personal data. It never
disables pattern-based credential redaction: recognized tokens, password assignments and key
patterns remain replaced with `<redacted:sensitive>`, including nested struct fields. This is
not a complete data-loss-prevention filter; other sensitive application values may still appear. It never
uses or creates a session-wide sensitive-value grant, and live reads still need their independent
operational permission. Metadata-only remains the default, including ordinary navigation to
referenced objects. Requires a CLI build supporting `includeSensitiveValues` and
`acknowledgeSensitiveValues`; an older CLI is reported explicitly rather than treated as having
revealed values. Strings are limited to 256 characters, array samples to 8 elements and fields to
256 entries (nested value types to depth 3); string truncation is shown, and field/element string
previews can also be shortened. A live reread may observe a different object/value after GC.
Sensitive responses are held only while displayed, not in navigation history or the exporter;
**Hide values**, closing the inspector or navigating to another address/history entry discards them. Back/Forward
requires a fresh reveal to read sensitive previews again. This is UI lifetime control, not secure
erasure of process memory, and it does not remove data already present in a source dump.

History holds at most 20 entries, caching only metadata-only results, and is cleared by a new heap
capture; only the current result is in the page. Addresses are never converted to JavaScript
numbers: a numeric address is flagged as imprecise and is neither linked nor copyable. The host
accepts webview navigation only for `object`, `gcroot` and `static-fields` with a strict hex
address and the panel's current heap handle. `gcdump` snapshots have no drilldown, `objsize`
and `duplicate-strings` are not exposed, and the live panel keeps its CLI connection open after a
live heap capture so the handle stays valid. Saved-capture panels support copying exact addresses;
they do not reattach to the original process for address navigation.
These views require a CLI build from #1116 or later;
`retained-exceptions` and `thread-statics` require a CLI build from #1126 or later, and an older CLI
rejects the new capture option or query and surfaces it as an unavailable view.

Use **Analyze Dump File** (tree-view action or Command Palette command
`dotnetDiagnostics.analyzeDumpFile`) to inspect heap types and threads/locks from a process dump
file that already exists on disk. This opens a native file picker (no extension filter — dumps can
be `.dmp`, `.dump`, or extensionless) and, once you choose a file, a **new, dedicated webview
panel** titled after the dump's file name — separate from the live counters panel, because that
panel's entire model (selected target, start/stop session) assumes a live PID, and a dump file has
none. The panel opens a short-lived capture-only CLI connection and fires both a heap-from-dump and
a thread-snapshot-from-dump request against it, rendering a heap top-types table (type, instance
count, bytes, plus the dump's file size and any analysis warnings) and a thread/lock table (thread
id, state, inferred wait reason, top stack frame) as each result arrives; the connection stays open
until the panel is closed so on-demand drilldown queries can reuse the thread snapshot. Dump-sourced heap and thread-snapshot analysis is classified **Moderate risk / Warn**
in Core's invocation-safety registry — not the High risk / Acknowledge tier of their live
counterparts — because there is no live attach or target suspension involved, only offline parsing
of a file already on disk. Matching CPU sampling's existing no-modal posture, this command proceeds
straight from the file picker to analysis with **no risk-acknowledgement modal**. This command only
**analyzes** an existing dump file, including offline object and GC-root address drilldown using
the current dump's heap handle. Generating a new process dump remains out of scope for this command.

Live streaming requires a CLI build that supports `stream --protocol jsonl` and the multi-kind
`kinds`/`capture` protocol (CLI builds from this repository starting with #1099); the `heap` capture
kind requires a CLI build from #1110 or later, and the `thread-snapshot` capture kind requires a CLI
build from #1112 or later. Analyzing a dump file requires a CLI build from #1114 or later, which adds
the `dump` heap-capture source and the `dumpFile` field accepted by both the `heap` and
`thread-snapshot` capture kinds. A CLI installed from an older package may still support process
discovery (or CPU/heap capture) but reject an unrecognized capture kind or field; update it or point
`dotnetDiagnostics.cliPath` at a compatible executable. The extension fails with a concise
compatibility message instead of displaying CLI help output in the counters panel.
An unexpected exit after a successful JSONL handshake is reported as a connection exit, with
exit code/signal and bounded stderr evidence, not as proof of an outdated CLI.

The counters chart and GC pauses table only keep a rolling trailing window of history, not the
full session — matching how other real-time diagnostics panels (e.g. VS Code's own
`vscode-js-profile-flame` real-time view) bound their visible window instead of rendering an
unbounded timeline. The window length is controlled by the window-scoped
`dotnetDiagnostics.liveView.historyDurationSeconds` setting (default 120 seconds); changing it takes
effect immediately in any already-open panel. The running GC pause count and total paused time in
the headline remain session-wide totals — only the chart points and table rows are windowed.


## Host support

The extension runs in VS Code desktop Node.js hosts on Windows, macOS, and Linux, including
workspace extension hosts in Remote SSH, WSL, and Dev Containers. The CLI must be installed and
runnable in that same workspace host environment. Web extension hosts are not supported because
they cannot spawn the CLI child process. Exact OS/architecture support follows the CLI's published
RID matrix.

## Development

```sh
cd vscode-extension
npm ci
npm test
```

The Windows CI run sets `DOTNET_DIAGNOSTICS_CLI_DLL` to the built CLI assembly, enabling an
additional real-process smoke test of the JSONL handshake and reported CLI version. Without that
variable, the smoke test is skipped and the extension's unit tests still run normally.

The extension tests also build a real VSIX in a temporary directory and inspect its archive:
every manifest-declared walkthrough Markdown resource must be present, non-empty, and match
the source contents. The package check also keeps unrelated media and development files excluded.

The Marketplace icon and README screenshots live in `media/` and are generated by
`scripts/render-media.cjs` (`npm run media`; requires Chrome for Testing 153.0.8010.12 — set `CHROME_PATH` to that binary, or an auto-detected Playwright `chromium-1243` cache). Only
`media/icon.png` and the `media/walkthrough/*.md` Getting Started documents are bundled from
`media/` into the VSIX. README screenshots use absolute
`raw.githubusercontent.com/.../main/vscode-extension/media/` URLs, because vsce ignores
`repository.directory` when rewriting relative image links; they resolve once the change is on `main`. Marketplace search ranking and
indexing can only be observed after a release is published.

Open the `vscode-extension` folder in VS Code and press F5 to launch an Extension Development Host. The package
manifest is scoped to the workspace/machine; no executable path can be supplied by workspace
settings.

## Releasing

The extension is versioned independently from the CLI and the .NET packages. Bump `version` in
`package.json` (and `package-lock.json`) and push a matching `ext-vscode-v<version>` tag, for example:

```sh
cd vscode-extension
npm version --no-git-tag-version 0.2.0
npm test
git tag ext-vscode-v0.2.0
git push origin ext-vscode-v0.2.0
```

The `VS Code Extension Release` workflow verifies that the tag matches the package version, runs
the extension tests, packages a VSIX, uploads it as a workflow artifact, and publishes it to the
Visual Studio Marketplace. Configure the repository Actions secret `VSCE_PAT` with a publisher
token that has Marketplace management/publish access. The normal CI workflow continues to test
extension changes on pushes to `main` and pull requests; it does not publish from those builds.
