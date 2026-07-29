#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/run-scenario-evaluation-isolated.sh [options]

Runs one scenario repetition per isolated `dotnet test` host process and writes:
  - per-attempt JSON artifacts emitted by the xUnit isolated-trial test
  - per-trial JSON summaries, including explicit "crashed" outcomes
  - an aggregate summary.json for the whole invocation

Options:
  --scenario <id>            Run one scenario ID. Repeat to run multiple.
  --repetitions <count>      Number of isolated trials per scenario (default: 1).
  --results-root <path>      Output root (default: artifacts/scenario-evaluation-isolated).
  --project <path>           Scenario test project path.
  --configuration <config>   Build configuration (default: Release).
  --build                    Omit --no-build when invoking dotnet test.
  --max-crash-retries <n>    Retry count for crash-only outcomes (default: 1).
  --help                     Show this help.
EOF
}

project="tests/DotnetDiagnostics.ScenarioEvaluation.Tests/DotnetDiagnostics.ScenarioEvaluation.Tests.csproj"
configuration="Release"
results_root="artifacts/scenario-evaluation-isolated"
repetitions=1
max_crash_retries=1
use_no_build=true
declare -a scenarios=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --scenario)
      [[ $# -ge 2 ]] || { echo "missing value for --scenario" >&2; exit 2; }
      scenarios+=("$2")
      shift 2
      ;;
    --repetitions)
      [[ $# -ge 2 ]] || { echo "missing value for --repetitions" >&2; exit 2; }
      repetitions="$2"
      shift 2
      ;;
    --results-root)
      [[ $# -ge 2 ]] || { echo "missing value for --results-root" >&2; exit 2; }
      results_root="$2"
      shift 2
      ;;
    --project)
      [[ $# -ge 2 ]] || { echo "missing value for --project" >&2; exit 2; }
      project="$2"
      shift 2
      ;;
    --configuration)
      [[ $# -ge 2 ]] || { echo "missing value for --configuration" >&2; exit 2; }
      configuration="$2"
      shift 2
      ;;
    --build)
      use_no_build=false
      shift
      ;;
    --max-crash-retries)
      [[ $# -ge 2 ]] || { echo "missing value for --max-crash-retries" >&2; exit 2; }
      max_crash_retries="$2"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

[[ "$repetitions" =~ ^[0-9]+$ ]] || { echo "--repetitions must be a non-negative integer" >&2; exit 2; }
[[ "$repetitions" -ge 1 ]] || { echo "--repetitions must be at least 1" >&2; exit 2; }
[[ "$max_crash_retries" =~ ^[0-9]+$ ]] || { echo "--max-crash-retries must be a non-negative integer" >&2; exit 2; }

repo_root=$(pwd)
manifest_dir="$repo_root/tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Scenarios"
[[ -d "$manifest_dir" ]] || { echo "scenario manifest directory not found: $manifest_dir" >&2; exit 1; }
python_bin="$(command -v python3 || command -v python || true)"
[[ -n "$python_bin" ]] || { echo "python3 or python is required" >&2; exit 1; }
project=$("$python_bin" - "$repo_root" "$project" <<'PY'
import pathlib
import sys

base = pathlib.Path(sys.argv[1])
value = pathlib.Path(sys.argv[2])
print((value if value.is_absolute() else base / value).resolve())
PY
)
results_root=$("$python_bin" - "$repo_root" "$results_root" <<'PY'
import pathlib
import sys

base = pathlib.Path(sys.argv[1])
value = pathlib.Path(sys.argv[2])
print((value if value.is_absolute() else base / value).resolve())
PY
)

if [[ ${#scenarios[@]} -eq 0 ]]; then
  current_platform=$("$python_bin" - <<'PY'
import platform
system = platform.system().lower()
if system == "linux":
    print("linux")
elif system == "windows":
    print("windows")
else:
    raise SystemExit(f"unsupported platform for live scenarios: {system}")
PY
)
  mapfile -t scenarios < <("$python_bin" - "$manifest_dir" "$current_platform" <<'PY'
import json
import pathlib
import sys

manifest_dir = pathlib.Path(sys.argv[1])
platform_name = sys.argv[2]
scenario_ids = []
for path in sorted(manifest_dir.glob("*.scenario.json")):
    with path.open("r", encoding="utf-8") as handle:
        manifest = json.load(handle)
    platforms = [value.lower() for value in manifest.get("supportedLivePlatforms", [])]
    if platform_name in platforms:
        scenario_ids.append(manifest["id"])
for scenario_id in scenario_ids:
    print(scenario_id)
PY
)
fi

[[ ${#scenarios[@]} -gt 0 ]] || { echo "no scenarios selected" >&2; exit 1; }
mapfile -t invalid_scenarios < <("$python_bin" - "$manifest_dir" "${scenarios[@]}" <<'PY'
import json
import pathlib
import sys

manifest_dir = pathlib.Path(sys.argv[1])
selected = sys.argv[2:]
known = set()
for path in manifest_dir.glob("*.scenario.json"):
    with path.open("r", encoding="utf-8") as handle:
        known.add(json.load(handle)["id"])
for scenario_id in selected:
    if scenario_id not in known:
        print(scenario_id)
PY
)
if [[ ${#invalid_scenarios[@]} -gt 0 ]]; then
  echo "unknown scenario id(s): ${invalid_scenarios[*]}" >&2
  exit 2
fi

attempts_root="$results_root/attempts"
logs_root="$results_root/logs"
test_results_root="$results_root/testresults"
trial_results_root="$results_root/trials"
metadata_root="$results_root/metadata"

rm -rf "$results_root"
mkdir -p "$attempts_root" "$logs_root" "$test_results_root" "$trial_results_root" "$metadata_root"

test_filter="FullyQualifiedName~DotnetDiagnostics.ScenarioEvaluation.Tests.ScenarioIsolatedTrialTests.IsolatedTrial_ExecutesScenarioFromEnvironment"
overall_exit=0
test_common_args=(
  "$project"
  --configuration "$configuration"
  --blame-hang-timeout 5m
  --blame-hang-dump-type none
  --filter "$test_filter"
)
if [[ "$use_no_build" == true ]]; then
  test_common_args+=(--no-build)
fi

write_attempt_record() {
  local metadata_path="$1"
  local scenario_id="$2"
  local trial="$3"
  local attempt="$4"
  local exit_code="$5"
  local attempt_outcome="$6"
  local attempt_artifact_path="$7"
  local trx_path="$8"
  local results_dir="$9"
  local log_path="${10}"
  local detail="${11}"

  "$python_bin" - "$metadata_path" "$scenario_id" "$trial" "$attempt" "$exit_code" "$attempt_outcome" \
    "$attempt_artifact_path" "$trx_path" "$results_dir" "$log_path" "$detail" <<'PY'
import json
import pathlib
import sys

metadata_path = pathlib.Path(sys.argv[1])
record = {
    "scenarioId": sys.argv[2],
    "trial": int(sys.argv[3]),
    "attempt": int(sys.argv[4]),
    "exitCode": int(sys.argv[5]),
    "attemptOutcome": sys.argv[6],
    "attemptArtifactPath": sys.argv[7] or None,
    "trxPath": sys.argv[8] or None,
    "resultsDirectory": sys.argv[9] or None,
    "logPath": sys.argv[10] or None,
    "detail": sys.argv[11],
}
metadata_path.parent.mkdir(parents=True, exist_ok=True)
with metadata_path.open("a", encoding="utf-8") as handle:
    handle.write(json.dumps(record))
    handle.write("\n")
PY
}

write_trial_summary() {
  local metadata_path="$1"
  local trial_output_path="$2"

  "$python_bin" - "$metadata_path" "$trial_output_path" <<'PY'
import json
import pathlib
import sys

metadata_path = pathlib.Path(sys.argv[1])
trial_output_path = pathlib.Path(sys.argv[2])
records = []
for line in metadata_path.read_text(encoding="utf-8").splitlines():
    if line.strip():
        records.append(json.loads(line))
if not records:
    raise SystemExit(f"no attempt records found in {metadata_path}")

latest = records[-1]
artifact = None
artifact_path = latest.get("attemptArtifactPath")
if artifact_path:
    candidate = pathlib.Path(artifact_path)
    if candidate.is_file():
        artifact = json.loads(candidate.read_text(encoding="utf-8"))

final_outcome = str(latest["attemptOutcome"]).lower()
final_failure_kind = "none"
final_detail = latest["detail"]
if final_outcome == "crashed":
    final_failure_kind = "environment"
    if artifact is not None:
        artifact_outcome = str(artifact.get("outcome", "unknown")).lower()
        final_detail = f"{final_detail} Artifact outcome before the host exit: {artifact_outcome}."
elif artifact is not None:
    final_failure_kind = str(artifact.get("failureKind", "none")).lower()
    final_detail = artifact.get("detail") or final_detail
elif final_outcome == "failed":
    final_failure_kind = "environment"

summary = {
    "schemaVersion": 1,
    "scenarioId": latest["scenarioId"],
    "trial": latest["trial"],
    "finalOutcome": final_outcome,
    "finalFailureKind": final_failure_kind,
    "detail": final_detail,
    "attemptCount": len(records),
    "attempts": records,
    "trialArtifact": artifact,
}
trial_output_path.parent.mkdir(parents=True, exist_ok=True)
trial_output_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")
PY
}

for scenario in "${scenarios[@]}"; do
  echo "==> scenario: ${scenario}"
  for trial in $(seq 1 "$repetitions"); do
    metadata_path="$metadata_root/${scenario}.trial-${trial}.attempts.ndjson"
    trial_output_path="$trial_results_root/${scenario}.trial-${trial}.result.json"
    rm -f "$metadata_path" "$trial_output_path"

    final_outcome="crashed"
    attempt_limit=$(( max_crash_retries + 1 ))
    for attempt in $(seq 1 "$attempt_limit"); do
      results_dir="$test_results_root/${scenario}/trial-${trial}/attempt-${attempt}"
      attempt_artifact_path="$attempts_root/${scenario}/trial-${trial}/attempt-${attempt}.json"
      log_path="$logs_root/${scenario}.trial-${trial}.attempt-${attempt}.log"
      trx_name="${scenario}.trial-${trial}.attempt-${attempt}.trx"
      trx_path="$results_dir/$trx_name"

      mkdir -p "$(dirname "$attempt_artifact_path")" "$results_dir" "$(dirname "$log_path")"
      rm -f "$attempt_artifact_path" "$log_path" "$trx_path"

      echo "---- trial ${trial}, attempt ${attempt}/${attempt_limit}"
      set +e
      env \
        DOTNET_DIAGNOSTICS_SCENARIO_ID="$scenario" \
        DOTNET_DIAGNOSTICS_SCENARIO_TRIAL="$trial" \
        DOTNET_DIAGNOSTICS_SCENARIO_ATTEMPT="$attempt" \
        DOTNET_DIAGNOSTICS_SCENARIO_TRIAL_ARTIFACT_PATH="$attempt_artifact_path" \
        dotnet test "${test_common_args[@]}" \
          --logger "trx;LogFileName=$trx_name" \
          --results-directory "$results_dir" \
          2>&1 | tee "$log_path"
      exit_code=${PIPESTATUS[0]}
      set -e

      attempt_outcome=""
      detail=""
      artifact_outcome=""
      if [[ -s "$attempt_artifact_path" ]]; then
        set +e
        artifact_outcome=$("$python_bin" - "$attempt_artifact_path" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as handle:
    artifact = json.load(handle)
print(str(artifact.get("outcome", "failed")).lower())
PY
)
        parse_exit_code=$?
        detail=$("$python_bin" - "$attempt_artifact_path" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as handle:
    artifact = json.load(handle)
print((artifact.get("detail") or "").replace("\n", " | "))
PY
)
        detail_parse_exit_code=$?
        set -e

        if [[ "$parse_exit_code" -eq 0 && "$detail_parse_exit_code" -eq 0 ]]; then
          if [[ "$exit_code" -ne 0 && "$artifact_outcome" == "passed" ]]; then
            attempt_outcome="crashed"
            detail="dotnet test exited ${exit_code} after producing a passed artifact at ${attempt_artifact_path}; treating the isolated trial as a testhost crash."
          else
            attempt_outcome="$artifact_outcome"
          fi
        elif [[ "$exit_code" -ne 0 ]]; then
          attempt_outcome="crashed"
          detail="dotnet test exited ${exit_code} and left an unreadable artifact at ${attempt_artifact_path}; treating the isolated trial as a testhost crash."
        else
          attempt_outcome="failed"
          detail="dotnet test exited 0 but left an unreadable artifact at ${attempt_artifact_path}."
        fi
      elif [[ "$exit_code" -ne 0 ]]; then
        attempt_outcome="crashed"
        detail="dotnet test exited ${exit_code} without producing ${attempt_artifact_path}; treating the isolated trial as a testhost crash."
      else
        attempt_outcome="failed"
        detail="dotnet test exited 0 without producing ${attempt_artifact_path}."
      fi

      write_attempt_record \
        "$metadata_path" \
        "$scenario" \
        "$trial" \
        "$attempt" \
        "$exit_code" \
        "$attempt_outcome" \
        "$attempt_artifact_path" \
        "$trx_path" \
        "$results_dir" \
        "$log_path" \
        "$detail"

      if [[ "$attempt_outcome" == "passed" ]]; then
        final_outcome="passed"
        break
      fi

      if [[ "$attempt_outcome" == "failed" ]]; then
        final_outcome="failed"
        break
      fi

      final_outcome="crashed"
      if [[ "$attempt" -lt "$attempt_limit" ]]; then
        echo "warning: isolated trial crashed; retrying once per issue #147 conventions."
      fi
    done

    write_trial_summary "$metadata_path" "$trial_output_path"

    if [[ "$final_outcome" != "passed" ]]; then
      overall_exit=1
    fi
  done
done

"$python_bin" - "$results_root" "$repetitions" "$max_crash_retries" "${scenarios[@]}" <<'PY'
import json
import pathlib
import sys
from collections import Counter

results_root = pathlib.Path(sys.argv[1])
repetitions = int(sys.argv[2])
max_crash_retries = int(sys.argv[3])
selected_scenarios = sys.argv[4:]
trial_dir = results_root / "trials"
trial_files = sorted(trial_dir.glob("*.result.json"))
trial_summaries = []
totals = Counter()
for path in trial_files:
    data = json.loads(path.read_text(encoding="utf-8"))
    final_outcome = str(data["finalOutcome"]).lower()
    totals[final_outcome] += 1
    trial_summaries.append(
        {
            "scenarioId": data["scenarioId"],
            "trial": data["trial"],
            "finalOutcome": final_outcome,
            "finalFailureKind": str(data["finalFailureKind"]).lower(),
            "path": str(path),
        }
    )
summary = {
    "schemaVersion": 1,
    "generatedAtUtc": __import__("datetime").datetime.now(__import__("datetime").timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
    "repetitions": repetitions,
    "maxCrashRetries": max_crash_retries,
    "selectedScenarios": selected_scenarios,
    "totals": {
        "passed": totals.get("passed", 0),
        "failed": totals.get("failed", 0),
        "crashed": totals.get("crashed", 0),
    },
    "trials": trial_summaries,
}
(results_root / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
print(json.dumps(summary, indent=2))
PY

exit "$overall_exit"
