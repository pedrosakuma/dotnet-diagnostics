# Method Parameter Live Capture — Feasibility Findings

**Issue**: #547 (Phase 16 P2) · **Branch**: `docs/method-parameter-capture-feasibility` · **Date**: 2026-07-06
**Status**: Research spike — no production code. Output is this findings doc plus a GO / NO-GO verdict.

## Executive summary

**Verdict: GO (with guardrails).**

`dotnet-monitor` parameter capture is **not** an EventPipe-only feature and **not** a pure startup-only trick. On .NET 8+, it is an **on-demand in-process instrumentation pipeline** composed of:

1. a **native notify-only profiler** (`MonitorProfiler`) that hosts the command bridge used by the startup hook,
2. a **native mutating profiler** (`MutatingMonitorProfiler`) implementing `ICorProfilerCallback*`, including `InitializeForAttach` and `GetReJITParameters`,
3. **runtime attach** via `DiagnosticsClient.AttachProfilerAsync(...)`,
4. **managed startup-hook injection** via `DiagnosticsClient.ApplyStartupHookAsync(...)`, and
5. **IL rewriting / ReJIT** that injects a probe call at method entry, boxes arguments into `object[]`, then emits captured values back out through dotnet-monitor's parameter-capturing EventPipe pipeline.

That means the feature is **architecturally feasible without modifying the target app or requiring startup-time environment variables on .NET 8+**, which keeps it inside this repo's core non-goal boundary (“no target-app modification”).

The cost is still high: it would be this repo's **first native binary payload**, first **in-process mutating instrumentation** feature, and first feature that can intentionally surface **arbitrary live secrets** from method arguments. So the right reading is **feasible, but expensive and security-sensitive** — not “cheap”.

## What dotnet-monitor actually does

### 1. It ships a real native profiler, not an EventPipe collector

The upstream source contains two native profiler projects under `src/Profilers/`: `MonitorProfiler` and `MutatingMonitorProfiler` (`dotnet/dotnet-monitor:src/Profilers/CMakeLists.txt`).

The mutating profiler is a COM profiler that derives from `ICorProfilerCallback11` via `ProfilerBase` and explicitly implements attach / ReJIT hooks:

- `ProfilerBase` implements `ICorProfilerCallback`, `ICorProfilerCallback2`, `...3`, `...4`, up through `ICorProfilerCallback11` (`src/Profilers/CommonMonitorProfiler/ProfilerBase.h`).
- `MutatingMonitorProfiler` overrides `InitializeForAttach`, `LoadAsNotificationOnly(FALSE)`, and `GetReJITParameters` (`src/Profilers/MutatingMonitorProfiler/MutatingMonitorProfiler.h`, `.cpp`).

This is the key fact: **parameter capture fundamentally depends on profiler-based CLR instrumentation**; EventPipe is used downstream to stream the captured results back out, but it is not the capture mechanism.

### 2. It rewrites IL to inject a probe call at method entry

The managed `FunctionProbesManager` P/Invokes into the native mutating profiler to:

- register a probe stub,
- request probe installation for selected functions, and
- request probe uninstallation

(`src/Microsoft.Diagnostics.Monitoring.StartupHook/ParameterCapturing/FunctionProbes/FunctionProbesManager.cs`).

Inside the native profiler:

- `ProbeInstrumentation::InstallProbes(...)` resolves `FunctionID -> (ModuleID, mdMethodDef)` and calls `ICorProfilerInfo12::RequestReJITWithInliners(...)`.
- `ProbeInstrumentation::GetReJITParameters(...)` calls `ProbeInjector::InstallProbe(...)`.
- `ProbeInjector::InstallProbe(...)` imports the method IL, prepends IL that:
  - loads a probe uniquifier,
  - allocates a new `object[]`,
  - loads each argument with `ldarg`,
  - boxes value types,
  - calls the probe method,
  - wraps the probe in a try/catch to fault-stop safely.

See `src/Profilers/MutatingMonitorProfiler/ProbeInstrumentation/ProbeInstrumentation.cpp` and `ProbeInjector.cpp`.

This is function-entry IL rewriting, i.e. true “capture every invocation of these methods for a bounded window”.

### 3. It attaches the profiler to an already-running process on .NET 8+

`dotnet-monitor`'s `ProfilerService` is explicit here (`src/Tools/dotnet-monitor/Profiler/ProfilerService.cs`):

