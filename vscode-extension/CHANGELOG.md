# Changelog

All notable changes to the .NET Diagnostics VS Code extension are documented here. The extension is
versioned independently from the CLI and the .NET packages.

## Unreleased

### Changed

- Marketplace metadata: categories are now `Debuggers` and `Visualization` (previously `Other`),
  and keywords, homepage, and bug-tracker links were added to improve discoverability.
- Clearer extension description covering live counters, GC pauses, CPU/heap/thread snapshots, and
  dump analysis.

### Added

- Process tree with refresh, filter, context captures and a per-workspace remembered selection.
- CLI-backed durable capture history with explicit new saved snapshots, offline drilldowns,
  paginated listing and confirmed deletion.
- One **Capture Diagnostics** picker for allocation, off-CPU, contention, thread pool, GC,
  GC/activity correlation, requests, Kestrel, database and networking captures, with
  structured tables, evidence quality and explicit unavailable/omitted data.

- Raster Marketplace icon and a dark gallery banner.
- README screenshots of live counters, heap drilldown, and thread snapshot panels. They are
  illustrative renders of the real webview panels using fixture data, not live captures.
- This changelog.

## 0.1.0

- Initial release: live runtime counters and GC pauses, point-in-time CPU, heap, and thread
  snapshots with follow-up drilldown views, and dump file analysis, backed by `dotnet-diagnostics-cli`.
