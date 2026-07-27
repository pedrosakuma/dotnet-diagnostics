#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

compose_file="deploy/docker-compose.crash-guard.yml"
project_name="dotnet-diagnostics-crash-guard-test"

cleanup() {
  docker compose --project-name "$project_name" --file "$compose_file" \
    down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

cleanup
docker compose --project-name "$project_name" --file "$compose_file" config --quiet
build_args=(--build)
if [[ "${DOCKER_CRASH_GUARD_SKIP_BUILD:-0}" == "1" ]]; then
  build_args=()
fi
docker compose --project-name "$project_name" --file "$compose_file" \
  up "${build_args[@]}" --detach --wait

DOTNET_DBG_MCP_DOCKER_CRASH_GUARD_TEST=1 \
  dotnet test tests/DotnetDiagnostics.Mcp.IntegrationTests/DotnetDiagnostics.Mcp.IntegrationTests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~DockerCrashGuardIntegrationTests" \
  --logger "console;verbosity=normal"
