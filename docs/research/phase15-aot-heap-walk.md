# Phase 15 C2: NativeAOT Heap Walk — Research Findings

**Issue**: #467 (Phase 15 C2) · **Branch**: `feature/c2-aot-research` · **Date**: 2026-06-29
**Status**: Research spike — no production code. Output is this findings doc + a GO/NO-GO and follow-up issues.

## Executive summary

**GO** — a managed heap walk *is* feasible on NativeAOT .NET 10 via the
`Microsoft-Windows-DotNETRuntime` EventPipe provider + `GCHeapSnapshot` keyword
(the same runtime-emitted, **non-DAC** path that backs `dotnet-gcdump`), at a
**reduced fidelity tier**:

- **Accurate**: object addresses, sizes, reference edges, and stack/handle root edges.
- **Caveat 1 — type names**: NativeAOT fires `BulkType` events but the `TypeName`
  field is hard-coded empty (`"No name in event"`); `TypeNameID` is a module-relative
  RVA, not a metadata token. Names require a separate ELF/PE symbol-table lookup
  (reusing this repo's existing `NativeAotSymbolDemangler`).
- **Caveat 2 — static roots**: `ETW::GCLog::WalkStaticsAndCOMForETW()` is a no-op
  stub on NativeAOT, so objects reachable *only* from static fields appear unrooted.

### Correction to the initial survey

An earlier note claimed the `GcDumpHeapSnapshotCollector` "does not exist" and that
Phase 14 A1 (#444) was unstarted. **That is wrong** — A1 #444 merged and the collector
exists at `src/DotnetDiagnostics.Core/Dump/GcDumpHeapSnapshotCollector.cs`, and it
already performs the CoreCLR-only `Microsoft-DotNETCore-SampleProfiler` type-flush
step (`GcDumpHeapSnapshotCollector.cs:32,263`). The real blocker is therefore **a
single capability gate**, not a missing collector:

```csharp
// src/DotnetDiagnostics.Core/Capabilities/CapabilityDetector.cs:80
var canCollectGcDump = runtime == RuntimeFlavor.CoreClr;
```

So the implementation cost is materially lower than a from-scratch collector: lift the
gate for `RuntimeFlavor.NativeAot`, and **skip the SampleProfiler type-flush step** on
AOT targets (it relies on a provider AOT does not ship).

## Avenue verdicts

| Avenue | Verdict | Key evidence |
|---|---|---|
| 1. EventPipe `GCHeapDump` events on AOT | **GO (partial fidelity)** | `eventtrace_gcheap.cpp` + `profheapwalkhelper.cpp` compiled into AOT EventPipe under `FEATURE_EVENT_TRACE`; `gen-eventing-event-inc.lst` lists `GCBulkNode/Edge/RootEdge/BulkType`; `ShouldWalkHeapObjectsForEtw()`/`ForceGC()` implemented |
| 2. `.map.xml` for type names | **PARTIAL (methods only)** | ILC map indexes `MethodCode` nodes, not MethodTable type objects; type names need the ELF `.symtab`, not the map |
| 3. `dotnet-gcdump` protocol on AOT | **GO (skip type-flush)** | dump session uses `Microsoft-Windows-DotNETRuntime` + `GCHeapSnapshot` (runtime-agnostic); only the `SampleProfiler` flush step is CoreCLR-only and is just an optimization |
| 4. SOS / createdump | **NO (heap) / YES (raw dump)** | SOS heap commands need the DAC, absent on AOT; `createdump` captures a native core but managed heap is not analyzable |

### Fidelity matrix (AOT gcdump vs CoreCLR gcdump)

| Capability | CoreCLR | AOT (proposed) |
|---|---|---|
| Object addresses / sizes | ✅ | ✅ |
| Reference graph (edges) | ✅ | ✅ |
| Roots: stack + handles | ✅ | ✅ |
| Roots: statics | ✅ | ❌ (`WalkStaticsAndCOMForETW` no-op) |
| Type names | ✅ full | ⚠️ via ELF symtab (RVA → name) |
| PerfView/VS `.gcdump` viewing | ✅ | ✅ (same format) |
| Field values / string content | ✅ | ❌ (no DAC) |

## GO / NO-GO

**GO** on the EventPipe `GCHeapDump` path, delivered as a new fidelity tier
(`heap-object-graph-partial`), explicitly **not** equivalent to the CoreCLR DAC heap
walk. Type-by-shape size accounting works immediately; type *names* are a Tier-2
enhancement via ELF symbol resolution.

## Recommended follow-up issues

- **C2-A (Tier 1, impl)**: lift `CanCollectGcDump` for NativeAOT in `CapabilityDetector.cs:80`
  (when EventPipe is reachable) and make `GcDumpHeapSnapshotCollector` skip the
  `Microsoft-DotNETCore-SampleProfiler` flush step on AOT. Annotate the artifact with
  `typeFidelity: "address-only"`. Update `docs/aot-coverage.md` (`inspect_heap` gcdump
  row ❌ → ⚠️) and the `CapabilityDetector` notes.
- **C2-B (Tier 2, impl)**: `NativeAotTypeSymbolMap` (parallel to `NativeAotMethodMap`)
  resolving MethodTable RVA → demangled type name from the ELF symtab; optional
  `nativeAotBinaryPath` param; `typeFidelity: "elf-symbol"`. Fall back to `<0xRVA>` on
  stripped binaries.
- **C2-C (docs)**: footnotes + a "My NativeAOT process is leaking" recipe tier in
  `aot-coverage.md`.
- **C2-D (upstream spike)**: investigate enabling `GCBulkRootStaticVar` on NativeAOT
  (`WalkStaticsAndCOMForETW` stub); likely a `dotnet/runtime` upstream ask.

## Sources

Repo: `docs/aot-coverage.md:81-98,65-66`; `CapabilityDetector.cs:80,346-352`;
`Dump/GcDumpHeapSnapshotCollector.cs:32,263`; `CpuSampling/NativeAotSymbolDemangler.cs`;
`CpuSampling/NativeAotMethodMap.cs`; `CpuSampling/PerfNativeAotCpuSampler.cs`.

Upstream `dotnet/runtime` (NativeAOT): `eventpipe/CMakeLists.txt` (`eventtrace_gcheap.cpp`,
`profheapwalkhelper.cpp` under `FEATURE_EVENT_TRACE`); `eventpipe/gen-eventing-event-inc.lst`
(GCBulk* + BulkType); `eventtrace_gcheap.cpp` (`ShouldWalkHeapObjectsForEtw`, `ForceGC`,
`WalkStaticsAndCOMForETW` stub); `eventtrace_bulktype.cpp` (empty TypeName, TypeNameID = RVA);
`profheapwalkhelper.cpp` (`HeapWalkHelper` → `ObjectReference`). `dotnet/diagnostics`
`dotnet-gcdump/DotNetHeapDump/EventPipeDotNetHeapDumper.cs` (SampleProfiler flush is
CoreCLR-only; dump session is `Microsoft-Windows-DotNETRuntime` + `GCHeapSnapshot`).
