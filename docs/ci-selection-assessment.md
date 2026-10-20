# CI selection assessment: retain observation-only mode

Assessment for #1198, following #1194. **Decision: defer general activation.** This
assessment changes neither the classifier nor CI execution. Full configured
coverage, required contexts, private-feed boundaries and evidence checks remain
in place. No release-only replacement gate or cross-SHA waiver is introduced.

Subsequent #1237 introduces a separately approved
[extension-only PR pilot](./ci-extension-fast-path.md), independent of the
general classifier assessed here. The original sample, findings and decision
below remain historical evidence, not authorization for general selection.

## Fixed sample and evidence boundary

Ten completed CI runs were selected before downloading their reports: targeted
MCP, mixed tests, scenario evaluation, CI infrastructure, two failing revisions
of the same Core/frontend feature, a failed dependency update, and historical
docs/frontend controls. This is a purposeful small sample, not random sampling
or ten independent feature trials. All runs are attempt 1; no runs were
dispatched or retried for this assessment.

Seven runs retain the hosted `ci-selection-observation` artifact. Their recorded
head equals the run's head SHA; `tested` equals `checkedOut`; policy is
`ci-selection-observer/1`; `selectionApplied` is false. Head and tested merge
SHAs remain distinct. Original JSON/Markdown artifacts and job/step metadata
were downloaded and retained outside the checkout.

Three runs have **no hosted observer artifact**. For those controls only, the
current classifier was replayed against the PR's final changed paths, after
verifying its head SHA equals the historical run's head and the file inventory
is complete. This is retrospective path classification, not a contemporaneous
base/tested-SHA report or evidence that the observer executed on those runs.

| PR / CI run | Evidence | Recommendation | Actual result | CI gate elapsed, min |
|---|---|---|---|---:|
| #1194 / [39057436433](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/39057436433) | Hosted | Full: CI/shared | Passed | 28.95 |
| #1195 / [39060080433](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/39060080433) | Hosted | Targeted MCP | Passed | 26.53 |
| #1208 / [39175726539](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/39175726539) | Hosted | Targeted MCP | Passed | 28.37 |
| #1210 / [39181853472](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/39181853472) | Hosted | Targeted MCP/tests | Passed | 28.15 |
| #1211 / [39246333937](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/39246333937) | Hosted | Full: test infrastructure | Passed | 28.82 |
| #1204 / [39173660701](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/39173660701) | Hosted | Full: Core/shared/unmapped | Failed: Windows Core | 28.85 |
| #1204 / [39170584018](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/39170584018) | Hosted | Full: Core/shared/unmapped | Failed: Ubuntu live | 27.55 |
| #1193 / [39057291408](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/39057291408) | Retrospective | Full: CI/dependency | Failed; several jobs failed or did not run | 1.52 |
| #1182 / [38644908856](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/38644908856) | Retrospective | Docs; no suites | Passed; existing docs-only fast path | 0.35 |
| #1188 / [38976855209](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/38976855209) | Retrospective | Full: frontend unmapped | Passed | 28.90 |

The observed failures were retained, not explained away as environmental.
Their full-validation recommendations would retain the failing jobs. The
dependency run failed before providing complete test evidence; it is not a
completed validation sample. The docs run does not demonstrate additional
savings: expensive steps were already skipped by the existing fast path.

The three targeted recommendations would omit Ubuntu Core/runtime smoke,
Windows Core/runtime smoke, Windows fast tests and Helm smoke. Their actual
omitted-candidate jobs/steps passed. That is **zero observed failures in three
passing targeted cases**, not an estimate of false-negative probability or
proof that omitted coverage is redundant.

## Time accounting: opportunity, not demonstrated acceleration

“CI gate elapsed” above is the interval from `Detect changes` job start through
the later Ubuntu/Windows aggregate gate completion in that CI workflow. It
includes scheduling/dependency waits after that start, excludes initial queue
wait, and excludes the separate Kind workflow and observer. It is an observed
gate-latency boundary, not a reconstructed causal critical-path graph or total
PR readiness time.

For the three targeted cases, successful omitted-candidate segments have these
measured execution totals:

| Run | Ubuntu Core + runtime-smoke steps | Windows Core + smoke job | Windows fast job | Helm job | Sum, runner-minutes |
|---|---:|---:|---:|---:|---:|
| 39060080433 | 17.93 | 20.50 | 4.80 | 0.12 | 43.35 |
| 39175726539 | 17.10 | 22.45 | 4.48 | 0.17 | 44.20 |
| 39181853472 | 17.42 | 21.98 | 6.42 | 0.12 | 45.93 |

