# .NET Diagnostics for VS Code

This desktop/workspace-host extension provides a live runtime-counters and GC-pauses panel, plus a
point-in-time CPU capture, backed by the Core-owned EventPipe/CPU-sampling sessions in
`dotnet-diagnostics-cli`. It does not collect diagnostics in TypeScript and does not use the MCP
server as a data transport.

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

The extension adds a **.NET Diagnostics** icon to the Activity Bar. Open its **Live Diagnostics**
view and select **Start Live Counters**, or use the same command from the Command Palette. Choose a
discovered process, then use the panel's Stop/Start controls. One streaming session now carries
both the live counters chart and a **GC pauses** table (timestamp, generation, reason, type, pause
duration, plus a running pause count and total paused time) — both signals start and stop together.
The extension runs `dotnet-diagnostics-cli processes --json` for discovery and starts
`dotnet-diagnostics-cli stream --protocol jsonl` for live samples. Stdout is parsed only as protocol
frames. The CLI path can be set in the machine-scoped `dotnetDiagnostics.cliPath` setting; the
extension host must be able to execute it. Runtime, missing-tool, and protocol-version errors are
shown with actionable guidance.

Use **Capture CPU Now** (tree-view action or Command Palette command
`dotnetDiagnostics.captureCpu`) to take a single point-in-time CPU sample of the selected process.
Unlike the counters/GC timeline, a capture is a one-shot snapshot: it reuses the panel's active
streaming connection if one is open, or opens (and then closes) a short-lived CLI connection
otherwise, and renders its own top-hotspot table (module, method, inclusive/exclusive sample
counts) that is replaced by the next capture rather than appended to a history. A capture does not
start or stop the live counters/GC session, and it is not compared against any baseline.

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
**analyzes** an existing dump file — it does not capture/generate a new process dump, and it does
not support interactive gcroot/object-graph drilldown by address; both remain out of scope for this
command.

Heap and thread captures also show follow-up drilldown views. Before a non-GC-dump heap capture
(and before Analyze Dump File) a multi-select picker offers four opt-in enrichments, all unchecked
by default: **Static fields**, **Delegate targets**, **Retention paths**, and **Retained
exceptions** (the `retained-exceptions` view). The `thread-statics` view needs an exact type name,
so it is never queried automatically: after a thread snapshot, type the exact type name into the
**Query thread statics** box and press the button; an empty name is rejected in the panel. Both
views require a CLI build from #1126 or later; with an older CLI the new capture option or query
is rejected and surfaces as an unavailable view.

Live streaming requires a CLI build that supports `stream --protocol jsonl` and the multi-kind
`kinds`/`capture` protocol (CLI builds from this repository starting with #1099); the `heap` capture
kind requires a CLI build from #1110 or later, and the `thread-snapshot` capture kind requires a CLI
build from #1112 or later. Analyzing a dump file requires a CLI build from #1114 or later, which adds
the `dump` heap-capture source and the `dumpFile` field accepted by both the `heap` and
`thread-snapshot` capture kinds. A CLI installed from an older package may still support process
discovery (or CPU/heap capture) but reject an unrecognized capture kind or field; update it or point
`dotnetDiagnostics.cliPath` at a compatible executable. The extension fails with a concise
compatibility message instead of displaying CLI help output in the counters panel.

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
npm install
npm test
```

Open this folder in VS Code and press F5 to launch an Extension Development Host. The package
manifest is scoped to the workspace/machine; no executable path can be supplied by workspace
settings.
