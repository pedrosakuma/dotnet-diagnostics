# Multi-stage Dockerfile for dotnet-diagnostics-mcp server.
#
# Build:  docker build -t dotnet-diagnostics-mcp:dev -f deploy/Dockerfile .
# Run:    docker run --rm -p 8787:8080 -e MCP_BEARER_TOKEN=changeme dotnet-diagnostics-mcp:dev
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
FROM mcr.microsoft.com/dotnet/sdk@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
WORKDIR /src

# Bring in the files needed to restore first so the layer caches well.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/DotnetDiagnostics.Core/DotnetDiagnostics.Core.csproj src/DotnetDiagnostics.Core/
COPY src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj src/DotnetDiagnostics.Mcp/
COPY src/DotnetDiagnostics.Cli/DotnetDiagnostics.Cli.csproj src/DotnetDiagnostics.Cli/

RUN dotnet restore src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj \
 && dotnet restore src/DotnetDiagnostics.Cli/DotnetDiagnostics.Cli.csproj

# Copy the rest of the source needed by the server project.
COPY src/ src/

RUN dotnet publish src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Standalone one-shot CLI (issue #288). Published framework-dependent (the aspnet
# runtime base already carries the .NET runtime) so operators can `kubectl exec`
# into the sidecar and run `dotnet-diagnostics-cli ...` against the shared target
# process without standing up the HTTP server. UseAppHost=true so the symlink in
# the runtime stage has a native launcher to point at.
RUN dotnet publish src/DotnetDiagnostics.Cli/DotnetDiagnostics.Cli.csproj \
    -c Release \
    -o /app/cli \
    --no-restore \
    -p:PackAsTool=false \
    /p:UseAppHost=true

# mcr.microsoft.com/dotnet/aspnet:10.0
FROM mcr.microsoft.com/dotnet/aspnet@sha256:ddcf70ad1ab963a4fcd41fbd722a6b660e404e87567cfbd46fd2809c21b02088 AS runtime
WORKDIR /app

# Kestrel listens on 8080 by default in our ASPNETCORE_URLS below.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
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
RUN chown -R diagnosticsmcp:diagnosticsmcp /app
USER diagnosticsmcp

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "DotnetDiagnostics.Mcp.dll"]