- It sets environment variables in the target process for profiler module path / shared path / runtime instance id.
- It then calls `client.AttachProfilerAsync(AttachTimeout, clsid, physicalPath, Array.Empty<byte>(), cancellationToken)`.
- Only if attach fails with `ServerErrorException` does it fall back to `client.SetStartupProfilerAsync(...)`.

The comment in `ApplyProfilerCoreAsync` is decisive:

> “If the process is already running, this will succeed. If failed (hopefully due to suspension) fallback to setting the startup profiler.”

So the upstream implementation itself treats **attach-to-running-process as the primary path**, not as a startup-only feature.

### 4. It also uses a notify-only profiler, startup hook, and EventPipe result pipeline

Parameter capture is not “mutating profiler only”. The target process also needs:

- the **notify-only** `MonitorProfiler` for the startup-hook message bridge (`ProfilerMessageSource` DllImports `MonitorProfiler`),
- the managed startup-hook assembly that hosts `ParameterCapturingService`, `FunctionProbesManager`, and object formatting, and
- an EventPipe listener that reads the `ParameterCapturingEvents.SourceName` provider and reconstructs `CapturedParameter` events into responses.

Relevant source:

- `InProcessFeatures.IsProfilerRequired` is true when parameter capture is enabled, so dotnet-monitor applies the notify-only profiler as well as the mutating profiler (`src/Tools/dotnet-monitor/InProcessFeatures/InProcessFeatures.cs`, `ProfilerService.cs`).
- `ProfilerMessageSource` DllImports `MonitorProfiler` to receive startup-hook command callbacks (`src/Microsoft.Diagnostics.Monitoring.StartupHook/MonitorMessageDispatcher/ProfilerMessageSource.cs`).
- `StartupHookService` applies `Microsoft.Diagnostics.Monitoring.StartupHook.dll` when startup-hook-requiring features are enabled (`src/Tools/dotnet-monitor/StartupHook/StartupHookService.cs`).
- `StartupHookApplicator.ApplyAsync(...)` checks the target runtime version. For **.NET 8+**, it calls `DiagnosticsClient.ApplyStartupHookAsync(fileInfo.PhysicalPath, token)`. For **runtime < 8**, it refuses automatic injection and logs manual instructions (`src/Tools/dotnet-monitor/StartupHook/StartupHookApplicator.cs`).
- `CaptureParametersOperation` starts an `EventParameterCapturingPipeline`, sends start/stop commands over `ProfilerChannel`, and reads `ParameterCapturingEvents.SourceName` EventPipe events back out of the target (`src/Tools/dotnet-monitor/ParameterCapturing/CaptureParametersOperation.cs`, `EventParameterCapturingPipeline.cs`).
- The public client package used by this repo already documents `DiagnosticsClient.ApplyStartupHook(...)` / `ApplyStartupHookAsync(...)` and `AttachProfiler(...)` (`~/.nuget/packages/microsoft.diagnostics.netcore.client/0.2.661903/lib/net8.0/Microsoft.Diagnostics.NETCore.Client.xml`).

This lines up with the official parameters doc:

- `.NET 8+`: no manual startup-hook preconfiguration required.
- `.NET 7`: manual startup hook + suspended start required.

Source: `documentation/api/parameters.md`.

### 5. .NET 7 is effectively out of scope for this repo's desired UX

The parameters doc states:

- if the target is using **.NET 7**, the startup hook must be manually configured and the target must start suspended;
- in **.NET 8+**, that is not required.

That means this feature only cleanly matches this repo's “attach to an already-running app, without modifying it” model on **.NET 8+**. A future implementation here should therefore be **explicitly capability-gated to .NET 8+ live targets** and return `NotSupported` on .NET 7.

## Feasibility against repo constraints

### Non-goal fit

`AGENTS.md` says this repo must avoid **modifying the target application** and should remain **stateless** server-side.

A .NET 8+ implementation would still fit those constraints:

- no app rebuild,
- no startup env-var requirement,
- no agent-installed app code,
- bounded attach / capture / stop-instrumentation over the diagnostic socket,
- no persistent server-side session state beyond the usual bounded-time artifact / handle model.

