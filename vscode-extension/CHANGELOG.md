# Changelog

All notable changes to the .NET Diagnostics VS Code extension are documented here. The extension is
versioned independently from the CLI and the .NET packages.

## Unreleased

- Make the canonical Workspace navigation progressive and task-oriented with
  **Area → Evidence to inspect → Analysis** stages. Replace singleton selectors with explicit
  headings, use **Result summary** instead of disabled **Capture result**, distinguish local
  switches, retained queries and new collections, keep concrete capture/load actions beside the
  current state, and preserve focus, narrow-webview stacking, deep links and evidence ownership.
- Replace the heap evidence button row with one labelled view selector. Retention paths, roots,
  fragmentation, handles, timers, and other captured views remain available without competing
  visually with the selected type's instance and paging actions.
- Use the webview page as the single vertical scroll owner for live and dump heap workspaces.
  Preserve the wide type/detail columns and narrow stacked navigation without competing scrollbars
  inside the type list and details pane.

## 0.6.0 - 2026-10-29

Use CLI **0.33.0 or later** for the complete feature set in this release. Base protocol-v2
workflows still support CLI 0.29.0, and older capability-bearing CLIs continue to expose the
features they advertise. The extension does not install or update the CLI automatically.

- Add an evidence-first **Memory & GC overview** to the existing live counters panel. It uses only
  retained `System.Runtime` counters and observed GC pauses to show compact receipt-time tracks for
  managed heap, committed memory, allocation rate, observed Gen2 collections, and RSS when present.
  The headline reports only whether the observed post-Gen2 managed-heap floor was stable/lower,
  rising, or not assessable. Sequence gaps, missing counters, expired selections, session
  boundaries, EventPipe loss availability, and bounded-queue drops remain explicit rather than
  becoming zero or a continuous timeline. The contextual action either navigates to existing
  allocation/heap evidence or explicitly starts the existing heap-capture flow; opening the
  overview never collects. The overview itself adds no heap comparison, type-instance enumeration,
  region map, persistent investigation context, leak verdict, or new CLI protocol field.
- Add an evidence-first **CPU overview** that distinguishes observed on-CPU samples, inferred wait
  matches, and unavailable scheduler evidence. It links to existing CPU, allocation, thread, and
  request evidence without assigning application/runtime ownership or collecting automatically.
- Make live CPU baseline/current deltas and method movers readable at 390 px and 540 px by
  projecting table rows as labelled evidence cards without changing table semantics. Preserve
  method presence, per-capture samples, shares and denominators, objective deltas, unavailable
  reasons, evidence actions, forced-colors borders and keyboard-focusable controls.
- Add an incremental live CPU baseline/current view over the CLI's advertised bounded
  `cpuHandleComparison` contract. Keep the two most recent handles as an explicit earlier baseline
  and later current capture, show backend/evidence/quality/denominators/unavailable reasons,
  objective raw-sample and share deltas, and bounded method movers without improved/regressed or
  causal verdicts. Navigate from either side back to overview, call tree, hot path, top methods and
  callers/callees while preserving each profile's cached focus and filters. Keep saved-capture
  comparison, redacted export, direct expert views and older-CLI degradation separate.
- Make saved-capture comparison direction explicit and reversible after a candidate artifact view
  loads successfully. Search the paged catalog up to 500 entries, retain an out-of-window current
  artifact, allow intentional self-comparison, and group matching, unknown and known
  family-mismatch choices using existing metadata while leaving full compatibility to the CLI.
  Show baseline/candidate identities, collection quality and structured CLI reasons without adding
  performance verdicts or workload-equivalence heuristics.
- Add an optional bounded **Investigation** lens for the seven capability-versioned evidence kinds.
  Keep source captures independently owned and expose only reported temporal relations, explicit
  observed/inferred/unavailable states, collector coexistence limits and direct navigation to
  existing specialist workspaces. The minimum sync-over-async scenario corroborates recurring
  ThreadPool/long-request symptoms only when retained evidence supports them and otherwise abstains;
  it never auto-captures, persists, exports, scores or assigns causality.
