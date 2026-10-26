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
the stream. Sortable headers cover source time, provider, event, numeric level and summary across
all retained search matches before slicing bounded 100-row pages. **Previous / Next** and the
shown range appear only when multiple pages exist. Sorting or paging pauses presentation without
stopping ingestion or acknowledgements; **Follow latest** explicitly restores descending source-time
order and the first page. Selection remains tied to the exact observation until retention evicts it.
The table supports bounded search and one selected event detail view. Managed
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
The native empty view links to **Open Getting Started**, **Check Environment**, and
**Refresh Processes**. No discoverable endpoints and no filter matches are distinct states;
the latter has a **Clear Filter** action. The tree labels the current controller-selected panel
target **active target**, including selections made through the quick picker. Persisted selections
are only **remembered hints**, never active targets after restart. Selection is not collection.

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

The live panel has **Monitoring** and **Workspace** tabs. Monitoring contains independent
**Live counters & GC pauses** and **Live Events** subviews.
The Workspace domain selector includes **CPU**, **Memory & GC**, **Threads & waits**,
**Requests & networking**, and **Exceptions**. Its compact **Domain → Evidence → View** controls
navigate one content panel.
Captured charts, rows, stacks and occurrences lead each result. Quantitative summaries use
small secondary text, while active loss, withheld verification, stale/unavailable evidence and
inferred candidates remain visible in compact alerts. Full reasons stay inspectable beside
those alerts. Static scope, retention guidance, export explanations and complete metrics follow
the data or use keyboard-operable disclosures. Sensitive-data and replacement warnings stay
at the capture action; native consent still presents the full policy before dispatch.
Thread snapshots and off-CPU, contention, and thread-pool captures appear under Threads & waits;
the point-in-time snapshot and the generic event capture remain separate evidence kinds.
Those three generic kinds share the same single replaceable result slot used by GC and the other
generic captures. A successful capture replaces that slot across domains, and the previous domain
reports that its evidence is no longer current. A failed attempt does not replace the slot.
Requests, Kestrel, database, and networking captures share that generic result slot in Requests &
networking. Managed-exception and crash-guard captures retain their separate single replaceable
exception result owner and display the actual mode, live EventPipe source, target PID, and bounded
retention notes. Busy or failed attempts before capture starts preserve the prior exception result;
once a replacement capture starts, the prior result is cleared. Request identities prevent late
status/results from replacing newer evidence, and status completion does not reclaim navigation
focus. Switching to Exceptions only displays already-captured evidence; it starts no CLI operation.
Exception messages may contain sensitive application data, and process exit alone does not prove
an unhandled exception. Monitoring and offline dump/saved panels remain separate result surfaces.
Groups organize collection entry points, not permissions: actions still require their actual
evidence prerequisites. Retention paths, thread wait chains, heap object inspection and other
drilldowns remain beside their evidence, not separate collectors.
**Analyze Dump File** remains a standalone action for an existing file with no selected process
required. **Saved Captures** remains a sibling view; **Capture and Save Snapshot** stays on the
process context menu and explicitly collects a new heap/GC dump/thread snapshot. Grouping does not
add persistence for other captures or convert current results into saved evidence.

For ordinary Requests, Kestrel, Database, and Networking captures, choose the kind in
**Workspace → Requests & networking → Evidence** and select **Capture**. The existing duration,
row-count, and applicable threshold prompts still apply; merely navigating never captures.
The Workspace and duration prompt disclose successful replacement of the shared generic result
before dispatch. No extra history or cache is retained, and ordinary captures have no verified
backend cancellation control here. One compact active-quality alert precedes returned tables; expand it for full loss
limitations and notes. The complete quality matrix, metrics and stable
search/sort/relationship guidance follow the captured rows.
Networking does not repeat the collector's long narrative before the operation table; the raw
summary remains in **Capture quality and scope**. Known normal acquisition facts (normal drain,
zero reported transport loss and zero parse errors) and static latency definitions stay there,
not in a warning. Unknown loss, early/failed completion, parse errors, unrecognized notes and
other limitations still have a visible compact alert; zero-valued counts alone never hide a limitation.
Diagnostic capture setup is bound to the selected target. If the selection changes during
the picker, parameter prompts, acknowledgement or CLI acquisition, no capture is dispatched;
select the intended target and start again.

