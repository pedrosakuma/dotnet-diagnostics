# Changelog

All notable changes to the .NET Diagnostics VS Code extension are documented here. The extension is
versioned independently from the CLI and the .NET packages.

## Unreleased

## 0.3.1

- Fix the Live counter chart staying blank when the panel was laid out hidden or at zero size; the chart now
  re-measures whenever the canvas gets a real size.

## 0.3.0

Use CLI **0.31.0 or later** for allocation exploration, sensitive-value reveal and array-range
navigation. Earlier protocol-v2 workflows remain usable with explicit compatibility guidance.

- Consolidate grouped thread stacks into a single tab with counts, shares, frames and representative
  thread links. Navigate to captured thread details and back without duplicate grouped views or new reads.
- Use accessible tabs for thread evidence views and the explorer's returned threads,
  waits and inferred cycles, with arrow/Home/End navigation and preserved selection.
- Label thread groups with missing/empty stacks as Stack unavailable,
  with an explicit explanation rather than blank stack content or an inferred failure cause.
- Place object-value reveal/hide beside the string preview, or above fields for other objects,
  instead of in the inspector toolbar. Preserve per-object consent and bounded preview behavior.
- Remove generic row disclosures that only repeat visible columns across heap/object, thread
  and diagnostic tables. Preserve actual nested fields, retention chains and navigation links.
- Keep heap type details beside the type list in wide live/dump panels. Narrow panels switch
  to details with Back to types, preserving table filters, sorting and scroll position.
- Improve CPU/allocation flame readability with short type/method labels, focused-share percentages,
  stable method colors and high-contrast text. Keep complete signatures/metrics in tooltips and
  details, support high-contrast themes, and scroll narrow panels instead of shrinking text.
- Add a sampled **Allocations** explorer with byte-weighted call tree/flame navigation, search,
  focus, subtree and caller/callee queries plus method/module/namespace rankings. Keep type/site
  tables, CPU state and tick semantics separate; require explicit CLI byte-unit confirmation.
  Enforce capture/aggregation limits with visible failures; sampling and ETLX conversion still
  have overhead and do not provide an exact object/lifetime tracker or a hard RSS budget.
- Explore generic struct array elements in place, including nested fields and exact reference
  links. Read previous/next or explicit array ranges of up to 8 elements, with a shared field
  budget, explicit omissions and independent per-read permissions. No Dictionary-only decoder.
- Add per-object sensitive-value reveal with separate, one-request native consent. Metadata-only
  stays the default; bounded previews are discarded on inspector close/navigation and are not
  cached in history or retained by redacted exports. Requires the matching CLI protocol support.
- Style heap type selection as a text link.
- Style sortable column headers as conventional table headings with direction indicators,
  rather than filled action buttons, while preserving keyboard activation and accessible sort state.
- Add visible, revocable live Object/GC root permissions with optional per-operation grants
  scoped to the current heap snapshot/CLI session. Native one-shot consent remains the default;
  captures and exports keep separate acknowledgements and no permission is stored persistently.
- Reduce stacked panes with state-preserving Heap/Threads evidence-view switching, thread explorer
  modes, and a diagnostic evidence-table selector. Hidden-query failures and inferred deadlock
  warnings remain discoverable. CPU hotspot summaries, inline thread summaries and exception
  aggregate tables remain available as collapsible supporting evidence.
- Add contextual heap object inspection, top-type filtering/sorting and captured retention-path
  focus; expandable root-to-object chains retain exact address navigation and live-read warnings.
- Connect thread selection, stack groups and wait-chain participants in a snapshot explorer;
  empty deadlock results remain neutral and inferred candidates use a warning rather than an error.
- Add connected retained-exception type/message/occurrence navigation, returned-row search,
  sorting in diagnostic tables, and receipt-time selection/zoom with counter and GC
  event inspection in Live. Filters remain explicit about returned/retained evidence boundaries.
- Organize live-panel capture actions/results into capture-specific tabs and add CPU sample drilldown views
  (triage, call tree, top methods, hot path, module/namespace aggregates, and callers/callees)
  against the captured artifact without re-sampling.
- Add linked expandable CPU call-tree and flame/icicle views with method/module search,
  exact loaded-branch focus, breadcrumbs/reset, selected-method evidence, and bounded
  method-based subtree queries. Omitted samples retain their visual weight; percentages
  name their denominator and cached profile navigation does not recollect.
- Check the CLI version during environment verification and report when protocol-v2 base workflows
  are available but the new allocation/value/range features need CLI 0.31.0 or later.
  The check offers an explicit update action without installing or updating the CLI.

## 0.2.0

Use CLI **0.30.0 or later** for all features in this release. CLI 0.29.0 still supports
the base protocol-v2 workflows; newer capture, navigation and export requests need the update.
The extension never installs or updates the CLI automatically.

### Changed

- Marketplace metadata: categories are now `Debuggers` and `Visualization` (previously `Other`),
  and keywords, homepage, and bug-tracker links were added to improve discoverability.
- Clearer extension description covering live counters, GC pauses, CPU/heap/thread snapshots, and
  dump analysis.
- Shorter Marketplace Overview focused on first use, with technical detail in the linked reference.

### Added

- Redacted measurement-only JSON/CSV exports in live, dump and saved-result panels.
- CPU investigation-summary export and explicitly confirmed local raw-trace capture/export.
- CLI-backed saved-capture baseline comparison with qualified deltas and unavailable-data reasons.

- Address navigation in heap drilldowns (#1168): exact-hex address links between retention paths,
  object fields, GC root paths and referencing statics, a Copy action on every valid address,
  bounded Back/Forward history with a breadcrumb, for live and dump panels. Live Object and GC
  root reads require an explicit acknowledgement (offline dump reads do not); all numeric
  addresses are flagged as imprecise, never linked or copied.
- Process tree with refresh, filter, context captures and a per-workspace remembered selection.
- CLI-backed durable capture history with explicit new saved snapshots, offline drilldowns,
  paginated listing and confirmed deletion.
- One **Capture Diagnostics** picker for allocation, off-CPU, contention, thread pool, GC,
  GC/activity correlation, requests, Kestrel, database and networking captures, with
  structured tables, evidence quality and explicit unavailable/omitted data.
- Guided Getting Started walkthrough and explicit environment checks with actionable CLI guidance.
- Managed-exception capture with type/message evidence, and crash-guard capture with
  stack and process-exit evidence.

- Raster Marketplace icon and a dark gallery banner.
- README screenshots of live counters, heap drilldown, and thread snapshot panels. They are
  illustrative renders of the real webview panels using fixture data, not live captures.
- This changelog.

## 0.1.0

- Initial release: live runtime counters and GC pauses, point-in-time CPU, heap, and thread
  snapshots with follow-up drilldown views, and dump file analysis, backed by `dotnet-diagnostics-cli`.
