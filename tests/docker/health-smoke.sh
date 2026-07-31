#!/usr/bin/env bash
set -euo pipefail

image="${1:-dotnet-diagnostics-mcp:health-smoke}"
container="dotnet-diagnostics-health-smoke-${GITHUB_RUN_ID:-local}-$$"
token="health-smoke-token"

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker build \
  --build-arg INSTALL_PERF=false \
  --tag "$image" \
  --file deploy/Dockerfile \
  .

# The smoke publishes only to host loopback and intentionally exercises the
# image's HTTP health path rather than production TLS configuration.
docker run --detach \
  --name "$container" \
  --env "MCP_BEARER_TOKEN=$token" \
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