This is closer in spirit to the existing on-demand attach features than to an always-on agent. The attach mechanism is different from the repo's ClrMD tools (`DataTarget.AttachToProcess(..., suspend: true)` in `src/DotnetDiagnostics.Core/Threads/ClrMdFrameVariableResolver.cs`), but it is still an **on-demand out-of-process diagnostic attach**, just via the runtime's profiler / startup-hook APIs rather than `ptrace`. One caveat: upstream source shows **stop/reset**, not full unload; once applied, the profiler/startup-hook payload likely remains resident in the target process until process exit, with probes merely installed/uninstalled on demand (`ProfilerService.ApplyProfilersAsync`, `FunctionProbesManager.StopCapturingAsync`).

### Managed dependency surface

The repo already depends on `Microsoft.Diagnostics.NETCore.Client` (`Directory.Packages.props`) and already uses `DiagnosticsClient` broadly for EventPipe and dump collection (for example `EventPipeCounterCollector.cs`, `DiagnosticsClientDumper.cs`).

So the **managed API family is already familiar** to the codebase. The new managed gap is not the client package itself; it is:

- invoking the profiler / startup-hook APIs,
- staging the native/managed helper payloads, and
- defining a bounded capture UX and artifact model.

### Biggest architectural departure: shipping native binaries

Today this repo is effectively “managed + external OS tools” (ClrMD, EventPipe, perf, BenchmarkDotNet diagnoser) and does **not** ship its own native profiler binaries.

A parameter-capture feature would require, at minimum:

- native profiler binaries per RID (notify-only + mutating, unless reimplemented differently),
- the managed startup-hook assembly,
- a staging / resolution mechanism similar to dotnet-monitor's shared-library system,
- an EventPipe-based result transport / parser for captured parameter events,
- packaging changes for the MCP server, CLI, Dockerfile, and likely BenchmarkDotNet diagnoser distribution.

This is the single biggest cost driver.

## Reuse vs reimplementation

### Reusing dotnet-monitor assets looks technically viable

Upstream licensing is **MIT** (`dotnet/dotnet-monitor:LICENSE.TXT`), so redistribution is legally straightforward assuming normal attribution / notice handling.

More importantly, the published `dotnet-monitor` tool package already contains the exact payloads needed for this feature. Inspecting `dotnet-monitor` **10.0.2** shows:

- native `MonitorProfiler` and `MutatingMonitorProfiler` binaries for:
  - `linux-x64`
  - `linux-arm64`
  - `linux-musl-x64`
  - `linux-musl-arm64`
  - `osx-x64`
  - `osx-arm64`
  - `win-x64`
  - `win-arm64`
  - `win-x86`
- `Microsoft.Diagnostics.Monitoring.StartupHook.dll` under `tools/net10.0/any/shared/any/net6.0/`

That makes **binary reuse / vendoring** much more plausible than writing a new profiler from scratch.

### But reuse is not “drop one DLL and done”

Even with reuse, a full implementation would still need this repo to decide one of two paths:

1. **Vendor dotnet-monitor's profiler + startup-hook payloads** (most pragmatic).
2. **Reimplement a smaller custom equivalent** (much more work; probably unjustified unless license / footprint / dependency isolation becomes a blocker).

Vendoring is the better first path because the hard part — native IL rewriting + managed/native bridge — already exists and is field-tested upstream.

### Why a greenfield profiler is not attractive

A custom implementation would need:

- a native COM profiler implementing the relevant `ICorProfilerCallback*` interfaces,
- ReJIT / IL rewriting code,
- ABI-stable managed/native interop for probe registration and faults,
- a managed in-process control plane and formatter,
- multi-RID build/test/publish infrastructure.

That is a large standalone product in its own right. There is no good reason to start there while an MIT implementation already exists.

## Security and operator-risk implications

This capability is much more sensitive than `frame-vars`.

`query_snapshot(view="frame-vars")` is a **point-in-time, one-thread, object-typed-only** ClrMD read from a snapshot handle. Parameter capture would instead stream **every invocation** of selected methods during a live window, including arbitrary strings / object renderings such as passwords, tokens, SQL, headers, PII, and business payloads.

Upstream's main safety controls are:

- global opt-in: `InProcessFeatures.ParameterCapturing.Enabled: true` (`documentation/api/parameters.md`),
- explicit per-request **method allowlisting** (`methods` array),
- an optional `durationSeconds` request bound (but upstream also permits `-1` for indefinite capture),
- an optional `captureLimit` policy (nullable in `CaptureParametersConfiguration`, so unlimited when omitted).