- Add bounded exact-type instance navigation for live and dump heap snapshots. Pages preserve the
  selected type and existing addresses, use opaque CLI cursors, reject stale responses, and open
  returned addresses in the existing object and GC-root inspector. Live pages require explicit
  read consent and remain best-effort; gcdump rows report addresses as unavailable.
- Add a compact fragmentation orientation view while retaining the segment table as authoritative
  evidence. It reports observed material free blocks, bounded retained ranges, omitted counts, and
  complete/partial/indeterminate quality without claiming full heap topology, retained size,
  pinning concentration, or allocation success.

## 0.5.0 - 2026-10-23

The full existing feature set uses CLI **0.31.0 or later**; **Requests + completed
Activities** additionally requires the advertised `requestActivities` v1 capability,
available in CLI **0.32.1**. Base protocol-v2 workflows still support CLI 0.29.0.
The extension does not install or update the CLI automatically.

- Add an explicit **Requests + completed Activities** reproduction in Requests & networking,
  using only the CLI's advertised `requestActivities` v1 capability (introduced with CLI 0.32.1; CLI 0.31.0 and the unpublished 0.32.0 tag do not provide it as a release).
  Keep simple request captures unchanged. Exact ActivitySource names, a bounded window and an
  EventPipe privacy/cost notice precede collection; no automatic discovery or historical recovery.
  Check the CLI capability before configuration. Expand an exact retained request row to query
  related completed Activities inline, with a compact summary and narrow-pane-friendly evidence.
  Use a concise **Capture** action, clear completed progress messages, and place stable explanations
  below the data while keeping active warnings visible. Preserve parent request identifiers after load.
  Align completed Activity operation/time rows with request times, with local detail chevrons,
  In flight request markers and explicit Weak link / Unverified association markers.
  Show structural relationship separately from verification/withheld reasons, window, source,
  loss and omissions; durations are not causality, exclusive delay or a critical path.
  Use the shared replaceable generic result and only one loaded selected-request response.
  Replacement, expiry and disconnect disable CLI reads; retained displays are labelled stale.
  No MCP dependency, open-operation tracking, persistence or joint-evidence export.
- Prioritize captured data across CPU, Memory & GC, Threads & waits, Exceptions,
  Requests & networking, and Monitoring. Keep stable explanations in disclosures or
  below the evidence while retaining active quality warnings and boundedness notes.
- Improve CPU profile exploration and keyboard focus restoration. Distinguish retained
  profiles from new captures and preserve the existing bounded drilldown/cache ownership.
- Replace per-thread action lists with an aligned Thread / State / Top frame table and
  inline stack expansion. Preserve exact thread identities, filters, direct links and
  return focus without additional diagnostic reads. Numeric runtime flags remain explicitly
  not decoded; raw values are available in technical details.
- Clarify exception capture modes, retained occurrences, filters and focus. Revalidate the
  selected target throughout setup and discard stale responses without replacing current evidence.
- Resolve pending thread-query views after capture failure and preserve another domain's
  View selection when thread capture or follow-up work finishes in the background.
- Make the selected process and capture entry points easier to discover. Preserve separate
  Monitoring stream ownership and distinguish counter sessions and live-time boundaries.
- Add sortable Live Events headers across all retained, filtered rows before pagination.
  Show discreet Previous / Next controls only for multiple pages. Sorting pauses following;
  Follow latest restores descending source time and the first page.
- Remove redundant Networking narrative from the primary data view. Retain the raw summary in
  closed capture details, distinguish known informational acquisition facts from active warnings,
  and keep unknown loss, partial completion and parse failures visible.
- Align offline dump buttons with the live theme and remove the redundant Explore heap details
  action. Narrow heap panels expose types and captured breakdowns in one flow; type navigation,
  local sorting and keyboard return focus remain available.

