# Method-parameter-capture spike findings

Issue: #555  
Date: 2026-07-06  
Status: partial success / blocking friction found

## What this spike staged

- Vendored `dotnet-monitor` `10.0.2` linux-x64 profiler payloads plus `Microsoft.Diagnostics.Monitoring.StartupHook.dll` under `spike/method-parameter-capture/vendor/`.
- Verified package license metadata is `MIT` directly from the published `dotnet-monitor.nuspec`.
- Added a standalone console spike at `spike/method-parameter-capture/ProfilerAttachSpike/`.
  - It is intentionally **not** added to `DotnetDiagnostics.slnx`.
  - It uses direct file references to the vendored `Microsoft.Diagnostics.NETCore.Client.dll` / `TraceEvent.dll` payloads from the tool package so the spike can exercise the same client surface dotnet-monitor ships, without changing the repo's central package management.

## Design-doc check

No landed `docs/design/method-parameter-capture-design.md` was present on `origin/main` when this spike ran, and no clearly-related open PR surfaced from the quick `gh pr list/search` checks, so this spike followed the MVP guidance in `docs/research/method-parameter-capture.md`.

## Actual execution performed

Command run:

```bash
dotnet run --project spike/method-parameter-capture/ProfilerAttachSpike/ProfilerAttachSpike.csproj -c Release
```

The spike launches a real `samples/CoreClrSample` process, discovers the generated local-function method name for `BurnCpu`, and then attempts:

1. `SetEnvironmentVariableAsync(...)` for the dotnet-monitor shared-path / runtime-instance / parameter-capture env vars.
2. `AttachProfilerAsync(...)` for `libMonitorProfiler.so`.
3. `AttachProfilerAsync(...)` for `libMutatingMonitorProfiler.so`.
4. `ApplyStartupHookAsync(...)` for `Microsoft.Diagnostics.Monitoring.StartupHook.dll`.
5. Wait for the dotnet-monitor shared socket and, if present, start the parameter-capture command channel.

## Observed output

Relevant observed output from the real run:

```text
Target method: CoreClrSample.dll!Program.<<Main>$>g__BurnCpu|0_10
Attaching notify-only profiler...
Notify-only profiler attach failed: UnsupportedCommandException: AttachProfilerAsync failed - Invalid command argument.
Attaching mutating profiler...
Mutating profiler attached.
Applying startup hook...
Startup hook applied.

=== ATTACH SUMMARY ===
Notify profiler attach: False
Mutating profiler attach: True
Startup hook apply: True
Startup hook env: 1
Managed messaging env: 1
Capture stage failed: TimeoutException: Profiler socket did not appear: .../<runtime-instance>.sock
```

## What worked exactly as the research doc predicted

- `ApplyStartupHookAsync(...)` **did succeed against an already-running .NET 10 CoreClrSample process**.
- The mutating profiler (`libMutatingMonitorProfiler.so`) **did attach successfully** to the already-running process.
- After startup-hook application, the target process reported:
  - `DotnetMonitor_InProcessFeatures_AvailableInfrastructure_StartupHook=1`
  - `DotnetMonitor_InProcessFeatures_AvailableInfrastructure_ManagedMessaging=1`

That confirms the general shape from the research doc: a running .NET 8+ / 10 process can be modified in-place with the startup hook and at least part of dotnet-monitor's profiler infrastructure.

## What broke / differed from the research expectations

### 1. Notify-only profiler attach did **not** behave like the mutating-profiler attach

The notify-only profiler attach consistently failed with:

```text
UnsupportedCommandException: AttachProfilerAsync failed - Invalid command argument.
```

while the mutating profiler attach succeeded in the same run against the same target process.

That means the research doc's broad statement "attach-to-running-process is the primary path" is directionally correct, but **the two profilers are not interchangeable in practice on this Linux/.NET 10 box**. The notify-only profiler needs additional investigation before issue #556 assumes a symmetric attach sequence.

