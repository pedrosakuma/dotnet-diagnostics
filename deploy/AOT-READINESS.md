# AOT / Trim readiness

Tracking issue for the AOT publish goal (todo `aot-publish-server`).

## TL;DR

`PublishAot=true` and even `PublishTrimmed=true` are **not viable today** for
`DotnetDiagnostics.Mcp`. The blockers live in dependencies we don't
control, not in our own code. The recommended sidecar build is therefore the
**framework-dependent (FDD)** publish, deployed on top of the official
`mcr.microsoft.com/dotnet/aspnet:10.0` base image.

| Mode | Recipe | Output size | Status |
|------|--------|-------------|--------|
| Framework-dependent (recommended) | `dotnet publish -c Release` | ~17 MB | ✅ Ships today |
| Self-contained | `+ -p:RuntimeIdentifier=linux-x64 --self-contained true` | ~123 MB | ✅ Ships today |
| Self-contained + ReadyToRun | `+ -p:PublishReadyToRun=true` | ~132 MB | ✅ Ships today (faster cold start) |
| Trimmed | `+ -p:PublishTrimmed=true` | n/a | ❌ Blocked by deps |
| NativeAOT | `+ -p:PublishAot=true` | n/a | ❌ Blocked by deps |

## What is in our code

Our own code is AOT-clean as of this commit:

- `InvestigationSummaryJsonContext` (Core) and `TraceSessionJsonContext` (Server)
  are `JsonSerializerContext` source-generated metadata. Every diagnostic
  payload serialized by the server uses the typed `JsonTypeInfo` overloads.
- `ProcessInfoReflection.GetPropertyValue<T>` has a documented
  `[UnconditionalSuppressMessage("AOT", "IL2070")]` justification — the
  reflected type lives inside `Microsoft.Diagnostics.NETCore.Client` and the
  bridge already returns `null` when the property goes missing.

## Blockers (dependencies)

Re-running `PublishTrimmed=true -p:TrimMode=partial -p:PublishReadyToRun=false`
surfaces three independent walls:

1. **`Microsoft.Diagnostics.Tracing.TraceEvent` + `Microsoft.Diagnostics.FastSerialization`**
   - `IL2104: Assembly produced trim warnings` — the FastSerialization
     binary-format codepath relies on runtime reflection over event-record
     types. Trimming silently strips half of TraceEvent's parsers.
   - No `IsTrimmable=true` annotation upstream as of TraceEvent 3.2.2.

2. **`Microsoft.NET.Sdk.Web` MVC stack**
   - The Web SDK pulls in `Microsoft.AspNetCore.Mvc.*`, all of which use
     reflection-based model binders, formatters, awaitable detection, and
     `ApplicationParts`. Hundreds of IL2026 warnings from MVC even though we
     only use Minimal APIs + `MapMcp`.
   - We could in principle migrate to a slim host that opts out of MVC, but
     `ModelContextProtocol.AspNetCore`'s `MapMcp` currently requires the full
     web stack.

3. **`ModelContextProtocol` SDK (1.3.x)**
   - Tool registration relies on reflection over `[McpServerToolType]` and
     `[McpServerTool]` attributes. The SDK does not yet ship an `IsAotCompatible=true`
     surface or source-generated tool registration.

`Microsoft.Diagnostics.Runtime` (ClrMD 3.x) also has a large reflection
surface but has not yet been audited because the earlier walls fire first.

## Intermediate path that works today

For the smallest sidecar image with the fewest moving parts, use the existing
`deploy/Dockerfile` — it publishes framework-dependent on top of
`mcr.microsoft.com/dotnet/aspnet:10.0`. The runtime layer is shared across
other .NET workloads on the node, so the on-disk delta per pod is the ~17 MB
framework-dependent publish:

```bash
docker build -t dotnet-diagnostics-mcp:dev -f deploy/Dockerfile .
```

For a fully self-contained image (no runtime base image dependency, larger but
portable to a `mcr.microsoft.com/dotnet/runtime-deps` minimal layer):

```bash
dotnet publish src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj \
    -c Release -p:RuntimeIdentifier=linux-x64 --self-contained true -o ./out
```

For faster cold start (recommended for short-lived ephemeral debug containers),
layer ReadyToRun on top of the self-contained publish. R2R precompiles the hot
methods, eliminating the first-request JIT spike at the cost of a ~9 MB bigger
image:

```bash
dotnet publish src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj \
    -c Release -p:RuntimeIdentifier=linux-x64 --self-contained true \
    -p:PublishReadyToRun=true -o ./out
```

## Re-evaluation triggers

This document should be revisited when **any** of these land:

- TraceEvent ships with `IsTrimmable=true` (track
  [microsoft/perfview#TBD](https://github.com/microsoft/perfview)).
- `ModelContextProtocol.AspNetCore` ships a source-generated `MapMcp`
  registration path (track upstream SDK).
- ASP.NET Core publishes a "MCP host" template that opts out of MVC.
- ClrMD ships an explicit AOT compatibility statement.

Until then, the FDD recipe above is the contract.
