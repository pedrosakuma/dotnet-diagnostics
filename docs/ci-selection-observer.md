# CI selection observer (observation only)

Phase 1 of change-risk-based CI selection. The `CI selection observer (observation only)` job in
`.github/workflows/ci.yml` classifies each change and records what a **hypothetical** selector would
run. It does **not** select, skip, or gate anything: every configured job and test still runs, the
docs-only fast path is unchanged, and required check contexts, triggers, trust gates and private
NuGet handling are untouched. The goal is to gather data before a later, separately approved policy.
This does not claim any CI acceleration.

## Safety properties

- Not a required check; no other job `needs` it; `continue-on-error: true`, so it can never fail CI.
- No secrets, `contents: read` only, `persist-credentials: false`, no `pull_request_target`.
- Runs after all other CI jobs (`if: always()`) so it can record their actual results.
- Unknown, unmapped, shared-sensitive (Core, test infrastructure, samples, build/dependency files,
  workflows, scripts) or unreadable changes recommend **full validation**. Mixed changes take the
  union, and any shared-sensitive path makes the whole change full.
- Markdown-only changes are classified as docs exactly like the existing `code` filter.

## Evidence

Artifact `ci-selection-observation` (`ci-selection.json` + `ci-selection.md`, also in the job summary):

| Field | Meaning |
|---|---|
| `policyVersion` | Classifier policy (`ci-selection-observer/1`); bump when rules or timings change |
| `status` | `assessed`, or `unavailable` (diff/SHA failure; recommendation is full, savings not assessed) |
| `shas` | `base`, `head`, `tested` (`GITHUB_SHA`, the merge commit on PRs) and `checkedOut` |
| `recommendation` | `hypothetical: true`; suites, `wouldSkipSuites`, per-path category/reason |
| `estimatedSavings` | Min/max runner-minutes of skipped suites that have measured timings |
| `actualExecution` | `selectionApplied: false` and the real result of every observed job |

Suites: `ubuntu-fast`, `ubuntu-core`, `ubuntu-smoke`, `ubuntu-mcp`, `windows-build`, `windows-fast`,
`windows-core`, `windows-smoke`, `windows-mcp`, `docker-smoke`, `helm-render-smoke`. Windows suites
imply `windows-build`.

## Savings are estimates with limits

Timings come from `scripts/ci-selection-timings.json` (three executed main runs, min/max). Suites
without measurements (`ubuntu-smoke`, `windows-fast`, `windows-smoke`, `docker-smoke`,
`helm-render-smoke`) are listed in `unmeasuredSuites` and `complete: false`; they are never counted
as zero. Values are summed runner-minutes, not wall-clock: jobs run in parallel and several suites
share a job, so skipping a suite would not shorten a job by that amount unless the job were split.
`null` means not assessed. The `Kind Integration` workflow is a separate workflow and is not observed.

## Maintenance

Rules and the job-to-suite map live in `scripts/ci_selection_observer.py`; tests in
`scripts/tests/test_ci_selection_observer.py` (run by the existing script unit-test step) cover safe
defaults, mixed changes, failure handling and workflow contracts. Cross-SHA waiver state and any
actual skipping are out of scope.
