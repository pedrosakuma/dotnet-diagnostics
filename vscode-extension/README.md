# .NET Diagnostics for VS Code

Investigate .NET performance problems without leaving VS Code: watch runtime counters, GC pauses and
EventPipe events, find CPU hotspots, inspect heap and thread snapshots, and analyze existing dumps.

## What you can do

- **Watch a running app:** live counters and GC pauses, or a separate EventPipe/EventSource stream
  with bounded search and event details in the Monitoring view.
- **Capture a problem:** CPU, heap, threads, exceptions, allocations, contention and more.
- **Choose the next CPU question:** an evidence-first overview distinguishes observed on-CPU
  samples, inferred wait matches and unavailable scheduler state, then links to the existing
  call tree, hot path, allocations, threads and request evidence without collecting automatically.
- **Compare two live CPU profiles:** the two most recent retained handles become an explicit
  earlier baseline and later current capture. Inspect objective raw-sample and sample-share deltas,
  unavailable rows, quality, backend and bounded top movers, then return to either profile's
  call tree, hot path, top methods or callers/callees.
- **Explore allocation pressure:** sampled, estimated-byte call trees and flame graphs with
  method/module navigation, plus allocation type/site tables. Not exact object/lifetime tracking.
- **Keep useful evidence:** save supported snapshots and reopen them after the app has stopped.
- **Share and compare:** export redacted measurements, save raw CPU traces locally, and compare
  compatible saved captures against a baseline.

Saved comparisons start only from a successfully loaded artifact. They name that artifact before
selection and let you explicitly keep it as candidate (right) or reverse it into baseline (left).
The picker searches up to the latest 500 catalog entries, groups matching, unknown and known
family-mismatch choices without replacing CLI validation, and keeps an out-of-window current
artifact available. Results preserve side identities, collection quality and the CLI's structured
compatibility reasons.

Comparisons summarize numeric deltas as up/down/flat/unavailable, not performance verdicts. The
extension uses the CLI's historical comparison contract (compatibility and measurement semantics),
not journey verdict labels such as `not_assessed`. Missing values remain unavailable; workload
equivalence, throughput and latency must be established separately. The operation stays offline
and does not contact the original process.

Live CPU comparison is distinct from that saved-capture flow. It requires the CLI's advertised
`cpuHandleComparison` v1 capability and keeps only one in-memory pair on the owning connection.
Direction follows capture chronology and is not reversed in the extension. **Use current as
baseline for the next capture** clears the displayed pair without collecting; the next successful
CPU capture creates a fresh pair. No workload equivalence, causation, application/runtime
composition, multi-evidence correlation or automatic capture is inferred.

## Get started

Requires VS Code **1.96+** and the **.NET 10 runtime**, or a self-contained CLI. Install
`dotnet-diagnostics-cli` **0.33.0+** in the environment where VS Code runs your workspace:

```sh
dotnet tool install -g dotnet-diagnostics-cli
```

Already installed? Run `dotnet tool update -g dotnet-diagnostics-cli`. The extension never
installs or updates the CLI automatically. CLI 0.29.0 supports the base protocol-v2 workflows;
the environment check identifies older versions and warns when the complete extension feature set
is unavailable. CLI 0.31.0 added allocation exploration and sensitive-value/array-range reads;
CLI 0.32.1 added request Activities; CLI 0.33.0 adds guided CPU comparison, bounded investigation
evidence, heap type-instance navigation, and observed fragmentation projection. Capability checks,
not version assumptions, continue to gate each optional workflow.

1. Open **.NET Diagnostics** in the Activity Bar.
2. Run **Check Environment** to verify host permissions and CLI feature compatibility, then select
   your app in **.NET Processes**.
3. Choose **Start Live Counters**, or capture a snapshot while reproducing the problem.

The empty **.NET Processes** view links to **Open Getting Started**, **Check Environment**,
and **Refresh Processes**. A filter with no matches offers **Clear Filter**, distinct from
no discoverable diagnostic endpoints. **Active target** identifies the current panel target
for both tree and picker selection; a **remembered hint** never means an active attachment.
**Saved Captures** labels the CLI catalog on this extension host, not workspace-local history;
row tooltips include available artifact kinds and producer provenance, without assuming a workspace.