### 2. No command socket appeared, so end-to-end parameter capture could not start

Even after:

- successful mutating-profiler attach,
- successful startup-hook application, and
- environment flags showing startup-hook + managed-messaging availability,

no dotnet-monitor shared socket appeared under the configured shared path / runtime-instance GUID. Without that socket, the spike could not deliver `StartCapturingParameters` to the in-process dispatcher, so it never reached the ReJIT/probe-install / EventPipe parameter-stream phase.

This is the main blocker that kept the spike at **partial success** instead of full end-to-end capture.

### 3. Targeting top-level-statement helper methods is awkward

`CoreClrSample`'s apparent `BurnCpu(int)` method is emitted as the generated local-function name:

```text
Program.<<Main>$>g__BurnCpu|0_10
```

So any eventual UX that targets arbitrary app methods will need to account for compiler-generated names, not just source-level names.

## Net result

**Partial success only.**

Achieved:
- package provenance + licensing verification,
- real vendoring of the upstream linux-x64 payloads,
- real attach attempt against a live process,
- real successful mutating-profiler attach,
- real successful startup-hook injection.

Not achieved:
- successful notify-only profiler attach,
- dotnet-monitor shared socket creation,
- start/stop parameter-capture command delivery,
- end-to-end observed parameter values via EventPipe.

## Recommendation for issue #556

Proceed, but **narrow the next spike/design step around the notify-only-profiler/socket bring-up path first**.

The strongest new empirical update is:

> On this Linux/.NET 10 dev box, `ApplyStartupHookAsync` and the mutating-profiler attach work on a live target, but the notify-only profiler attach fails with `Invalid command argument`, and without the notify/socket path the parameter-capture control plane never comes up.

So #556 should not assume "vendor payloads + call the two attach APIs + done". The next design iteration should explicitly cover:

1. why `libMonitorProfiler.so` attach fails while `libMutatingMonitorProfiler.so` succeeds,
2. what exact preconditions create the shared socket, and
3. whether the notify-only profiler must be loaded through a different path/order on .NET 10 Linux.

## Root-cause investigation (#560)

### Source anchor used

`vendor/README.md` records the vendored payload version as `dotnet-monitor` `10.0.2` (`spike/method-parameter-capture/vendor/README.md:7-10`). For upstream source inspection I used the published `dotnet-monitor` tag `v10.0.2` (`7accd0788ebecbaee1ffdd1c69090c9cba75376b`), which matches the staged package version.

### 1. CLSID verification: the spike's notify-profiler GUID was correct

The notify-only profiler really is `6A494330-5848-4A23-9D87-0E57BBF6DE79` in both the managed tool constants and the native profiler itself:

- `src/Tools/dotnet-monitor/Profiler/ProfilerIdentifiers.cs:14-25` defines `ProfilerIdentifiers.NotifyOnlyProfiler.Clsid.StringWithDashes = "6A494330-5848-4A23-9D87-0E57BBF6DE79"`.
- `src/Profilers/MonitorProfiler/MainProfiler/MainProfiler.cpp:27-30` returns the same CLSID from `MainProfiler::GetClsid()`.

So the spike was **not** reusing the mutating profiler's GUID by mistake. The mutating profiler remains the distinct `38759DC4-0685-4771-AD09-A7627CE1B3B4` (`src/Tools/dotnet-monitor/Profiler/ProfilerIdentifiers.cs:43-55`, `src/Profilers/MutatingMonitorProfiler/MutatingMonitorProfiler.cpp:17-20`).

### 2. Real attach ordering/grouping in dotnet-monitor

Upstream applies the profilers as **two separate sequential operations**, not one combined attach:

- `ProfilerService` sets `RuntimeInstanceId` / optional `SharedPath`, then calls `ApplyNotifyOnlyProfilerAsync(...)` first and `ApplyMutatingProfilerAsync(...)` second (`src/Tools/dotnet-monitor/Profiler/ProfilerService.cs:128-146`).
- Each helper just forwards to `ApplyProfilerCoreAsync(...)` with that profiler's module path env var + CLSID (`src/Tools/dotnet-monitor/Profiler/ProfilerService.cs:254-273`).
- `ApplyProfilerCoreAsync(...)` first calls `SetEnvironmentVariableAsync(...)`, then tries `AttachProfilerAsync(...)`, and only falls back to `SetStartupProfilerAsync(...)` if attach throws `ServerErrorException` (`src/Tools/dotnet-monitor/Profiler/ProfilerService.cs:277-313`).

So dotnet-monitor really does intend the notify-only profiler to be attached live to an already-running process, before the mutating profiler, using a normal `AttachProfilerAsync(...)` call.

### 3. Is the notify-only profiler meant to be live-attached at all?

Yes. Upstream's own code says "yes":

- `MainProfiler` implements `InitializeForAttach(...)` (`src/Profilers/MonitorProfiler/MainProfiler/MainProfiler.cpp:136-146`).
- `MainProfiler::LoadAsNotificationOnly(...)` returns `TRUE` (`src/Profilers/MonitorProfiler/MainProfiler/MainProfiler.cpp:148-155`).
- The runtime's profiler-load path explicitly checks `LoadAsNotificationOnly`, allocates notification-only profilers in their own slots, and then calls `InitializeForAttach(...)` for attach loads (`dotnet/runtime release/10.0 @ 71719654f7dcb3c804f90ab5ed805cda0e255dbd: src/coreclr/vm/profilinghelper.cpp:1173-1203,1233-1236`).

This means live-attach of `MonitorProfiler` is **architecturally supported** on this platform/runtime family; it is not a startup-only profiler by design.

### 4. Real root cause of the `Invalid command argument`

The failure was not the GUID and not the IPC command format. It was the **Unix-domain socket path length** created by the notify-only profiler.

Why this only hit the notify profiler:

- `MainProfiler::InitializeCommon()` ends by calling `InitializeCommandServer()` (`src/Profilers/MonitorProfiler/MainProfiler/MainProfiler.cpp:157-203`).
- `InitializeCommandServer()` builds `<sharedPath>/<runtimeInstanceId>.sock` and starts the `CommandServer` on that path (`src/Profilers/MonitorProfiler/MainProfiler/MainProfiler.cpp:225-255`).
- On Unix, `IpcCommServer::Bind()` returns `E_INVALIDARG` when `rootAddress.length() >= sizeof(sockaddr_un.sun_path)` (`src/Profilers/MonitorProfiler/Communication/IpcCommServer.cpp:20-39`).
- The mutating profiler does **not** create this socket during `InitializeCommon()`; it only sets env vars / event masks / probe services (`src/Profilers/MutatingMonitorProfiler/MutatingMonitorProfiler.cpp:72-119`), which explains why mutating attach succeeded while notify attach failed.

The spike's original socket path shape was:

```text
/home/pedrotravi/dotnet-dbg-mcp-worktrees/wt-555/spike/method-parameter-capture/run/shared-<guid>/<guid>.sock
```

On this dev box that resolves to **169 characters**, well above the Linux `sockaddr_un.sun_path` limit that dotnet-monitor's `IpcCommServer` enforces. That `E_INVALIDARG` propagates back through the runtime attach path:

- the diagnostics IPC attach payload is just `{ timeout, GUID, profiler path, client data }` (`dotnet/runtime release/10.0: src/native/eventpipe/ds-profiler-protocol.h:27-38`);
- `ds_rt_profiler_attach(...)` forwards it to `ProfilingAPIUtility::LoadProfilerForAttach(...)` (`dotnet/runtime release/10.0: src/coreclr/vm/eventing/eventpipe/ds-rt-coreclr.h:250-267`);
- `DiagnosticsClient.AttachProfilerAsync(...)` maps `DiagnosticsIpcError.InvalidArgument` to `UnsupportedCommandException("... Invalid command argument.")` (`dotnet/diagnostics @ d8a1d0d60680defa0d30fdc6df4f5aea98a8deb0: src/Microsoft.Diagnostics.NETCore.Client/DiagnosticsClient/DiagnosticsClient.cs:248-252,702-715,858-886`).

