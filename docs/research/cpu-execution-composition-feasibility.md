# CPU execution-composition feasibility

**Issue:** [#1262](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1262) ·
**Parent:** [#1257](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1257) ·
**Date:** 2026-10-26 · **Verdict:** **NO-GO**

## Diagnostic question

Can Core expose a factual, mutually understandable CPU-sample composition for
`application-managed`, `framework/runtime`, `GC/runtime internals`, `native`, and
`unresolved`, shared by CLI and MCP without relying on extension-side string heuristics?

## Decision

No. The current evidence can identify many managed methods precisely, preserve the
sampling backend and its scheduler-state semantics, and retain native or unresolved
resolution facts. It does **not** carry an authoritative ownership tag that says whether
a managed method belongs to the application or shared framework/runtime, nor whether a
runtime-looking managed or native frame is specifically a GC/runtime-internal operation.

`System.*`, assembly names, module names, and installation-path prefixes are useful search
hints, but they are not ownership authority. Applications can define `System.*` types,
framework assemblies can be copied or bundled with an application, self-contained
deployments change paths, and native runtime/helper names vary by runtime, platform,
build, symbol source, and optimization. Shipping the requested projection would therefore
turn naming conventions into facts.

## Evidence inspected

| File and symbol | Evidence available | Missing authority |
|---|---|---|
| `src/DotnetDiagnostics.Core/Memory/InvestigationSummary.cs` — `MethodIdentity` | Managed module name/path, MVID, metadata token, declaring type, method name, and generic details. `(MVID, token)` is a stable method-definition identity. | No application/framework/runtime/GC owner or origin. |
| `src/DotnetDiagnostics.Core/CpuSampling/EventPipeCpuSampler.cs` — `BuildMethodIdentities`, `ClassifyLeafEvidence` | Builds `MethodIdentity` from TraceEvent module/method data; separately marks known wait-name matches versus unknown scheduler state. | Module/type names and wait recognition do not establish code ownership. |
| `src/DotnetDiagnostics.Core/CpuSampling/CpuSample.cs` — `CpuSampleEvidence`, `NativeLeafResolutionStatus`, `NativeLeafCoverage` | Distinguishes EventPipe stack-frequency observations from Linux perf/Windows ETW on-CPU samples; preserves native resolution success/failure and bounded coverage accounting. | Backend and symbol quality do not identify application versus runtime ownership. |
| `src/DotnetDiagnostics.Core/CpuSampling/CallTreeNode.cs` — `CpuSampleTraceArtifact` | Retains the merged tree, method identities, evidence metadata, notes, and optional native-leaf coverage. | No ownership dimension exists on frames or identities. |
| `src/DotnetDiagnostics.Core/CpuSampling/CpuSampleViews.cs` — `CpuSampleAnalytics.MethodKey`, `ModuleOf`, `NamespaceOf` | Groups exact managed methods by MVID/token when available and exposes module/namespace projections. | Grouping keys and display namespaces are not category attestations. |
| `src/DotnetDiagnostics.Core/Symbols/NativeAddressResolver.cs` — `NativeAddressLocation`, `NativeAddressClassifier` | Classifies addresses as managed, module-backed native, mapped non-module, or unmapped/not captured, with module path, RVA, build ID, and optional managed identity. | A native module may be application code, a framework component, a runtime helper, or a third-party dependency. |
| `src/DotnetDiagnostics.Core/CpuSampling/PerfScriptFrameParser.cs` — `PerfJitFrameSymbolizer` | Resolves captured CoreCLR JIT ranges to `MethodIdentity`; unmatched candidates remain raw and are counted in notes. | Resolution does not add ownership. |
| `src/DotnetDiagnostics.Core/CpuSampling/NativeAotMethodMap.cs` — `NativeAotMethodMap` | An optional ILC map authoritatively distinguishes genuine managed `MethodCode` symbols from helpers/stubs for NativeAOT. | It has no application-versus-framework origin, MVID/token identity, or GC-internal taxonomy. |
| `src/DotnetDiagnostics.Core/Threads/WellKnownWaitFrameClassifier.cs` — `WellKnownWaitFrameClassifier` | Name-based wait/park hints used conservatively for scheduler-state presentation. | Wait reason is orthogonal to ownership and cannot be repurposed as runtime/GC provenance. |

No trusted runtime-module inventory, shared-framework ownership manifest, or equivalent
per-frame provenance contract was found in the CPU artifact or query path.

## Category support matrix

| Requested category | Current support | Reason |
|---|---|---|
| `application-managed` | **No** | Managed identity is exact, but ownership is absent. Names and deployment paths are not authoritative. |
| `framework/runtime` | **No** | Core cannot distinguish shared framework/runtime code from copied, bundled, self-contained, third-party, or application-defined lookalikes. |
| `GC/runtime internals` | **No** | Neither managed identity nor native symbols carry a stable runtime-subsystem tag; helper names are version- and platform-dependent. |
| `native` | **Partial fact, not a sufficient requested bucket** | OS backends and address resolution can establish native/module-backed or unresolved native evidence, but “native” mixes application code, runtime internals, system libraries, and dependencies. |
| `unresolved` | **Yes as resolution status only** | Unknown symbols/addresses can be preserved honestly, but unresolved evidence may conceal any ownership category and cannot complete the requested composition. |

## Why managed/native/unresolved is insufficient

A three-way representation split would answer “what kind of frame was resolved?”, not “whose
execution was observed?”. It would collapse framework and application managed code together,
collapse application native code with the CLR, GC helpers, libc, and third-party libraries, and
move missing symbols into `unresolved`. Renaming those representation states to the requested
ownership categories would be misleading.

The projection would also need an explicit attribution rule. Exclusive/leaf samples reconcile to
the sample population; inclusive stack membership does not, because one mixed stack can contain
application-managed, framework-managed, runtime-native, and unresolved frames simultaneously.
No ownership rule currently chooses among those frames.

## Required limitations

- **NativeAOT:** automatic CPU sampling uses perf or ETW, not EventPipe. NativeAOT commonly lacks
  IL metadata identity; the optional ILC map identifies managed method bodies but not their
  application/framework origin. A single native image can contain application, framework, and
  runtime-generated code.
- **Mixed stacks:** managed/native is a frame property, not a mutually exclusive stack owner.
  Leaf-only attribution loses caller context; inclusive attribution double-counts categories.
- **Missing symbols:** stripped binaries, unavailable/rejected PDBs, unmatched JIT ranges,
  anonymous mappings, and unreadable/unmapped addresses can hide any proposed category.
- **Bounded retention:** perf data, EventPipe buffers, JIT maps, symbol lookups, and native-leaf
  inventories are bounded. `NativeLeafCoverageCollector` retains the first 4,096 PCs and 512
  modules while reporting unretained sample weight; EventPipe loss and other cap notes likewise
  make completeness conditional. Missing evidence must remain unknown, not be redistributed.
- **EventPipe versus OS evidence:** `EventPipeSampleProfiler` reports periodic managed stack
  frequency and heuristic waits, not measured CPU time or scheduler state. Linux perf and Windows
  ETW report sampled on-CPU observations but have different privilege, stack, and symbol coverage.
  A common ownership label would not remove those backend-specific semantics.

## Rejected alternatives

1. **Prefix/path heuristics:** classifying `System.*`, `Microsoft.*`, `coreclr`, `libcoreclr`,
   runtime-pack directories, or shared-framework paths. These are neither exclusive nor stable,
   especially for self-contained, single-file, NativeAOT, copied, or adversarial assemblies.
2. **Repurposing wait reasons:** `WellKnownWaitFrameClassifier` identifies selected wait/park
   display names. It does not prove framework ownership, active CPU execution, or GC/runtime
   internals.
3. **Partial decorative projection:** shipping only managed/native/unresolved, or populating
   unsupported categories with zero/unknown, would visually resemble the requested composition
   while answering a different question.

## What could turn this into GO

The projection needs trusted, versioned provenance, for example:

- a runtime-supplied module inventory or ownership manifest keyed by exact module identity
  (preferably MVID plus assembly/image identity), explicitly distinguishing application,
  shared framework/runtime, and runtime-internal/GC code; or
- an equivalent sampler/runtime/compiler contract that tags each resolved frame with those
  ownership categories, including NativeAOT-generated methods and native runtime helpers.

That contract must define self-contained and single-file behavior, mixed-stack attribution,
unknown handling, symbol-loss behavior, backend parity, and denominator/reconciliation rules.
Only then should fixtures validate every category and degraded case.

## Surface and validation consequence

Do not change CPU `triage`, other `query_snapshot` views, CLI output, or MCP output for this issue,
and add no MCP tool. Because no behavior or contract ships, add no production fixtures or tests.
This research artifact and index entry are the complete documentation-only outcome.
