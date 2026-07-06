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
