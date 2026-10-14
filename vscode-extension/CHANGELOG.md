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

- Address navigation in heap drilldowns (#1168): exact-hex address links between retention paths,
  object fields, GC root paths and referencing statics, a Copy action on every valid address,
  bounded Back/Forward history with a breadcrumb, for live and dump panels. Live Object and GC
  root reads require an explicit acknowledgement (offline dump reads do not); all numeric
  addresses are flagged as imprecise, never linked or copied.
- Raster Marketplace icon and a dark gallery banner.
- README screenshots of live counters, heap drilldown, and thread snapshot panels. They are
  illustrative renders of the real webview panels using fixture data, not live captures.
- This changelog.

## 0.1.0

- Initial release: live runtime counters and GC pauses, point-in-time CPU, heap, and thread
  snapshots with follow-up drilldown views, and dump file analysis, backed by `dotnet-diagnostics-cli`.
