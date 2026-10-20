# Extension-only pull request CI fast path

Issue #1237 activates a bounded policy, `ci-extension-pr/1`. This is **not**
activation of the general [risk classifier](./ci-selection-observer.md), whose
[assessment](./ci-selection-assessment.md) identified unresolved dependency and
evidence gaps.

## Eligibility and fallback

Only same-repository, non-Dependabot pull requests changing non-Markdown files
exclusively under `vscode-extension/`, plus optional Markdown anywhere, qualify.
Markdown-only changes retain the existing docs-only path. Shared .NET project/
build manifests are rejected even inside the extension directory.

`scripts/ci_extension_selection.py` independently verifies full commit identities,
the checked-out/tested SHA, and the tested merge's exact base/head parents. It
classifies both the PR diff and the actual base-to-tested tree changes, with
rename detection disabled so moving a shared file cannot hide its original
path. An unknown, mixed, shared, unsafe or unreadable change retains existing
validation. Missing/stale/mismatched identities cannot authorize omission.
The selector uses no earlier run's outcome or artifact to authorize a new SHA.

Pushes to `main` do not qualify: non-Markdown pushes retain full Linux/Windows
live suites, runtime smoke and deployment coverage. Fork and Dependabot PRs
retain the existing private-feed trust rejection; this path does not expose
secrets to them or introduce public package fallback.

## What runs and what is omitted

Eligible PRs run `Extension PR tests (ubuntu-latest)` and
`Extension PR tests (windows-latest)` on separate runners:

- Restore only the CLI project and its Core dependency using `NUGET_CONFIG`;
  build the CLI from the tested checkout with the pinned SDK.
- Run the existing Python CI/selection controls.
- Install extension dependencies from the private npm proxy, compile TypeScript,
  and run the entire existing extension suite, including actual VSIX resource
  integrity and a real JSONL handshake against the freshly built CLI.
- Retain the test output as `extension-pr-tests-<os>`, including on failure.

The full solution build, .NET CLI/BenchmarkDotNet suites, Core/MCP live suites,
cross-runtime smoke, Docker smoke, Kind and Helm rendering are omitted for these
PRs. This is an intentional reduction in PR integration coverage, not proof that
those suites are redundant. The extension's regular tests, platform-specific
controls and real CLI boundary remain required; full non-documentation `main`
coverage remains independent.

Both existing `Build & Test` required contexts explicitly require the successful
Linux/Windows extension matrix before accepting an extension-only omission.
Failure, cancellation, skipped/missing matrix results, untrusted source or failed
classification cannot be treated as a docs-only pass. The required Kind and Helm
jobs still run; Kind explicitly checks the classification before omitting its
expensive steps. No branch-protection context is renamed or removed.

## Evidence and observer compatibility

Both workflows run the same selector with the same SHA/trust inputs. Artifacts
`ci-extension-selection` and `kind-extension-selection` retain `selection.json`:
policy, eligibility, reason, run/attempt identity, exact SHAs and the complete
changed-path inventory. Their job summaries distinguish selection from fallback.
These are current-run preselection records, not post-run authorization.

The non-gating observer's policy is now `ci-selection-observer/2`. Its general
recommendations remain hypothetical and may still recommend full frontend
validation. `actualExecution.selectionApplied` separately records whether the
bounded extension path was used, `selectionPolicy` names its policy, and
`extension-tests` records the real matrix outcome. Missing outcomes remain
unknown, never inferred successful.

## Rollback and timing

Set the repository Actions variable `CI_EXTENSION_FAST_PATH_DISABLED` to `true`
to retain full validation for subsequent classifications without another code
change. Any nonempty value other than `false` also disables the pilot.
Already-classified runs keep their original decision; start a fresh run if
full validation is required for that revision. Remove the variable or set it
to `false` to re-enable the policy.

Disable the pilot if independent coverage finds an omitted-suite regression
attributable to an eligible PR, classification evidence is missing/conflicting,
or a shared dependency is discovered. Preserve the failing revision and run
identity; do not substitute a retry or a passing different SHA.

Changes to the selector, its controls or either CI workflow also exercise the
extension matrix while retaining full validation. This validates the new
Linux/Windows jobs on the policy PR without pretending it is extension-only.

Measure hosted required-gate completion and initial queue delay separately
from runner consumption. A full-CI run of the workflow-changing PR validates
fallback, the extension jobs and wiring, **not** the extension-only omission
branch. That branch needs a
subsequent genuinely eligible hosted PR on the merged policy; report any
unexercised platform or trigger honestly. No wall-clock speedup is claimed
until such a run completes.
