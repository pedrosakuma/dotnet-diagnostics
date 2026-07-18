#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 7 ]]; then
  echo "usage: $0 <repo-url> <main-ref> <pr-ref> <work-root> <runner-class> <runner-image> <run-prefix>" >&2
  exit 2
fi

repo_url=$1
main_ref=$2
pr_ref=$3
work_root=$4
runner_class=$5
runner_image=$6
run_prefix=$7

refs_root="$work_root/refs"
compact_root="$work_root/artifacts/paired/compact"
raw_root="$work_root/artifacts/paired/raw"
stage_metrics="$work_root/artifacts/paired/stages.tsv"
mkdir -p "$refs_root" "$compact_root/measurements" "$raw_root"
: > "$stage_metrics"

now_ns() {
  date +%s%N
}

elapsed_seconds() {
  awk -v start="$1" -v end="$2" 'BEGIN { printf "%.4f", (end - start) / 1000000000 }'
}

path_bytes() {
  local total=0
  local path
  for path in "$@"; do
    if [[ -f "$path" ]]; then
      total=$((total + $(stat -c %s "$path")))
    elif [[ -d "$path" ]]; then
      local bytes
      bytes=$(find "$path" -type f -printf '%s\n' | awk '{ total += $1 } END { print total + 0 }')
      total=$((total + bytes))
    fi
  done
  printf '%s' "$total"
}

record_stage() {
  local kind=$1
  local name=$2
  local started=$3
  local bytes=$4
  local ref=${5:-}
  local pair=${6:-}
  local ended
  ended=$(now_ns)
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$kind" "$name" "$(elapsed_seconds "$started" "$ended")" "$bytes" "$ref" "$pair" \
    >> "$stage_metrics"
}

checkout_ref() {
  local ref_name=$1
  local destination=$2
  local label=$3
  local started
  started=$(now_ns)
  git init --quiet "$destination"
  git -C "$destination" remote add origin "$repo_url"
  git -C "$destination" fetch --quiet --depth=1 origin "$ref_name"
  git -C "$destination" checkout --quiet --detach FETCH_HEAD
  record_stage checkout "checkout-$label" "$started" "$(path_bytes "$destination/.git")" "$label"
}

checkout_ref "$main_ref" "$refs_root/main" main
checkout_ref "$pr_ref" "$refs_root/pr" pull_request

main_sha=$(git -C "$refs_root/main" rev-parse HEAD)
pr_sha=$(git -C "$refs_root/pr" rev-parse HEAD)
main_sdk=$(cd "$refs_root/main" && dotnet --version)
pr_sdk=$(cd "$refs_root/pr" && dotnet --version)
if [[ "$main_sdk" != "$pr_sdk" ]]; then
  echo "SDK mismatch: main=$main_sdk pull_request=$pr_sdk" >&2
  exit 1
fi

build_ref() {
  local directory=$1
  local label=$2
  local started
  started=$(now_ns)
  dotnet restore "$directory/benchmarks/DiagnosedBenchmarks/DiagnosedBenchmarks.csproj"
  dotnet build "$directory/benchmarks/DiagnosedBenchmarks/DiagnosedBenchmarks.csproj" \
    --no-restore --configuration Release
  record_stage restore_build "restore-build-$label" "$started" \
    "$(path_bytes "$directory/benchmarks/DiagnosedBenchmarks/bin/Release")" "$label"
}

build_ref "$refs_root/main" main
build_ref "$refs_root/pr" pull_request

measure_ref() {
  local directory=$1
  local label=$2
  local sha=$3
  local pair=$4
  local output="$compact_root/measurements/$label-$pair.json"
  local artifacts="$raw_root/$label/clean-$pair"
  (
    cd "$directory/benchmarks/DiagnosedBenchmarks"
    dotnet run --project . \
      --configuration Release --no-build -- \
      perf-regression measure \
      --run-id "$run_prefix-$label-$pair" \
      --output "$output" \
      --artifacts "$artifacts" \
      --runner-class "$runner_class" \
      --runner-image "$runner_image" \
      --baseline-build-id "$sha" \
      --baseline-commit "$sha" \
      --candidate-build-id "$sha" \
      --candidate-commit "$sha"
  )
}

for pair in 1 2 3; do
  pair_started=$(now_ns)
  if (( pair % 2 == 1 )); then
    measure_ref "$refs_root/main" main "$main_sha" "$pair"
    measure_ref "$refs_root/pr" pull_request "$pr_sha" "$pair"
    order=main_then_pr
  else
    measure_ref "$refs_root/pr" pull_request "$pr_sha" "$pair"
    measure_ref "$refs_root/main" main "$main_sha" "$pair"
    order=pr_then_main
  fi
  pair_bytes=$(path_bytes \
    "$compact_root/measurements/main-$pair.json" \
    "$compact_root/measurements/pull_request-$pair.json" \
    "$raw_root/main/clean-$pair" \
    "$raw_root/pull_request/clean-$pair")
  record_stage clean_pair "clean-pair-$pair-$order" "$pair_started" "$pair_bytes" "" "$pair"
done

diagnostic_started=$(now_ns)
(
  cd "$refs_root/pr/benchmarks/DiagnosedBenchmarks"
  dotnet run --project . \
    --configuration Release --no-build -- \
    perf-regression diagnose \
    --output "$compact_root/diagnostic.json" \
    --artifacts "$raw_root/pull_request/diagnostic" \
    --runner-class "$runner_class" \
    --runner-image "$runner_image" \
    --candidate-build-id "$pr_sha" \
    --candidate-commit "$pr_sha"
)
record_stage diagnostics diagnostics-pull-request "$diagnostic_started" \
  "$(path_bytes "$compact_root/diagnostic.json" "$raw_root/pull_request/diagnostic")" pull_request

cat > "$work_root/artifacts/paired/refs.env" <<EOF
MAIN_SHA=$main_sha
PR_SHA=$pr_sha
SELECTED_SDK=$main_sdk
EOF
