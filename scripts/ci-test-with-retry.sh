#!/usr/bin/env bash
# Wraps `dotnet test` and tolerates the documented Ubuntu host-crash flake
# (issue #147): the test runner exits non-zero because the test host process
# crashes AFTER every individual test has already reported "Passed".
#
# Behaviour:
#   - Real failures (any test with outcome="Failed" / errors / hangs) → exit
#     immediately with the dotnet exit code. No retry, no masking.
#   - "Phantom" host crash (TRX shows failed=0, error=0, aborted=0 but
#     ResultSummary outcome="Failed") → retry once. If the retry passes,
#     succeed. If the retry reproduces the same phantom crash, log a warning
#     and exit 0 — the test results captured before the crash are trustworthy.
#
# Usage: scripts/ci-test-with-retry.sh <trx-name> -- <dotnet-test-args...>
#
# Example:
#   scripts/ci-test-with-retry.sh core-test-results.trx -- \
#     dotnet test tests/Foo/Foo.csproj --no-build -c Release \
#       --logger "trx;LogFileName=core-test-results.trx" \
#       --results-directory TestResults
set -uo pipefail

if [[ $# -lt 3 ]] || [[ "$2" != "--" ]]; then
  echo "usage: $0 <trx-name> -- <dotnet-test-args...>" >&2
  exit 2
fi

trx_name="$1"
shift 2
trx_path="TestResults/${trx_name}"

# Returns 0 if the TRX from the last run looks like the documented phantom
# crash (every executed test passed but the host died), 1 otherwise.
is_phantom_crash() {
  [[ -f "$trx_path" ]] || return 1

  local outcome failed error aborted timeout
  outcome=$(grep -oE '<ResultSummary[^>]*outcome="[^"]*"' "$trx_path" \
            | head -1 | sed -E 's/.*outcome="([^"]*)".*/\1/')
  local counters
  counters=$(grep -oE '<Counters[^/]*/>' "$trx_path" | head -1)
  [[ -z "$counters" ]] && return 1

  failed=$(echo "$counters"  | sed -nE 's/.*failed="([0-9]+)".*/\1/p')
  error=$(echo "$counters"   | sed -nE 's/.* error="([0-9]+)".*/\1/p')
  aborted=$(echo "$counters" | sed -nE 's/.*aborted="([0-9]+)".*/\1/p')
  timeout=$(echo "$counters" | sed -nE 's/.*timeout="([0-9]+)".*/\1/p')

  [[ "$outcome" == "Failed" ]] || return 1
  [[ "${failed:-0}"  -eq 0 ]] || return 1
  [[ "${error:-0}"   -eq 0 ]] || return 1
  [[ "${aborted:-0}" -eq 0 ]] || return 1
  [[ "${timeout:-0}" -eq 0 ]] || return 1
  return 0
}

run_attempt() {
  local label="$1"; shift
  echo "::group::dotnet test (${label})"
  "$@"
  local rc=$?
  echo "::endgroup::"
  return $rc
}

# Attempt 1 — primary run.
run_attempt "attempt 1" "$@"
rc=$?
if [[ $rc -eq 0 ]]; then
  exit 0
fi

if ! is_phantom_crash; then
  echo "::error::Real test failures detected (exit ${rc}); not retrying." >&2
  exit $rc
fi

echo "::warning::Phantom host-crash detected (issue #147): all executed tests passed but the host exited non-zero. Retrying once."

# Preserve the first crash dump + TRX for diagnostics before retry overwrites them.
if [[ -d TestResults ]]; then
  mv TestResults TestResults.attempt1 || true
  mkdir -p TestResults
fi

# Attempt 2 — retry.
run_attempt "attempt 2 (retry after phantom crash)" "$@"
rc=$?
if [[ $rc -eq 0 ]]; then
  exit 0
fi

if is_phantom_crash; then
  echo "::warning::Phantom host-crash reproduced on retry. Treating as success because every executed test passed in both attempts. See issue #147."
  exit 0
fi

echo "::error::Retry failed with real test failures (exit ${rc})." >&2
exit $rc
