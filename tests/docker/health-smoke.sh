#!/usr/bin/env bash
set -euo pipefail

image="${1:-dotnet-diagnostics-mcp:health-smoke}"
container="dotnet-diagnostics-health-smoke-${GITHUB_RUN_ID:-local}-$$"
token="health-smoke-token"

if [[ -z "${NUGET_CONFIG:-}" ]]; then
  echo "NUGET_CONFIG must explicitly name a private NuGet.Config; the user-level default is not accepted." >&2
  exit 1
fi
nuget_config="$NUGET_CONFIG"
if [[ ! -s "$nuget_config" ]]; then
  echo "The private NuGet.Config does not exist or is empty: $nuget_config" >&2
  exit 1
fi
if grep -Eiq 'api\.nuget\.org|(^|[^[:alnum:].-])nuget\.org([^[:alnum:].-]|$)' "$nuget_config"; then
  echo "The supplied NuGet.Config references NuGet.org; no public package or audit source is permitted." >&2
  exit 1
fi

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker build \
  --secret "id=nugetconfig,src=$nuget_config" \
  --build-arg INSTALL_PERF=false \
  --tag "$image" \
  --file deploy/Dockerfile \
  .

# The smoke publishes only to host loopback and intentionally exercises the
# image's HTTP health path rather than production TLS configuration.
docker run --detach \
  --name "$container" \
  --env "MCP_BEARER_TOKEN=$token" \
  --env "ASPNETCORE_URLS=http://0.0.0.0:8080" \
  --env "MCP_ALLOW_INSECURE_HTTP=true" \
  --publish 127.0.0.1::8080 \
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

port="$(docker port "$container" 8080/tcp | sed -n 's/.*:\([0-9][0-9]*\)$/\1/p' | head -n 1)"
if [[ -z "$port" ]]; then
  echo "Could not resolve the published MCP port." >&2
  exit 1
fi

response="$(
  curl --fail --silent --show-error \
    --request POST "http://127.0.0.1:${port}/mcp" \
    --header "Authorization: Bearer $token" \
    --header 'Content-Type: application/json' \
    --header 'Accept: application/json, text/event-stream' \
    --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"docker-health-smoke","version":"1"}}}'
)"

grep -q '"jsonrpc":"2.0"' <<<"$response"
grep -q '"id":1' <<<"$response"
grep -q '"result":' <<<"$response"

echo "Container reached healthy status and the authenticated MCP initialize request succeeded."