So the `"Invalid command argument"` text was a misleading surface symptom of the profiler returning `E_INVALIDARG` from its socket bind path, not evidence of a malformed GUID or broken attach protocol.

### 5. Tested fix and observed before/after output

I changed the spike to use a much shorter shared directory (`<repo>/.dm`), which produced a 94-character socket path on this box:

```text
/home/pedrotravi/dotnet-dbg-mcp-worktrees/wt-555/.dm/<guid>.sock
```

#### Before (original spike)

```text
Attaching notify-only profiler...
Notify-only profiler attach failed: UnsupportedCommandException: AttachProfilerAsync failed - Invalid command argument.
Attaching mutating profiler...
Mutating profiler attached.
Applying startup hook...
Startup hook applied.
...
Capture stage failed: TimeoutException: Profiler socket did not appear: .../<runtime-instance>.sock
```

#### After (short shared path)

Observed from the real rerun on this Linux dev box:

```text
Shared path: /home/pedrotravi/dotnet-dbg-mcp-worktrees/wt-555/.dm
Profiler socket path: /home/pedrotravi/dotnet-dbg-mcp-worktrees/wt-555/.dm/88f9d218-15b6-4161-93d5-55d88e38499d.sock (94 chars)
...
Attaching notify-only profiler...
Notify-only profiler attached.
Attaching mutating profiler...
Mutating profiler attached.
Applying startup hook...
Startup hook applied.

=== ATTACH SUMMARY ===
Notify profiler attach: True
Mutating profiler attach: True
Startup hook apply: True
Startup hook env: 1
Managed messaging env: 1
...
Profiler socket: /home/pedrotravi/dotnet-dbg-mcp-worktrees/wt-555/.dm/88f9d218-15b6-4161-93d5-55d88e38499d.sock
Sending StartCapturingParameters request 1a76217c-0bc0-428e-98b5-96653a9ebd3f...
Capture stage failed: TimeoutException: Timed out waiting for parameter capture start.
```

That rerun proves the notify-only profiler **can** be live-attached here once the socket path is short enough, and that the missing socket in the original run was a direct consequence of the overlong Unix socket path.

### Final verdict

For the narrow question in #560, the notify-only profiler live-attach path is **not** an architectural dead end. The concrete root cause was the spike's overlong Unix socket path, and shortening it fixed the `AttachProfilerAsync failed - Invalid command argument` failure.

Recommendation: **architecture path 1 remains viable** (full dotnet-monitor replication is not blocked by an unattachable notify-only profiler). However, this rerun still did **not** complete end-to-end parameter capture: the control channel came up, but `StartCapturingParameters` timed out waiting for the "started" signal. So #560 removes the live-attach blocker, but it does **not** by itself prove the full socket/control protocol has been replicated correctly yet.

## Control-channel handshake investigation (#561)

### Source anchor used

For the control-plane follow-up I cloned the real upstream `dotnet-monitor` source at the same version already tied to this spike: tag `v10.0.2`, commit `7accd0788ebecbaee1ffdd1c69090c9cba75376b`.

### 1. The wire format is a single Unix-socket request: header + UTF-8 JSON payload

The managed client in dotnet-monitor does **not** use a custom binary DTO beyond the framing header:

- `ProfilerChannel.SendMessage(...)` connects to the Unix socket, writes `ushort commandSet`, `ushort command`, `int payloadLength`, then writes the payload bytes, and finally waits for a `ServerResponse/Status` reply carrying a 4-byte HRESULT (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Microsoft.Diagnostics.Monitoring.WebApi/ProfilerChannel.cs:37-74,76-125`).
- `JsonProfilerMessage` serializes the payload with `JsonSerializer.Serialize(...)` and UTF-8 encodes it (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Microsoft.Diagnostics.Monitoring.WebApi/ProfilerMessage.cs:49-69`).
- The enum values are the expected ones: `CommandSet.StartupHook == 2`, `StartupHookCommand.StartCapturingParameters == 0`, `StopCapturingParameters == 1`, and `ServerResponseCommand.Status == 0` (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Microsoft.Diagnostics.Monitoring.WebApi/ProfilerMessage.cs:15-40`; native mirror: `src/Profilers/MonitorProfiler/Communication/Messages.h:21-57`).

So the spike was already targeting the right command set / command numbers and the right response envelope.

### 2. There is no extra hello/version handshake before `StartCapturingParameters`

Upstream starts the EventPipe listener and then immediately sends `StartCapturingParameters`; there is no preceding hello, subscribe, or version-negotiation message:

- `CaptureParametersOperation.ExecuteAsync(...)` starts `EventParameterCapturingPipeline`, then directly sends `new JsonProfilerMessage(StartupHookCommand.StartCapturingParameters, new StartCapturingParametersPayload { ... })`, and then waits for the started completion source (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Tools/dotnet-monitor/ParameterCapturing/CaptureParametersOperation.cs:123-155`).

That ruled out the “missing handshake step” theory.

### 3. The request really does go to the notify-only profiler socket, which forwards `StartupHook` commands into managed startup-hook code

The actual actor pipeline is:

1. The notify-only profiler creates `<sharedPath>/<runtimeInstanceId>.sock` in `MainProfiler::InitializeCommandServer()` (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Profilers/MonitorProfiler/MainProfiler/MainProfiler.cpp:225-268`).
2. The startup hook bootstraps a `ProfilerMessageSource(CommandSet.StartupHook)` only when the notify-only profiler product-version env var is present, and enables the `ManagedMessaging` availability env var from there (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Microsoft.Diagnostics.Monitoring.StartupHook/DiagnosticsBootstrapper.cs:38-60`).
3. `ProfilerMessageSource` P/Invokes `RegisterMonitorMessageCallback(commandSet, callback)` against `libMonitorProfiler.so`, so the managed startup hook is registering its callback **with the native notify-only profiler** (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Microsoft.Diagnostics.Monitoring.StartupHook/MonitorMessageDispatcher/ProfilerMessageSource.cs:16-41,48-71`).
4. The native `CommandServer` ACKs the client with `S_OK` immediately, then queues unmanaged-only `Profiler` commands to the profiler thread but queues `StartupHook` commands to the managed-client queue (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Profilers/MonitorProfiler/Communication/CommandServer.cpp:113-135,137-169`).
5. `MonitorMessageDispatcher` receives the native callback payload and deserializes the JSON directly into the registered managed DTO for that command (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Microsoft.Diagnostics.Monitoring.StartupHook/MonitorMessageDispatcher/MonitorMessageDispatcher.cs:37-90`).

So the spike was correct to send `StartCapturingParameters` to the **notify-only profiler’s socket**. The mutating profiler is not the control-channel endpoint for this request.

### 4. Method targeting is string-based reflection, not MethodDef-token based

The request contract does **not** carry metadata tokens:

- `MethodDescription` contains only `ModuleName`, `TypeName`, and `MethodName` (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Microsoft.Diagnostics.Monitoring.WebApi/Models/MethodDescription.cs:14-29`).
- `MethodResolver` scans loaded assemblies/modules, gets the declaring type by `TypeName`, and matches methods by `method.Name == methodDescription.MethodName` (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Microsoft.Diagnostics.Monitoring.StartupHook/ParameterCapturing/MethodResolver.cs:19-100`).

That means the compiler-generated local function name (`Program.<<Main>$>g__BurnCpu|0_10`) is valid input as long as the string names match what reflection sees at runtime. The timeout was **not** caused by a missing MethodDef token.

### 5. Real cause of the remaining timeout: the spike diverged from the real client in payload JSON shape and EventPipe event interpretation

Two concrete mismatches remained in the spike:

1. **JSON property names for `CaptureParametersConfiguration`.** Upstream decorates the nested config DTO with `[JsonPropertyName("methods")]`, `[JsonPropertyName("useDebuggerDisplayAttribute")]`, and `[JsonPropertyName("captureLimit")]` (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Microsoft.Diagnostics.Monitoring.WebApi/Models/CaptureParametersConfiguration.cs:14-26`). The spike’s local DTO originally emitted PascalCase property names for those fields, so I updated it to match the real contract (`spike/method-parameter-capture/ProfilerAttachSpike/Program.cs:680-689`).
2. **Started/stopped acknowledgement parsing.** Upstream’s own EventPipe consumer switches on `traceEvent.EventName` (`"Capturing/Start"`, `"Capturing/Stop"`, `"FailedToCapture"`, etc.), not on hard-coded numeric IDs (`dotnet-monitor @ 7accd0788ebecbaee1ffdd1c69090c9cba75376b: src/Tools/dotnet-monitor/ParameterCapturing/EventParameterCapturingPipeline.cs:43-107`). The spike observer was still keying off raw numeric IDs, so it missed the real started event even when it arrived. I changed the spike to match upstream and to subscribe with the same provider settings (`EventLevel.Informational`, `EventKeywords.All`) used by the real pipeline (`spike/method-parameter-capture/ProfilerAttachSpike/Program.cs:143-145,577-689`).

