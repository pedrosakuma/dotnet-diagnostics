#!/usr/bin/env bash
# perf-sidecar-smoke.sh
#
# Advisory live perf compatibility smoke in ONE production-like Linux x64 sidecar
# topology (issue #934): a PID-namespace anchor, a NativeAOT target and a CoreCLR
# target joined to that namespace, one shared /tmp volume, and a sidecar running the
# production image's CLI with narrowly scoped capabilities (no privileged mode, no
# host PID, no shared-host sysctl changes).
#
# Plans exactly one outcome per capture (see scripts/perf_sidecar_evaluate.py):
#   cpu (NativeAOT, perf), off_cpu, native-alloc, native-lock-contention,
#   plus two negative-prerequisite controls (no CAP_PERFMON, no perf binary).
#
# Required environment:
#   SIDECAR_IMAGE   production image (deploy/Dockerfile, INSTALL_PERF=true) providing
#                   perf and the CLI at /app/cli/dotnet-diagnostics
#   AOT_DIR         published NativeAotSample (linux-x64)
#   BAD_DIR         published BadCodeSample
# Optional:
#   OUT_DIR (default TestResults/perf-sidecar), CAPTURE_SECONDS (8), CAPTURE_TIMEOUT (120)

set -uo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
evaluator="$repo_root/scripts/perf_sidecar_evaluate.py"
: "${SIDECAR_IMAGE:?SIDECAR_IMAGE is required}"
: "${AOT_DIR:?AOT_DIR is required}"
: "${BAD_DIR:?BAD_DIR is required}"
out="${OUT_DIR:-$repo_root/TestResults/perf-sidecar}"
seconds="${CAPTURE_SECONDS:-8}"
bound="${CAPTURE_TIMEOUT:-120}"
run_id="$$"
prefix="perf-sidecar-$run_id"
anchor="$prefix-anchor"; aot="$prefix-aot"; bad="$prefix-bad"; side="$prefix-side"
neg_caps="$prefix-neg-caps"; neg_perf="$prefix-neg-perf"
volume="$prefix-tmp"
cli=/app/cli/dotnet-diagnostics
planned=(cpu-nativeaot off_cpu native-alloc native-lock-contention neg-no-perfmon neg-no-perf)

mkdir -p "$out"
out="$(cd "$out" && pwd)"
rm -f "$out"/*.outcome.json
raw="$out/topology.raw"
: > "$raw"

find_free_port() {
  python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])'
}
aot_port="$(find_free_port)"
bad_port="$(find_free_port)"

cleanup() {
  set +e
  for c in "$side" "$neg_caps" "$neg_perf" "$aot" "$bad" "$anchor"; do
    docker logs "$c" > "$out/$c.log" 2>&1
    docker inspect "$c" > "$out/$c.inspect.json" 2>&1
  done
  docker rm -f "$side" "$neg_caps" "$neg_perf" "$aot" "$bad" "$anchor" > /dev/null 2>&1
  docker volume rm "$volume" > /dev/null 2>&1
  rm -f "$out"/*.stop
}
trap cleanup EXIT

# Probes are recorded as key=value lines; the topology report is assembled afterwards.
record() { echo "$1=$2" >> "$raw"; }
probe_container() {
  local name="$1" container="$2"
  docker exec "$container" sh -c '
    echo "id=$(id -u):$(id -g)"
    echo "CapEff=$(sed -n "s/^CapEff:[[:space:]]*//p" /proc/self/status)"
    echo "Seccomp=$(sed -n "s/^Seccomp:[[:space:]]*//p" /proc/self/status)"
    echo "NoNewPrivs=$(sed -n "s/^NoNewPrivs:[[:space:]]*//p" /proc/self/status)"
    echo "perf_event_paranoid=$(cat /proc/sys/kernel/perf_event_paranoid 2>/dev/null)"
    perf_bin=$(ls /usr/lib/linux-tools-*/perf 2>/dev/null | tail -1)
    echo "perfPath=${perf_bin:-none}"
    [ -n "$perf_bin" ] && echo "perfVersion=$($perf_bin --version 2>&1 | head -1)"
    [ -e /sys/kernel/tracing/events/sched/sched_switch ] && echo "tracepoint.sched_switch=available" || echo "tracepoint.sched_switch=unavailable"
    [ -w /sys/kernel/tracing/uprobe_events ] && echo "uprobe_events=writable" || echo "uprobe_events=unavailable"
  ' 2>&1 | sed "s/^/$name./" >> "$raw"
}