Seconds-resolution GitHub job/step timestamps supply these measurements.
Totals are calculated before rounding. Boundaries deliberately mix whole jobs
for wholly omitted execution with individual steps inside the still-needed
Ubuntu live job; those segments do not overlap. Ubuntu setup/build, Windows
build and selected MCP/fast jobs are not counted as omitted. These are execution
minutes, not billing claims.

The observer's older partial baseline estimate was 34.8–37.2 runner-minutes per
targeted case. The new measured segments are a different, more complete
accounting boundary, not proof of a regression in CI speed. Neither total is a
wall-clock saving. Ubuntu Core and MCP share one serialized job; Windows jobs
run in parallel after a common build. No selector was executed, no counterfactual
schedule was measured, and no acceleration number is established. A future
experiment must measure required-gate completion, queue time and Kind separately.

## Blocking gaps found in the current recommendation

### The test dependency boundary is not isolated

`tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj`
references the **MCP project**, and its live tests call MCP tools directly.
Nevertheless, a path-only MCP change recommends omitting both Core suites.
All three targeted hosted controls have this limitation. Their passing omitted
suites cannot establish that MCP edits cannot break those tests.

Activation requires a dependency-aware suite map, or a conservative full
recommendation for MCP changes until the boundary is separated and verified.
The same analysis is needed for CLI/BenchmarkDotNet recommendations; this sample
contains no hosted targeted case for either. Frontend is currently unmapped,
so this assessment does not demonstrate a frontend selection policy.

### Shared build rules can lose to earlier directory rules

The classifier uses first-match rules. For example,
`src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj` is classified as MCP,
not shared build/dependencies, because the directory rule precedes `**/*.csproj`.
The same precedence concern applies to other project-local build files.
The existing shared-root dependency tests do not establish conservative handling
of these nested manifests.

A future activation candidate must prioritize shared-sensitive manifests over
directory categories and add explicit nested/mixed-path controls. This is a
demonstrated classifier-policy gap, not a claim that any sampled PR changed a
project file and escaped validation: all actual configured jobs still ran.

### Evidence reporting is not an activation gate

Existing deterministic controls confirm full recommendations for unknown paths,
mixed shared/Core changes and unreadable or missing base/head diffs. However:

- Missing actual job evidence is recorded as `unknown`; it does not change an
  otherwise targeted recommendation to full.
- A checked-out/tested SHA mismatch is noted, but an otherwise readable diff can
  remain `assessed` with a targeted recommendation.

Those are acceptable diagnostics for a non-gating observer, **not fail-closed
authorization to skip**. They were confirmed with synthetic controls, not
misrepresented as hosted incidents. The 25 existing observer tests pass but
currently preserve these reporting semantics.

An active selector needs an independently specified evidence/identity gate:
missing, unreadable, stale, mismatched or incomplete evidence must yield full
validation. A report describing uncertainty cannot itself authorize omission.
Do not require current-run outcomes before deciding that same run's jobs; define
the preselection inputs separately from post-run outcome auditing.

## Proposed future policy and rollback conditions

Keep `selectionApplied: false` now. Any activation is a separately approved
change, not an automatic consequence of this assessment.

Before such a proposal:

1. Correct shared-manifest precedence, model transitive test dependencies, and
   cover negative/mixed path controls; conservatively retain full MCP coverage
   until its Core-test dependency is addressed.
2. Define exact base/head/tested identity and trusted-input checks, fail-closed
   handling, complete discovery/TRX inventories and audit artifacts.
3. Add prospective representative hosted cases for isolated CLI, BenchmarkDotNet,
   frontend and dependency changes, retaining failures, cancellations and missing
   observations. Do not select only successful revisions.
4. Preserve Windows ETW/DIA and Linux perf/ptrace coverage, private-feed trust,
   Kind, all required check identities and live-suite isolation. Any nightly or
   exact-SHA prepublication replacement must exist and be approved before coverage
   is reduced, not be delegated to the publishing workflow.
5. Measure actual critical-path/queue boundaries in a separately approved bounded
   experiment; do not convert summed runner-minutes into predicted wall time.

If activation is eventually approved, revert to full validation on an omitted-
suite failure discovered by independent coverage, missing or conflicting source/
inventory evidence, an unmapped/shared dependency, or loss of a required
platform/trust gate. Retain the failing revision and run identity, disable
selection before investigating, and never use retry or a green different SHA
as a substitute for coverage.

This closes the bounded observation assessment with a **defer** decision. It
does not close the dependency/evidence gaps, prove selection safety, or authorize
skipping any test.