The **Live Diagnostics** action tree groups captures under **CPU**, **Memory & GC**,
**Threads & waits**, **Requests & networking**, and **Exceptions**. **Monitoring**
contains separate **Live counters & GC pauses** and **Live Events** views. Switching views
does not start or stop either stream, collect or reopen evidence, or change event acknowledgements.
Each stream keeps its own lifecycle, status, retained buffer and controls. Expand an action-tree
group to browse; only selecting an action enters its existing capture/consent flow.
Counter history remains bounded across Stop/Start. The panel identifies the current counter
session and retained session numbers; chart lines break at session boundaries and observed
sequence gaps rather than imply continuous collection. Sequence-gap counts belong to the current
counter/GC session, while the chart-series cap and GC totals remain panel-wide. The counter
identity includes its provider/name, reported kind and unit; the value axis auto-scales and is not
necessarily zero-based. Chart bounds and window controls use receipt time, not target time.
Click or use Left/Right/Home/End to mark the nearest retained observation without interpolation.
**Pause following** freezes the receipt-time selection, not collection or retention. **Follow
latest** resumes following. Window choices only select retained data, never extend retention;
the receipt-time disclosure exposes exact bounds and custom start/end sliders. Expired paused
selections can become empty, which is not a zero measurement.

Live Events configuration uses explicit provider names, a named EventSource level and a signed
decimal keyword mask; it does not guess provider presets. The panel keeps the requested PID,
providers, level and exact keyword value visible. **Waiting for first event** remains until a
non-empty event batch arrives, including after a restart; an empty stream does not establish
target inactivity. Newest rows appear first. Pausing shows arrivals and retention evictions while
acknowledgements continue; expired mounted rows and selected payload details are removed.
Quality distinguishes current-stream source/delivery counts from panel-lifetime UI evictions
and truncations. Nonzero counts and unknown source loss remain prominent, with every layer
available in the quality disclosure. **Clear** only removes retained rows and selection; it
does not reset quality accounting, the subscription or whether the stream has observed an event.
**Capture Diagnostics** remains the searchable expert catalog for all ten capture kinds. Its
generic results appear in their canonical **Workspace** area; allocations keep their dedicated
profile and query path.
The live panel's **Workspace** progresses through **Area → Evidence to inspect → Analysis** for CPU,
Memory & GC, Threads & waits, Requests & networking, and Exceptions. A stage with only one
meaningful option becomes a heading instead of a disabled selector, so ordinary captures show
**Result summary** rather than a disabled **Capture result** choice. A compact state label
distinguishes **Local switch**, **Retained query**, and **New collection**, while concrete actions
such as **Capture CPU samples** and **Load call tree** stay beside the current state. The evidence
identity and lifecycle disclosure keeps source, target, quality, replacement and display-only
context available. Merely changing Area, Evidence to inspect or Analysis never collects,
reattaches, elevates, reopens an origin or replaces evidence. Thread snapshots and
off-CPU/contention/thread-pool captures share their area's presentation without sharing evidence
ownership: the snapshot handle is independent from the single replaceable generic-capture result.
Standalone requests, Kestrel, database, and networking captures share the Requests & networking workspace and
the existing single replaceable generic-capture result; its **Result summary** heading is descriptive,
not a retained-handle query. Select the ordinary kind under **Evidence to inspect**, then use its
explicit capture action in the Workspace to use its existing parameter prompts. Replacement and
sensitive-data guidance is
visible before capture; ordinary captures cannot be cancelled here. A compact active-quality alert precedes the returned data, with full reasons
available at the alert. Metrics, full quality scope and stable table guidance follow the rows. In-flight request results lead with the capture-window summary and
retained request rows; state filters and search apply only to returned rows, with truncation and
capture-quality notes kept visible. Managed-exception and crash-guard results use a separate
single replaceable exception result, keep the selected mode and live EventPipe target visible, and
do not become history or queryable evidence by navigating. Results are temporary and are not saved;
sending a capture clears the current exception result; if collection then fails, it cannot be restored.
Exception messages can contain sensitive application data; a process exit alone does not prove an
unhandled exception. Started timestamps display in UTC; the original ISO value remains available
to assistive technology and in the timestamp tooltip. Monitoring, dump analysis, and saved evidence
keep their source-specific result surfaces.
**Analyze Dump File** and **Saved Captures** remain separate offline entry points.

