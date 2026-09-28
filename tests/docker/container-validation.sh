#!/usr/bin/env bash
set -euo pipefail

image="${1:-dotnet-diagnostics:container-validation}"
container="dotnet-diagnostics-container-validation-${GITHUB_RUN_ID:-local}-$$"
token="container-validation-token"

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker run --detach \
  --name "$container" \
  --env "MCP_BEARER_TOKEN=$token" \
  "$image" >/dev/null

deadline=$((SECONDS + 90))
while true; do
  status="$(docker inspect --format '{{.State.Health.Status}}' "$container")"
  case "$status" in
    healthy)
      break
      ;;
    unhealthy)
      docker inspect --format '{{json .State.Health}}' "$container"
      docker logs "$container"
      exit 1
      ;;
  esac

  if (( SECONDS >= deadline )); then
    docker inspect --format '{{json .State.Health}}' "$container"
    docker logs "$container"
    echo "Timed out waiting for the container to become healthy." >&2
    exit 1
  fi
  sleep 2
done

docker exec --user 10001 "$container" sh -ec '
  test "$(id -u)" = 10001
  test "$(stat -c %u /app)" = 0
  test "$(stat -c %u /app/DotnetDiagnostics.Mcp.dll)" = 0
  test "$(stat -c %u /app/cli/dotnet-diagnostics)" = 0
  test ! -w /app
  test ! -w /app/DotnetDiagnostics.Mcp.dll
  test ! -w /app/.dotnet-diagnostics
  test -w /app/.dotnet-diagnostics/bootstrap-profiles
  test ! -w /app/.dotnet-diagnostics/bootstrap-profile-support-v1
  find /app -path /app/.dotnet-diagnostics/bootstrap-profiles -prune -o \
    \( -type d -o -type f \) -exec sh -ec "test ! -w \"\$1\"" sh {} \;
'

docker exec --user 10001 "$container" dotnet-diagnostics-cli processes

echo "Default loopback server is healthy; CLI runs as UID 10001 and shipped application files are immutable."