**Saved Captures** lists the CLI's durable capture catalog, 50 entries per page (up to 500 loaded,
with an explicit cap notice), with refresh,
reopen and confirmed CLI-managed deletion. It survives extension and CLI process restarts;
ordinary panel results and ephemeral handles are not history entries. On a process, choose
**Capture and Save Snapshot** to collect a **new** live heap, gcdump or thread snapshot, rather
than converting an older panel result. Saving requires explicit high-risk acknowledgement
(suspension risk for live heap/threads, a blocking Gen2 GC for gcdump). Saved evidence can contain
sensitive data and remains on disk until deleted.
The view explicitly labels the catalog on this extension host, not workspace-local history.
Rows expose artifact kinds; tooltips include reported producer PID, runtime, and capture start
when supplied by the CLI. Missing provenance is labelled as unreported. Origin workspace is
not reported and is never inferred from the current folder or process selection.

Reopened panels query only advertised offline views by capture/artifact IDs, never contact the
original process, and reuse the shared drilldown renderer. Heap types, thread summaries and GC
measurements also have structured saved-evidence tables. Unavailable data and omissions remain
visible. Views needing unsupported parameters are labelled **use CLI**; interrupted captures
must be recovered with the CLI first. CPU/exception persistence is not exposed by this UI.
History calls inherit the CLI artifact root (including `MCP_ARTIFACT_ROOT`), have a 60-second
deadline and a combined 8388608-character output cap; failures are explicit, not partial JSON.

