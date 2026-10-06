# Live diagnostics panel UX — GC series + point-in-time capture

Status: proposed. Feeds #1098 (Core GC session), #1099 (CLI protocol
generalization), #1100 (extension panel), #1102 (bounded history window).

## Context

The counters panel shipped in #1091-#1093 is the only concrete instance of the
"live streaming panel" pattern today (`CounterPanelController` in
`vscode-extension/src/extension.ts`, one status bar item → one command →
one webview). #1100 extends it to a second continuous signal (GC pauses) and
a first point-in-time signal (CPU capture). The high-level direction was
already validated against market conventions
(`docs/research/live-diagnostics-panel-market-analysis.md`): one shared
timeline, continuous series overlaid with discrete markers, explicit
non-blocking capture, no cross-snapshot comparison. This doc turns that
direction into concrete, implementable decisions so #1100 isn't blocked on
product ambiguity.

## 1. Panel layout: one shared timeline, stacked series, shared time axis

- The webview keeps a single chart surface per session, not one chart per
  signal kind. All continuous series (counters, GC pauses) share the same
  X axis (wall-clock time since session start).
- Continuous numeric series with heterogeneous scales (e.g. `cpu-usage` %
  vs GC pause duration in ms) do **not** share a Y axis. Render as stacked
  mini-tracks under the same time axis (like VS's Diagnostic Tools strip),
  each with its own small Y scale and label on the left — not a single
  overloaded axis with two unrelated units.
- Each track has a checkbox-style legend entry (series name + color swatch)
  that toggles visibility without stopping collection — purely a render
  filter, matching "turn off a series without losing data" from the
  surveyed tools.
- Point-in-time captures (CPU) render as a **vertical marker line** at their
  timestamp, spanning all tracks (so it's visible regardless of which track
  the user is looking at), with a small diamond glyph at the top the user
  can click/hover.

```
┌─ .NET Diagnostics: <process> ───────────────────────────────┐
│ ☑ cpu-usage        ╭─╮      ╭──╮         ◆ CPU capture      │
│ ───────────────────╯ ╰──────╯  ╰──────────┆─────────────────│
│ ☑ gen0-gc-count    │        ▏              ┆                │
│ ───────────────────┴────────┴──────────────┆─────────────────│
│ ☑ gc-pause-ms          ▂  ▅        ▂        ┆                │
│ ───────────────────────┴──┴────────┴────────┆─────────────────│
│                                              └ hover for detail│
└───────────────────────────────────────────────────────────────┘
  [⏸ Pause] [⏹ Stop] [📷 Capture CPU now]           00:42 elapsed
```

## 2. Point-in-time marker interaction

- **Hover** the diamond glyph → lightweight tooltip: capture kind, timestamp,
  1-3 headline numbers (e.g. for CPU: process CPU %, top method if cheaply
  available). No navigation, no modal — matches "inline summary" from the
  market analysis.
- **Click** the diamond glyph → expands an inline detail card docked below
  the chart (not a new webview/tab), showing the full `capture` response
  payload from #1099 in a simple key/value or small table view. Click again
  (or click elsewhere) collapses it.
- No dedicated "capture browser" view in this round. If the user captures
  more than ~5-10 times in a session, markers simply accumulate on the
  timeline at their real timestamps — we do **not** build a side list/gallery
  now. (That's the natural seed of future comparison tooling, explicitly
  deferred.)

## 3. Capture action placement

- Primary entry point: a **panel toolbar button** "Capture CPU now" shown
  only while a session is active (next to existing Pause/Stop), consistent
  with "it's a per-session action", not a global one.
- Mirrored as a command (`dotnetDiagnostics.captureCpu`) in the command
  palette, enabled only when a panel/session is active — same pattern
  already used for `startCounters`/`stopCounters`.
- Not added to the global status bar: the status bar item stays reserved for
  session start/stop (its existing single responsibility); overloading it
  with a second action regresses discoverability of the primary toggle.

## 4. Bounded history window (#1102) settings UX

- New setting `dotnetDiagnostics.liveView.historyDurationSeconds`
  (default **120**), editable via standard VS Code Settings UI
  (`package.json` `contributes.configuration`) — no custom in-webview slider
  in this round, to keep #1102 small and consistent with how the extension
  already exposes configuration (if any precedent exists, follow it; if not,
  this is the first).
- Chart tracks evict/drop points older than the window on each incoming
  observation (client-side, mirrors the server-side bounded-collector
  pattern in `docs/resource-boundedness.md`).
- Point-in-time markers (captures) are **not** subject to the rolling window
  by default — they're sparse, user-triggered, and intentionally kept for
  the life of the panel so the user doesn't lose a capture they just took.
  Revisit only if this proves to leak in practice.

## 5. Non-goals (unchanged from #1100)

- No cross-snapshot comparison/diff UI.
- No capture gallery/list view.
- No per-series Y-axis customization (fixed auto-scale per track for now).

## Open questions for follow-up (not blocking #1098 start)

- Exact color palette / theme-awareness (light vs dark) for tracks and the
  marker glyph — defer to implementation time, follow existing VS Code
  theming tokens used elsewhere in the extension.
- Whether `historyDurationSeconds` should have a "unbounded" option — lean
  no (contradicts the whole point of #1102) unless a concrete use case shows
  up.
