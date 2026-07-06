# Vendored dotnet-monitor profiler payloads (experimental spike only)

This directory stages a minimal subset of the published `dotnet-monitor` .NET tool package plus one required managed transitive dependency for the method-parameter-capture spike tracked by issue #555.

## Provenance

- Package: `dotnet-monitor`
- Version: `10.0.2`
- NuGet SHA-256: `487e0c73fc917d83e6930c1628bd6849be13d6e968b712c5f0e0cafa1dc1caff`
- NuGet metadata repository: `https://github.com/dotnet/dotnet-monitor` @ `a2c81101cea07e1b11a15ae8c30bbe9a0fa1a3d5`
- License: `MIT` (verified from `dotnet-monitor.nuspec` in the package: `<license type="expression">MIT</license>`)

## Files staged for this spike

- `linux-x64/native/libMonitorProfiler.so`
  - SHA-256: `b749f2ed44edd28a3d875b391434ec5500f20e81add813b50c3c69d6be1f8493`
- `linux-x64/native/libMutatingMonitorProfiler.so`
  - SHA-256: `96ef7467a02234d1aac63a0366bfe03fecf23b0e95c82fbdd4203d979762e953`
- `shared/any/net6.0/Microsoft.Diagnostics.Monitoring.StartupHook.dll`
  - SHA-256: `28fc0ed8f0c2a5311ef96e7a9c1d199c15e574b4df7627d51e969274831d5633`
- `managed/net10.0/Microsoft.Diagnostics.NETCore.Client.dll`
  - SHA-256: `e2cc9e1f6c97b3c4ee45ebabaa92408eb9b2fc81cb08ceeb342fe48bf79db15f`
- `managed/net10.0/Microsoft.Diagnostics.Tracing.TraceEvent.dll`
  - SHA-256: `7ad04f5abbd704e8bd9c7b29f9aeab951f20cdc5d5c8e701f256be52a6e8543e`
- `managed/net10.0/Microsoft.Diagnostics.FastSerialization.dll`
  - SHA-256: `4cce4b44f8e2dcfda8a67bc4e8131d22c63d06e38c23793728906bed51d34523`

## Scope warning

These payloads are checked in only to support the throwaway implementation spike in issue #555. They are **not approved for production inclusion** and must not be treated as a shipping decision. Any production path still needs the security/legal/design review tracked separately by issue #556.

The managed helper assemblies are staged only so the standalone spike app can compile and run against the same diagnostics client/EventPipe bits that shipped inside the inspected tool package, without changing the repository's central package management setup. `Microsoft.Diagnostics.FastSerialization.dll` is included because `Microsoft.Diagnostics.Tracing.TraceEvent.dll` depends on it transitively but direct file references do not copy transitive assemblies automatically.