echo "== host"
record host.kernel "$(uname -r)"
record host.arch "$(uname -m)"
record host.docker "$(docker version --format '{{.Server.Version}}' 2>&1)"
record host.cgroup "$(docker info --format '{{.CgroupVersion}}' 2>&1)"
record host.securityOptions "$(docker info --format '{{join .SecurityOptions ","}}' 2>&1)"
record host.runtime "$(docker info --format '{{.DefaultRuntime}}' 2>&1)"
record host.tracefs "$([ -d /sys/kernel/tracing/events ] && echo mounted || echo missing)"
record images.sidecar "$(docker image inspect "$SIDECAR_IMAGE" --format '{{.Id}}' 2>&1)"
record images.sidecarBase "$(docker image inspect "$SIDECAR_IMAGE" --format '{{index .Config.Labels "org.opencontainers.image.base.digest"}}' 2>&1)"

docker volume create "$volume" > /dev/null

# 1) Stable PID-namespace anchor (docs/local-docker-sidecar.md): survives target exit.
docker run -d --name "$anchor" --entrypoint tail "$SIDECAR_IMAGE" -f /dev/null > /dev/null || exit 2

# 2) Targets join the anchor namespace and share /tmp (diagnostic IPC sockets). Same UID as the sidecar.
docker run -d --name "$bad" --pid="container:$anchor" --user 0 -v "$volume:/tmp" -v "$BAD_DIR:/app:ro" \
  -p "127.0.0.1:$bad_port:8080" -e ASPNETCORE_URLS=http://0.0.0.0:8080 -w /app \
  --entrypoint dotnet "$SIDECAR_IMAGE" BadCodeSample.dll > /dev/null || exit 2
docker run -d --name "$aot" --pid="container:$anchor" --user 0 -v "$volume:/tmp" -v "$AOT_DIR:/app:ro" \
  -p "127.0.0.1:$aot_port:8080" -e ASPNETCORE_URLS=http://0.0.0.0:8080 -w /app \
  --entrypoint /app/NativeAotSample "$SIDECAR_IMAGE" > /dev/null || exit 2

# 3) Sidecar: PERFMON for perf_event_open, SYS_PTRACE for /proc/<pid>/root + libc resolution,
#    host tracefs (uprobe_events + sched tracepoints). Nothing else; not privileged.
docker run -d --name "$side" --pid="container:$anchor" --user 0 -v "$volume:/tmp" \
  -v /sys/kernel/tracing:/sys/kernel/tracing --cap-drop ALL --cap-add PERFMON --cap-add SYS_PTRACE \
  --entrypoint tail "$SIDECAR_IMAGE" -f /dev/null > /dev/null || exit 2

# Negative controls: a sidecar without CAP_PERFMON, and one whose perf binary is removed.
docker run -d --name "$neg_caps" --pid="container:$anchor" --user 0 -v "$volume:/tmp" \
  --cap-drop ALL --entrypoint tail "$SIDECAR_IMAGE" -f /dev/null > /dev/null || exit 2
docker run -d --name "$neg_perf" --pid="container:$anchor" --user 0 -v "$volume:/tmp" \
  --cap-drop ALL --cap-add PERFMON --cap-add SYS_PTRACE \
  --entrypoint tail "$SIDECAR_IMAGE" -f /dev/null > /dev/null || exit 2
docker exec "$neg_perf" sh -c 'rm -rf /usr/lib/linux-tools* /usr/bin/perf'

echo "== waiting for targets"
for port in "$aot_port" "$bad_port"; do
  ready=0
  for _ in $(seq 1 60); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/weatherforecast")"
    if [[ "$code" != 000 ]]; then ready=1; break; fi
    sleep 1
  done
  [[ $ready == 1 ]] || { echo "target on port $port never became ready" >&2; exit 2; }
done