Open **Workspace**, choose **CPU** under **Domain**, and select **Capture** (also available as the **Capture CPU sample** tree-view action or
Command Palette command `dotnetDiagnostics.captureCpu`) to take a single point-in-time CPU sample
of the selected process.
Unlike the counters/GC timeline, a capture is a one-shot snapshot: it reuses the panel's active
streaming connection if one is open, or opens a capture-only CLI connection otherwise.
The connection is retained while the panel owns a CPU export or allocation-explorer handle and closes with the panel.
The capture renders its own top-hotspot table (module, method, inclusive/exclusive sample
counts) that is replaced by the next capture rather than appended to a history. A capture does not
start or stop the live counters/GC session, and it is not compared against any baseline.
The current CPU profile cannot be saved or compared in the extension; the CLI's separate retained-artifact
CPU comparison workflow and its limits are documented in [Historical comparisons](./historical-comparisons.md#supported-matrix).
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
whole table. At widths of 850 px or less, details follow the type list in a single flow, so
all captured breakdowns remain accessible without an extra navigation button. **Back to types**
returns focus to the selected type or type table and preserves filters, sort and page scroll position. In the domain workspace,
**View → Heap details** navigates to the captured details without choosing a type; **Snapshot totals**
returns to the type list. Neither live nor dump analysis has an **Explore heap details** button.
Navigation uses the same responsive pane and starts no additional diagnostic reads.

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
never recollect on navigation. A new allocation capture replaces the displayed profile. If no
allocation call tree is returned, the panel says that the response does not identify why attribution
is unavailable and suggests checking capture quality/retrying under representative allocation
activity; it does not claim a cause or diagnosis.

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

### Request-to-completed-Activity navigation

**Requests + completed Activities** (`dotnetDiagnostics.captureRequestActivities`) is an
explicit reproduction action under **Requests & networking**, also present in that Workspace
domain and the Command Palette. It does not alter the standalone in-flight request capture.
The CLI must actually advertise `hello.requestActivities` version 1, its two query views and
bounded limits. The capability is available in CLI 0.32.1; the general CLI 0.31.0 feature floor
does not imply support. CLI 0.32.1 includes this capability, but the extension checks the actual
hello contract. Unsupported/missing capabilities produce actionable guidance before configuration
and without dispatching a capture. Communication remains extension → CLI → Core only.

Enter 1–20 exact ActivitySource names from the application's configuration or documentation,
each 1–200 ASCII letters/digits or `.`, `_`, `-`; comma separators and surrounding whitespace are
accepted by the input UI. No source is preselected, discovered or wildcard-enabled. Choose
1–300 seconds and review the explicit
EventPipe scope, overhead, privacy and replacement notice. This is not a new privileged/high-risk
attach consent. The owning CLI's live counters must be stopped first; independent Live Events
retain their lifecycle. Coexistence with other DiagnosticSource sessions is not established. Collection starts only
after this action; `capture-started` acknowledges dispatch, not provider readiness.
Startup latency and instrumentation coverage remain limitations.

The main table shows at most 100 first-accepted requests still pending at observation close,
not the oldest requests or completed-request history. All / Long-running counts operate on
shown rows; **≥ 1,000 ms is a local UI-derived threshold**, independent of the standalone
collector's configurable classification. Expand a request row using its keyboard-operable
chevron. Its first expansion explicitly queries the retained CLI artifact, binding the connection,
handle, capture ID and request row ID—never a method/path or trace ID alone. It reads immutable
captured evidence, not the process again, and does not start a new capture. Collapse/reopen the
same row locally; expanding another replaces the one cached response. A late response for a row
that is no longer expanded is discarded. Retry an errored or superseded lookup explicitly from
its expanded row.

The related view preserves request identity, exact sources, process lifetime, window and quality.
It distinguishes structural `direct-child`, `observed-descendant` and `same-trace-only` bases
from verified relationships or explicit verification-withheld reasons. A structurally direct
child remains unverified when quality prevents verification. Finished durations are wall-clock
observations, not causal explanations, exclusive delay or a critical path; never sum concurrent
durations. Only completed Activity Stop records are retained (up to 200). A query shows up to
100 rows with total/omitted counts. Empty results do not establish no work: open operations and
missing instrumentation/coverage are unknown. Partial completion and source loss are called out
before the request data; unverified relationship reasons stay visible on each Activity row.
Remaining counters and provenance are available below the data.

One joint result occupies the existing shared generic slot; there is no independent multi-kind
history. Only one selected-request response is cached. The inline expanded row shows a compact
summary before its evidence, with source/identifier/timing details available in secondary
disclosures and full technical quality/provenance after the data. Partial completion, event loss,
and unverified relationship reasons remain visible without expanding technical details. Narrow
panels keep the request table compact and wrap long names; the details remain within the same
workspace rather than opening a separate view.
Invalid input or a rejected joint request preserves prior evidence until dispatch is acknowledged.
Any ordinary capture on the owning CLI connection invalidates joint read capability at dispatch.
A new generic result releases the prior joint UI state; unrelated CPU, heap, thread and exception
owners retain their existing behavior.

The CLI artifact expires ten minutes after completion or on disconnect/replacement. Further
queries are disabled; any still-displayed loaded evidence is explicitly labelled retained-only,
not live or queryable. Stale responses cannot replace a newer owner. Cancellation can return
explicit partial evidence with verification withheld. No automatic persistence, export, historical
recovery, origin reopening, telemetry lookup, privileged attach or MCP dependency is added.
Open operations remain a separate Part 2. See the
[CLI contract](./cli-reference.md#joint-requestactivity-reproduction-capture-part-1) and
[resource limits](./resource-boundedness.md#joint-requestactivity-capture-part-1).

### Exception captures

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

Start capture **before** the workload. The extension confirms when it sends the request, but the
protocol does not report when EventPipe is ready. A one-time failure may therefore happen before
the capture window; keep the workload running or repeat the problem throughout a new capture rather
than relying on a fixed startup delay. Sending a capture clears the current exception result; if
collection then fails, it cannot be restored. Results are temporary and are not saved by this
panel. A busy connection rejects exception capture with retry guidance instead of queueing it behind an earlier
capture, query or session startup. Captures do not start/stop the live counters session. These
capture kinds require a CLI build containing
the #1166 JSONL extension; older protocol-v2 CLIs report the kind as unsupported with update guidance.

Use **Capture Heap Snapshot** (tree-view action or Command Palette command
`dotnetDiagnostics.captureHeap`) to take a single point-in-time heap snapshot. Unlike CPU sampling,
this capture requires choosing a **source**: **Live** (ClrMD) or **GC Dump** (EventPipe). Both
sources are classified High risk / Acknowledge in Core's invocation-safety registry (CPU sampling
is only Moderate), so after picking a source and any optional enrichments the extension shows a
native modal naming the application and PID, source, selected extra heap data, and impact. A live
walk uses a platform-specific privileged attach, may suspend the target, and exposes heap type and
object-graph metadata; a GC dump induces a managed GC and exposes aggregate heap type metadata; both
may expose possibly confidential data. The extension cannot stop a dispatched capture. Its
120-second result-wait timeout is a frontend limit, not a guarantee that target-side work has
stopped. The request is sent only after you select **Acknowledge and Capture**. The result panel
shows the top types by total bytes (type, instance
count, bytes), the suspend duration for `live` captures, and any concerning GC-dump completion flags
(e.g. timed out, reader failed) for `gcdump` captures; each capture replaces the previous rendering.
A live heap walk requires the host's corresponding privileged attach access (on Linux, see
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
as heap's `live` source, so the extension shows a native modal naming the application and PID
before sending the request. It explains that a platform-specific privileged ClrMD attach may
briefly suspend the target and exposes stack, type, and method names, some of which may be
confidential. The extension cannot stop a dispatched capture; its 120-second result-wait timeout
does not bound suspension or guarantee target-side cancellation. The request is sent only after
you select **Acknowledge and Capture**. The result panel shows each thread's
managed thread id, state (flagged when likely blocked), inferred wait reason, and its top stack
frame, bounded to a summary-first table; each capture replaces the previous rendering. A live thread
snapshot needs the host's corresponding privileged attach access (on Linux, see AGENTS.md's
"🪪 `CAP_SYS_PTRACE` for live memory readers" section) and fails with an actionable
permission-denied message when that access is unavailable.

In **Workspace > Threads & waits**, **Thread explorer** is the default after a successful snapshot
and is also directly selectable in **View**. Its compact table aligns **Thread**, **State** and
**Top frame**. Each thread's chevron expands its stack immediately below that row, in
top-of-stack-first order; several stacks can remain open. Native buttons support Enter/Space,
and top-frame previews wrap to two lines while expanded stacks preserve the full frame text.
Right/Left arrows expand/collapse, and Escape from a stack collapses it and returns focus to
its thread. The labeled filter searches returned thread fields and frames only; expansion is
preserved by managed thread ID while filtering or receiving related evidence. A new snapshot
resets it. Raw numeric runtime flags are shown as **Not decoded**, not guessed as running or
waiting; **Technical details** retains the exact reported state, OS ID and inferred wait reason.
Missing frames remain explicitly **Unavailable**. The
inline summary and each returned thread ID in related evidence link directly into the expanded
row; returning to related evidence restores focus to the originating link.
The **Thread explorer** connects searchable returned threads, expandable stack groups,
representative group member IDs, inferred cycle participants and wait-chain owners. Selecting
a thread shows all its returned inline frames and related wait edges; selecting an owner moves
to that participant. A participant omitted from the inline list is explicitly unavailable, not
invented from another thread's stack. Group counts may exceed the representative IDs returned.
All of this is snapshot navigation, not a scheduler timeline; a new snapshot resets selections.
Its mode selector switches between **Returned threads**, **Wait chains** and **Inferred cycle
candidates**, preserving the selected thread. Grouped stack evidence remains available from
**View**. The original inline table remains available under **Inline thread summary**. The Workspace
view selector and status identify inferred deadlock candidates and unavailable/pending snapshot
queries; a terminal capture or query failure is shown as unavailable rather than left pending. If
the owning CLI connection closes, returned evidence remains display-only and live thread-statics
queries are disabled.

**Follow-up drilldown views.** After a heap or thread capture succeeds, the extension issues
follow-up `query` requests against the capture's handle (sequentially, over the same streaming
connection). Heap and saved/dump thread results have an **Evidence views** switcher instead of stacking all
sections vertically. Switching displays one result at a time and preserves its controls and
selection, without querying again; new results do not steal the selected view. In the live thread
snapshot Workspace, the **View** selector labels pending/unavailable queries and returned inferred
cycle candidates, while its visible status reports failures and warnings. Heap snapshots whose
retained connection closes remain display-only, and live object/GC-root address follow-ups are
disabled. Thread explorer is the default live thread view; detailed query responses remain
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

