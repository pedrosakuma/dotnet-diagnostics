# Vendored dotnet-monitor profiler payloads

This directory stages the shipping runtime assets used by
`collect_sample(kind="method-params")`: the notify-only profiler, the mutating profiler,
and the startup-hook assembly proven in the upstream spike and then promoted into the
production package for issue #562.

## Provenance

- Package: `dotnet-monitor`
- Version: `10.0.2`
- NuGet SHA-256: `487e0c73fc917d83e6930c1628bd6849be13d6e968b712c5f0e0cafa1dc1caff`
- NuGet metadata repository: `https://github.com/dotnet/dotnet-monitor` @ `a2c81101cea07e1b11a15ae8c30bbe9a0fa1a3d5`
- License: `MIT` (verified from `dotnet-monitor.nuspec` in the package: `<license type="expression">MIT</license>`)

## Files staged in V1

- `linux-x64/native/libMonitorProfiler.so`
  - SHA-256: `b749f2ed44edd28a3d875b391434ec5500f20e81add813b50c3c69d6be1f8493`
- `linux-x64/native/libMutatingMonitorProfiler.so`
  - SHA-256: `96ef7467a02234d1aac63a0366bfe03fecf23b0e95c82fbdd4203d979762e953`
- `win-x64/native/MonitorProfiler.dll`
  - SHA-256: `aee5a01931293bef2ea42562b8f19119ba076b0ea652b15413c5d8e3c111924b`
- `win-x64/native/MutatingMonitorProfiler.dll`
  - SHA-256: `70377a099c242f05d947a8256c84d344426d574698fac132dd0b623853206651`
- `shared/any/net6.0/Microsoft.Diagnostics.Monitoring.StartupHook.dll`
  - SHA-256: `28fc0ed8f0c2a5311ef96e7a9c1d199c15e574b4df7627d51e969274831d5633`

Both `linux-x64` and `win-x64` native assets were extracted from the *same* NuGet package
recorded above (re-verified: the downloaded `.nupkg` SHA-256 matches the recorded package
hash) — the `win-x64` binaries were not sourced from a different version or a separate
download. The startup-hook DLL is RID-agnostic (`shared/any/net6.0`) and already covers
both platforms.

## Windows transport notes (issue #564)

dotnet-monitor's profiler `IpcCommServer`/`IpcCommClient` (see
`src/Profilers/MonitorProfiler/Communication/IpcCommServer.cpp` upstream) binds a genuine
`AF_UNIX` domain socket at `<sharedPath>/<runtimeInstanceId>.sock` on **both** Windows and
Linux — the `TARGET_WINDOWS`/`TARGET_UNIX` branches in that file only change the
accept-timeout polling primitive (`select` vs `poll`), not the socket family or wire
protocol. Windows has supported `AF_UNIX` sockets natively since Windows 10 1803
(`afunix.sys`), and .NET's `System.Net.Sockets.Socket`/`UnixDomainSocketEndPoint` work
against that transport unmodified. Consequently `MethodParameterCaptureCollector`'s existing
`SendProfilerMessageAsync`/`WaitForSocketAsync` methods required **no changes** for win-x64 —
only the shared-directory hardening needed a platform-specific implementation, since POSIX
`0700` permission bits have no Windows equivalent: the collector applies an explicit,
non-inherited ACL granting `FullControl` to the current user only.

CLSIDs (`6A494330-5848-4A23-9D87-0E57BBF6DE79` notify-only,
`38759DC4-0685-4771-AD09-A7627CE1B3B4` mutating) are defined in shared, RID-independent C++
source in dotnet-monitor and registered identically via `DllGetClassObject` on every
platform; they are not baked into the binary as a discoverable string or byte pattern (the
comparison is compiled/optimized code, not a static constant we can grep for), so this was
verified by reading the upstream source rather than by inspecting the shipped binaries.

## Scope and known gaps

- V1 shipped **linux-x64** only (issue #562). Issue #564 added **win-x64**, validated by
  running the diagnostics server natively on Windows (not WSL) against a native Windows
  CoreClrSample target.
- macOS/arm64/other RIDs remain open follow-up work, not covered by this change.
- The source/provenance for these exact binaries is the validated spike branch
  `spike/method-parameter-capture-attach` (linux-x64) and the same `dotnet-monitor` 10.0.2
  NuGet package (win-x64) — the production implementation reuses those verified payloads
  rather than re-downloading them ad hoc.