aot_pid="$(docker exec "$side" sh -c "ps -eo pid,args | grep '[N]ativeAotSample' | head -1 | awk '{print \$1}'")"
bad_pid="$(docker exec "$side" sh -c "ps -eo pid,args | grep '[B]adCodeSample.dll' | head -1 | awk '{print \$1}'")"
record topology.aotPid "$aot_pid"
record topology.badPid "$bad_pid"
[[ -n "$aot_pid" && -n "$bad_pid" ]] || { echo "targets not visible from the sidecar PID namespace" >&2; exit 2; }

probe_container sidecar "$side"
probe_container negNoPerfmon "$neg_caps"
probe_container negNoPerf "$neg_perf"
python3 "$evaluator" topology --raw "$raw" --out "$out/topology.json"

# Workload driver: runs on the host against the published target port until a stop file appears,
# logging "<status> <seconds>" per request as activation evidence.
drive() {
  local name="$1" port="$2"; shift 2
  local stop="$out/$name.stop" log="$out/$name.activation.log"
  : > "$log"; rm -f "$stop"
  (
    while [[ ! -e "$stop" ]]; do
      for path in "$@"; do
        curl -s -o /dev/null -w '%{http_code} %{time_total}\n' --max-time 20 "http://127.0.0.1:$port$path" >> "$log"
      done
      sleep 0.1
    done
  ) &
  echo $! > "$out/$name.driver.pid"
}
stop_drive() {
  local name="$1"
  touch "$out/$name.stop"
  wait "$(cat "$out/$name.driver.pid")" 2> /dev/null
}

run_capture() {
  local name="$1" kind="$2" container="$3" pid="$4" port="$5" workload="$6"; shift 6
  local expect=("$@")
  echo "== $name"
  if [[ "$workload" != none ]]; then
    IFS=',' read -r -a paths <<< "$workload"
    drive "$name" "$port" "${paths[@]}"
  else
    : > "$out/$name.activation.log"
  fi
  sleep 1
  local args=()
  case "$kind" in
    cpu) args=(--top 10) ;;
    off_cpu) args=(--top 10) ;;
    native-alloc) args=(--native-alloc-sample-period 10) ;;
    native-lock-contention) args=(--native-lock-contention-sample-period 10) ;;
  esac
  docker exec "$container" timeout -k 5 "$bound" "$cli" collect --kind "$kind" --pid "$pid" \
    --duration "$seconds" --acknowledge-risk high --json "${args[@]}" \
    > "$out/$name.json" 2> "$out/$name.stderr.log"
  local code=$?
  [[ "$workload" == none ]] || stop_drive "$name"
  echo "$code" > "$out/$name.exit"
  local flags=()
  [[ $code -eq 124 || $code -eq 137 ]] && flags+=(--timed-out)
  python3 "$evaluator" capture --name "$name" --kind "$kind" --json "$out/$name.json" --exit-code "$code" \
    --activation "$out/$name.activation.log" --out "$out/$name.outcome.json" "${flags[@]}" "${expect[@]}"
}

run_capture cpu-nativeaot cpu "$side" "$aot_pid" "$aot_port" "/cpu"
run_capture off_cpu off_cpu "$side" "$bad_pid" "$bad_port" "/lock-storm?seconds=2&blockers=2"
run_capture native-alloc native-alloc "$side" "$bad_pid" "$bad_port" "/native-bloat?mb=1"
run_capture native-lock-contention native-lock-contention "$side" "$bad_pid" "$bad_port" "/lock-storm?seconds=2&blockers=4"
# Negative controls assert an actionable, classified failure while the target is active.
run_capture neg-no-perfmon cpu "$neg_caps" "$aot_pid" "$aot_port" "/cpu" \
  --expect-error-kind PermissionDenied --expect-message permission
run_capture neg-no-perf cpu "$neg_perf" "$aot_pid" "$aot_port" "/cpu" \
  --expect-error-kind UnsupportedPrerequisite --expect-message perf

python3 "$evaluator" report --dir "$out" --planned "${planned[@]}" \
  --summary-json "$out/summary.json" --summary-md "$out/summary.md"
status=$?
cat "$out/summary.md"
[[ -z "${GITHUB_STEP_SUMMARY:-}" ]] || cat "$out/summary.md" >> "$GITHUB_STEP_SUMMARY"
exit "$status"