The `fragmentation` view keeps the ranked segment table as the authoritative detail and precedes it
with a compact observed free-block summary for each segment. A used/free/unaccounted strip is
orientation only and explicitly does not claim hole topology. The summary distinguishes missing
projection fields from numeric zero, shows the largest observed material free block and observed
material-block count, and reports the producer's `complete`, `partial`, or `indeterminate` state.
An accessible disclosure contains at most the bounded retained ranges, observed/retained/omitted
counts, exact addresses, material threshold, retention limit, provenance, and limitations. Opening
this disclosure does not start a live read, and its range bounds are deliberately display-only
rather than object-navigation links. The evidence describes directly observed CLR free records; it
does not establish allocator success, object liveness, retained size, or pinned-object
concentration.

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
`address`. For a live panel that read uses the host's privileged attach mechanism and may suspend
the target (High risk; Linux uses ptrace), so
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
`package.json` (and `package-lock.json`) and, only when publication is authorized, push a matching
`ext-vscode-v<version>` tag, for example:

```sh
cd vscode-extension
npm version --no-git-tag-version 0.2.0
npm test
git tag ext-vscode-v0.2.0
git push origin ext-vscode-v0.2.0
```

The `VS Code Extension Release` workflow verifies that the tag matches the package version, runs
the extension tests, packages a VSIX, uploads it as a workflow artifact, and publishes it to the
Visual Studio Marketplace. A matching tag push therefore publishes; do not create or push one as
part of release preparation. Configure the repository Actions secret `VSCE_PAT` with a publisher
token that has Marketplace management/publish access. The normal CI workflow continues to test
extension changes on pushes to `main` and pull requests; it does not publish from those builds.