### 6. Tested fix: rerun on a real live `CoreClrSample` process now reaches end-to-end parameter capture

#### Before

Real baseline rerun before the above spike changes:

```text
Profiler socket: /home/pedrotravi/dotnet-dbg-mcp-worktrees/wt-555/.dm/5049960a-eb2b-4da0-8227-3bb7be3eb95c.sock
Sending StartCapturingParameters request 9f9dae42-e46e-4fc5-806a-1f7893291f02...
Capture stage failed: TimeoutException: Timed out waiting for parameter capture start.
```

#### After

Real rerun after matching the real JSON contract and the real EventPipe event interpretation:

```text
Profiler socket: /home/pedrotravi/dotnet-dbg-mcp-worktrees/wt-555/.dm/51e76321-3b4b-47d3-8fbe-a5956f6b1966.sock
Sending StartCapturingParameters request 6e1b7a50-0949-43e0-a283-6c908f39d482...
Triggered /cpu-burn?ms=123 => 200 OK
Sending StopCapturingParameters...

=== OBSERVED EVENTS ===
eventId=11, eventName=Capturing/Start, payloads=RequestId=6e1b7a50-0949-43e0-a283-6c908f39d482
eventId=13, eventName=CapturedParameter/Start, payloads=RequestId=6e1b7a50-0949-43e0-a283-6c908f39d482; ...; methodName=<<Main>$>g__BurnCpu|0_10; methodModuleName=CoreClrSample.dll; methodDeclaringTypeName=Program
eventId=14, eventName=CapturedParameter, payloads=RequestId=6e1b7a50-0949-43e0-a283-6c908f39d482; ...; parameterName=milliseconds; ...; parameterValue=123; ...
eventId=16, eventName=Capturing/Stop, payloads=RequestId=6e1b7a50-0949-43e0-a283-6c908f39d482

=== SUMMARY ===
Started: True
Stopped: True
Captured parameters:
  milliseconds=123
```

### Final verdict

Issue #561 is **closed by the spike**: the control-channel handshake itself works.

The key evidence is that the real upstream source shows there is no hidden handshake beyond the single framed JSON request, and the fixed spike now observes the full event sequence (`Capturing/Start` → `CapturedParameter*` → `Capturing/Stop`) plus a real captured parameter value (`milliseconds=123`) from a live `samples/CoreClrSample` process.
