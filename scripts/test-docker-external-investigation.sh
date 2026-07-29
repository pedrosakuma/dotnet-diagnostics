#!/usr/bin/env bash
# test-docker-external-investigation.sh
#
# Docker-bootstrap external-investigation acceptance test (issues #712/#752).
# Starts a real target, invokes the CLI from this checkout as the current user,
# starts a central MCP from the bootstrap-emitted profile config, then runs the
# protocol-level DockerExternalInvestigationTests acceptance test.
#
# Usage:
#   scripts/test-docker-external-investigation.sh
#
# Skip build (use current Release outputs and already-built local images):
#   DOCKER_EXT_INV_SKIP_BUILD=1 scripts/test-docker-external-investigation.sh
#
# Requirements:
#   - Docker Engine with access for the current non-root user
#   - curl and Python 3
#   - .NET 10 SDK on PATH

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

run_id="${DOCKER_EXT_INV_RUN_ID:-$$}"
run_id="${run_id//[^A-Za-z0-9_.-]/-}"
prefix="dotnet-diagnostics-bootstrap-e2e-${run_id}"
target_name="${prefix}-target"
sidecar_name="${prefix}-sidecar"
central_name="${prefix}-central"
network_name="${prefix}-network"
profile_name="bootstrap-${run_id//[^A-Za-z0-9_-]/-}"
central_token="central-${run_id}-token"
sidecar_token="sidecar-${run_id}-token"
delegation_key="delegation-${run_id}-key"
artifact_dir="${DOCKER_EXT_INV_ARTIFACT_DIR:-TestResults/docker-bootstrap-e2e}"
bootstrap_json="$artifact_dir/bootstrap.json"
central_env="$artifact_dir/central.env"

mkdir -p "$artifact_dir"

find_free_port() {
  python3 - <<'PY'
import socket
with socket.socket() as sock:
    sock.bind(("127.0.0.1", 0))
    print(sock.getsockname()[1])
PY
}

target_port="${DOCKER_EXT_INV_TARGET_PORT:-$(find_free_port)}"
central_port="${DOCKER_EXT_INV_CENTRAL_PORT:-$(find_free_port)}"

capture_diagnostics() {
  for container in "$target_name" "$sidecar_name" "$central_name"; do
    docker inspect "$container" >"$artifact_dir/${container}.inspect.json" 2>&1 || true
    docker logs "$container" >"$artifact_dir/${container}.log" 2>&1 || true
  done
  docker network inspect "$network_name" >"$artifact_dir/${network_name}.inspect.json" 2>&1 || true
}

cleanup() {
  local status=$?
  set +e
  if [[ $status -ne 0 ]]; then
    capture_diagnostics
  fi
  docker rm -f "$central_name" "$sidecar_name" "$target_name" >/dev/null 2>&1 || true
  docker network rm "$network_name" >/dev/null 2>&1 || true
  return "$status"
}
trap cleanup EXIT

if [[ "$(id -u)" == "0" ]]; then
  echo "docker-bootstrap E2E must run as a normal non-root user." >&2
  exit 1
fi

if [[ "${DOCKER_EXT_INV_SKIP_BUILD:-0}" != "1" ]]; then
  dotnet build DotnetDiagnostics.slnx --configuration Release
  docker build --tag dotnet-diagnostics-mcp:dev --file deploy/Dockerfile .
  docker build --tag coreclr-sample:dev --file samples/CoreClrSample/Dockerfile .
fi

cli_dll="src/DotnetDiagnostics.Cli/bin/Release/net10.0/dotnet-diagnostics.dll"
[[ -f "$cli_dll" ]] || {
  echo "Missing $cli_dll; build the solution or omit DOCKER_EXT_INV_SKIP_BUILD=1." >&2
  exit 1
}

docker network create "$network_name" >/dev/null

docker run --detach \
  --name "$target_name" \
  --publish "127.0.0.1:${target_port}:8080" \
  --env DOTNET_EnableDiagnostics=1 \
  coreclr-sample:dev >/dev/null

for _ in {1..60}; do
  if curl --fail --silent "http://127.0.0.1:${target_port}/weatherforecast" >/dev/null; then
    break
  fi
  sleep 1
done
curl --fail --silent "http://127.0.0.1:${target_port}/weatherforecast" >/dev/null

docker run --detach \
  --name "$central_name" \
  --network "$network_name" \
  --publish "127.0.0.1:${central_port}:8080" \
  --env ASPNETCORE_URLS=http://0.0.0.0:8080 \
  --env DOTNET_EnableDiagnostics=0 \
  --env DOTNET_NOLOGO=1 \
  --env MCP_BEARER_TOKEN="$central_token" \
  --health-cmd "dotnet DotnetDiagnostics.Mcp.dll --health-check --urls http://127.0.0.1:8080" \
  --health-interval 2s \
  --health-timeout 2s \
  --health-start-period 10s \
  --health-retries 30 \
  dotnet-diagnostics-mcp:dev >/dev/null

for _ in {1..90}; do
  health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$central_name")"
  [[ "$health" == "healthy" ]] && break
  [[ "$health" == "unhealthy" ]] && {
    echo "Initial central MCP became unhealthy." >&2
    exit 1
  }
  sleep 1
done
[[ "$(docker inspect --format '{{.State.Health.Status}}' "$central_name")" == "healthy" ]]

dotnet "$cli_dll" docker-bootstrap \
  --target-container "$target_name" \
  --central-container "$central_name" \
  --sidecar-name "$sidecar_name" \
  --sidecar-image dotnet-diagnostics-mcp:dev \
  --profile-name "$profile_name" \
  --bearer-token "$sidecar_token" \
  --delegation-key "$delegation_key" \
  --wait 120 \
  --json | tee "$bootstrap_json"