### 0.5.0 preparation evidence and limits

The latest installed layout-smoke source is `f1a2105f3f522da2ec5e90896c2584ead57b3ef5`.
Release preparation subsequently corrected terminal thread-query failure state and
cross-domain View ownership, with emitted-webview regression coverage.
The extension suite passed 317 tests with three skips (320 total), and independent
integration and focused follow-up reviews were completed. The Windows smoke used an
isolated, instrumented installed VS Code 1.143 host and the official CLI 0.32.1 release,
with release checksums and attestation verified. A separate development-loaded command
bridge invoked the installed candidate; this is not an uninstrumented-host or
assistive-technology certification.

Installed candidates returned populated CPU, allocation, exception, counter/GC,
thread, request, Kestrel, database, Networking and Live Events evidence. A separately
confirmed joint reproduction returned two requests and four completed Activities,
with two associated rows in a retained request query; an earlier empty reproduction
remains inconclusive. The frozen batch used `645ab937`; Networking and Live Events
fixes were rechecked on `6ed1327`, and subsequent dump layout changes were checked on
`a16b1aa` and `f1a2105`. The whole matrix was not repeated at each revision or against
the versioned release package. Package identity changes require separate integrity
verification; these source smokes are not an exact release-VSIX capture certification.

Live Events initially exposed 158 rows and drained to 159 before stopping; source
loss availability remained unknown, not an inferred one-event loss. Exception
collection observed 640 occurrences and retained 100: the difference is a retention
omission, not necessarily EventPipe loss. An explicitly approved owned sample dump
was generated locally and reopened read-only, without optional enrichments, for
populated heap/thread analysis and local root-category navigation. No sensitive dump
or local smoke profile is part of the release. Optional object values, complete
type-to-instance navigation, off-CPU/elevated paths, every interaction permutation,
and assistive-technology behavior were not certified. An extremely narrow dump
viewport retained horizontal overflow. Bounded synthetic normal/narrow renderer
checks supplement, but do not replace, installed-host evidence.