A dotnet-diagnostics implementation should be stricter, not looser. It should impose its own hard caps rather than inheriting dotnet-monitor's more permissive defaults. Minimum recommended controls:

1. **Server-wide off by default** feature flag distinct from heap-sensitive-value settings.
2. **Per-call explicit opt-in / acknowledgment** (`includeSensitiveValues`-style flag, but parameter-specific wording).
3. **Principal scope** stronger than `sensitive-heap-read` (for example a new `sensitive-parameter-read`).
4. **Disable `DebuggerDisplay` formatting by default** and warn that formatting may execute target code (`ToString()`, `IFormattable`, reflected `DebuggerDisplay` getters/methods).
5. **Required method allowlist**; never “capture everything”.
6. **Hard caps** on duration, event count, and per-value preview length.
7. **No automatic persistence beyond the normal artifact lifetime**; default to inline preview + expiring handle.
8. **Audit-friendly summaries** that clearly state capture was enabled and which methods were instrumented.

The existing `SensitiveValueGate` pattern (`src/DotnetDiagnostics.Core/Security/SensitiveValueGate.cs`) is the right conceptual starting point, but this feature deserves its **own gate/scope**, not reuse of the heap flag verbatim.

## MVP shape if this repo chooses to build it

### Scope recommendation

If implemented, keep V1 deliberately narrow:

- **CoreCLR only**
- **.NET 8+ only**
- **local / sidecar attach only**
- **exact method filter required** (`moduleName`, `typeName`, `methodName`; signature optional but desirable)
- **bounded duration only** (no indefinite streams)
- **bounded captureLimit / maxEvents only**
- **best-effort formatted previews**, not perfect object serialization
- **`DebuggerDisplay` off by default** because formatter evaluation can execute target code

### Tool shape

To respect the repo's “one tool per concept” guidance, this should extend an existing bounded sampler rather than add a whole new conceptual surface.

Best fit:

- extend `collect_sample` with `kind="method-params"`

Suggested request shape:

```json
{
  "pid": 1234,
  "kind": "method-params",
  "durationSeconds": 15,
  "maxEvents": 200,
  "methods": [
    {
      "moduleName": "MyService.dll",
      "typeName": "MyService.Auth.TokenService",
      "methodName": "Validate"
    }
  ],
  "includeSensitiveValues": true,
  "useDebuggerDisplayAttribute": false
}
```

Suggested response shape:

- inline preview of first N captured invocations,
- handle-backed artifact for the full bounded capture,
- summary including method filter, capture count, dropped/truncated counts, runtime version, and whether values were truncated / redacted.

### Core components needed

1. **RID-aware profiler payload staging**
   - ship vendored `MonitorProfiler`, `MutatingMonitorProfiler`, and startup-hook assets.
2. **Profiler attach orchestrator** in `DotnetDiagnostics.Core`
   - set env vars,
   - apply startup hook,
   - attach mutating profiler,
   - detect already-applied state,
   - clean up / stop capture.
3. **Capture operation wrapper**
   - method resolution / validation input,
   - bounded timer / cancellation,
   - max-events cap,
   - stream parsing into an artifact.
4. **Security gate**
   - server opt-in + caller opt-in + auth scope.
5. **Capability detection / UX**
   - reject .NET 7 and NativeAOT,
   - reject Hot Reload targets,
   - reject targets already using a non-notify-only profiler (per upstream doc),
   - explain sidecar/shared-path requirements clearly.

## Reasons this is still not a “free win”

Even with a GO verdict, there are real reasons to defer execution scheduling:

- first native payload in this repo,
- cross-RID packaging work,
- security review burden is much higher than normal EventPipe features,
- potential coexistence issues with existing profilers / Hot Reload,
- extra work to decide whether the CLI and BenchmarkDotNet diagnoser should also expose it.

So the right interpretation is:

- **technically feasible and aligned with repo non-goals on .NET 8+**,
- **worth keeping open as an implementable capability**,
- but **should land only if the team accepts the native-binary and security-cost tradeoff**.

## Final verdict

**GO** for a **.NET 8+ only**, **bounded**, **heavily gated** method-parameter sampler.

### Single most important supporting fact

The decisive evidence is upstream `ProfilerService.ApplyProfilerCoreAsync(...)` plus `StartupHookApplicator.ApplyAsync(...)`:

- `ProfilerService` uses `DiagnosticsClient.AttachProfilerAsync(...)` as the **primary** path for the native profiler against a running process.
- `StartupHookApplicator` uses `DiagnosticsClient.ApplyStartupHookAsync(...)` on **.NET 8+** to inject the managed startup hook without preconfigured startup env vars.

Together, those prove the feature is reachable for an already-running target **without restart and without target-app modification**.

## Recommended follow-up issues

1. **Implementation spike**: vendor / stage dotnet-monitor parameter-capture payloads in a sandbox branch and prove attach + start/stop against `samples/CoreClrSample`.
2. **Security design**: define the dedicated parameter-value auth scope, server flag, truncation policy, and audit wording.
3. **UX design**: finalize `collect_sample(kind="method-params")` input/output contract and artifact retention behavior.
4. **Capability gate**: document and enforce `.NET 8+ only`, `CoreCLR only`, `Hot Reload unsupported`, and “existing mutating profiler attached” failure mode.

## Sources

### This repository

- `AGENTS.md` — non-goals and attach conventions.
- `Directory.Packages.props` — existing `Microsoft.Diagnostics.NETCore.Client` dependency.
- `src/DotnetDiagnostics.Core/Counters/EventPipeCounterCollector.cs` — existing `DiagnosticsClient` usage.
- `src/DotnetDiagnostics.Core/Dump/DiagnosticsClientDumper.cs` — existing `DiagnosticsClient` usage.
- `src/DotnetDiagnostics.Core/Threads/ClrMdFrameVariableResolver.cs` — current post-hoc `frame-vars` attach model.
- `src/DotnetDiagnostics.Core/Security/SensitiveValueGate.cs` — existing sensitive-value gating pattern.
- `src/DotnetDiagnostics.Mcp/Tools/QuerySnapshotTool.cs` — `frame-vars` MCP wiring and summary semantics.

### Upstream dotnet-monitor

- `documentation/api/parameters.md`
- `src/Tools/dotnet-monitor/Profiler/ProfilerService.cs`
- `src/Tools/dotnet-monitor/StartupHook/StartupHookApplicator.cs`
- `src/Tools/dotnet-monitor/ParameterCapturing/CaptureParametersOperation.cs`
- `src/Tools/dotnet-monitor/ParameterCapturing/EventParameterCapturingPipeline.cs`
- `src/Tools/dotnet-monitor/StartupHook/StartupHookService.cs`
- `src/Tools/dotnet-monitor/InProcessFeatures/InProcessFeatures.cs`
- `src/Tools/dotnet-monitor/InProcessFeatures/InProcessFeaturesService.cs`
- `src/Microsoft.Diagnostics.Monitoring.StartupHook/DiagnosticsBootstrapper.cs`
- `src/Microsoft.Diagnostics.Monitoring.StartupHook/ParameterCapturing/ParameterCapturingService.cs`
- `src/Microsoft.Diagnostics.Monitoring.StartupHook/ParameterCapturing/FunctionProbes/FunctionProbesManager.cs`
- `src/Microsoft.Diagnostics.Monitoring.StartupHook/ParameterCapturing/FunctionProbes/ProfilerAbi.cs`
- `src/Microsoft.Diagnostics.Monitoring.StartupHook/MonitorMessageDispatcher/ProfilerMessageSource.cs`
- `src/Profilers/CMakeLists.txt`
- `src/Profilers/CommonMonitorProfiler/ProfilerBase.h`
- `src/Profilers/MutatingMonitorProfiler/MutatingMonitorProfiler.h`
- `src/Profilers/MutatingMonitorProfiler/MutatingMonitorProfiler.cpp`
- `src/Profilers/MutatingMonitorProfiler/ProbeInstrumentation/ProbeInstrumentation.cpp`
- `src/Profilers/MutatingMonitorProfiler/ProbeInstrumentation/ProbeInjector.cpp`
- `LICENSE.TXT`
- `dotnet-monitor` NuGet tool package `10.0.2` contents (RID-specific native profiler binaries + `Microsoft.Diagnostics.Monitoring.StartupHook.dll`).

### Client package docs inspected locally

- `~/.nuget/packages/microsoft.diagnostics.netcore.client/0.2.661903/lib/net8.0/Microsoft.Diagnostics.NETCore.Client.xml`
