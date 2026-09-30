# syntax=docker/dockerfile:1
# Multi-stage Dockerfile for dotnet-diagnostics-mcp server.
#
# Build:  docker build --secret id=nugetconfig,src="$HOME/.nuget/NuGet/NuGet.Config" \
#           -t dotnet-diagnostics:dev -f deploy/Dockerfile .
# Run:    configure ASPNETCORE_URLS and transport security deliberately before exposing a port.
#
# When deployed as a Kubernetes sidecar, the server attaches to the target .NET
# process via its Diagnostic IPC socket; see deploy/k8s/README.md for the
# pod-level requirements (shareProcessNamespace + shared emptyDir over /tmp).

# Base images are pinned by digest (immutable). The original floating tag is
# documented on the line immediately above each FROM as a comment so it remains
# human-greppable and Dependabot's docker ecosystem (see .github/dependabot.yml,
# #167) can keep them current. The DOTNET_VERSION ARG is preserved for downstream
# consumers that build from this Dockerfile with a different floating tag; the
# digest pin still wins unless they override the FROM line.
ARG DOTNET_VERSION=10.0

# mcr.microsoft.com/dotnet/sdk:10.0
FROM mcr.microsoft.com/dotnet/sdk@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 \
    DOTNET_NOLOGO=1

# Bring in the files needed to restore first so the layer caches well.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/DotnetDiagnostics.Core/DotnetDiagnostics.Core.csproj src/DotnetDiagnostics.Core/
COPY src/DotnetDiagnostics.Core/Build/ src/DotnetDiagnostics.Core/Build/
COPY src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj src/DotnetDiagnostics.Mcp/
COPY src/DotnetDiagnostics.Cli/DotnetDiagnostics.Cli.csproj src/DotnetDiagnostics.Cli/

RUN --mount=type=secret,id=nugetconfig,target=/run/secrets/NuGet.Config,required=true \
    dotnet restore src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj \
      --configfile /run/secrets/NuGet.Config \
 && dotnet restore src/DotnetDiagnostics.Cli/DotnetDiagnostics.Cli.csproj \
      --configfile /run/secrets/NuGet.Config

# The deny-all .dockerignore also permits optional, already-produced native assets.
# No native compiler is installed or run here; opting in requires the prepared artifact.
COPY . ./
ARG INCLUDE_PORTABLE_CAPTURE_WORKER=false

RUN if [ "$INCLUDE_PORTABLE_CAPTURE_WORKER" = "true" ]; then \
      test "$(dotnet --info | sed -n 's/^ *RID: *//p' | head -n 1)" = "linux-x64" || exit 1; \
      export PortableCaptureWorkerAssetsDir=/src/artifacts/portable-worker/linux-x64 \
                RequirePortableCaptureWorkerAssets=true; \
    elif [ "$INCLUDE_PORTABLE_CAPTURE_WORKER" != "false" ]; then exit 1; fi \
 && dotnet publish src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Standalone one-shot CLI (issue #288). Published framework-dependent (the aspnet
# runtime base already carries the .NET runtime) so operators can `kubectl exec`
# into the sidecar and run `dotnet-diagnostics-cli ...` against the shared target
# process without standing up the HTTP server. UseAppHost=true so the symlink in
# the runtime stage has a native launcher to point at.
RUN if [ "$INCLUDE_PORTABLE_CAPTURE_WORKER" = "true" ]; then \
      export PortableCaptureWorkerAssetsDir=/src/artifacts/portable-worker/linux-x64 \
             RequirePortableCaptureWorkerAssets=true; \
    fi \
 && dotnet publish src/DotnetDiagnostics.Cli/DotnetDiagnostics.Cli.csproj \
    -c Release \
    -o /app/cli \
    --no-restore \
    -p:PackAsTool=false \
    /p:UseAppHost=true

# mcr.microsoft.com/dotnet/aspnet:10.0
FROM mcr.microsoft.com/dotnet/aspnet@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7 AS runtime
WORKDIR /app

# Loopback is a safe default for cleartext HTTP. Operators exposing the container
# must explicitly select HTTPS, a trusted proxy, or the development-only override.
ENV ASPNETCORE_URLS=http://127.0.0.1:8080 \
    DOTNET_EnableDiagnostics=1 \
    DOTNET_NOLOGO=1

# Non-root user for least privilege. The sidecar still needs PID visibility on the
# *target* container to use DiagnosticsClient — see deploy/k8s/README.md.
RUN groupadd --system --gid 10001 diagnosticsmcp \
 && useradd --system --uid 10001 --gid diagnosticsmcp --create-home --shell /usr/sbin/nologin diagnosticsmcp

# Install elfutils (eu-stack) for Linux NativeAOT thread snapshots, plus Linux perf
# (linux-tools-common wrapper + linux-tools-generic kernel-versioned binary) for
# NativeAOT CPU sampling fallback and off-CPU sched capture. Ubuntu Noble (the .NET 10
# base) does not ship the transitional `linux-perf` meta-package; PerfBinaryResolver
# iterates /usr/lib/linux-tools-* to locate the binary, so a host-kernel mismatch is
# benign for our usage.
#
# At runtime the container additionally needs CAP_PERFMON (or perf_event_paranoid <= 1
# on the host) for perf to actually collect — see deploy/k8s/ephemeral-attach.patch.json
# for the K8s recipe.
#
# If you enable external symbol servers, prefer a writable shared cache such as
# /var/cache/mcp/symbols and point MCP_SYMBOL_PATH at it, e.g.
#   MCP_SYMBOL_PATH='srv*/var/cache/mcp/symbols*https://msdl.microsoft.com/download/symbols'
#
# Pass --build-arg INSTALL_PERF=false to opt OUT (keeps the image ~80MB smaller but
# disables collect_off_cpu_sample and the perf-replay thread-snapshot fallback).
ARG INSTALL_PERF=true
RUN apt-get update \
 && apt-get install -y --no-install-recommends elfutils \
 && if [ "$INSTALL_PERF" = "true" ]; then \
        apt-get install -y --no-install-recommends \
            linux-tools-common linux-tools-generic; \
    fi \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY --from=build /app/cli /app/cli
# Surface the standalone CLI on PATH under its packaged command name. The apphost
# is named after the assembly (dotnet-diagnostics); users invoke dotnet-diagnostics-cli.
RUN ln -s /app/cli/dotnet-diagnostics /usr/local/bin/dotnet-diagnostics-cli
RUN mkdir -p /app/.dotnet-diagnostics/bootstrap-profiles \
 && touch /app/.dotnet-diagnostics/bootstrap-profile-support-v1
# Keep the application and all shipped binaries/assets immutable. Only the
# profile directory is writable under /app; artifact output uses the writable
# system temp directory and user-scoped runtime state uses the user's home.
RUN chown -R root:root /app \
 && chown diagnosticsmcp:diagnosticsmcp /app/.dotnet-diagnostics/bootstrap-profiles \
 && chmod 0700 /app/.dotnet-diagnostics/bootstrap-profiles \
 && find /app -path /app/.dotnet-diagnostics/bootstrap-profiles -prune -o -type d -exec chmod 0555 {} + \
 && find /app -path /app/.dotnet-diagnostics/bootstrap-profiles -prune -o -type f -exec chmod a-w {} +
USER diagnosticsmcp

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "DotnetDiagnostics.Mcp.dll", "--health-check"]

ENTRYPOINT ["dotnet", "DotnetDiagnostics.Mcp.dll"]
