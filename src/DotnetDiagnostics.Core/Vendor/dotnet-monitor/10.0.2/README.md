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
- `shared/any/net6.0/Microsoft.Diagnostics.Monitoring.StartupHook.dll`
  - SHA-256: `28fc0ed8f0c2a5311ef96e7a9c1d199c15e574b4df7627d51e969274831d5633`

Only the runtime payloads needed at target-process attach time are vendored here; the
repository continues to consume `Microsoft.Diagnostics.NETCore.Client` / `TraceEvent`
through normal NuGet references for managed code.

## Scope and known gaps

- V1 intentionally ships **linux-x64** only. Adding more RIDs is follow-up work, not part of
  issue #562.
- The source/provenance for these exact binaries is the validated spike branch
  `spike/method-parameter-capture-attach`; the production implementation reuses those
  verified payloads rather than re-downloading them ad hoc.
