# Request latency distribution-shift evidence

Issue: [#1306](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1306)<br>
Roadmap: [#1305](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1305)<br>
Foundation: [#1308](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1308),
[#1311](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1311)<br>
Verdict: **GO for a separate product contract issue**

## Question and safe next action

Can bounded request-latency statistics distinguish a known distribution shift from
repeated A/A variability well enough to route a user toward deeper request, database,
or network investigation?

The evidence may recommend that deeper investigation. It does not declare a regression,
improvement, cause, production importance, or equivalent offered load.

## Protocol

- The live target is `BadCodeSample` over loopback HTTP. The test-only
  `/request-latency-fixture` route applies a bounded server delay and optional HTTP error.
- Each exact arm retains 8 non-overlapping windows with 12 sequential requests per
  window, for 96 completed requests per arm.
- Baseline-first and candidate-first window-pair order alternates.
- Request-level p50, p95, and p99 use R7 quantiles and retain source/retained counts.
  These quantiles are descriptive; request events are not treated as independent
  inferential units.
- The paired-window estimate is the median of candidate-minus-baseline window-quantile
  deltas.
- The 95% interval uses 2,048 deterministic percentile-bootstrap resamples over whole
  window-pair deltas through the #1311 harness.
- The scenario-local robust effect is matched-pairs rank-biserial correlation v1. It is
  available only for exact, compatible, complete paired windows with at least 8 completed
  requests per window and at least 8 complete window pairs.
- A scenario recommends deeper investigation only when p95 or p99 has an interval lower
  bound of at least 10 ms and absolute rank-biserial effect of at least 0.75. These are
  synthetic-scenario routing boundaries, not product-wide thresholds.
- Bounds are fixed at 16 cases, 32 windows per arm, 256 retained values per window, and
  10,000 maximum bootstrap resamples in the shared harness.

## Live evidence

Captured locally on Windows on 2026-10-30. Values are milliseconds.

| Case | Quantile | Baseline (n) | Candidate (n) | Absolute delta | Relative delta | Paired-window median delta | 95% bootstrap interval | Rank-biserial |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| A/A | p50 | 16.100 (96) | 15.562 (96) | -0.538 | -3.34% | -0.615 | [-1.467, -0.162] | -0.889 |
| A/A | p95 | 23.019 (96) | 19.066 (96) | -3.953 | -17.17% | -3.085 | [-5.124, -0.690] | -0.944 |
| A/A | p99 | 24.475 (96) | 23.036 (96) | -1.439 | -5.88% | -1.536 | [-4.975, -0.636] | -0.944 |
| Injected shift | p50 | 15.943 (96) | 64.248 (96) | 48.305 | 302.98% | 49.273 | [44.625, 49.703] | 1.000 |
| Injected shift | p95 | 19.675 (96) | 68.316 (96) | 48.641 | 247.23% | 49.133 | [46.528, 49.980] | 1.000 |
| Injected shift | p99 | 23.273 (96) | 69.915 (96) | 46.642 | 200.41% | 49.320 | [44.309, 50.353] | 1.000 |

The A/A case remains inconclusive. The controlled injected shift recommends deeper
investigation at p95 and p99. The A/A interval excluded zero in this run, but its
magnitude remained below the predeclared 10 ms routing boundary; a narrow interval
does not establish operational importance or equivalent offered load. The injected
shift separation is larger than the maximum absolute A/A paired-window interval
bound under this protocol.

## Edge cases

| Case | Primary availability | Preserved evidence | Result |
|---|---|---|---|
| Changed request mix | `incompatible` | Per-arm quantiles and request counts; workload mix mismatch | No pooled delta, interval, effect size, or action claim |
| Low sample count | `insufficient_observations` | Two completed requests in one pair | Descriptive per-arm quantiles only |
| Reservoir approximation | `unsupported_approximation` | Source count, retained count, deterministic bounded sample, descriptive quantiles | No interval or effect-size claim |
| Target/window/filter mismatch | `incompatible` | All mismatching dimensions | No pooling |
| Errors, censored requests, missing work | `insufficient_observations` | Completed, error, client-cancelled, and started-without-terminal-response-at-window-boundary counts remain separate | Completed-request quantiles do not stand in for unfinished work |

The deterministic replay fixture contains all seven cases. In the live incomplete-work
case, the missing count is observed by starting a request that has no terminal response at
the window boundary; the test drains it afterward so cleanup remains owned. The live test
reproduces all seven from one bounded target lifetime and writes an optional JSON report when
`DOTNET_DIAGNOSTICS_REQUEST_LATENCY_REPORT_PATH` is set.

## GO rationale and limits

**GO:** repeated A/A variability and a 47 ms injected server-delay difference were
distinguishable under explicit identity, sample, completion, exact-representation, and
window-pair requirements. This is enough to justify a separate minimal Core/CLI product
contract issue.

The GO does not approve the scenario-local DTOs, thresholds, route, or effect-size
implementation as a shipping API. It does not establish equivalent offered load,
endpoint mix, production importance, or causation. MCP must reuse an existing tool, and
VS Code must continue consuming CLI JSONL if a later product contract ships.

## Reproduction

```powershell
dotnet build tests\DotnetDiagnostics.ScenarioEvaluation.Tests\DotnetDiagnostics.ScenarioEvaluation.Tests.csproj -c Release

dotnet test tests\DotnetDiagnostics.ScenarioEvaluation.Tests\DotnetDiagnostics.ScenarioEvaluation.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~RequestLatencyEvidenceTests"

$env:DOTNET_DIAGNOSTICS_REQUEST_LATENCY_REPORT_PATH = "request-latency-live-report.json"
dotnet test tests\DotnetDiagnostics.ScenarioEvaluation.Tests\DotnetDiagnostics.ScenarioEvaluation.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~RequestLatencyLiveEvidenceTests"
```
