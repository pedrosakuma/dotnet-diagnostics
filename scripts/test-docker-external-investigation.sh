#!/usr/bin/env bash
# test-docker-external-investigation.sh
#
# Opt-in acceptance test for the external-investigation flow (issue #712).
# Builds the topology described in deploy/docker-compose.external-investigation.yml,
# waits for all services to be healthy, then runs the
# DockerExternalInvestigationTests acceptance test.
#
# Usage:
#   scripts/test-docker-external-investigation.sh
#
# Skip rebuild (use already-built local images):
#   DOCKER_EXT_INV_SKIP_BUILD=1 scripts/test-docker-external-investigation.sh
#
# Requirements:
#   - Docker with Compose v2 (docker compose)
#   - .NET 10 SDK on PATH

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

compose_file="deploy/docker-compose.external-investigation.yml"
project_name="dotnet-diagnostics-ext-inv-test"

cleanup() {
  docker compose --project-name "$project_name" --file "$compose_file" \
    down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

cleanup
docker compose --project-name "$project_name" --file "$compose_file" config --quiet

build_args=(--build)
if [[ "${DOCKER_EXT_INV_SKIP_BUILD:-0}" == "1" ]]; then
  build_args=()
fi

docker compose --project-name "$project_name" --file "$compose_file" \
  up "${build_args[@]}" --detach --wait

DOTNET_DBG_MCP_DOCKER_EXT_INV_TEST=1 \
  dotnet test tests/DotnetDiagnostics.Mcp.IntegrationTests/DotnetDiagnostics.Mcp.IntegrationTests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~DockerExternalInvestigationTests" \
  --logger "console;verbosity=normal"