python3 - "$bootstrap_json" "$central_env" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    envelope = json.load(source)
if envelope.get("error") is not None or envelope.get("data") is None:
    raise SystemExit("docker-bootstrap returned an error envelope")
report = envelope["data"]
if report["route"] != "docker-network":
    raise SystemExit(f"expected docker-network route, got {report['route']}")
if report["dockerNetwork"] is None:
    raise SystemExit("central-aware bootstrap did not report a selected Docker network")
if report["profileUrl"] != f"http://{report['dockerNetworkAlias']}:8080/mcp":
    raise SystemExit(f"unexpected private profile URL: {report['profileUrl']}")
if report["hostPortPublished"]:
    raise SystemExit("central-aware bootstrap unnecessarily published a sidecar host port")
if len(report["allowedCidrs"]) != 1 or not report["allowedCidrs"][0].endswith("/32"):
    raise SystemExit(f"expected a single sidecar /32 allowlist, got {report['allowedCidrs']}")
with open(sys.argv[2], "w", encoding="utf-8") as target:
    for line in report["centralEnvLines"]:
        target.write(f"{line}\n")
PY

cat >>"$central_env" <<EOF
ASPNETCORE_URLS=http://0.0.0.0:8080
DOTNET_EnableDiagnostics=0
DOTNET_NOLOGO=1
Orchestrator__Enabled=true
Auth__BearerTokens__0__Name=bootstrap-e2e
Auth__BearerTokens__0__Token=${central_token}
Auth__BearerTokens__0__Scopes__0=root
Auth__BearerTokens__0__Scopes__1=orchestrator-admin
EOF

python3 - "$sidecar_name" "$bootstrap_json" <<'PY'
import json
import subprocess
import sys

inspection = json.loads(subprocess.check_output(
    ["docker", "inspect", "--type", "container", sys.argv[1]],
    text=True,
))[0]
mounts = inspection.get("Mounts", [])
if any(mount.get("Source") == "/proc" or mount.get("Destination") == "/host/proc" for mount in mounts):
    raise SystemExit("bootstrap sidecar unexpectedly contains the obsolete host /proc bind mount")
if any(
    mount.get("Source") == "/var/run/docker.sock"
    or mount.get("Destination") == "/var/run/docker.sock"
    for mount in mounts
):
    raise SystemExit("bootstrap sidecar must not receive Docker-socket access")
with open(sys.argv[2], encoding="utf-8") as source:
    report = json.load(source)["data"]
expected_tmp = f"/proc/{report['targetNamespacePid']}/root/tmp"
actual_tmp = next(
    (item.removeprefix("TMPDIR=") for item in inspection["Config"]["Env"] if item.startswith("TMPDIR=")),
    None,
)
if actual_tmp != expected_tmp:
    raise SystemExit(f"sidecar TMPDIR mismatch: expected {expected_tmp}, got {actual_tmp}")
if inspection.get("HostConfig", {}).get("PortBindings", {}).get("8080/tcp"):
    raise SystemExit("central-aware sidecar unexpectedly published port 8080")
selected_network = report["dockerNetwork"]
if selected_network not in inspection["NetworkSettings"]["Networks"]:
    raise SystemExit(f"sidecar is not connected to selected network {selected_network}")
PY

docker rm -f "$central_name" >/dev/null

docker run --detach \
  --name "$central_name" \
  --network "$network_name" \
  --publish "127.0.0.1:${central_port}:8080" \
  --env-file "$central_env" \
  --health-cmd "dotnet DotnetDiagnostics.Mcp.dll --health-check --urls http://127.0.0.1:8080" \
  --health-interval 2s \
  --health-timeout 2s \
  --health-start-period 10s \
  --health-retries 30 \
  dotnet-diagnostics-mcp:dev >/dev/null

for _ in {1..90}; do
  health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$central_name")"
  [[ "$health" == "healthy" ]] && break
  [[ "$health" == "unhealthy" ]] && {
    echo "Central MCP became unhealthy." >&2
    exit 1
  }
  sleep 1
done
[[ "$(docker inspect --format '{{.State.Health.Status}}' "$central_name")" == "healthy" ]]

python3 - "$central_name" <<'PY'
import json
import subprocess
import sys

inspection = json.loads(subprocess.check_output(
    ["docker", "inspect", "--type", "container", sys.argv[1]],
    text=True,
))[0]
if any(
    mount.get("Source") == "/var/run/docker.sock"
    or mount.get("Destination") == "/var/run/docker.sock"
    for mount in inspection.get("Mounts", [])
):
    raise SystemExit("central MCP must not receive Docker-socket access")
PY

DOTNET_DBG_MCP_DOCKER_EXT_INV_TEST=1 \
DOTNET_DBG_MCP_DOCKER_EXT_INV_CENTRAL_URL="http://127.0.0.1:${central_port}/mcp" \
DOTNET_DBG_MCP_DOCKER_EXT_INV_CENTRAL_TOKEN="$central_token" \
DOTNET_DBG_MCP_DOCKER_EXT_INV_PROFILE="$profile_name" \
DOTNET_DBG_MCP_DOCKER_EXT_INV_TARGET_URL="http://127.0.0.1:${target_port}" \
  dotnet test tests/DotnetDiagnostics.Mcp.IntegrationTests/DotnetDiagnostics.Mcp.IntegrationTests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~DockerExternalInvestigationTests" \
  --logger "trx;LogFileName=docker-bootstrap-e2e.trx" \
  --logger "console;verbosity=normal" \
  --results-directory "$artifact_dir"

capture_diagnostics
