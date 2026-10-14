# .NET Diagnostics for VS Code

This desktop/workspace-host extension provides a live runtime-counters and GC-pauses panel, plus a
point-in-time CPU capture, backed by the Core-owned EventPipe/CPU-sampling sessions in
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
The extension requires CLI **0.29.0 or later** (JSONL protocol version 2). Older CLIs are rejected
with a protocol mismatch error; update with `dotnet tool update -g dotnet-diagnostics-cli`.

New to the extension? Open the **Get started with .NET Diagnostics** walkthrough (`.NET Diagnostics:
Open Getting Started`). **`.NET Diagnostics: Check Environment`** runs the configured
`dotnetDiagnostics.cliPath` for the protocol-v2 handshake and `doctor --json`, then reports a missing
executable or .NET runtime, an outdated CLI, and ptrace/UID/permission problems with the CLI's own
fix. The check is host-only, so target-specific permissions (socket UID) are reported as not verified.
For a custom `cliPath` it gives path-specific update guidance instead of the global update command. It can copy the install/update command and recheck, but never runs it for you.

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

**Follow-up drilldown views.** After a heap or thread capture succeeds, the extension issues
follow-up `query` requests against the capture's handle (sequentially, over the same streaming
connection) and renders each result as its own collapsible section below the summary table; one
failing view shows an inline error without blocking the others.

- **Heap** (7 always queried): `roots-by-kind`, `finalizer-queue`, `fragmentation`, `gchandles`,
  `async`, `timers`, `alc`. Four more are opt-in via a multi-select QuickPick shown before a
  `live` capture (all unchecked by default): **Static fields** (`static-fields`), **Delegate
  targets** (`delegate-targets`), **Retention paths** (`retention-paths`) and **Retained
  exceptions** (`retained-exceptions`). The `gcdump` source
  skips the picker and the follow-up views, since the CLI rejects every drilldown view for it.
- **Thread snapshot** (4): `deadlocks` (open by default and highlighted — an inferred wait-for
  cycle), `unique-stacks`, `wait-chains`, `threadpool`. `thread-statics` needs an exact type name,
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
so JavaScript preserves all 64 bits for future address-based navigation. Address-targeted views
(`object`, `gcroot`, `objsize`, `duplicate-strings`) are not exposed through the streaming protocol
yet. These views require a CLI build from #1116 or later;
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
**analyzes** an existing dump file — it does not capture/generate a new process dump, and it does
not support interactive gcroot/object-graph drilldown by address; both remain out of scope for this
command.

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

The Marketplace icon and README screenshots live in `media/` and are generated by
`scripts/render-media.cjs` (`npm run media`; requires Chrome for Testing 153.0.8010.12 — set `CHROME_PATH` to that binary, or an auto-detected Playwright `chromium-1243` cache). Only
`media/icon.png` is bundled in the VSIX. README screenshots use absolute
`raw.githubusercontent.com/.../main/vscode-extension/media/` URLs, because vsce ignores
`repository.directory` when rewriting relative image links; they resolve once the change is on `main`. Marketplace search ranking and
indexing can only be observed after a release is published.

Open this folder in VS Code and press F5 to launch an Extension Development Host. The package
manifest is scoped to the workspace/machine; no executable path can be supplied by workspace
settings.

## Releasing

The extension is versioned independently from the CLI and the .NET packages. Bump `version` in
`package.json` (and `package-lock.json`) and push a matching `ext-vscode-v<version>` tag, for example:

```sh
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