### 0.4.0 preparation evidence and limits

The 2026-10-21 preparation baseline is `4e52d0586e354f3aadedec2202a3d05ad7cdb02b`.
Full main CI [run 39664451981](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/39664451981)
and Kind Integration [run 39664451789](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/39664451789)
completed successfully for that source. These runs predate the 0.4.0 package and do not validate
its VSIX. The rebuilt 0.4.0 VSIX passed manifest/changelog/walkthrough-resource integrity checks
(59 packaged files), installed and activated in an isolated VS Code 1.143 profile, and its bundled
CLI runner/parser completed a JSONL handshake with a rebuilt CLI
`1.0.0+4e52d0586e354f3aadedec2202a3d05ad7cdb02b` (protocol v2). That is the local build's default
assembly version, not a published CLI release or evidence of full feature compatibility.
The supported full-feature floor remains CLI 0.31.0; CLI 0.29.0 remains the base protocol-v2 floor.
The installed **Check Environment** command did not complete in the automation harness, so no
successful installed environment-check or full-compatibility result is claimed.
Independent release review found a missing saved-comparison changelog entry. The corrected VSIX
was rebuilt and archive-checked: only `extension/changelog.md` changed; runtime and resource bytes
match the installed candidate. The extension suite passed 255 tests with one skip, including a
real handshake against that CLI build. This is package-integrity,
protocol, and minimal-launch evidence, not a full VS Code interaction or capture-correctness
certification; a second full capture matrix is not part of this preparation.

The installed 0.3.1 candidate was exercised on Windows VS Code 1.143 with CLI
`0.31.0+4e52d0586e354f3aadedec2202a3d05ad7cdb02b` (protocol v2). One instrumented packaged
allocation capture returned 418 events and 44,635,168 estimated bytes; a 12-node call-tree query
on the same handle returned the same root-byte count. An earlier empty allocation result remains
unexplained and is deferred to [issue #1244](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1244);
this evidence does not establish that it is fixed. Live Events passed a bounded
Microsoft-Windows-DotNETRuntime sample alongside counters, including pause/filter/clear/stop
interactions, but source-drop availability was unknown; only local drop counters were zero.

A trusted-worker import of the known portable fixture succeeded on native WSL Ubuntu 24.04 and
produced sealed records-only captures with a bounded one-row query. Because the VSIX Saved Captures
view excludes `records`, this did not verify GUI query/export, and it does not establish supported
container import. The earlier nested Docker fixture attempt was inconclusive harness evidence
(zero captures published), not a product regression. One explicitly authorized 337,129,472-byte
full sample dump was collected through diagnostic IPC; the installed extension reported complete
heap and thread-snapshot captures and opened a redacted JSON export as an unsaved document, which
was discarded. Individual view payloads were not inspected, so this is not a full query-correctness
claim. These accepted limits apply to preparation only and are not publication certification.
