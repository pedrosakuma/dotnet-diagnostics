# Market analysis: how existing tools show and process live diagnostic indicators

Context for planning the VS Code extension's panel expansion beyond live
counters (#1090, follow-ups #1098/#1099/#1100). Surveys how established
.NET and general-purpose profiling/observability tools present continuous
signals (counters, GC, exceptions) versus point-in-time captures (CPU
samples, heap snapshots), to ground our panel/protocol design in proven
conventions rather than inventing a UX from scratch.

## Surveyed tools

| Tool | Continuous signal model | Point-in-time signal model | Drilldown | Cross-capture comparison |
|---|---|---|---|---|
| **Visual Studio Diagnostic Tools** (CPU Usage / Memory Usage tools) | Single shared timeline graph (CPU %, memory) with small icon/line **markers** overlaid for GC, exceptions, break events | "Take snapshot" button creates a memory snapshot pinned to the timeline at that instant | Click a marker/snapshot to see what was happening at that moment (call stacks, allocations) | Yes — select two memory snapshots to diff what was allocated/retained between them |
| **JetBrains dotTrace / dotMemory** | Timeline profiling mode records CPU/threads/GC/I/O continuously; graph updates live | Manual or conditional "Get Snapshot" button creates a non-blocking point-in-time capture, shown as a marker/bookmark on the same timeline | Double-click a marker to open the full snapshot viewer (call stacks, retained objects) | Yes — comparing any two snapshots is a first-class, prominent workflow |
| **dotnet-monitor + Prometheus/Grafana** | Every signal (CPU, GC, memory, HTTP latency, exceptions) is a scraped time series rendered as a line/bar/heatmap panel, grouped by category, with alert thresholds and color coding (green/yellow/red) | Not really point-in-time captures — Grafana's model is uniformly time-series; a "snapshot" there is just a single scrape tick, not a richer capture artifact | Query editor / panel drill-down via PromQL, not a built-in artifact viewer | Possible via time-range comparison overlays, but not an artifact-level diff |
| **dotnet-counters** (console) | Simple scrolling table, one row per refresh interval, one column per counter — no graph, just current values | N/A — it's a pure continuous monitor, no capture concept | None — point readers pipe to `dotnet-trace`/`dotnet-dump` for anything deeper | N/A |
| **VS Code's own `vscode-js-profile-flame`** (built-in Node.js CPU profiler) | Has an explicit **"real-time view"** mode: a live-updating flame chart in a webview, with user-configurable `realtimePollInterval` and `realtimeViewDuration` (a rolling window, not unbounded history) | A completed `.cpuprofile` capture reopens as a static flame chart (left-heavy/time-ordered toggle) | Canvas-rendered flame chart with zoom/pan, hover tooltips, ctrl-click to jump to source | Not applicable (single profile per view) |

## Patterns common across tools

1. **One shared timeline, multiple overlaid signal types.** Every tool that
   mixes continuous and point-in-time data (Visual Studio, dotTrace/dotMemory)
   renders them on **one timeline**, not separate disconnected views —
   continuous signals as a line/area graph, discrete events as markers/icons
   on the same time axis. This directly validates the marker-overlay design
   already decided for #1100 (GC pauses as series, CPU capture as a marker on
   the same panel) rather than a separate tab per signal.

2. **Point-in-time capture is always an explicit, user-triggered, non-blocking
   action** ("Take Snapshot" / "Get Snapshot"), never automatic polling of a
   heavy collector. This matches the `capture` request design in #1099 — the
   user (or a future condition) triggers one bounded collection, not a
   continuous resampling loop.

3. **Bounded live history, not unbounded accumulation.** `vscode-js-profile-flame`'s
   `realtimeViewDuration` setting is the closest precedent to our own
   resource-boundedness conventions (`docs/resource-boundedness.md`) — a
   rolling window of recent data, explicit and configurable, not "keep
   everything forever." Our counters panel and the planned GC panel should
   adopt an equivalent bounded chart-history setting if one doesn't already
   exist.

4. **Comparison/diff between point-in-time captures is a distinct, heavier
   feature layered on top of the base capture mechanism** — not bundled into
   the initial capture UX in any surveyed tool. Visual Studio and
   dotMemory both support it, but as a deliberate second action (select two
   snapshots, then diff), confirming the plan to defer comparison out of
   #1100's scope ("comparação é outra batalha").

5. **Console/table tools (`dotnet-counters`) intentionally have no graph or
   capture model at all** — they're a fast at-a-glance monitor and nothing
   more. This is a useful lower bound: our panel should not feel compelled to
   replicate every visualization style, just the ones that fit a running
   timeline (graphs + markers), which is what the research-phase panel
   mapping (#1085) already aimed for.

6. **Drilldown is always opt-in and separate from the live view.** No
   surveyed tool tries to render full call-stack/retained-object detail
   inline in the live timeline — clicking a marker/snapshot opens a focused
   detail view. This matches our own "split collector, unified drilldown"
   MCP architecture (`query_snapshot`) and suggests the extension's
   point-in-time markers should similarly open a lightweight detail view
   rather than inlining everything into the timeline.

## Recommendations carried into the open issues

- **#1098 (Core GC session) / #1100 (extension GC panel + CPU capture):**
  render GC pauses as markers/series on the *same* timeline as counters
  (pattern 1), not a separate panel — already the planned design.
- **#1099 (CLI protocol generalization):** keep the `capture` request
  strictly single-shot and explicitly triggered (pattern 2); do not add
  periodic auto-capture in this round.
- **Follow-up (not yet an issue):** add an explicit bounded
  "chart history window" setting to the extension (pattern 3), analogous to
  `realtimeViewDuration`, before adding more concurrent signal kinds —
  worth its own small issue once #1100 lands.
- **Explicitly out of scope, consistent with earlier decision:** snapshot
  comparison/diff (pattern 4) remains a separate, later effort, likely
  building on `compare_to_baseline` concepts already in the MCP surface
  rather than reinventing one in the extension.

## Non-goals of this analysis

This is a design-input survey, not a feature commitment — it does not
obligate the extension to match every convention above (e.g., alerting
thresholds and PromQL-style querying from the Grafana model are out of scope
for a lightweight editor panel). Treat it as evidence for the choices already
reflected in #1098/#1099/#1100, and as a reference the next panel (exceptions,
contention, thread-pool, heap) should re-check against before diverging.