Known limits: a type selection does not enumerate its object instances. Address-based object
and root inspection remains reachable from captured address links, not from a complete
type-to-instance browser; follow-up work is tracked in [#1255](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1255).
An extremely narrow dump panel can still require horizontal scrolling. These changes do not add
dump analysis capabilities, decode numeric thread flags, or establish capture completeness.

## 0.4.0 - 2026-10-21

Use CLI **0.31.0 or later** for the full feature set; the base protocol-v2 workflows still
support CLI 0.29.0. The extension never installs or updates the CLI automatically.

- Add a separate, bounded EventPipe Live Events stream with explicit provider selection and
  sensitive-payload acknowledgement, pause-following, search, paging and event details.
  Keep source-loss availability and local delivery/retention drops distinct; unsupported event
  schemas remain generic rather than being inferred as managed exceptions.
- Group sidebar capture entry points by profiling domain and introduce compact
  Domain / Evidence / View navigation for CPU and Memory & GC. Navigate only existing bounded
  evidence: CPU, allocation profiles, heap snapshots and generic captures retain their distinct
  ownership, query paths, permissions and replacement rules.
- Consolidate live counters/GC pauses and Live Events under Monitoring as independent local views.
  Switching views preserves each stream's lifecycle, status, retained buffer, selected event and
  delivery acknowledgements; navigation does not collect or reopen evidence. Route all generic
  capture results into their canonical Workspace domains, keep allocations on their dedicated
  profile path, and retire the obsolete Diagnostics result tab without removing the expert command.
- Move managed-exception and crash-guard results into the Exceptions domain of the compact live
  Workspace, retaining one dedicated, replaceable exception result independent of the generic
  capture slot. Bind statuses and results to the capture request and mode; navigation performs no
  new reads, stale responses are ignored, and failed or busy attempts preserve the current result.
  Keep the live EventPipe source, target PID, capture mode, retention notes, and sensitive-message
  disclaimer visible without turning exception modes into retained history.
- Place in-flight request, Kestrel, database, and networking capture results in the Requests &
  networking workspace. They reuse the single replaceable generic-capture result and expose no
  synthetic retained-query view or load action. Prioritize the in-flight request summary and
  retained rows, with bounded state filters and capture limitations visible beside the evidence.
  Display Started timestamps in UTC while preserving the original ISO value and chronological sort.
- Add Threads & waits to the compact live Workspace for point-in-time thread snapshots and
  off-CPU, contention, and thread-pool captures. Snapshot views remain distinct from generic
  event captures; generic results keep their single replacement slot and truthful cross-kind status.
  Thread snapshot capture, preload, and explicit statics-query responses are bound to evidence IDs.
- Keep generic evidence tables horizontally scrollable within their own wrappers so narrow
  workspaces do not overflow and every captured column remains reachable. Wrap table selectors
  and row filters within narrow panels, and allow long evidence notes to wrap.
- Gate CPU exports, retained-profile exploration and saved-view export/comparison on available
  evidence. Failed or rejected captures preserve prior results; redacted CPU measurements remain
  exportable after disconnection. Explain unavailable actions without removing keyboard focus.
  Disable idle Stop, empty live-time reset, empty Events Clear and permission revocation without grants.
- Theme webview text/search fields and dropdowns with native VS Code input/dropdown tokens,
  including keyboard focus, placeholders and disabled states across live, dump and saved evidence.
  Keep fields within narrow panes instead of imposing a fixed thread-statics minimum width.

- Include the Getting Started walkthrough Markdown in the VSIX so installed extensions can render all three steps.
- Keep result-tab keyboard navigation in visible DOM order, show selected receipt-time bounds on
  live controls, and distinguish a ready selected process from active live counters. Collection
  failures now retain their original reason and offer applicable Environment/Output recovery actions.
- Repair CPU export action binding while preserving measurement-only redaction and the retained
  capture's ownership and request correlation.
- Summarize saved comparisons with counts of observed up, down, flat and unavailable deltas.
  Direction is not a performance verdict; equivalent workloads, throughput and latency must be
  established separately.

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
