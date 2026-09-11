#!/usr/bin/env bash
set -euo pipefail

[[ "$(uname -s)" == Linux ]] || { echo "Linux is required." >&2; exit 1; }
export DOTNET_DBG_MCP_RUN_QUARANTINED_LINUX_TESTS=1
export DOTNET_CLI_UI_LANGUAGE=en-US

root=TestResults/clrmd-revalidation
# Never count stale evidence or overwrite a previous sequence.
mkdir -p "$(dirname "$root")"
mkdir "$root"
dotnet --info > "$root/environment.txt"
git rev-parse HEAD >> "$root/environment.txt"
cp Directory.Packages.props "$root/package-versions.txt"

core=tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj
mcp=tests/DotnetDiagnostics.Mcp.IntegrationTests/DotnetDiagnostics.Mcp.IntegrationTests.csproj
# Blame also dumps deliberately crashed child processes. Keep crash-guard's
# expected target crash in normal CI, outside this zero-crash evidence gate.
core_filter='FullyQualifiedName!=DotnetDiagnostics.Core.Tests.LiveCoreClrProcessTests.CrashGuard_CapturesUnhandledException_FromBadCodeSample'
mcp_filter='Category!=KindIntegration&Category!=DockerIntegration'

timeout --signal=TERM --kill-after=30s 2m dotnet test "$core" -c Release --no-build \
  --filter "$core_filter" --list-tests > "$root/core-discovery.txt" 2>&1
timeout --signal=TERM --kill-after=30s 2m dotnet test "$mcp" -c Release --no-build \
  --filter "$mcp_filter" --list-tests > "$root/mcp-discovery.txt" 2>&1

for iteration in $(seq -w 1 20); do
  for suite in core mcp; do
    project="$core"
    filter=(--filter "$core_filter")
    if [[ "$suite" == mcp ]]; then
      project="$mcp"
      filter=(--filter "$mcp_filter")
    fi
    results="$root/$iteration/$suite"
    mkdir -p "$results"
    echo "::group::Iteration $iteration/20 — $suite (no retries)"
    # pipefail preserves dotnet/timeout failure even when all reported tests pass.
    # Each suite gets a new testhost, runs sequentially, and keeps its own evidence.
    timeout --signal=TERM --kill-after=30s 15m \
      dotnet test "$project" -c Release --no-build "${filter[@]}" \
      --blame-hang-timeout 5m --blame-hang-dump-type none \
      --blame-crash --blame-crash-dump-type full \
      --logger "trx;LogFileName=$suite.trx" --results-directory "$results" \
      2>&1 | tee "$results/console.log"
    python3 scripts/verify-clrmd-revalidation.py "$root" "$iteration" "$suite"
    echo "::endgroup::"
  done
  echo "Iteration $iteration/20: Core and MCP passed with complete evidence." \
    | tee -a "$root/completed.txt"
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    tail -n 1 "$root/completed.txt" >> "$GITHUB_STEP_SUMMARY"
  fi
done