**Requests + completed Activities** is a separate explicit reproduction action in Requests &
networking and the Command Palette. It requires the additive `hello.requestActivities` v1
capability. CLI 0.32.1 includes it, but the extension still checks the actual hello contract rather
than relying on the version string; missing capability is reported before configuration. No
fallback combination of independent captures is attempted. Enter exact ActivitySource names from
the app's configuration or documentation (no wildcards or automatic discovery), choose a
1–300 second window and review the EventPipe scope/privacy notice.
Stop the owning CLI's live counters first; independent Monitoring streams retain their lifecycle.
Coexistence with other DiagnosticSource sessions is not established.
Dispatch acknowledges the request, not EventPipe readiness.
Start capture before reproducing; it cannot recover operations from an earlier request.

The request table shows only first-accepted requests still pending at window close.
The **Capture** action keeps scope in the Evidence selector. A compact result summary shows
retained counts and the requested duration; retention, coverage and duration-interpretation
guidance follows the data. Active partial/loss/unavailable warnings remain next to the results.
Its All / Long-running filter uses a clearly labelled **local ≥ 1,000 ms threshold**, not the
standalone collector's classification. Expand a request row with the keyboard-operable chevron;
the first expansion queries completed Activities already retained by the exact CLI capture, not
the process, and starts no new capture. Collapse/reopen of the same row is local. A different row
replaces the sole cached response. The request's trace/span identifiers and start time remain
accessible in its inline disclosure after Activities load. Structural direct-child/descendant/shared-trace basis is separate
from verified/withheld status; finished durations are not a critical path or proof of exclusive delay.
Associated Activities use compact operation/time rows aligned with the request's time column.
Requests carry an **In flight** marker: their time is elapsed at window close, while Activity times
are finished durations. Flat indentation groups associations, not a call tree. **Weak link** marks
same-trace-only evidence; **Unverified** remains visible where verification is withheld.
Expand an Activity locally for source, timing, identifiers, relationship basis and withheld reason;
this performs no additional CLI query or collection.
The joint result replaces the shared generic slot.
CLI evidence expires after ten minutes, disconnect or another capture; loaded displays then say
retained-only and further reads are disabled. Only completed Activity Stop records are supported,
so empty results do not mean no open or uninstrumented work. No export, saving, telemetry lookup,
privileged attach or MCP connection is added. See the [reference](../docs/vscode-extension-reference.md#request-to-completed-activity-navigation).

For a guided introduction, run **.NET Diagnostics: Open Getting Started** from the Command Palette.

## Screenshots

*Illustrative panels using fixture data, not live captures.*

**Live counters and GC pauses**

![Live runtime counters and GC pauses (illustrative)](https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/vscode-extension/media/live-counters.png)

**Heap snapshot and drilldowns**

![Heap snapshot and GC roots drilldown (illustrative)](https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/vscode-extension/media/heap-drilldown.png)

**Thread snapshot**

![Thread snapshot and inferred deadlock candidates (illustrative)](https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/vscode-extension/media/thread-snapshot.png)

## Permissions and sensitive data

Run the CLI as the same OS user as your app. Some captures can briefly pause the target or
trigger a GC; live heap/thread inspection also needs OS tracing permissions. Higher-impact
actions ask for confirmation before running.

Captures can contain sensitive application data. JSON/CSV sharing exports intentionally exclude
application text and identifiers; raw traces are **not redacted**, require explicit confirmation,
and are only saved locally. No automatic upload occurs.

Windows, macOS and Linux desktop/workspace hosts are supported, including Remote SSH, WSL and
Dev Containers. Install the CLI in that same host environment; browser-only VS Code is not supported.

## Learn more

- [Detailed usage and troubleshooting](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/vscode-extension-reference.md)
- [CLI installation and reference](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/cli-reference.md)
- [Configuration and host support](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/vscode-extension-reference.md#prerequisites)
- [Development and release guide](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/vscode-extension-reference.md#development)
- [Report a problem](https://github.com/pedrosakuma/dotnet-diagnostics/issues)
